using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SqlExplorer.App.Controls;
using SqlExplorer.App.ViewModels;
using SqlExplorer.Core.Connections;
using SqlExplorer.Core.History;

namespace SqlExplorer.App.Views;

public partial class MainView : UserControl
{
    private MainViewModel? _viewModel;
    private ListBox? _outputList;

    public MainView()
    {
        InitializeComponent();

        var schemaTree = this.FindControl<TreeView>("SchemaTree");
        if (schemaTree is not null)
        {
            schemaTree.DoubleTapped += OnTreeDoubleTapped;
            // Right-click selects the node under the cursor first, so the context menu acts on it
            // (Avalonia's TreeView doesn't select on right-press by default).
            schemaTree.AddHandler(InputElement.PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);
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

        _outputList = this.FindControl<ListBox>("OutputList");
        if (_outputList is not null)
        {
            // Right-click selects the row under the cursor so the context menu's Copy targets it
            // (ListBox doesn't select on right-press by default).
            _outputList.AddHandler(InputElement.PointerPressedEvent, OnOutputPointerPressed, RoutingStrategies.Tunnel);
        }

        var documentTabs = this.FindControl<TabControl>("DocumentTabs");
        if (documentTabs is not null)
        {
            // Middle-click a tab header closes that tab (common editor gesture).
            documentTabs.AddHandler(InputElement.PointerPressedEvent, OnDocumentTabsPointerPressed, RoutingStrategies.Tunnel);
        }

        DataContextChanged += OnDataContextChanged;

        // SE-154: drag a .sql file onto the window to open it in a new query tab. handledEventsToo is
        // essential — the DragDrop events bubble, and child controls that do their own drag-drop (notably
        // the AvaloniaEdit SQL editor and the result grid) mark them Handled first; without this flag our
        // window-level handler would never see a drop that lands over the editor. We gate on the payload
        // carrying a file (not on a resolvable path, which isn't always available mid-drag) and only mark
        // the event Handled for file drops, so the editor's own internal text drag-drop still works.
        AddHandler(DragDrop.DragOverEvent, OnDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Bubble, handledEventsToo: true);
        DragDrop.SetAllowDrop(this, true);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer?.Contains(DataFormat.File) == true)
        {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (_viewModel is null || e.DataTransfer?.Contains(DataFormat.File) != true)
        {
            return;
        }

        e.Handled = true;
        foreach (var file in e.DataTransfer.TryGetFiles() ?? [])
        {
            var path = file.TryGetLocalPath();
            if (path is not null
                && path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)
                && _viewModel.OpenQueryFileCommand.CanExecute(path))
            {
                _viewModel.OpenQueryFileCommand.Execute(path);
            }
        }
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

    // --- Tool-window sizes (SE-123) -----------------------------------------------------------------
    // Same shape as the sidebar: GridLength lives in XAML, the VM keeps a plain double, and these two
    // methods bridge them at startup/shutdown. Grid rows/columns can't bind a double directly, and
    // x:Name on a RowDefinition/ColumnDefinition generates no field — hence the index access.
    private const int OutputRowIndex = 3;
    private const int HistoryColumnIndex = 4;
    // SE-164 panel seam: the shared bottom region for plugin-contributed panels (RootGrid row 5, its
    // splitter row 4). One region for all plugin panels in v1; its height is session-local (not persisted).
    private const int SubsystemRowIndex = 5;
    private double _subsystemHeight = 200;

    /// <summary>Push the persisted sizes into the grid definitions (call once, after DataContext is set).</summary>
    public void RestoreToolWindowSizes(double outputHeight, double historyWidth)
    {
        if (outputHeight > 0)
        {
            RootGrid.RowDefinitions[OutputRowIndex].Height = new GridLength(outputHeight);
        }

        if (historyWidth > 0)
        {
            BodyGrid.ColumnDefinitions[HistoryColumnIndex].Width = new GridLength(historyWidth);
        }

        ApplyToolWindowVisibility();
    }

    // Minimum sizes while a panel is open; they must drop to 0 when it closes, or the track keeps
    // reserving MinHeight/MinWidth pixels regardless of the Height/Width we set (that's what left an
    // empty band above the status bar). Hence: set Min* together with the size, never in XAML.
    private const double OutputMinHeight = 80;
    private const double HistoryMinWidth = 200;
    private const double SubsystemMinHeight = 80;

    // A hidden panel must not keep reserving its track: IsVisible on the Border alone still leaves the
    // fixed-pixel row/column occupying space. Collapse the track to 0 while hidden and restore the VM's
    // size when shown — that also means the splitter can only be dragged while the panel is open.
    private void ApplyToolWindowVisibility()
    {
        if (_viewModel is null)
        {
            return;
        }

        var outputRow = RootGrid.RowDefinitions[OutputRowIndex];
        outputRow.MinHeight = _viewModel.OutputWindow.IsVisible ? OutputMinHeight : 0;
        outputRow.Height = _viewModel.OutputWindow.IsVisible
            ? new GridLength(_viewModel.OutputWindow.Size)
            : new GridLength(0);

        var historyColumn = BodyGrid.ColumnDefinitions[HistoryColumnIndex];
        historyColumn.MinWidth = _viewModel.HistoryWindow.IsVisible ? HistoryMinWidth : 0;
        historyColumn.Width = _viewModel.HistoryWindow.IsVisible
            ? new GridLength(_viewModel.HistoryWindow.Size)
            : new GridLength(0);

        // Plugin panel region (SE-164): the row (and its splitter) live only while at least one plugin panel
        // is toggled open — a shared bottom region, same collapse-to-0 rule as Output.
        var anyPanelVisible = _viewModel.SubsystemPanels.Any(p => p.Window.IsVisible);
        var subsystemRow = RootGrid.RowDefinitions[SubsystemRowIndex];
        subsystemRow.MinHeight = anyPanelVisible ? SubsystemMinHeight : 0;
        subsystemRow.Height = anyPanelVisible ? new GridLength(_subsystemHeight) : new GridLength(0);
        SubsystemSplitter.IsVisible = anyPanelVisible;
    }

    // Keep the VM's Size in step with a splitter drag, and collapse/restore the track on toggle.
    private void OnToolWindowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not ToolWindow window)
        {
            return;
        }

        if (e.PropertyName != nameof(ToolWindow.IsVisible))
        {
            return;
        }

        // Capture the current size before collapsing, so re-opening restores what the user dragged to.
        if (!window.IsVisible)
        {
            CaptureToolWindowSizes();
        }

        ApplyToolWindowVisibility();
    }

    /// <summary>Read the current sizes back into the VM's tool windows so they can be persisted. A
    /// collapsed track reads 0 — skip it so closing a panel doesn't persist a zero size.</summary>
    public void CaptureToolWindowSizes()
    {
        if (_viewModel is null)
        {
            return;
        }

        var outputHeight = RootGrid.RowDefinitions[OutputRowIndex].Height.Value;
        if (outputHeight > 0)
        {
            _viewModel.OutputWindow.Size = outputHeight;
        }

        var historyWidth = BodyGrid.ColumnDefinitions[HistoryColumnIndex].Width.Value;
        if (historyWidth > 0)
        {
            _viewModel.HistoryWindow.Size = historyWidth;
        }

        var subsystemHeight = RootGrid.RowDefinitions[SubsystemRowIndex].Height.Value;
        if (subsystemHeight > 0)
        {
            _subsystemHeight = subsystemHeight;
        }
    }

    // Handled in the tunnel (before the TreeViewItem), so we can act before its own selection/expand logic.
    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel is null
            || e.Source is not Visual source
            || source.FindAncestorOfType<TreeViewItem>() is not { } item
            || item.DataContext is not TreeNodeViewModel node)
        {
            return;
        }

        var props = e.GetCurrentPoint(sender as Visual).Properties;

        // Right-click selects the node under the cursor so the context menu targets it. Set the VM's
        // SelectedNode directly (not only item.IsSelected) so it's updated synchronously before the
        // context menu's bindings (CanShowProperties, ApplicableTools, …) evaluate.
        if (props.IsRightButtonPressed)
        {
            item.IsSelected = true;
            _viewModel.SelectedNode = node;
            return;
        }

        // Double left-click on a table/view browses it — and we swallow the press so the TreeViewItem
        // doesn't also toggle its column list open (the default double-click-to-expand).
        if (props.IsLeftButtonPressed && e.ClickCount == 2 && node.IsTableOrView)
        {
            _viewModel.SelectedNode = node;
            if (_viewModel.BrowseTableCommand.CanExecute(null))
            {
                _viewModel.BrowseTableCommand.Execute(null);
            }

            e.Handled = true;
        }
        // Double left-click a procedure/function/trigger opens its definition in a tab (same suppress as above).
        else if (props.IsLeftButtonPressed && e.ClickCount == 2 && node.CanViewDefinition)
        {
            _viewModel.SelectedNode = node;
            if (_viewModel.ViewDefinitionCommand.CanExecute(null))
            {
                _viewModel.ViewDefinitionCommand.Execute(null);
            }

            e.Handled = true;
        }
    }

    // Double-click a connection root: (re)connect. (Table/view browse is handled on pointer-press above,
    // so the default expand can be suppressed there.)
    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel is null
            || e.Source is not Visual source
            || source.FindAncestorOfType<TreeViewItem>()?.DataContext is not TreeNodeViewModel node)
        {
            return;
        }

        if (node.IsConnectionNode && _viewModel.ConnectCommand.CanExecute(null))
        {
            _viewModel.SelectedNode = node;
            _viewModel.ConnectCommand.Execute(null);
        }
    }

    // Keep the newest line visible now the Output panel is the only feedback channel. Deferred to the
    // next UI tick so it also lands after the panel auto-opens on an error (it's collapsed until then).
    private void OnOutputEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || _outputList is not { } list
            || _viewModel is not { OutputEntries.Count: > 0 } vm)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => list.ScrollIntoView(vm.OutputEntries[^1]));
    }

    // Tab-strip pointer gestures: middle-click a tab header closes it; double-click the empty strip opens
    // a new query tab. Clicks in the tab content pane (inside a DocumentView) are ignored.
    private void OnDocumentTabsPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel is null || e.Source is not Visual source)
        {
            return;
        }

        var point = e.GetCurrentPoint(sender as Visual).Properties;
        var tabItem = source.FindAncestorOfType<TabItem>();

        if (point.IsMiddleButtonPressed && tabItem is { DataContext: DocumentViewModel document })
        {
            if (_viewModel.CloseTabCommand.CanExecute(document))
            {
                _viewModel.CloseTabCommand.Execute(document);
            }

            e.Handled = true;
            return;
        }

        // Double-click the empty strip (not a tab header, not the content pane) → new query tab.
        if (point.IsLeftButtonPressed && e.ClickCount == 2 && tabItem is null
            && source.FindAncestorOfType<DocumentView>() is null
            && _viewModel.NewQueryTabCommand.CanExecute(null))
        {
            _viewModel.NewQueryTabCommand.Execute(null);
            e.Handled = true;
        }
    }

    // Right-click an output row selects it first, so the context menu's Copy acts on that line.
    private void OnOutputPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source
            || source.FindAncestorOfType<ListBoxItem>() is not { } item
            || !e.GetCurrentPoint(sender as Visual).Properties.IsRightButtonPressed)
        {
            return;
        }

        item.IsSelected = true;
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
            _viewModel.OutputEntries.CollectionChanged -= OnOutputEntriesChanged;
            foreach (var window in _viewModel.ToolWindows)
            {
                window.PropertyChanged -= OnToolWindowPropertyChanged;
            }
        }

        _viewModel = DataContext as MainViewModel;
        if (_viewModel is not null)
        {
            // Push the persisted tool-window sizes into the grid (the VM read them from settings) and
            // track visibility so a hidden panel's track collapses instead of reserving its size.
            RestoreToolWindowSizes(_viewModel.OutputWindow.Size, _viewModel.HistoryWindow.Size);
            foreach (var window in _viewModel.ToolWindows)
            {
                window.PropertyChanged += OnToolWindowPropertyChanged;
            }

            _viewModel.OutputEntries.CollectionChanged += OnOutputEntriesChanged;
            _viewModel.ConnectionManagerRequested = ShowConnectionManagerAsync;
            _viewModel.CreateObjectDialogRequested = ShowCreateObjectDialogAsync;
            _viewModel.NewUserDialogRequested = ShowNewUserDialogAsync;
            _viewModel.AlterObjectDialogRequested = ShowAlterObjectDialogAsync;
            _viewModel.ClipboardRequested = CopyToClipboardAsync;
            _viewModel.ImportCsvFileRequested = PickCsvFileAsync;
            _viewModel.PickOpenQueryFilesRequested = PickOpenQueryFilesAsync;
            _viewModel.PickSaveQueryFileRequested = PickSaveQueryFileAsync;
            _viewModel.SaveOnCloseRequested = PromptSaveOnCloseAsync;
            _viewModel.ImportCsvDialogRequested = ShowImportCsvDialogAsync;
            _viewModel.ExportFormatRequested = ShowExportFormatDialogAsync;
            _viewModel.ExportFileRequested = WriteExportFileAsync;
            _viewModel.SettingsDialogRequested = ShowSettingsDialogAsync;
            _viewModel.ToolDialogRequested = ShowToolDialogAsync;
            _viewModel.ShowPluginDialogRequested = ShowPluginDialogAsync;
            _viewModel.RoutineParametersRequested = ShowRoutineParametersDialogAsync;
            _viewModel.NodeInfoRequested = ShowNodeInfoDialogAsync;
            _viewModel.SecurityViewRequested = ShowSecurityDialogAsync;
            _viewModel.PluginStoreRequested = ShowPluginStoreAsync;
            _viewModel.PluginUpdates.ChangelogRequested = ShowPluginChangelogAsync;
            _viewModel.QueryLogRequested = ShowQueryLogAsync;
            _viewModel.RestartRequested = () => { AppRestart.Restart(); return Task.CompletedTask; };
            _viewModel.ConfirmRequested = ShowConfirmAsync;
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

    // The VM asks; the view owns the window, so it creates and shows the Connection Manager (modal).
    private async Task ShowConnectionManagerAsync(ConnectionManagerViewModel managerViewModel)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var window = new ConnectionManagerWindow { DataContext = managerViewModel };
        await window.ShowDialog(owner);
    }

    // Yes/no confirmation (e.g. "reconnect now?"). Yes → true, No/closed → false.
    private async Task<bool> ShowConfirmAsync(string title, string message)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner || _viewModel is null)
        {
            return false;
        }

        var dialog = new ConfirmDialog(title, message, _viewModel.Loc["Yes"], _viewModel.Loc["No"]);
        return await dialog.ShowDialog<bool>(owner);
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

    private async Task<string?> ShowNewUserDialogAsync(NewUserDialogViewModel dialogViewModel)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return null;
        }

        var dialog = new NewUserDialog { DataContext = dialogViewModel };
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

    // SE-154: pick one or more .sql files to open. Returns local paths (empty if cancelled); the VM reads
    // and opens them, so drag-drop and the Recent menu share the same open path.
    private async Task<IReadOnlyList<string>> PickOpenQueryFilesAsync()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return [];
        }

        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true,
            FileTypeFilter = [SqlFileType, FilePickerFileTypes.All]
        });

        return files
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p))
            .Cast<string>()
            .ToList();
    }

    // SE-154: pick a .sql save location, pre-filled with the suggested file name. Returns the local path,
    // or null on cancel.
    private async Task<string?> PickSaveQueryFileAsync(string suggestedFileName)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return null;
        }

        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "sql",
            FileTypeChoices = [SqlFileType]
        });

        return file?.TryGetLocalPath() ?? file?.Path.LocalPath;
    }

    // SE-154: three-way "save before closing?" prompt for a dirty query file.
    private async Task<SaveCloseChoice> PromptSaveOnCloseAsync(string tabName)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner || _viewModel is null)
        {
            return SaveCloseChoice.DontSave; // no owner for a modal → don't block the close
        }

        var dialog = new SaveCloseDialog(
            _viewModel.Loc["SaveCloseTitle"],
            _viewModel.Loc.Get("SaveCloseMessage", tabName),
            _viewModel.Loc["Save"],
            _viewModel.Loc["SaveCloseDontSave"],
            _viewModel.Loc["Cancel"]);
        return await dialog.ShowDialog<SaveCloseChoice>(owner);
    }

    private static readonly FilePickerFileType SqlFileType = new("SQL") { Patterns = ["*.sql"] };

    private async Task ShowSettingsDialogAsync(SettingsViewModel dialogViewModel)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var dialog = new SettingsWindow { DataContext = dialogViewModel };
        await dialog.ShowDialog(owner);
    }

    private async Task ShowToolDialogAsync(ToolDialogViewModel dialogViewModel)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var dialog = new ToolDialog { DataContext = dialogViewModel };
        await dialog.ShowDialog(owner);
    }

    // SE-164 menu seam: host a plugin-built control in a modal window (generic chrome — the plugin's control
    // carries its own buttons and closes the window itself). The plugin owns the content; we own the frame.
    private async Task ShowPluginDialogAsync(string title, Control content)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var dialog = new Window
        {
            Title = title,
            Content = content,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            MinWidth = 400
        };
        if (this.TryFindResource("SEPanelBgBrush", out var bg) && bg is Avalonia.Media.IBrush brush)
        {
            dialog.Background = brush;
        }

        await dialog.ShowDialog(owner);
    }

    private async Task ShowRoutineParametersDialogAsync(RoutineParametersDialogViewModel dialogViewModel)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var dialog = new RoutineParametersDialog { DataContext = dialogViewModel };
        await dialog.ShowDialog(owner);
    }

    private async Task ShowNodeInfoDialogAsync(NodeInfoDialogViewModel dialogViewModel)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var dialog = new NodeInfoDialog { DataContext = dialogViewModel };
        await dialog.ShowDialog(owner);
    }

    private async Task ShowSecurityDialogAsync(NodeInfoDialogViewModel dialogViewModel)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var dialog = new SecurityDialog { DataContext = dialogViewModel };
        await dialog.ShowDialog(owner);
    }

    private async Task ShowPluginStoreAsync(PluginStoreViewModel dialogViewModel)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var dialog = new PluginStoreWindow { DataContext = dialogViewModel };
        await dialog.ShowDialog(owner);
    }

    // SE-138 phase 2: the plugin-update notification's "View changelog" opens the per-plugin changelog.
    private async Task ShowPluginChangelogAsync(PluginChangelogViewModel dialogViewModel)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var dialog = new PluginChangelogWindow(dialogViewModel);
        await dialog.ShowDialog(owner);
    }

    // The Query Log is non-modal so it can stay open while the app is used, and single-instance so the menu
    // (or the tray) just brings the existing window to the front instead of stacking copies.
    private QueryLogWindow? _queryLogWindow;

    private Task ShowQueryLogAsync(QueryLogViewModel dialogViewModel)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return Task.CompletedTask;
        }

        if (_queryLogWindow is { } existing)
        {
            existing.Activate();
            return Task.CompletedTask;
        }

        dialogViewModel.OpenInEditorRequested = entry =>
        {
            _viewModel?.OpenHistoryEntryCommand.Execute(entry); // resolves the connection + adds a query tab
            owner.Activate(); // bring the main window (with the new tab) to the front
        };

        var window = new QueryLogWindow { DataContext = dialogViewModel };
        window.Closed += (_, _) =>
        {
            (window.DataContext as IDisposable)?.Dispose(); // detach from IQueryLog.Changed
            _queryLogWindow = null;
        };
        _queryLogWindow = window;
        window.Show(owner);
        return Task.CompletedTask;
    }

    private Task CopyToClipboardAsync(string text) =>
        CopyFeedback.CopyAsync(this, text, _viewModel?.Loc["CopiedToClipboard"] ?? "Copied");
}
