using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Highlighting;
using SqlExplorer.App.Completion;
using SqlExplorer.App.Controls;
using SqlExplorer.App.Converters;
using SqlExplorer.App.ViewModels;
using SqlExplorer.Core.Editing;
using SqlExplorer.Core.Shortcuts;

namespace SqlExplorer.App.Views;

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
    private EditableRow? _currentRow;
    private CompletionWindow? _completionWindow;

    public DocumentView()
    {
        InitializeComponent();

        _sqlEditor = this.FindControl<TextEditor>("SqlEditor");
        if (_sqlEditor is not null)
        {
            _sqlEditor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("TSQL");
            ApplyEditorSyntaxTheme();
            ActualThemeVariantChanged += (_, _) => ApplyEditorSyntaxTheme();
            _sqlEditor.TextChanged += OnEditorTextChanged;
            _sqlEditor.TextArea.TextEntered += OnSqlTextEntered;
            _sqlEditor.KeyDown += OnSqlEditorKeyDown;
            _sqlEditor.TextArea.Caret.PositionChanged += OnCaretPositionChanged;
            _sqlEditor.TextArea.SelectionChanged += OnEditorSelectionChanged;
        }

        _resultsGrid = this.FindControl<DataGrid>("ResultsGrid");
        if (_resultsGrid is not null)
        {
            _resultsGrid.Sorting += OnGridSorting;
            _resultsGrid.CellPointerPressed += OnCellPointerPressed;
            _resultsGrid.SelectionChanged += OnGridSelectionChanged;
            _resultsGrid.LoadingRow += OnLoadingRow;
        }

        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    // Focus the SQL editor when a query tab opens so you can type immediately (no click needed).
    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is { IsQueryMode: true })
        {
            _sqlEditor?.Focus();
        }
    }

    // Number the rows in the row header (1-based, current grid position) as they're realized.
    private static void OnLoadingRow(object? sender, DataGridRowEventArgs e) =>
        e.Row.Header = (e.Row.Index + 1).ToString();

    // Browse mode pages server-side, so a header click must re-query with ORDER BY over the whole
    // table — not client-sort the current page. Cancel the built-in sort and drive the VM instead.
    private async void OnGridSorting(object? sender, DataGridColumnEventArgs e)
    {
        // Browse sorts server-side (whole table, ORDER BY); monitor sorts the materialised list
        // client-side. Either way the built-in DataGrid sort is bypassed.
        if (_viewModel is { IsBrowseMode: true } browse)
        {
            e.Handled = true;
            if (e.Column.Tag is string baseColumn)
            {
                await browse.SortByAsync(baseColumn);
            }
        }
        else if (_viewModel is { IsMonitorMode: true } monitor)
        {
            e.Handled = true;
            if (e.Column.Tag is string column)
            {
                // Defer past this Sorting event: SortMonitorBy rebuilds the grid columns, which must not
                // happen while the DataGrid is mid-sort (browse gets this deferral for free via its await).
                Avalonia.Threading.Dispatcher.UIThread.Post(() => monitor.SortMonitorBy(column));
            }
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
            _currentRow = row;
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
        _viewModel.ConfirmRequested = ShowConfirmAsync;
        _viewModel.CellActionRequested = ShowCellActionDialogAsync;

        // The TabControl reuses one DocumentView across tabs (swapping DataContext), so the SQL
        // editor row must be set BOTH ways: collapsed for browse, restored for query. Otherwise a
        // browse tab collapses the row and every later query tab shows no SQL pane.
        if (this.FindControl<Grid>("RootGrid") is { } grid)
        {
            // The SQL editor row only belongs to query mode — collapse it for browse AND monitor so the
            // grid isn't pushed down under a tall empty gap.
            grid.RowDefinitions[1].Height = _viewModel.IsQueryMode
                ? new GridLength(2, GridUnitType.Star)
                : new GridLength(0);
        }

        if (_sqlEditor is not null && _viewModel.EditorFontSize is { } fontSize)
        {
            _sqlEditor.FontSize = fontSize;
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

    // Exports the grid's current selection, or the whole active result set when nothing is selected —
    // same selection source as RecomputeAggregation. Format dialog first, then a save-file picker.
    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner || _viewModel is null || _resultsGrid is null)
        {
            return;
        }

        var selected = _resultsGrid.SelectedItems.OfType<EditableRow>().ToList();
        var isSelection = selected.Count > 0;
        var rowCount = isSelection ? selected.Count : _viewModel.Editable?.Rows.Count ?? 0;
        if (rowCount == 0)
        {
            return;
        }

        var formatDialog = new ExportDialog(_viewModel.Loc, rowCount, isSelection);
        var format = await formatDialog.ShowDialog<ExportFormat?>(owner);
        if (format is not { } chosenFormat)
        {
            return;
        }

        var text = _viewModel.BuildExportText(chosenFormat, isSelection ? selected : null);
        var (extension, typeName) = chosenFormat switch
        {
            ExportFormat.Csv => ("csv", "CSV"),
            ExportFormat.Json => ("json", "JSON"),
            _ => ("sql", "SQL")
        };

        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = $"export.{extension}",
            FileTypeChoices = [new FilePickerFileType(typeName) { Patterns = [$"*.{extension}"] }]
        });

        if (file is null)
        {
            return;
        }

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(text);
    }

    // Set the last-clicked column of each selected row to SQL NULL (a pending edit until Save).
    private void OnSetNullClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not { IsResultEditable: true } || _resultsGrid is null)
        {
            return;
        }

        // Prefer the multi-row selection; fall back to the right-clicked cell's row when nothing's selected.
        var rows = _resultsGrid.SelectedItems.OfType<EditableRow>().ToList();
        if (rows.Count == 0 && _currentRow is not null)
        {
            rows.Add(_currentRow);
        }

        foreach (var row in rows)
        {
            row[_currentColumnIndex] = null;
        }
    }

    // Monitor row actions. The right-click that opens the context menu also fires OnCellPointerPressed,
    // so _currentRow is the row under the cursor. Kill/Cancel confirm (destructive) inside the VM.
    private void OnKillSessionClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null && _currentRow is not null)
        {
            _ = _viewModel.KillSessionAsync(_currentRow);
        }
    }

    private void OnCancelQueryClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null && _currentRow is not null)
        {
            _ = _viewModel.CancelQueryAsync(_currentRow);
        }
    }

    // Leave the monitor's own polling session visible but with Kill/Cancel disabled (Rick's decision #3),
    // so you can't shoot down the very connection driving the refresh.
    private void OnGridContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_viewModel is not { IsMonitorMode: true } || sender is not ContextMenu menu)
        {
            return;
        }

        var enabled = _currentRow is not null && !_viewModel.IsOwnSession(_currentRow);
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            if (item.Name is "KillSessionItem" or "CancelQueryItem")
            {
                item.IsEnabled = enabled;
            }
        }
    }

    // Show a provider-owned cell-action dialog (e.g. MSSQL's blocking-session view) in the shared chrome.
    private async Task ShowCellActionDialogAsync(NodeInfoDialogViewModel dialogViewModel)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var dialog = new NodeInfoDialog { DataContext = dialogViewModel };
        await dialog.ShowDialog(owner);
    }

    // A link cell (provider-declared action) was clicked: open its dialog. The Tag carries the column index
    // and the button's DataContext is the row.
    private void OnCellActionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int index, DataContext: EditableRow row } && _viewModel is not null)
        {
            _ = _viewModel.OpenCellActionAsync(index, row);
        }
    }

    // Yes/no confirmation for a destructive monitor action; Yes → true, No/closed → false.
    private async Task<bool> ShowConfirmAsync(string title, string message)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner || _viewModel is null)
        {
            return false;
        }

        var dialog = new ConfirmDialog(title, message, _viewModel.Loc["Yes"], _viewModel.Loc["No"]);
        return await dialog.ShowDialog<bool>(owner);
    }

    private void OnCopyCellClick(object? sender, RoutedEventArgs e) => _ = CopyCellAsync();

    // Copy just the value of the cell under the cursor (tracked by OnCellPointerPressed, which also fires
    // on the right-click that opens this menu).
    private async Task CopyCellAsync()
    {
        if (_currentRow is null
            || _currentColumnIndex < 0
            || _currentColumnIndex >= _currentRow.Cells.Count)
        {
            return;
        }

        var value = _currentRow.Cells[_currentColumnIndex].Value;
        await CopyFeedback.CopyAsync(this, value?.ToString() ?? string.Empty, _viewModel?.Loc["CopiedToClipboard"] ?? "Copied");
    }

    private void OnCopyClick(object? sender, RoutedEventArgs e) => _ = CopyTsvAsync(includeHeaders: false);

    private void OnCopyWithHeadersClick(object? sender, RoutedEventArgs e) => _ = CopyTsvAsync(includeHeaders: true);

    // Plain tab-separated copy of the selected rows (or the whole result), the everyday clipboard action.
    private async Task CopyTsvAsync(bool includeHeaders)
    {
        if (_viewModel is null || _resultsGrid is null)
        {
            return;
        }

        var selected = _resultsGrid.SelectedItems.OfType<EditableRow>().ToList();
        var text = _viewModel.BuildClipboardTsv(includeHeaders, selected.Count > 0 ? selected : null);
        if (text.Length > 0)
        {
            await CopyFeedback.CopyAsync(this, text, _viewModel.Loc["CopiedToClipboard"]);
        }
    }

    private void OnCopyAsCsvClick(object? sender, RoutedEventArgs e) => _ = CopyResultAsync(ExportFormat.Csv);

    private void OnCopyAsMarkdownClick(object? sender, RoutedEventArgs e) => _ = CopyResultAsync(ExportFormat.Markdown);

    private void OnCopyAsInsertClick(object? sender, RoutedEventArgs e) => _ = CopyResultAsync(ExportFormat.Sql);

    private void OnCopyAsHtmlClick(object? sender, RoutedEventArgs e) => _ = CopyResultAsync(ExportFormat.Html);

    // SQL-editor context menu: standard edit actions (AvaloniaEdit ships no default menu) …
    private void OnEditorCutClick(object? sender, RoutedEventArgs e) => _sqlEditor?.Cut();

    private void OnEditorCopyClick(object? sender, RoutedEventArgs e) => _sqlEditor?.Copy();

    private void OnEditorPasteClick(object? sender, RoutedEventArgs e) => _sqlEditor?.Paste();

    private void OnEditorSelectAllClick(object? sender, RoutedEventArgs e) => _sqlEditor?.SelectAll();

    // … plus Generate GUID: insert a fresh v4 (random) or v7 (time-ordered) GUID at the caret,
    // replacing any current selection.
    private void OnGenerateGuidV4Click(object? sender, RoutedEventArgs e) => InsertGuid(Guid.NewGuid());

    private void OnGenerateGuidV7Click(object? sender, RoutedEventArgs e) => InsertGuid(Guid.CreateVersion7());

    private void InsertGuid(Guid guid)
    {
        if (_sqlEditor is null)
        {
            return;
        }

        var text = guid.ToString();
        var selection = _sqlEditor.TextArea.Selection;
        if (!selection.IsEmpty)
        {
            selection.ReplaceSelectionWithText(text);
        }
        else
        {
            _sqlEditor.Document.Insert(_sqlEditor.CaretOffset, text);
        }

        _sqlEditor.Focus();
    }

    // Grid-cell Generate GUID: set the current column of each selected row (or the right-clicked cell's
    // row) to a fresh, unique GUID. Editable grids only (menu item is hidden otherwise).
    private void OnGridGuidV4Click(object? sender, RoutedEventArgs e) => SetCellsGuid(version7: false);

    private void OnGridGuidV7Click(object? sender, RoutedEventArgs e) => SetCellsGuid(version7: true);

    private void SetCellsGuid(bool version7)
    {
        if (_viewModel is not { IsResultEditable: true } || _resultsGrid is null)
        {
            return;
        }

        var rows = _resultsGrid.SelectedItems.OfType<EditableRow>().ToList();
        if (rows.Count == 0 && _currentRow is not null)
        {
            rows.Add(_currentRow);
        }

        foreach (var row in rows)
        {
            if (_currentColumnIndex >= 0 && _currentColumnIndex < row.Cells.Count)
            {
                row[_currentColumnIndex] = (version7 ? Guid.CreateVersion7() : Guid.NewGuid()).ToString();
            }
        }
    }

    // Same selection-or-whole-result source as "Export…", straight to the clipboard instead of a file.
    private async Task CopyResultAsync(ExportFormat format)
    {
        if (_viewModel is null || _resultsGrid is null)
        {
            return;
        }

        var selected = _resultsGrid.SelectedItems.OfType<EditableRow>().ToList();
        var text = _viewModel.BuildExportText(format, selected.Count > 0 ? selected : null);
        if (text.Length > 0)
        {
            await CopyFeedback.CopyAsync(this, text, _viewModel.Loc["CopiedToClipboard"]);
        }
    }

    // Enter in a per-column filter box applies immediately, same as the free-text WHERE box's Apply
    // button being the editor's IsDefault — no separate auto-apply/debounce logic needed.
    private void OnColumnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _viewModel?.ApplyFilterCommand.Execute(null);
        }
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

    // Kept in sync so "Run"/"Run at cursor"/"Explain" know what text to act on without the VM reaching
    // back into AvaloniaEdit (Notes: VM stays free of the editor's document type).
    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null && _sqlEditor is not null)
        {
            _viewModel.CaretOffset = _sqlEditor.CaretOffset;
        }
    }

    private void OnEditorSelectionChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null && _sqlEditor is not null)
        {
            _viewModel.SelectionText = _sqlEditor.SelectedText ?? string.Empty;
        }
    }

    // Auto-trigger completion right after typing "." — the common alias.column moment.
    private void OnSqlTextEntered(object? sender, TextInputEventArgs e)
    {
        if (e.Text == ".")
        {
            OpenCompletion();
        }
    }

    private void ApplyEditorSyntaxTheme()
    {
        if (_sqlEditor?.SyntaxHighlighting is { } definition)
        {
            SqlSyntaxTheme.Apply(definition, ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark);
        }
    }

    // Ctrl+Space explicitly requests completion anywhere in the query; the line-comment toggle is
    // resolved live from the user's keymap (default Ctrl+/) so it honours a rebind from Settings.
    private void OnSqlEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.Control)
        {
            OpenCompletion();
            e.Handled = true;
        }
        else if (MatchesShortcut(e, ShortcutCatalog.Ids.ToggleComment, Key.OemQuestion, KeyModifiers.Control))
        {
            ToggleLineComment();
            e.Handled = true;
        }
        else if (MatchesShortcut(e, ShortcutCatalog.Ids.ZoomIn, Key.OemPlus, KeyModifiers.Control))
        {
            _sqlEditor!.FontSize = Math.Min(_sqlEditor.FontSize + 1, 32);
            _viewModel?.PersistEditorFontSize(_sqlEditor.FontSize);
            e.Handled = true;
        }
        else if (MatchesShortcut(e, ShortcutCatalog.Ids.ZoomOut, Key.OemMinus, KeyModifiers.Control))
        {
            _sqlEditor!.FontSize = Math.Max(_sqlEditor.FontSize - 1, 8);
            _viewModel?.PersistEditorFontSize(_sqlEditor.FontSize);
            e.Handled = true;
        }
    }

    // Default Ctrl+OemQuestion (Ctrl+/); falls back to that when no keymap is available (e.g. previewer)
    // or the persisted gesture is malformed.
    // Resolve an editor-scoped shortcut from the live keymap (so it honours rebinds from Settings ▸
    // Keyboard), falling back to a fixed default only when the keymap isn't available yet.
    private static bool MatchesShortcut(KeyEventArgs e, string commandId, Key fallbackKey, KeyModifiers fallbackMods)
    {
        var gesture = KeymapService.Current?.Resolve(commandId);
        if (gesture is { Length: > 0 })
        {
            try
            {
                return KeyGesture.Parse(gesture).Matches(e);
            }
            catch (Exception)
            {
                // Fall through to the default below.
            }
        }

        return e.Key == fallbackKey && e.KeyModifiers == fallbackMods;
    }

    // Comment or uncomment the selected lines (or the caret line) with "-- ", DataGrip/VS Code style.
    // If every non-blank line in range is already commented, it uncomments; otherwise it comments all.
    private void ToggleLineComment()
    {
        if (_sqlEditor is null)
        {
            return;
        }

        var doc = _sqlEditor.Document;
        var selection = _sqlEditor.TextArea.Selection;
        int firstLine, lastLine;
        if (!selection.IsEmpty && selection.SurroundingSegment is { } segment)
        {
            firstLine = doc.GetLineByOffset(segment.Offset).LineNumber;
            lastLine = doc.GetLineByOffset(segment.EndOffset).LineNumber;
        }
        else
        {
            firstLine = lastLine = _sqlEditor.TextArea.Caret.Line;
        }

        var allCommented = true;
        for (var n = firstLine; n <= lastLine; n++)
        {
            var line = doc.GetLineByNumber(n);
            var content = doc.GetText(line.Offset, line.Length).TrimStart();
            if (content.Length > 0 && !content.StartsWith("--", StringComparison.Ordinal))
            {
                allCommented = false;
                break;
            }
        }

        doc.BeginUpdate();
        try
        {
            for (var n = firstLine; n <= lastLine; n++)
            {
                var line = doc.GetLineByNumber(n);
                var lineText = doc.GetText(line.Offset, line.Length);
                if (lineText.Trim().Length == 0)
                {
                    continue; // leave blank lines untouched
                }

                if (allCommented)
                {
                    var dashes = lineText.IndexOf("--", StringComparison.Ordinal);
                    var remove = dashes + 2 < lineText.Length && lineText[dashes + 2] == ' ' ? 3 : 2;
                    doc.Remove(line.Offset + dashes, remove);
                }
                else
                {
                    var indent = lineText.Length - lineText.TrimStart().Length;
                    doc.Insert(line.Offset + indent, "-- ");
                }
            }
        }
        finally
        {
            doc.EndUpdate();
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
        var monitor = _viewModel.IsMonitorMode;
        for (var i = 0; i < editable.Columns.Count; i++)
        {
            var column = editable.Columns[i];
            var baseName = column.BaseColumn ?? column.Name;
            // Browse sorts on the base column (server-side); monitor sorts any column by its display name
            // (client-side). Both drive the same header-arrow + Tag path below.
            var sortable = (browse && column.BaseColumn is not null) || monitor;

            // Show the active sort direction with an arrow in the header (the built-in sort glyph is
            // bypassed since we sort server-side).
            var arrow = sortable && _viewModel.SortColumn == baseName
                ? _viewModel.SortDescending ? " ▼" : " ▲"
                : string.Empty;

            // Resolve the column's action-capability once here (cheap, by name) rather than per cell.
            var mayHaveActions = _viewModel.ColumnMayHaveCellActions(column.Name);
            var gridColumn = new DataGridTemplateColumn
            {
                Header = column.Name + arrow,
                IsReadOnly = column.IsReadOnly,
                CanUserSort = sortable,
                CellTemplate = BuildCellTemplate(i, mayHaveActions),
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

    // Per-row template: a cell the provider marks actionable (ICustomCellActionUi — e.g. MSSQL's
    // blocking_session_id > 0) renders as a clickable link; every other cell is the plain value.
    // <paramref name="mayHaveActions"/> is the provider's cheap, value-independent column pre-filter,
    // resolved ONCE per column in SetGridColumns — a column that can never carry an action skips the
    // per-cell HasCellAction call entirely, so the scroll/render hot path pays no provider lookup or
    // context allocation on the vast majority of cells (the fix for sluggish large-grid scrolling).
    private IDataTemplate BuildCellTemplate(int index, bool mayHaveActions) =>
        new FuncDataTemplate<EditableRow>((row, _) =>
            mayHaveActions && row is not null && _viewModel?.HasCellAction(index, row) == true
                ? BuildActionCell(index)
                : BuildTextCell(index));

    private static Control BuildTextCell(int index)
    {
        var text = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 12, 0)
        };
        // NULL shows as a faded "NULL" marker so it can't be confused with an empty string.
        text.Bind(TextBlock.TextProperty, new Binding($"Cells[{index}].Value") { Converter = NullCellTextConverter.Instance });
        text.Bind(Visual.OpacityProperty, new Binding($"Cells[{index}].Value") { Converter = NullCellOpacityConverter.Instance });

        var border = new Border { Child = text };
        border.Bind(Border.BackgroundProperty, new Binding($"Cells[{index}].IsModified")
        {
            Converter = ModifiedCellBrushConverter.Instance
        });

        return border;
    }

    private Control BuildActionCell(int index)
    {
        var text = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextDecorations = TextDecorations.Underline
        };
        text.Bind(TextBlock.TextProperty, new Binding($"Cells[{index}].Value") { Converter = NullCellTextConverter.Instance });
        text[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("SEAccentBrush");

        var button = new Button
        {
            Tag = index,
            Content = text,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        button.Click += OnCellActionClick;
        return button;
    }

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
