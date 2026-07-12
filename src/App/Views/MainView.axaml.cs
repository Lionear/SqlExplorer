using System.ComponentModel;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Lionear.SqlExplorer.App.ViewModels;
using Lionear.SqlExplorer.Core.Connections;
using Lionear.SqlExplorer.Core.History;

namespace Lionear.SqlExplorer.App.Views;

public partial class MainView : UserControl
{
    private MainViewModel? _viewModel;

    public MainView()
    {
        InitializeComponent();

        var schemaTree = this.FindControl<TreeView>("SchemaTree");
        if (schemaTree is not null)
        {
            schemaTree.DoubleTapped += OnTreeDoubleTapped;
        }

        var historyList = this.FindControl<ListBox>("HistoryList");
        if (historyList is not null)
        {
            historyList.DoubleTapped += OnHistoryDoubleTapped;
        }

        var searchResultsList = this.FindControl<ListBox>("SearchResultsList");
        if (searchResultsList is not null)
        {
            searchResultsList.DoubleTapped += OnSearchResultDoubleTapped;
        }

        var searchBox = this.FindControl<TextBox>("SearchBox");
        if (searchBox is not null)
        {
            searchBox.KeyDown += OnSearchBoxKeyDown;
        }

        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>Current width of the connection sidebar column, in pixels (for persistence).</summary>
    public double SidebarWidth => BodyGrid.ColumnDefinitions[0].Width.Value;

    /// <summary>Applies a persisted sidebar width; ignores null/non-positive values so the design default stands.</summary>
    public void RestoreSidebarWidth(double? width)
    {
        if (width is > 0)
        {
            BodyGrid.ColumnDefinitions[0].Width = new GridLength(width.Value);
        }
    }

    // Double-click: a connection root (re)connects; a table/view opens a browse tab.
    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel?.SelectedNode is not { } node)
        {
            return;
        }

        if (node.IsConnectionNode && _viewModel.ConnectCommand.CanExecute(null))
        {
            _viewModel.ConnectCommand.Execute(null);
        }
        else if (node.IsTableOrView && _viewModel.BrowseTableCommand.CanExecute(null))
        {
            _viewModel.BrowseTableCommand.Execute(null);
        }
    }

    // Double-click a history row: re-run its SQL in a new query tab.
    private void OnHistoryDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: QueryHistoryEntry entry }
            && _viewModel?.OpenHistoryEntryCommand.CanExecute(entry) == true)
        {
            _viewModel.OpenHistoryEntryCommand.Execute(entry);
        }
    }

    // Double-click a quick-open hit: open its browse tab.
    private void OnSearchResultDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: IQuickOpenItem result }
            && _viewModel?.OpenSearchResultCommand.CanExecute(result) == true)
        {
            _viewModel.OpenSearchResultCommand.Execute(result);
        }
    }

    // Enter opens the highlighted (or first) hit; Escape closes the overlay without picking one.
    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            _viewModel.IsSearchVisible = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            var searchResultsList = this.FindControl<ListBox>("SearchResultsList");
            var result = searchResultsList?.SelectedItem as IQuickOpenItem ?? _viewModel.SearchResults.FirstOrDefault();
            if (result is not null && _viewModel.OpenSearchResultCommand.CanExecute(result))
            {
                _viewModel.OpenSearchResultCommand.Execute(result);
            }

            e.Handled = true;
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as MainViewModel;
        if (_viewModel is not null)
        {
            _viewModel.ConnectionDialogRequested = ShowConnectionDialogAsync;
            _viewModel.CreateObjectDialogRequested = ShowCreateObjectDialogAsync;
            _viewModel.AlterObjectDialogRequested = ShowAlterObjectDialogAsync;
            _viewModel.ClipboardRequested = CopyToClipboardAsync;
            _viewModel.ImportCsvFileRequested = PickCsvFileAsync;
            _viewModel.ImportCsvDialogRequested = ShowImportCsvDialogAsync;
            _viewModel.ExportFormatRequested = ShowExportFormatDialogAsync;
            _viewModel.ExportFileRequested = WriteExportFileAsync;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    // Focus the search box the moment the quick-open overlay opens. Deferred a tick: the overlay's
    // IsVisible binding hasn't applied to the visual tree yet at the point this handler runs, and a
    // hidden/not-yet-arranged control won't take focus.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsSearchVisible) && _viewModel is { IsSearchVisible: true })
        {
            Dispatcher.UIThread.Post(() => this.FindControl<TextBox>("SearchBox")?.Focus());
        }
    }

    // The VM asks; the view owns the window, so it creates and shows the modal dialog.
    private async Task<SavedConnection?> ShowConnectionDialogAsync(ConnectionDialogViewModel dialogViewModel)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return null;
        }

        var dialog = new ConnectionDialog { DataContext = dialogViewModel };
        return await dialog.ShowDialog<SavedConnection?>(owner);
    }

    private async Task<string?> ShowCreateObjectDialogAsync(CreateObjectDialogViewModel dialogViewModel)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return null;
        }

        var dialog = new CreateObjectDialog { DataContext = dialogViewModel };
        return await dialog.ShowDialog<string?>(owner);
    }

    private async Task<string?> ShowAlterObjectDialogAsync(AlterObjectDialogViewModel dialogViewModel)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return null;
        }

        var dialog = new AlterObjectDialog { DataContext = dialogViewModel };
        return await dialog.ShowDialog<string?>(owner);
    }

    private async Task<string?> PickCsvFileAsync()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return null;
        }

        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }]
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() ?? files[0].Path.ToString() : null;
    }

    private async Task<bool> ShowImportCsvDialogAsync(ImportCsvDialogViewModel dialogViewModel)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return false;
        }

        var dialog = new ImportCsvDialog { DataContext = dialogViewModel };
        return await dialog.ShowDialog<bool>(owner);
    }

    private async Task<ExportFormat?> ShowExportFormatDialogAsync(int rowCount)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner || _viewModel is null)
        {
            return null;
        }

        var dialog = new ExportDialog(_viewModel.Loc, rowCount, isSelection: false);
        return await dialog.ShowDialog<ExportFormat?>(owner);
    }

    private async Task WriteExportFileAsync(string text, ExportFormat format)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var (extension, typeName) = format switch
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

    private async Task CopyToClipboardAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }
}
