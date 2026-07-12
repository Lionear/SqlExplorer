using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Highlighting;
using Lionear.SqlExplorer.App.Completion;
using Lionear.SqlExplorer.App.Converters;
using Lionear.SqlExplorer.App.ViewModels;
using Lionear.SqlExplorer.Core.Editing;

namespace Lionear.SqlExplorer.App.Views;

/// <summary>
/// The content of one editor tab. Owns the AvaloniaEdit ↔ VM.Sql sync (query mode), the
/// per-result-set DataGrid columns, and the save-review dialog. Kept free of the AvaloniaEdit
/// document type on the VM side (see Notes §3/§6).
/// </summary>
public partial class DocumentView : UserControl
{
    private DocumentViewModel? _viewModel;
    private TextEditor? _sqlEditor;
    private DataGrid? _resultsGrid;
    private bool _syncingSql;
    private int _currentColumnIndex;
    private CompletionWindow? _completionWindow;

    public DocumentView()
    {
        InitializeComponent();

        _sqlEditor = this.FindControl<TextEditor>("SqlEditor");
        if (_sqlEditor is not null)
        {
            _sqlEditor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("TSQL");
            _sqlEditor.TextChanged += OnEditorTextChanged;
            _sqlEditor.TextArea.TextEntered += OnSqlTextEntered;
            _sqlEditor.KeyDown += OnSqlEditorKeyDown;
        }

        _resultsGrid = this.FindControl<DataGrid>("ResultsGrid");
        if (_resultsGrid is not null)
        {
            _resultsGrid.Sorting += OnGridSorting;
            _resultsGrid.CellPointerPressed += OnCellPointerPressed;
            _resultsGrid.SelectionChanged += OnGridSelectionChanged;
        }

        DataContextChanged += OnDataContextChanged;
    }

    // Browse mode pages server-side, so a header click must re-query with ORDER BY over the whole
    // table — not client-sort the current page. Cancel the built-in sort and drive the VM instead.
    private async void OnGridSorting(object? sender, DataGridColumnEventArgs e)
    {
        if (_viewModel is not { IsBrowseMode: true } vm)
        {
            return;
        }

        e.Handled = true;
        if (e.Column.Tag is string baseColumn)
        {
            await vm.SortByAsync(baseColumn);
        }
    }

    // Clicking a cell shows its full value in the viewer panel (long text / JSON read comfortably).
    private void OnCellPointerPressed(object? sender, DataGridCellPointerPressedEventArgs e)
    {
        if (_viewModel is null || sender is not DataGrid grid)
        {
            return;
        }

        var columnIndex = grid.Columns.IndexOf(e.Column);
        if (e.Row.DataContext is EditableRow row && columnIndex >= 0 && columnIndex < row.Cells.Count)
        {
            _viewModel.ShowCell(columnIndex, row.Cells[columnIndex].Value);
            _currentColumnIndex = columnIndex;
            RecomputeAggregation();
        }
    }

    private void OnGridSelectionChanged(object? sender, SelectionChangedEventArgs e) => RecomputeAggregation();

    // Aggregate the current column's values across the selected rows.
    private void RecomputeAggregation()
    {
        if (_viewModel is null || _resultsGrid is null)
        {
            return;
        }

        var column = _currentColumnIndex;
        var values = _resultsGrid.SelectedItems
            .OfType<EditableRow>()
            .Where(row => column >= 0 && column < row.Cells.Count)
            .Select(row => row.Cells[column].Value)
            .ToList();

        _viewModel.UpdateAggregation(values);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as DocumentViewModel;
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.SaveReviewRequested = ShowSaveReviewAsync;

        // The TabControl reuses one DocumentView across tabs (swapping DataContext), so the SQL
        // editor row must be set BOTH ways: collapsed for browse, restored for query. Otherwise a
        // browse tab collapses the row and every later query tab shows no SQL pane.
        if (this.FindControl<Grid>("RootGrid") is { } grid)
        {
            grid.RowDefinitions[1].Height = _viewModel.IsBrowseMode
                ? new GridLength(0)
                : new GridLength(2, GridUnitType.Star);
        }

        PushSqlToEditor();
        RebuildResultColumns();
    }

    private async Task<bool> ShowSaveReviewAsync(string sql)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner || _viewModel is null)
        {
            return false;
        }

        var dialog = new SaveReviewDialog(_viewModel.Loc, sql);
        return await dialog.ShowDialog<bool>(owner);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentViewModel.Editable))
        {
            RebuildResultColumns();
        }
        else if (e.PropertyName == nameof(DocumentViewModel.Sql))
        {
            PushSqlToEditor();
        }
    }

    private void PushSqlToEditor()
    {
        if (_syncingSql || _sqlEditor is null || _viewModel is null || _sqlEditor.Text == _viewModel.Sql)
        {
            return;
        }

        _syncingSql = true;
        _sqlEditor.Text = _viewModel.Sql;
        _syncingSql = false;
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_syncingSql || _sqlEditor is null || _viewModel is null)
        {
            return;
        }

        _syncingSql = true;
        _viewModel.Sql = _sqlEditor.Text;
        _syncingSql = false;
    }

    // Auto-trigger completion right after typing "." — the common alias.column moment.
    private void OnSqlTextEntered(object? sender, TextInputEventArgs e)
    {
        if (e.Text == ".")
        {
            OpenCompletion();
        }
    }

    // Ctrl+Space explicitly requests completion anywhere in the query.
    private void OnSqlEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.Control)
        {
            OpenCompletion();
            e.Handled = true;
        }
    }

    // Schema-aware completion (1.3): the VM ranks tables/columns/keywords for the current caret
    // context (Core/Completion/SqlCompletionProvider), this just renders them in AvaloniaEdit's
    // built-in popup and lets it handle the replacement on accept.
    private void OpenCompletion()
    {
        if (_viewModel is not { IsQueryMode: true } || _sqlEditor is null)
        {
            return;
        }

        var result = _viewModel.GetCompletions(_sqlEditor.Text, _sqlEditor.CaretOffset);
        if (result.Items.Count == 0)
        {
            return;
        }

        _completionWindow = new CompletionWindow(_sqlEditor.TextArea) { StartOffset = result.ReplaceStart };
        foreach (var item in result.Items)
        {
            _completionWindow.CompletionList.CompletionData.Add(new SqlCompletionData(item));
        }

        _completionWindow.Show();
        _completionWindow.Closed += (_, _) => _completionWindow = null;
    }

    // Arbitrary result sets need dynamic columns; each cell binds two-way to its EditableCell.Value,
    // over a background that lights up while the cell is modified-unsaved.
    private void RebuildResultColumns()
    {
        var grid = this.FindControl<DataGrid>("ResultsGrid");
        if (grid is null)
        {
            return;
        }

        grid.Columns.Clear();

        var editable = _viewModel?.Editable;
        if (editable is null)
        {
            grid.ItemsSource = null;
            return;
        }

        var browse = _viewModel!.IsBrowseMode;
        for (var i = 0; i < editable.Columns.Count; i++)
        {
            var column = editable.Columns[i];
            var baseName = column.BaseColumn ?? column.Name;
            var sortable = browse && column.BaseColumn is not null;

            // Show the active sort direction with an arrow in the header (the built-in sort glyph is
            // bypassed since we sort server-side).
            var arrow = sortable && _viewModel.SortColumn == baseName
                ? _viewModel.SortDescending ? " ▼" : " ▲"
                : string.Empty;

            var gridColumn = new DataGridTemplateColumn
            {
                Header = column.Name + arrow,
                IsReadOnly = column.IsReadOnly,
                CanUserSort = sortable,
                CellTemplate = BuildCellTemplate(i),
                CellEditingTemplate = column.IsReadOnly ? null : BuildCellEditingTemplate(i)
            };

            // The Tag carries the base column name the server-side ORDER BY needs.
            if (sortable)
            {
                gridColumn.Tag = baseName;
            }

            grid.Columns.Add(gridColumn);
        }

        grid.ItemsSource = editable.Rows;

        // Fresh result: forget the previous column/selection so the aggregation strip resets.
        _currentColumnIndex = 0;
        _viewModel?.UpdateAggregation([]);
    }

    private static IDataTemplate BuildCellTemplate(int index) =>
        new FuncDataTemplate<EditableRow>((_, _) =>
        {
            var text = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 12, 0)
            };
            text.Bind(TextBlock.TextProperty, new Binding($"Cells[{index}].Value"));

            var border = new Border { Child = text };
            border.Bind(Border.BackgroundProperty, new Binding($"Cells[{index}].IsModified")
            {
                Converter = ModifiedCellBrushConverter.Instance
            });

            return border;
        });

    private static IDataTemplate BuildCellEditingTemplate(int index) =>
        new FuncDataTemplate<EditableRow>((_, _) =>
        {
            var box = new TextBox
            {
                BorderThickness = new Thickness(0),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            box.Bind(TextBox.TextProperty, new Binding($"Cells[{index}].Value") { Mode = BindingMode.TwoWay });
            return box;
        });
}
