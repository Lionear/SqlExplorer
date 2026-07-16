using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.Controls.ApplicationLifetimes;
using SqlExplorer.App.Theming;
using SqlExplorer.Core.Connections;
using SqlExplorer.Core.Editing;
using SqlExplorer.Core.Export;
using SqlExplorer.Core.Formatting;
using SqlExplorer.Core.History;
using SqlExplorer.Core.Import;
using SqlExplorer.Core.Localization;
using SqlExplorer.Core.Logging;
using SqlExplorer.Core.Providers;
using SqlExplorer.Core.Schema;
using SqlExplorer.Core.Session;
using SqlExplorer.Core.Settings;
using SqlExplorer.Core.Shortcuts;
using SqlExplorer.Core.Tools;
using SqlExplorer.Sdk;
using SqlExplorer.Sdk.Localization;
using SqlExplorer.Sdk.Tools;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SqlExplorer.App.ViewModels;

/// <summary>
/// The window shell: the connection sidebar tree plus a set of editor tabs
/// (<see cref="DocumentViewModel"/>). Query/result/save-flow logic lives in the documents;
/// this VM owns connections, the tree and tab management.
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    private readonly IDbProviderRegistry _providers;
    private readonly ISqlFormatter _formatter;
    private readonly ConnectionService _connections;
    private readonly IQueryHistoryStore _history;
    private readonly IQueryLog _queryLog;
    private readonly Func<QueryLogViewModel> _queryLogFactory;
    private readonly ISchemaCache _schemaCache;
    private readonly Func<ConnectionManagerViewModel> _connectionManagerFactory;
    private readonly Func<CreateObjectDialogViewModel> _createDialogFactory;
    private readonly Func<NewUserDialogViewModel> _newUserDialogFactory;
    private readonly Func<AlterObjectDialogViewModel> _alterDialogFactory;
    private readonly Func<ImportCsvDialogViewModel> _importCsvDialogFactory;
    private readonly Func<SettingsViewModel> _settingsDialogFactory;
    private readonly Func<PluginStoreViewModel> _pluginStoreFactory;
    private readonly Core.Plugins.PluginCatalogService _pluginCatalog;
    private readonly IToolRegistry _tools;
    private readonly Func<ToolDialogViewModel> _toolDialogFactory;
    private readonly Func<RoutineParametersDialogViewModel> _routineParamsDialogFactory;
    private readonly IAppSettingsStore _settingsStore;
    private readonly IOpenTabsStore _openTabsStore;

    // Selected tree node drives the active connection: any node knows its owning connection.
    [ObservableProperty]
    private TreeNodeViewModel? _selectedNode;

    [ObservableProperty]
    private SavedConnection? _selectedConnection;

    [ObservableProperty]
    private DocumentViewModel? _selectedDocument;

    [ObservableProperty]
    private bool _isHistoryVisible;

    [ObservableProperty]
    private string _historySearch = string.Empty;

    // Output/log panel (toggleable): the single place every execution outcome — connection results,
    // query row counts, and failures — is surfaced. A failure also pops the panel open (see ReportOutput).
    [ObservableProperty]
    private bool _isOutputVisible;

    [ObservableProperty]
    private bool _isSearchVisible;

    [ObservableProperty]
    private string _searchText = string.Empty;

    public MainViewModel(
        IDbProviderRegistry providers,
        ISqlFormatter formatter,
        ConnectionService connections,
        IQueryHistoryStore history,
        IQueryLog queryLog,
        Func<QueryLogViewModel> queryLogFactory,
        ISchemaCache schemaCache,
        Func<ConnectionManagerViewModel> connectionManagerFactory,
        Func<CreateObjectDialogViewModel> createDialogFactory,
        Func<NewUserDialogViewModel> newUserDialogFactory,
        Func<AlterObjectDialogViewModel> alterDialogFactory,
        Func<ImportCsvDialogViewModel> importCsvDialogFactory,
        Func<SettingsViewModel> settingsDialogFactory,
        IToolRegistry tools,
        Func<ToolDialogViewModel> toolDialogFactory,
        Func<RoutineParametersDialogViewModel> routineParamsDialogFactory,
        Func<PluginStoreViewModel> pluginStoreFactory,
        Core.Plugins.PluginCatalogService pluginCatalog,
        IAppSettingsStore settingsStore,
        IOpenTabsStore openTabsStore,
        ILocalizer localizer)
    {
        _providers = providers;
        _formatter = formatter;
        _connections = connections;
        _history = history;
        _queryLog = queryLog;
        _queryLogFactory = queryLogFactory;
        _schemaCache = schemaCache;
        _connectionManagerFactory = connectionManagerFactory;
        _createDialogFactory = createDialogFactory;
        _newUserDialogFactory = newUserDialogFactory;
        _alterDialogFactory = alterDialogFactory;
        _importCsvDialogFactory = importCsvDialogFactory;
        _settingsDialogFactory = settingsDialogFactory;
        _tools = tools;
        _toolDialogFactory = toolDialogFactory;
        _routineParamsDialogFactory = routineParamsDialogFactory;
        _pluginStoreFactory = pluginStoreFactory;
        _pluginCatalog = pluginCatalog;
        _settingsStore = settingsStore;
        _openTabsStore = openTabsStore;
        Loc = localizer;

        _history.Changed += OnHistoryChanged;
        RefreshConnections();
        RestoreOpenTabs();
        EvaluatePluginRestart();
    }

    // Reopen the query tabs from the previous session (skipping any whose connection no longer exists).
    private void RestoreOpenTabs()
    {
        if (!_settingsStore.Load().RestoreTabsOnStartup)
        {
            return;
        }

        foreach (var tab in _openTabsStore.Load())
        {
            if (_connections.List().FirstOrDefault(c => c.Id == tab.ConnectionId) is not { } connection)
            {
                continue;
            }

            var document = NewDocument();
            document.InitQuery(connection, tab.Database);
            document.Sql = tab.Sql;
            AddDocument(document);
        }
    }

    /// <summary>Persist the open query tabs so the next launch can reopen them (called by the view on close).</summary>
    public void PersistOpenTabs() =>
        _openTabsStore.Save(Documents
            .Where(d => d is { IsQueryMode: true, Connection: not null })
            .Select(d => new OpenTabState(d.Connection!.Id, d.SelectedDatabase, d.Sql))
            .ToList());

    /// <summary>True when the Plugin Store has staged changes that need a restart — shows a main-window banner.</summary>
    [ObservableProperty]
    private bool _pluginRestartRequired;

    /// <summary>Re-check whether staged plugin changes need a restart (called at startup and after the store closes).</summary>
    public void EvaluatePluginRestart() => PluginRestartRequired = _pluginCatalog.HasPendingChanges;

    /// <summary>Set by the view to relaunch the app (applies staged plugin changes).</summary>
    public Func<Task>? RestartRequested { get; set; }

    [RelayCommand]
    private async Task RestartApp()
    {
        if (RestartRequested is not null)
        {
            await RestartRequested();
        }
    }

    /// <summary>Query-history rows shown in the (toggleable) history panel, newest first.</summary>
    public ObservableCollection<QueryHistoryEntry> HistoryEntries { get; } = [];

    [RelayCommand]
    private void ToggleHistory()
    {
        IsHistoryVisible = !IsHistoryVisible;
        if (IsHistoryVisible)
        {
            RefreshHistory();
        }
    }

    [RelayCommand]
    private void ClearHistory() => _history.Clear();

    /// <summary>Rolling output/log lines (connection results, errors), oldest first, capped.</summary>
    public ObservableCollection<OutputLogEntry> OutputEntries { get; } = [];

    [RelayCommand]
    private void ToggleOutput() => IsOutputVisible = !IsOutputVisible;

    [RelayCommand]
    private void ClearOutput() => OutputEntries.Clear();

    // Copy a single output line to the clipboard (right-click ▸ Copy). Reuses the same clipboard hook the
    // schema tree's copy actions go through (wired by the view).
    [RelayCommand]
    private async Task CopyOutputEntry(OutputLogEntry? entry)
    {
        if (entry is not null && ClipboardRequested is not null)
        {
            await ClipboardRequested(entry.CopyText);
        }
    }

    // Copy the whole Output log at once (right-click ▸ Copy all) — handy to paste a run into a ticket.
    [RelayCommand]
    private async Task CopyAllOutput()
    {
        if (OutputEntries.Count > 0 && ClipboardRequested is not null)
        {
            await ClipboardRequested(string.Join(Environment.NewLine, OutputEntries.Select(e => e.CopyText)));
        }
    }

    // Every execution outcome lands here — the Output panel is the one place it shows. A failure also
    // pops the panel open (if collapsed) so it's never missed; successes just append silently.
    private void ReportOutput(OutputLevel level, string? source, string message)
    {
        Append(level, source, message);
        if (level == OutputLevel.Error)
        {
            IsOutputVisible = true;
        }
    }

    private void ReportError(string? source, string message) => ReportOutput(OutputLevel.Error, source, message);

    private void ReportInfo(string? source, string message) => ReportOutput(OutputLevel.Info, source, message);

    private const int MaxOutputEntries = 500;

    private void Append(OutputLevel level, string? source, string message)
    {
        OutputEntries.Add(new OutputLogEntry(DateTime.Now, level, source, message));
        while (OutputEntries.Count > MaxOutputEntries)
        {
            OutputEntries.RemoveAt(0);
        }
    }

    // Re-run a history entry: drop its SQL into a fresh query tab on its own connection (or the current
    // one if that connection is gone).
    [RelayCommand]
    private void OpenHistoryEntry(QueryHistoryEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        var connection = _connections.List().FirstOrDefault(c => c.Id == entry.ConnectionId)
            ?? SelectedConnection
            ?? _connections.List().FirstOrDefault();
        if (connection is null)
        {
            return;
        }

        var document = NewDocument();
        document.InitQuery(connection);
        document.Sql = entry.Sql;
        AddDocument(document);
    }

    partial void OnHistorySearchChanged(string value)
    {
        if (IsHistoryVisible)
        {
            RefreshHistory();
        }
    }

    private void OnHistoryChanged()
    {
        if (IsHistoryVisible)
        {
            Dispatcher.UIThread.Post(RefreshHistory);
        }
    }

    private void RefreshHistory()
    {
        HistoryEntries.Clear();
        foreach (var entry in _history.Search(HistorySearch).Take(200))
        {
            HistoryEntries.Add(entry);
        }
    }

    /// <summary>Quick-open ("go to table" / run a command) hits for <see cref="SearchText"/>, best match first.</summary>
    public ObservableCollection<IQuickOpenItem> SearchResults { get; } = [];

    // Global, context-free actions surfaced in the same Ctrl+K overlay as schema objects. Destructive
    // or tree-selection-dependent commands (Drop*, AddColumn, …) stay off this list — they already have
    // their own confirm-dialog path from the tree, and blind-executing them from a text box invites
    // mistakes. Rebuilt per search (cheap, a dozen items) rather than cached, so labels follow a
    // language switch immediately instead of freezing at construction time.
    private IEnumerable<CommandQuickOpenItem> QuickOpenCommands()
    {
        yield return new CommandQuickOpenItem(Loc["NewQueryTab"], "Command", () => NewQueryTabCommand.Execute(null));
        yield return new CommandQuickOpenItem(Loc["History"], "Command", () => ToggleHistoryCommand.Execute(null));
        yield return new CommandQuickOpenItem(Loc["ClearHistory"], "Command", () => ClearHistoryCommand.Execute(null));
        yield return new CommandQuickOpenItem(Loc["Settings"], "Command", () => OpenSettingsCommand.Execute(null));
        yield return new CommandQuickOpenItem(Loc["NewConnection"], "Command", () => NewConnectionCommand.Execute(null));
        yield return new CommandQuickOpenItem(Loc["ManageConnections"], "Command", () => ManageConnectionsCommand.Execute(null));
        yield return new CommandQuickOpenItem(Loc["Connect"], "Command", () => ConnectCommand.Execute(null));
        yield return new CommandQuickOpenItem(Loc["Disconnect"], "Command", () => DisconnectCommand.Execute(null));
        yield return new CommandQuickOpenItem(Loc["Run"], "Command", () => RunActiveDocumentCommand.Execute(null));
        yield return new CommandQuickOpenItem(Loc["RunAtCursor"], "Command", () => RunActiveDocumentAtCursorCommand.Execute(null));
        yield return new CommandQuickOpenItem(Loc["Browse"], "Command", () => BrowseTableCommand.Execute(null));
        yield return new CommandQuickOpenItem(Loc["Export"], "Command", () => ExportTableCommand.Execute(null));
        yield return new CommandQuickOpenItem(Loc["ImportCsv"], "Command", () => ImportCsvCommand.Execute(null));
        yield return new CommandQuickOpenItem(Loc["CopyName"], "Command", () => CopyNameCommand.Execute(null));
    }

    [RelayCommand]
    private void ToggleSearch()
    {
        IsSearchVisible = !IsSearchVisible;
        if (!IsSearchVisible)
        {
            SearchText = string.Empty;
        }
    }

    partial void OnSearchTextChanged(string value) => RefreshSearchResults();

    // F5/Ctrl+Enter live as window-level KeyBindings (MainWindow.axaml, same reasoning as Ctrl+K —
    // a keypress needs somewhere to land even with nothing focused yet) and forward to whichever
    // document tab is currently selected.
    [RelayCommand]
    private void RunActiveDocument() => SelectedDocument?.RunCommand.Execute(null);

    [RelayCommand]
    private void RunActiveDocumentAtCursor() => SelectedDocument?.RunAtCursorCommand.Execute(null);

    // Window-level shortcut targets that forward to the active tab (Ctrl+S save, Ctrl+Shift+F format,
    // Ctrl+W close). Same reasoning as F5/Ctrl+Enter: the keypress lands on the window, not a tab.
    [RelayCommand]
    private void SaveActiveDocument() => SelectedDocument?.SaveCommand.Execute(null);

    [RelayCommand]
    private void FormatActiveDocument() => SelectedDocument?.FormatCommand.Execute(null);

    [RelayCommand]
    private async Task CloseActiveTab()
    {
        if (SelectedDocument is { } document)
        {
            await TryCloseAsync(document);
        }
    }

    /// <summary>
    /// Maps a <see cref="ShortcutCatalog"/> command id to the window-level command it triggers, so the
    /// main window can build its key bindings dynamically from the live keymap. Editor-scoped commands
    /// (e.g. toggle comment) are handled inside the editor and are not resolved here.
    /// </summary>
    public System.Windows.Input.ICommand? ResolveShortcut(string commandId) => commandId switch
    {
        ShortcutCatalog.Ids.NewQueryTab => NewQueryTabCommand,
        ShortcutCatalog.Ids.CloseTab => CloseActiveTabCommand,
        ShortcutCatalog.Ids.ReopenTab => ReopenClosedTabCommand,
        ShortcutCatalog.Ids.Run => RunActiveDocumentCommand,
        ShortcutCatalog.Ids.RunAtCursor => RunActiveDocumentAtCursorCommand,
        ShortcutCatalog.Ids.Save => SaveActiveDocumentCommand,
        ShortcutCatalog.Ids.Format => FormatActiveDocumentCommand,
        ShortcutCatalog.Ids.ToggleSearch => ToggleSearchCommand,
        ShortcutCatalog.Ids.RefreshTree => RefreshNodeCommand,
        _ => null
    };

    // Fuzzy quick-open across every connection's cached snapshot (1.1): a table/view whose qualified
    // name or one of its columns matches. Connections without a snapshot yet (never connected, or
    // still walking) simply contribute nothing rather than blocking the search.
    private void RefreshSearchResults()
    {
        SearchResults.Clear();

        var query = SearchText.Trim();
        if (query.Length == 0)
        {
            return;
        }

        var hits = new List<(int Rank, IQuickOpenItem Result)>();
        foreach (var connection in _connections.List())
        {
            var snapshot = _schemaCache.Get(connection.Id);
            if (snapshot is null)
            {
                continue;
            }

            foreach (var obj in snapshot.Objects)
            {
                if (SchemaSearch.TryRank(obj.QualifiedName, query, out var rank))
                {
                    hits.Add((rank, new SchemaSearchResult(connection, obj, null)));
                    continue;
                }

                var column = obj.Columns.FirstOrDefault(c => SchemaSearch.TryRank(c.Name, query, out _));
                if (column is not null)
                {
                    SchemaSearch.TryRank(column.Name, query, out var columnRank);
                    hits.Add((columnRank + 10, new SchemaSearchResult(connection, obj, column.Name)));
                }
            }
        }

        // Commands rank alongside schema objects on the same fuzzy score, so typing "export" surfaces
        // the Export command ahead of any table that merely contains "export" in its name.
        foreach (var command in QuickOpenCommands())
        {
            if (SchemaSearch.TryRank(command.Display, query, out var rank))
            {
                hits.Add((rank, command));
            }
        }

        foreach (var hit in hits.OrderBy(h => h.Rank).ThenBy(h => h.Result.Display, StringComparer.OrdinalIgnoreCase).Take(50))
        {
            SearchResults.Add(hit.Result);
        }
    }

    // Quick-open result picked: a schema object jumps straight to a browse tab (no tree-reveal — the
    // tree is lazily loaded per-node, so re-walking ancestors to select a node deep in it isn't worth
    // it for MVP); a command runs directly.
    [RelayCommand]
    private async Task OpenSearchResultAsync(IQuickOpenItem? result, CancellationToken ct)
    {
        if (result is null)
        {
            return;
        }

        IsSearchVisible = false;
        SearchText = string.Empty;

        switch (result)
        {
            case SchemaSearchResult schemaResult:
                await OpenBrowseTabAsync(schemaResult.Connection, schemaResult.Database, schemaResult.Schema, schemaResult.Name, ct);
                break;
            case CommandQuickOpenItem command:
                command.Execute();
                break;
        }
    }

    public ILocalizer Loc { get; }

    /// <summary>The sidebar tree: one root node per saved connection, children loaded lazily.</summary>
    public ObservableCollection<TreeNodeViewModel> ConnectionNodes { get; } = [];

    /// <summary>Open editor tabs (query panes and table-browse panes).</summary>
    public ObservableCollection<DocumentViewModel> Documents { get; } = [];

    /// <summary>Set by the view so the VM can open the Connection Manager window (master-detail). Replaces
    /// the old per-connection modal — one window covers add/edit/delete/duplicate/group.</summary>
    public Func<ConnectionManagerViewModel, Task>? ConnectionManagerRequested { get; set; }

    /// <summary>Set by the view so the VM can request the DDL Create dialog; returns the confirmed
    /// (possibly user-edited) SQL to run, or null on cancel.</summary>
    public Func<CreateObjectDialogViewModel, Task<string?>>? CreateObjectDialogRequested { get; set; }

    /// <summary>Set by the view so the VM can request the DROP/ALTER confirmation dialog; returns the
    /// confirmed (possibly user-edited) SQL to run, or null on cancel.</summary>
    public Func<AlterObjectDialogViewModel, Task<string?>>? AlterObjectDialogRequested { get; set; }

    /// <summary>Set by the view so the VM can copy text to the OS clipboard.</summary>
    public Func<string, Task>? ClipboardRequested { get; set; }

    /// <summary>Set by the view so the VM can prompt for a CSV file; returns the local path, or null on cancel.</summary>
    public Func<Task<string?>>? ImportCsvFileRequested { get; set; }

    /// <summary>Set by the view so the VM can show the CSV column-mapping dialog; true if the user confirmed.</summary>
    public Func<ImportCsvDialogViewModel, Task<bool>>? ImportCsvDialogRequested { get; set; }

    /// <summary>Set by the view so the VM can ask which format to export a whole table as (given its row
    /// count); null on cancel. Same dialog "Export…" on a document tab uses, just not tied to a grid.</summary>
    public Func<int, Task<ExportFormat?>>? ExportFormatRequested { get; set; }

    /// <summary>Set by the view so the VM can hand off exported text to a save-file dialog + write it.</summary>
    public Func<string, ExportFormat, Task>? ExportFileRequested { get; set; }

    /// <summary>Set by the view so the VM can ask a yes/no question (title, message); false if unavailable.</summary>
    public Func<string, string, Task<bool>>? ConfirmRequested { get; set; }

    partial void OnSelectedNodeChanged(TreeNodeViewModel? value)
    {
        SelectedConnection = value?.Connection;
        RefreshApplicableTools(value);
    }

    /// <summary>Tool-plugin actions applicable to the selected node, as a (possibly nested) menu tree shown
    /// in the sidebar context menu. Top-level nodes sit directly under the "Tools" item.</summary>
    public ObservableCollection<ToolMenuNode> ApplicableTools { get; } = [];

    public bool HasApplicableTools => ApplicableTools.Count > 0;

    // A tool applies to a connection node or a schema-object node; recompute whenever selection changes.
    private void RefreshApplicableTools(TreeNodeViewModel? node)
    {
        ApplicableTools.Clear();

        if (node is not null && (node.IsConnectionNode || node.NodeKind is not null) && node.Connection is { } connection)
        {
            foreach (var tool in _tools.Applicable(connection.ProviderId, node.NodeKind))
            {
                var captured = tool;
                var title = _tools.LocalizerFor(tool.Id).Resolve(tool.TitleKey, tool.Title);
                var leaf = new ToolMenuNode(title, new RelayCommand(() => RunToolCommand.Execute(captured)));

                // Walk the tool's MenuPath, creating or reusing a group node per segment, so tools that
                // share a path (even from different plugins) land in the same submenu.
                var siblings = ApplicableTools;
                foreach (var segment in tool.MenuPath)
                {
                    var group = siblings.FirstOrDefault(n => n.Run is null && n.Title == segment)
                        ?? AddTo(siblings, new ToolMenuNode(segment, null));
                    siblings = group.Children;
                }

                siblings.Add(leaf);
            }
        }

        OnPropertyChanged(nameof(HasApplicableTools));
    }

    private static ToolMenuNode AddTo(ObservableCollection<ToolMenuNode> collection, ToolMenuNode node)
    {
        collection.Add(node);
        return node;
    }

    // Run a tool: resolve the profile/provider/node for the selection, open the generic tool dialog.
    [RelayCommand]
    private async Task RunToolAsync(IToolPlugin? tool)
    {
        if (tool is null || SelectedNode is not { } node || node.Connection is not { } connection || ToolDialogRequested is null)
        {
            return;
        }

        var provider = _providers.Get(connection.ProviderId);
        var profile = _connections.Resolve(connection, node.DatabaseName);
        DbNodeRef? nodeRef = node.NodeKind is { } kind ? new DbNodeRef(kind, node.Name) : null;

        var dialog = _toolDialogFactory();
        dialog.Configure(tool, profile, nodeRef, provider, connection.ProviderId);
        await ToolDialogRequested(dialog);
    }

    /// <summary>Set by the view so the VM can show the generic tool dialog.</summary>
    public Func<ToolDialogViewModel, Task>? ToolDialogRequested { get; set; }

    // Full rebuild — used only at startup. Add/edit/delete go through the targeted helpers below so
    // that touching one connection never collapses the whole tree (loses every other node's expand +
    // loaded subtree). See Upsert/RemoveConnectionNode.
    private void RefreshConnections()
    {
        ConnectionNodes.Clear();
        // Ungrouped first (Folder null sorts before names), then folder groups, each alphabetical.
        foreach (var connection in _connections.List().OrderBy(c => c.Folder ?? string.Empty).ThenBy(c => c.Name))
        {
            PlaceConnectionNode(BuildConnectionNode(connection));
        }
    }

    private TreeNodeViewModel BuildConnectionNode(SavedConnection connection)
    {
        var node = TreeNodeViewModel.ForConnection(
            connection, _providers.Get(connection.ProviderId), ResolveIconImage(connection.ProviderId), LoadNodeChildrenAsync);
        // Drive the schema cache off the root's connection state: build once it reaches Connected
        // (via the Connect command or a manual expand), drop it again on disconnect/error.
        node.PropertyChanged += OnConnectionNodeStateChanged;
        return node;
    }

    private void OnConnectionNodeStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TreeNodeViewModel.State) || sender is not TreeNodeViewModel node)
        {
            return;
        }

        switch (node.State)
        {
            case ConnectionState.Connected:
                RebuildSchemaCache(node.Connection);
                break;
            case ConnectionState.Disconnected or ConnectionState.Error:
                _schemaCache.Invalidate(node.Connection.Id);
                break;
        }
    }

    // Fire-and-forget background walk; best-effort, so a failed build just leaves search/completion
    // without this connection rather than surfacing an error.
    private async void RebuildSchemaCache(SavedConnection connection)
    {
        try
        {
            await _schemaCache.BuildAsync(connection);
        }
        catch
        {
            // ignored — snapshot stays absent for this connection
        }
    }

    // Add a new connection node, or replace an edited one in place, leaving every OTHER node's
    // expand/loaded state untouched. A replaced (edited) node resets its own subtree, since its
    // connection parameters may have changed — but the rest of the tree stays exactly as it was.
    private void UpsertConnectionNode(SavedConnection saved, bool select = true)
    {
        if (FindConnectionNode(saved.Id) is { } current)
        {
            DetachConnectionNode(current);
        }

        var node = BuildConnectionNode(saved);
        PlaceConnectionNode(node);
        if (select)
        {
            SelectedNode = node;
        }
    }

    private void RemoveConnectionNode(string id)
    {
        if (FindConnectionNode(id) is not { } node)
        {
            return;
        }

        if (SelectedNode == node)
        {
            SelectedNode = null;
        }

        DetachConnectionNode(node);
    }

    // --- Folder grouping (FR-6, nested): ConnectionNodes holds folder nodes + ungrouped connection
    // roots. SavedConnection.Folder is a /-joined path (e.g. "Klanten/Klant A"); the host splits it and
    // builds nested folder nodes on demand, pruning empty ones back out. Single-segment names (the
    // original FR-6 shape) still work — they're just a one-deep path. ---

    /// <summary>Every connection root, whether at the tree root or nested inside folders.</summary>
    private IEnumerable<TreeNodeViewModel> AllConnectionNodes() => FlattenConnections(ConnectionNodes);

    // Recurse into folder nodes only; a connection root's own children are schema nodes, not connections.
    private static IEnumerable<TreeNodeViewModel> FlattenConnections(IEnumerable<TreeNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.IsFolder)
            {
                foreach (var nested in FlattenConnections(node.Children))
                {
                    yield return nested;
                }
            }
            else
            {
                yield return node;
            }
        }
    }

    private TreeNodeViewModel? FindConnectionNode(string id) =>
        AllConnectionNodes().FirstOrDefault(n => n.Connection.Id == id);

    // Put a connection node under its (nested) folder, or at the root when it has none.
    private void PlaceConnectionNode(TreeNodeViewModel node) =>
        ResolveFolderChildren(node.Connection.Folder).Add(node);

    // Walk the /-path segment by segment, creating folder nodes as needed, and return the child
    // collection the connection should live in (ConnectionNodes itself when ungrouped).
    private ObservableCollection<TreeNodeViewModel> ResolveFolderChildren(string? folderPath)
    {
        var segments = SplitFolderPath(folderPath);
        var current = ConnectionNodes;
        var pathSoFar = string.Empty;
        foreach (var segment in segments)
        {
            pathSoFar = pathSoFar.Length == 0 ? segment : $"{pathSoFar}/{segment}";
            var folder = current.FirstOrDefault(n => n.IsFolder && n.Name == segment);
            if (folder is null)
            {
                folder = TreeNodeViewModel.ForFolder(segment, pathSoFar);
                current.Add(folder);
            }

            current = folder.Children;
        }

        return current;
    }

    // Empty/whitespace segments ("A//B", leading/trailing slashes) are ignored rather than becoming
    // blank folders, so a malformed path degrades gracefully instead of crashing.
    private static string[] SplitFolderPath(string? folderPath) =>
        string.IsNullOrWhiteSpace(folderPath)
            ? []
            : folderPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // Remove a connection node from wherever it lives; drop any folders left empty up the chain.
    private void DetachConnectionNode(TreeNodeViewModel node) => DetachFrom(ConnectionNodes, node);

    private static bool DetachFrom(ObservableCollection<TreeNodeViewModel> container, TreeNodeViewModel node)
    {
        if (container.Remove(node))
        {
            return true;
        }

        foreach (var folder in container.Where(n => n.IsFolder).ToList())
        {
            if (DetachFrom(folder.Children, node))
            {
                if (folder.Children.Count == 0)
                {
                    container.Remove(folder);
                }

                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reconcile the sidebar tree against the connection store after the Connection Manager closes:
    /// remove nodes for deleted connections, add new ones, and update the rest in place so an open
    /// (connected) subtree survives — only a provider swap or folder move relocates/rebuilds a node.
    /// </summary>
    public void SyncConnectionsFromStore()
    {
        var saved = _connections.List();
        var savedIds = saved.Select(c => c.Id).ToHashSet();

        foreach (var node in AllConnectionNodes().ToList())
        {
            if (!savedIds.Contains(node.Connection.Id))
            {
                _schemaCache.Invalidate(node.Connection.Id);
                RemoveConnectionNode(node.Connection.Id);
            }
        }

        foreach (var connection in saved)
        {
            var existing = FindConnectionNode(connection.Id);
            if (existing is null || existing.Connection.ProviderId != connection.ProviderId)
            {
                UpsertConnectionNode(connection, select: false);
                continue;
            }

            var folderChanged = !string.Equals(existing.Connection.Folder, connection.Folder, StringComparison.Ordinal);
            existing.UpdateConnection(connection);
            if (folderChanged)
            {
                DetachConnectionNode(existing);
                PlaceConnectionNode(existing);
            }
        }

        // The selected connection may have been edited or removed; refresh the status-bar binding.
        SelectedConnection = SelectedNode?.Connection;
    }

    // A provider may ship a brand image and/or a glyph. We render the image when the host can decode
    // it; the tree otherwise falls back to a generic connection line-icon. The glyph is left to hosts
    // that can render emoji (Linux/Avalonia can't), so it is not used here. SVG needs an extra
    // renderer, so raster only for now.
    private IImage? ResolveIconImage(string providerId) =>
        PluginIconRenderer.Render(_providers.Get(providerId).Icon);

    // The lazy loader every tree node calls on expand: resolve secrets, ask the provider.
    private async Task<IReadOnlyList<DbTreeNode>> LoadNodeChildrenAsync(
        SavedConnection connection,
        IReadOnlyList<DbNodeRef> ancestors)
    {
        try
        {
            var profile = _connections.Resolve(connection);
            var provider = _providers.Get(connection.ProviderId);

            // Expanding the root = actually connect, so the status dot tells the truth. Some providers
            // list their top-level folders statically (SQL Server's Databases/Security/Administration)
            // without touching the server, which would otherwise show green for an unreachable host.
            if (ancestors.Count == 0 && !await provider.TestConnectionAsync(profile, CancellationToken.None))
            {
                throw new InvalidOperationException(Loc["ConnectionFailed"]);
            }

            var nodes = await provider.GetChildNodesAsync(profile, ancestors, CancellationToken.None);
            if (ancestors.Count == 0)
            {
                ReportInfo(connection.Name, Loc.Get("StatusConnected", nodes.Count));
            }

            // Hide engine-managed system databases unless the user opted in.
            return _settingsStore.Load().ShowSystemDatabases
                ? nodes
                : nodes.Where(n => !n.IsSystem).ToList();
        }
        catch (Exception ex)
        {
            // Surface the message (status bar + banner + output log) and let the tree node mark itself
            // errored (root shows a red status dot).
            ReportError(connection.Name, ex.Message);
            throw;
        }
    }

    // The sidebar "+" / File ▸ New Connection: open the manager on a fresh draft (prefilled with the
    // selected folder, if a folder — or a grouped connection — is highlighted).
    [RelayCommand]
    private Task NewConnectionAsync() => OpenConnectionManagerAsync(manager => manager.StartNewConnection(SelectedFolderPath()));

    // Right-click "Edit…" on a connection root: open the manager with that connection selected.
    [RelayCommand]
    private Task EditConnectionAsync()
    {
        if (SelectedConnection is not { } connection)
        {
            return Task.CompletedTask;
        }

        return OpenConnectionManagerAsync(manager => manager.SelectConnection(connection.Id));
    }

    /// <summary>Open the Connection Manager window, optionally pre-positioned, then reconcile the tree
    /// with whatever changed while it was open (add/edit/delete/move) — see <see cref="SyncConnectionsFromStore"/>.</summary>
    [RelayCommand]
    private Task ManageConnectionsAsync() => OpenConnectionManagerAsync(null);

    private async Task OpenConnectionManagerAsync(Action<ConnectionManagerViewModel>? position)
    {
        if (ConnectionManagerRequested is null)
        {
            return;
        }

        var manager = _connectionManagerFactory();
        position?.Invoke(manager);
        await ConnectionManagerRequested(manager);
        SyncConnectionsFromStore();
    }

    // The folder path to prefill a new connection with: the selected folder itself, or the folder the
    // selected connection lives in (null = ungrouped / nothing folder-ish selected).
    private string? SelectedFolderPath() => SelectedNode switch
    {
        { IsFolder: true, FolderPath: var path } => path,
        { IsConnectionNode: true, Connection.Folder: var folder } => folder,
        _ => null
    };

    // Sidebar quick action (kept alongside the manager): delete the selected connection — after a
    // confirm, since it's destructive and (unlike a drop) has no editable-SQL step to pause on.
    [RelayCommand]
    private async Task DeleteConnectionAsync()
    {
        if (SelectedConnection is not { } connection)
        {
            return;
        }

        if (ConfirmRequested is not null
            && !await ConfirmRequested(Loc["Delete"], string.Format(Loc["ConfirmDeleteConnection"], connection.Name)))
        {
            return;
        }

        _connections.Delete(connection.Id);
        _schemaCache.Invalidate(connection.Id);
        RemoveConnectionNode(connection.Id);
    }

    // DDL Create (host-API v12): "New Database…" on a connection root — no parent schema, and the
    // profile connects with no database override so it lands on the connection's own default catalog
    // (matches each provider's CREATE DATABASE convention: Postgres/MsSql run it there, not "inside"
    // the database being created).
    [RelayCommand]
    private async Task NewDatabaseAsync()
    {
        if (SelectedNode is not { CanCreateDatabase: true } node)
        {
            return;
        }

        await CreateObjectAsync(node, DbObjectKind.Database, parentSchema: null, database: null);
    }

    // "New Schema…" on a Schemas folder — runs against that folder's own database.
    [RelayCommand]
    private async Task NewSchemaAsync()
    {
        if (SelectedNode is not { CanCreateSchema: true } node)
        {
            return;
        }

        await CreateObjectAsync(node, DbObjectKind.Schema, parentSchema: null, node.DatabaseName);
    }

    // "New Table…" on a Tables folder — runs against that folder's schema + database (MySQL has no
    // schema layer, so SchemaName is null there and the provider ignores it).
    [RelayCommand]
    private async Task NewTableAsync()
    {
        if (SelectedNode is not { CanCreateTable: true } node)
        {
            return;
        }

        await CreateObjectAsync(node, DbObjectKind.Table, node.SchemaName, node.DatabaseName);
    }

    // Shared DDL Create flow: open the dialog pre-configured for `kind`, run the (possibly user-edited)
    // SQL it returns via ExecuteDdlAsync, then refresh `node` so the new object appears in the tree —
    // `node` is always the one the "New …" menu item appeared on, i.e. already the right parent to reload.
    private async Task CreateObjectAsync(TreeNodeViewModel node, DbObjectKind kind, string? parentSchema, string? database)
    {
        if (CreateObjectDialogRequested is null)
        {
            return;
        }

        var provider = _providers.Get(node.Connection.ProviderId);
        var dialog = _createDialogFactory();
        dialog.Configure(provider, kind, parentSchema);

        var sql = await CreateObjectDialogRequested(dialog);
        if (string.IsNullOrWhiteSpace(sql))
        {
            return;
        }

        try
        {
            var profile = _connections.Resolve(node.Connection, database);
            await provider.ExecuteDdlAsync(profile, sql, CancellationToken.None);
            await node.RefreshAsync();
        }
        catch (Exception ex)
        {
            ReportError(SelectedConnection?.Name, ex.Message);
        }
    }

    /// <summary>Set by the view so the VM can show the "New User…" dialog.</summary>
    public Func<NewUserDialogViewModel, Task<string?>>? NewUserDialogRequested { get; set; }

    // "New User…" on a Users folder: collect fields + roles, preview the provider-built SQL, run it. For
    // SQL Server the folder sits under a Database, so the create runs against that database's context.
    [RelayCommand]
    private async Task NewUserAsync()
    {
        if (SelectedNode is not { CanManageUsers: true } node || NewUserDialogRequested is null)
        {
            return;
        }

        var provider = _providers.Get(node.Connection.ProviderId);
        var profile = _connections.Resolve(node.Connection, node.DatabaseName);

        IReadOnlyList<string> roles;
        try
        {
            roles = await provider.GetAssignableRolesAsync(profile, node.NodePath, CancellationToken.None);
        }
        catch
        {
            roles = []; // best-effort: no role picker rather than blocking the whole dialog
        }

        var dialog = _newUserDialogFactory();
        dialog.Configure(provider, provider.UserFields, roles);

        var sql = await NewUserDialogRequested(dialog);
        if (string.IsNullOrWhiteSpace(sql))
        {
            return;
        }

        try
        {
            await provider.ExecuteDdlAsync(profile, sql, CancellationToken.None);
            await node.RefreshAsync();
        }
        catch (Exception ex)
        {
            ReportError(node.Connection.Name, ex.Message);
        }
    }

    // "Delete" on a User node: provider builds the exact DROP (MySQL needs the host from the node name),
    // confirm with the statement shown, then run it against the right database context.
    [RelayCommand]
    private async Task DeleteUserAsync()
    {
        if (SelectedNode is not { CanDeleteUser: true, NodeKind: { } kind } node || ConfirmRequested is null)
        {
            return;
        }

        var provider = _providers.Get(node.Connection.ProviderId);
        var statement = provider.BuildDropUserStatement(new DbNodeRef(kind, node.Name), node.NodePath);

        if (!await ConfirmRequested(Loc["DeleteUser"], string.Format(Loc["ConfirmDeleteUser"], node.Name)))
        {
            return;
        }

        try
        {
            var profile = _connections.Resolve(node.Connection, node.DatabaseName);
            await provider.ExecuteDdlAsync(profile, statement.Text, CancellationToken.None);
            var refreshTarget = node.Parent ?? FindConnectionNode(node.Connection.Id);
            if (refreshTarget is not null)
            {
                await refreshTarget.RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            ReportError(node.Connection.Name, ex.Message);
        }
    }

    // Server logins (SQL Server): the provider owns the view (ICustomSecurityUi, Route B). "New Login…" on
    // the Logins folder, "Properties…" on a login leaf — both host the provider view in the node-info dialog
    // and refresh the folder afterwards. "Drop Login…" is a plain host-side confirm + DROP LOGIN.
    [RelayCommand]
    private Task NewLoginAsync() =>
        SelectedNode is { CanManageLogins: true } node
            ? OpenSecurityViewAsync(node, SecurityUiAction.NewLogin)
            : Task.CompletedTask;

    [RelayCommand]
    private Task LoginPropertiesAsync() =>
        SelectedNode is { CanManageLogin: true } node
            ? OpenSecurityViewAsync(node, SecurityUiAction.LoginProperties)
            : Task.CompletedTask;

    private async Task OpenSecurityViewAsync(TreeNodeViewModel node, SecurityUiAction action)
    {
        if (SecurityViewRequested is null || node.NodeKind is not { } kind
            || _providers.Get(node.Connection.ProviderId) is not ICustomSecurityUi security)
        {
            return;
        }

        try
        {
            var profile = _connections.Resolve(node.Connection, null); // logins are server-level
            var target = action == SecurityUiAction.LoginProperties ? new DbNodeRef(kind, node.Name) : (DbNodeRef?)null;
            var view = security.CreateSecurityView(new SecurityUiContext(action, profile, node.NodePath, target, (IDbProvider)security));
            var title = action == SecurityUiAction.NewLogin ? Loc["NewLogin"] : string.Format(Loc["LoginPropertiesTitle"], node.Name);
            await SecurityViewRequested(new NodeInfoDialogViewModel(title, view, Loc));

            // The folder gets the new/changed login; on a leaf, refresh its parent folder.
            var refreshTarget = action == SecurityUiAction.NewLogin ? node : node.Parent ?? FindConnectionNode(node.Connection.Id);
            if (refreshTarget is not null)
            {
                await refreshTarget.RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            ReportError(node.Connection.Name, ex.Message);
        }
    }

    [RelayCommand]
    private async Task DropLoginAsync()
    {
        if (SelectedNode is not { CanManageLogin: true } node || ConfirmRequested is null)
        {
            return;
        }

        if (!await ConfirmRequested(Loc["DropLogin"], string.Format(Loc["ConfirmDropLogin"], node.Name)))
        {
            return;
        }

        try
        {
            var provider = _providers.Get(node.Connection.ProviderId);
            var quoted = provider.Dialect?.QuoteIdentifier(node.Name) ?? node.Name;
            var profile = _connections.Resolve(node.Connection, null);
            await provider.ExecuteDdlAsync(profile, $"DROP LOGIN {quoted}", CancellationToken.None);
            var refreshTarget = node.Parent ?? FindConnectionNode(node.Connection.Id);
            if (refreshTarget is not null)
            {
                await refreshTarget.RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            ReportError(node.Connection.Name, ex.Message);
        }
    }

    // DROP/ALTER (host-only SQL, see Core/Ddl/AlterStatementBuilder — no SDK member, no host-API bump):
    // "Drop Database…" on a Database node — resolves with no database override (`database: null`) so
    // it connects via the connection's own default catalog, never the one being dropped (Postgres/MsSql
    // both refuse DROP DATABASE on the connection you're using it from).
    [RelayCommand]
    private async Task DropDatabaseAsync()
    {
        if (SelectedNode is not { CanDropDatabase: true } node)
        {
            return;
        }

        await AlterObjectAsync(node, AlterKind.DropDatabase, database: null, node.Name, schema: null, node.Name);
    }

    [RelayCommand]
    private async Task DropSchemaAsync()
    {
        if (SelectedNode is not { CanDropSchema: true } node)
        {
            return;
        }

        await AlterObjectAsync(node, AlterKind.DropSchema, node.DatabaseName, node.Name, schema: null, node.Name);
    }

    [RelayCommand]
    private async Task DropTableAsync()
    {
        if (SelectedNode is not { CanDropTable: true } node)
        {
            return;
        }

        await AlterObjectAsync(node, AlterKind.DropTable, node.DatabaseName, node.Name, node.SchemaName, node.Name,
            isView: node.NodeKind == DbNodeKind.View);
    }

    [RelayCommand]
    private async Task AddColumnAsync()
    {
        if (SelectedNode is not { CanAddColumn: true } node)
        {
            return;
        }

        await AlterObjectAsync(node, AlterKind.AddColumn, node.DatabaseName, node.Name, node.SchemaName, node.Name);
    }

    [RelayCommand]
    private async Task TruncateTableAsync()
    {
        if (SelectedNode is not { CanTruncate: true } node)
        {
            return;
        }

        await AlterObjectAsync(node, AlterKind.TruncateTable, node.DatabaseName, node.Name, node.SchemaName, node.Name);
    }

    // Context-menu "Collapse all": tidy up a container's expanded subtree without forcing lazy loads.
    [RelayCommand]
    private void CollapseAllNode() => SelectedNode?.CollapseAll();

    // Context-menu "Test connection": a connectivity check that doesn't populate the tree.
    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (SelectedNode is not { IsConnectionNode: true } node)
        {
            return;
        }

        try
        {
            var provider = _providers.Get(node.Connection.ProviderId);
            var profile = _connections.Resolve(node.Connection);
            if (await provider.TestConnectionAsync(profile, CancellationToken.None))
            {
                ReportInfo(node.Connection.Name, Loc["TestConnectionOk"]);
            }
            else
            {
                ReportError(node.Connection.Name, Loc["TestConnectionFailed"]);
            }
        }
        catch (Exception ex)
        {
            ReportError(node.Connection.Name, ex.Message);
        }
    }

    [RelayCommand]
    private async Task DropColumnAsync()
    {
        if (SelectedNode is not { CanDropColumn: true } node || node.TableName is not { } table)
        {
            return;
        }

        await AlterObjectAsync(node, AlterKind.DropColumn, node.DatabaseName, $"{table}.{node.Name}", node.SchemaName, table, existingColumn: node.Name);
    }

    [RelayCommand]
    private async Task RenameColumnAsync()
    {
        if (SelectedNode is not { CanRenameColumn: true } node || node.TableName is not { } table)
        {
            return;
        }

        await AlterObjectAsync(node, AlterKind.RenameColumn, node.DatabaseName, $"{table}.{node.Name}", node.SchemaName, table, existingColumn: node.Name);
    }

    // Shared DROP/ALTER flow: open the confirmation dialog, run the (possibly user-edited) SQL it
    // returns, then reload only the affected container so the rest of the tree keeps its expansion.
    // Adding a column changes the table's own child list (refresh the node); a drop/rename changes the
    // parent's list and may remove the node itself (refresh the parent). The schema cache is rebuilt
    // explicitly afterwards since we no longer bounce the connection through the Connected state.
    private async Task AlterObjectAsync(
        TreeNodeViewModel node, AlterKind kind, string? database, string objectLabel, string? schema, string target,
        bool isView = false, string? existingColumn = null)
    {
        if (AlterObjectDialogRequested is null)
        {
            return;
        }

        var provider = _providers.Get(node.Connection.ProviderId);
        var dialog = _alterDialogFactory();
        dialog.Configure(kind, provider, node.Connection.ProviderId, provider.Dialect, provider.ColumnTypes, objectLabel, database, schema, target, isView, existingColumn);

        var sql = await AlterObjectDialogRequested(dialog);
        if (string.IsNullOrWhiteSpace(sql))
        {
            return;
        }

        try
        {
            var profile = _connections.Resolve(node.Connection, database);
            await provider.ExecuteDdlAsync(profile, sql, CancellationToken.None);

            // Reload just the affected container, preserving the rest of the tree's expanded state.
            var refreshTarget = kind == AlterKind.AddColumn ? node : node.Parent ?? FindConnectionNode(node.Connection.Id);
            if (refreshTarget is not null)
            {
                await refreshTarget.RefreshAsync();
            }

            // Keep quick-open/autocomplete in sync (the old full-tree refresh did this via the state wiring).
            RebuildSchemaCache(node.Connection);
        }
        catch (Exception ex)
        {
            ReportError(SelectedConnection?.Name, ex.Message);
        }
    }

    /// <summary>Context-menu "Refresh" on a container node — reload just its children (not the whole
    /// connection) and refresh the schema cache, so a manual refresh keeps the rest of the tree expanded.</summary>
    [RelayCommand]
    private async Task RefreshNodeAsync()
    {
        if (SelectedNode is not { CanRefresh: true } node)
        {
            return;
        }

        await node.RefreshAsync();
        RebuildSchemaCache(node.Connection);
    }

    // Tree-level "Export…": the document-tab Export button needs an open, populated grid, but exporting
    // a whole table shouldn't require browsing it first — this reads the table directly (no LIMIT/paging,
    // unlike browse) and reuses the same ExportDialog + ResultExporter the grid button uses.
    [RelayCommand]
    private async Task ExportTableAsync()
    {
        if (SelectedNode is not { IsTableOrView: true } node || ExportFormatRequested is null || ExportFileRequested is null)
        {
            return;
        }

        try
        {
            var connection = node.Connection;
            var provider = _providers.Get(connection.ProviderId);
            var dialect = provider.Dialect;
            var qualified = dialect.QualifyName(node.DatabaseName, node.SchemaName, node.Name);
            var profile = _connections.Resolve(connection, node.DatabaseName);
            var result = await provider.ExecuteQueryAsync(profile, $"SELECT * FROM {qualified}", CancellationToken.None);

            var format = await ExportFormatRequested(result.Rows.Count);
            if (format is not { } chosenFormat)
            {
                return;
            }

            var text = chosenFormat switch
            {
                ExportFormat.Csv => ResultExporter.ToCsv(result.Columns, result.Rows),
                ExportFormat.Json => ResultExporter.ToJson(result.Columns, result.Rows),
                ExportFormat.Sql => ResultExporter.ToSqlInserts(result.Columns, result.Rows, dialect, connection.ProviderId, QuotedTarget(dialect, node)),
                _ => string.Empty
            };

            await ExportFileRequested(text, chosenFormat);
        }
        catch (Exception ex)
        {
            ReportError(SelectedConnection?.Name, ex.Message);
        }
    }

    private static string QuotedTarget(ISqlDialect dialect, TreeNodeViewModel node) =>
        node.SchemaName is { Length: > 0 } schema
            ? $"{dialect.QuoteIdentifier(schema)}.{dialect.QuoteIdentifier(node.Name)}"
            : dialect.QuoteIdentifier(node.Name);

    // File pick -> parse -> column-mapping dialog -> parameterised INSERT batch, same shape as the
    // editable-grid save-flow (Notes §8) but for a whole external file instead of pending row edits.
    [RelayCommand]
    private async Task ImportCsvAsync()
    {
        if (SelectedNode is not { CanImportCsv: true } node || ImportCsvFileRequested is null || ImportCsvDialogRequested is null)
        {
            return;
        }

        var path = await ImportCsvFileRequested();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var csv = CsvParser.Parse(await File.ReadAllTextAsync(path));
            if (csv.Rows.Count == 0)
            {
                return;
            }

            var connection = node.Connection;
            var provider = _providers.Get(connection.ProviderId);
            var qualified = provider.Dialect.QualifyName(node.DatabaseName, node.SchemaName, node.Name);
            var targetColumns = await FetchColumnsAsync(connection, node.DatabaseName, qualified);

            var dialog = _importCsvDialogFactory();
            dialog.Configure(csv, targetColumns);

            if (!await ImportCsvDialogRequested(dialog))
            {
                return;
            }

            var statements = dialog.BuildInsertStatements(provider.Dialect, qualified);
            if (statements.Count == 0)
            {
                return;
            }

            var profile = _connections.Resolve(connection, node.DatabaseName);
            var affected = await provider.ExecuteBatchAsync(profile, statements, CancellationToken.None);
            ReportInfo(SelectedConnection?.Name, Loc.Get("ImportOk", affected));

            var root = FindConnectionNode(connection.Id);
            if (root is not null)
            {
                await root.RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            ReportError(SelectedConnection?.Name, ex.Message);
        }
    }

    // Copy the selected connection (secret included) under a "… (copy)" name, then select the copy.
    [RelayCommand]
    private void DuplicateConnection()
    {
        if (SelectedConnection is null)
        {
            return;
        }

        var copy = _connections.Duplicate(SelectedConnection.Id, $"{SelectedConnection.Name} {Loc["CopySuffix"]}");
        UpsertConnectionNode(copy);
    }

    // Connect = (re)load the selected connection's tree from the root and expand it.
    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (SelectedConnection is null)
        {
            return;
        }

        var root = FindConnectionNode(SelectedConnection.Id);
        if (root is not null)
        {
            await root.RefreshAsync();
        }
    }

    // Collapse and forget the selected connection's tree; its status dot goes back to grey.
    [RelayCommand]
    private void Disconnect()
    {
        if (SelectedConnection is null)
        {
            return;
        }

        var root = FindConnectionNode(SelectedConnection.Id);
        root?.Disconnect();
    }

    /// <summary>Open a new, empty query tab against the selected connection.</summary>
    [RelayCommand]
    private void NewQueryTab()
    {
        var connection = SelectedConnection ?? _connections.List().FirstOrDefault();
        if (connection is null)
        {
            return;
        }

        var document = NewDocument();
        document.InitQuery(connection);
        AddDocument(document);
    }

    // Table/view context action or double-click: open (or focus) a browse tab for that table.
    [RelayCommand]
    private async Task BrowseTableAsync(CancellationToken ct)
    {
        if (SelectedNode is not { IsTableOrView: true } node)
        {
            return;
        }

        await OpenBrowseTabAsync(node.Connection, node.DatabaseName, node.SchemaName, node.Name, ct);
    }

    // Shared by the tree's browse action and quick-open (1.2): focus the existing tab for this
    // table/view if one is already open, otherwise open and load a fresh one.
    private async Task OpenBrowseTabAsync(SavedConnection connection, string? database, string? schema, string table, CancellationToken ct)
    {
        var existing = Documents.FirstOrDefault(d => d.MatchesBrowse(connection.Id, database, schema, table));
        if (existing is not null)
        {
            SelectedDocument = existing;
            return;
        }

        var document = NewDocument();
        document.InitBrowse(connection, database, schema, table);
        AddDocument(document);
        await document.LoadPageAsync(ct);
    }

    // Activity Monitor on a connection root: open (or focus) the live-sessions tab for that connection.
    // The tab starts its own auto-refresh (default 5s) as soon as it opens (DocumentViewModel.InitMonitor).
    [RelayCommand]
    private void ActivityMonitor()
    {
        if (SelectedNode is not { CanShowActivityMonitor: true } node || node.Connection is not { } connection)
        {
            return;
        }

        var existing = Documents.FirstOrDefault(d => d.MatchesMonitor(connection.Id));
        if (existing is not null)
        {
            SelectedDocument = existing;
            return;
        }

        var document = NewDocument();
        document.InitMonitor(connection);
        AddDocument(document);
    }

    // View Definition (double-click or context menu) on a procedure/function/trigger: fetch its CREATE
    // text and open it in a normal editable query tab — same mechanism as browse, no new viewer.
    [RelayCommand]
    private async Task ViewDefinitionAsync(CancellationToken ct)
    {
        if (SelectedNode is not { CanViewDefinition: true } node || node.Connection is not { } connection)
        {
            return;
        }

        try
        {
            var provider = _providers.Get(connection.ProviderId);
            var profile = _connections.Resolve(connection, node.DatabaseName);
            var definition = await provider.GetObjectDefinitionAsync(profile, node.NodePath, ct);
            if (string.IsNullOrWhiteSpace(definition))
            {
                ReportError(connection.Name, Loc["DefinitionUnavailable"]);
                return;
            }

            OpenDefinitionTab(connection, definition, node.Name);
        }
        catch (Exception ex)
        {
            ReportError(connection.Name, ex.Message);
        }
    }

    // Execute… on a procedure/function: collect IN parameter values (skipping the dialog when there are
    // none), then generate a call script and open it in a tab WITHOUT running it — the user presses Run.
    [RelayCommand]
    private async Task ExecuteRoutineAsync(CancellationToken ct)
    {
        if (SelectedNode is not { CanExecuteRoutine: true } node || node.Connection is not { } connection)
        {
            return;
        }

        try
        {
            var provider = _providers.Get(connection.ProviderId);
            var profile = _connections.Resolve(connection, node.DatabaseName);
            var parameters = await provider.GetRoutineParametersAsync(profile, node.NodePath, ct);

            IReadOnlyDictionary<string, string?> values = new Dictionary<string, string?>();
            if (parameters.Any(p => !p.IsOutput) && RoutineParametersRequested is not null)
            {
                var dialog = _routineParamsDialogFactory();
                dialog.Configure(node.Name, parameters);
                await RoutineParametersRequested(dialog);
                if (!dialog.Confirmed)
                {
                    return;
                }

                values = dialog.Values;
            }

            var statement = provider.BuildCallStatement(node.NodePath, parameters, values);
            OpenDefinitionTab(connection, statement.Text, node.Name);
        }
        catch (Exception ex)
        {
            ReportError(connection.Name, ex.Message);
        }
    }

    // Open generated SQL (a definition or a routine call) in a fresh, editable query tab titled after the
    // object, so the user can inspect/edit and run it themselves.
    private void OpenDefinitionTab(SavedConnection connection, string sql, string title)
    {
        var document = NewDocument();
        document.InitQuery(connection);
        document.Sql = sql;
        document.Title = title;
        AddDocument(document);
    }

    /// <summary>Set by the view so the VM can show the routine-parameter dialog before generating a call.</summary>
    public Func<RoutineParametersDialogViewModel, Task>? RoutineParametersRequested { get; set; }

    // Properties…: show the provider's read-only info view (ICustomNodeInfoUi) for the selected node in the
    // lightweight node-info dialog. Informational only, so it is a separate context item, not a tool.
    [RelayCommand]
    private async Task ShowPropertiesAsync()
    {
        if (SelectedNode is not { CanShowProperties: true } node || node.Connection is not { } connection
            || node.NodeKind is not { } kind || NodeInfoRequested is null)
        {
            return;
        }

        if (_providers.Get(connection.ProviderId) is not ICustomNodeInfoUi info)
        {
            return;
        }

        try
        {
            var provider = _providers.Get(connection.ProviderId);
            var profile = _connections.Resolve(connection, node.DatabaseName);
            var nodeRef = new DbNodeRef(kind, node.Name);
            var view = info.CreateInfoView(new NodeInfoContext(profile, nodeRef, provider));
            var dialog = new NodeInfoDialogViewModel(info.InfoTitle(nodeRef), view, Loc);
            await NodeInfoRequested(dialog);
        }
        catch (Exception ex)
        {
            ReportError(connection.Name, ex.Message);
        }
    }

    /// <summary>Set by the view so the VM can show the node-info (properties) dialog.</summary>
    public Func<NodeInfoDialogViewModel, Task>? NodeInfoRequested { get; set; }

    /// <summary>Set by the view so the VM can host a provider security view (ICustomSecurityUi) in a dialog.</summary>
    public Func<NodeInfoDialogViewModel, Task>? SecurityViewRequested { get; set; }

    [RelayCommand]
    private async Task CloseTab(DocumentViewModel? document)
    {
        if (document is not null)
        {
            await TryCloseAsync(document);
        }
    }

    [RelayCommand]
    private async Task CloseOtherTabs(DocumentViewModel? keep)
    {
        if (keep is not null)
        {
            await CloseManyAsync(Documents.Where(d => d != keep).ToList());
        }
    }

    [RelayCommand]
    private Task CloseAllTabs() => CloseManyAsync(Documents.ToList());

    // Duplicate a query tab: a fresh tab on the same connection with the same SQL. (Browse tabs reopen
    // from the tree, so there's nothing to duplicate.)
    [RelayCommand]
    private void DuplicateTab(DocumentViewModel? document)
    {
        if (document is { IsQueryMode: true, Connection: { } connection })
        {
            var copy = NewDocument();
            copy.InitQuery(connection);
            copy.Sql = document.Sql;
            AddDocument(copy);
        }
    }

    // Close one tab, confirming first if it has unsaved grid edits. Returns false if the user cancelled.
    private async Task<bool> TryCloseAsync(DocumentViewModel document)
    {
        if (document.HasChanges && !await ConfirmDiscardAsync(1))
        {
            return false;
        }

        RemoveTab(document);
        return true;
    }

    // Close a batch (Close others / Close all): one combined confirm if any of them have unsaved edits,
    // rather than a dialog per tab.
    private async Task CloseManyAsync(IReadOnlyList<DocumentViewModel> documents)
    {
        var dirty = documents.Count(d => d.HasChanges);
        if (dirty > 0 && !await ConfirmDiscardAsync(dirty))
        {
            return;
        }

        foreach (var document in documents)
        {
            RemoveTab(document);
        }
    }

    // No confirm hook wired (headless/tests) → proceed rather than block.
    private async Task<bool> ConfirmDiscardAsync(int count) =>
        ConfirmRequested is null
        || await ConfirmRequested(
            Loc["ConfirmDiscardTitle"],
            count == 1 ? Loc["ConfirmDiscardChanges"] : string.Format(Loc["ConfirmDiscardChangesMany"], count));

    private void RemoveTab(DocumentViewModel document)
    {
        // A monitor tab polls on a background timer — stop it so a closed tab doesn't keep querying.
        if (document.IsMonitorMode)
        {
            document.StopMonitor();
        }

        // Remember closed query tabs so Ctrl+Shift+T can bring them back (browse tabs reopen from the tree).
        if (document.IsQueryMode && document.Connection is { } connection)
        {
            _closedTabs.Push((connection, document.Sql));
            if (_closedTabs.Count > ClosedTabHistory)
            {
                _closedTabs = new Stack<(SavedConnection, string)>(_closedTabs.Take(ClosedTabHistory).Reverse());
            }
        }

        var index = Documents.IndexOf(document);
        Documents.Remove(document);

        if (SelectedDocument == document)
        {
            SelectedDocument = Documents.Count == 0
                ? null
                : Documents[Math.Min(index, Documents.Count - 1)];
        }
    }

    private const int ClosedTabHistory = 15;
    private Stack<(SavedConnection Connection, string Sql)> _closedTabs = new();

    // Ctrl+Shift+T: reopen the most recently closed query tab with its SQL and connection restored.
    [RelayCommand]
    private void ReopenClosedTab()
    {
        if (_closedTabs.Count == 0)
        {
            return;
        }

        var (connection, sql) = _closedTabs.Pop();
        var document = NewDocument();
        document.InitQuery(connection);
        document.Sql = sql;
        AddDocument(document);
    }

    // Copy a dialect-quoted identifier so it pastes straight into SQL — crucial on Postgres, where
    // an unquoted PascalCase name folds to lowercase and fails (42703). Tables/views come schema-qualified.
    [RelayCommand]
    private async Task CopyNameAsync()
    {
        if (SelectedNode is not { IsCopyable: true } node || SelectedConnection is null || ClipboardRequested is null)
        {
            return;
        }

        var dialect = _providers.Get(SelectedConnection.ProviderId).Dialect;
        var text = node.IsTableOrView && node.SchemaName is { } schema
            ? $"{dialect.QuoteIdentifier(schema)}.{dialect.QuoteIdentifier(node.Name)}"
            : dialect.QuoteIdentifier(node.Name);

        await ClipboardRequested(text);
    }

    // "Copy qualified name" — the fully database-qualified identifier (SQL Server: [db].[schema].[table]),
    // ready to paste into a query tab that has no database context of its own.
    [RelayCommand]
    private async Task CopyQualifiedNameAsync()
    {
        if (SelectedNode is not { IsTableOrView: true } node || SelectedConnection is null || ClipboardRequested is null)
        {
            return;
        }

        var dialect = _providers.Get(SelectedConnection.ProviderId).Dialect;
        await ClipboardRequested(dialect.QualifyName(node.DatabaseName, node.SchemaName, node.Name));
    }

    // Generate a SQL template (SELECT/INSERT/UPDATE/…) for the selected table into a new query tab —
    // the DBeaver/DataGrip "SQL commands" action. Column-based templates read the live column metadata.
    [RelayCommand]
    private async Task GenerateSqlAsync(string? kind)
    {
        if (SelectedNode is not { IsTableOrView: true } node)
        {
            return;
        }

        var connection = node.Connection;
        var provider = _providers.Get(connection.ProviderId);
        var dialect = provider.Dialect;
        // Generated SQL opens in a free query tab with no database context, so qualify it fully — the
        // dialect decides how far (SQL Server: three-part [db].[schema].[table]; Postgres: schema.table).
        var qualified = dialect.QualifyName(node.DatabaseName, node.SchemaName, node.Name);

        try
        {
            // Let the provider own the query text first (a non-SQL engine returns its own — e.g. a MongoDB
            // find). Null falls back to the host's SQL generation below, unchanged for SQL providers.
            var nodeQueryKind = MapNodeQueryKind(kind);
            var custom = provider.BuildNodeQuery(nodeQueryKind, node.NodePath, columns: null);
            var sql = custom ?? kind switch
            {
                "Select" => $"SELECT * FROM {qualified};",
                "SelectTop" => $"{dialect.Paginate($"SELECT * FROM {qualified}", 1000, 0)};",
                "Count" => $"SELECT COUNT(*) FROM {qualified};",
                _ => SqlTemplateBuilder.Build(kind ?? "Select", qualified, dialect, await FetchColumnsAsync(connection, node.DatabaseName, qualified))
            };

            var document = NewDocument();
            // Provider-owned text (e.g. a MongoDB db.coll.find()) isn't self-qualified with the database, so
            // bind the tab to the node's database; host SQL is fully qualified and keeps a free-context tab.
            document.InitQuery(connection, custom is not null ? node.DatabaseName : null);
            document.Sql = sql;
            AddDocument(document);

            // "Select top 1000" is a read-only convenience — run it straight away. The other kinds
            // (INSERT/UPDATE/DELETE templates) are only scaffolding and must never auto-execute.
            if (kind == "SelectTop")
            {
                await document.RunCommand.ExecuteAsync(null);
            }
        }
        catch (Exception ex)
        {
            ReportError(SelectedConnection?.Name, ex.Message);
        }
    }

    // Map the menu's CommandParameter string to the SDK's NodeQueryKind for provider-owned generation.
    private static NodeQueryKind MapNodeQueryKind(string? kind) => kind switch
    {
        "SelectTop" => NodeQueryKind.SelectTop,
        "Count" => NodeQueryKind.Count,
        "SelectColumns" => NodeQueryKind.SelectColumns,
        "Insert" => NodeQueryKind.Insert,
        "Update" => NodeQueryKind.Update,
        "Delete" => NodeQueryKind.Delete,
        _ => NodeQueryKind.SelectAll
    };

    // A zero-row query is the cheapest way to read a relation's column metadata (names, PK) per dialect.
    private async Task<IReadOnlyList<ResultColumn>> FetchColumnsAsync(SavedConnection connection, string? database, string qualified)
    {
        var provider = _providers.Get(connection.ProviderId);
        var profile = _connections.Resolve(connection, database);
        // One row is enough for the column schema and stays valid across dialects — SQL Server's
        // OFFSET/FETCH rejects FETCH NEXT 0, so a zero-row probe is not portable.
        var probe = provider.Dialect.Paginate($"SELECT * FROM {qualified}", 1, 0);
        var result = await provider.ExecuteQueryAsync(profile, probe, CancellationToken.None);
        return result.Columns;
    }

    [RelayCommand]
    private void SetLanguage(string code)
    {
        Loc.SetCulture(CultureInfo.GetCultureInfo(code));
        var settings = _settingsStore.Load();
        settings.Language = code;
        _settingsStore.Save(settings);
    }

    [RelayCommand]
    private void SetTheme(AppTheme theme)
    {
        ThemeApplier.Apply(theme);
        var settings = _settingsStore.Load();
        settings.Theme = theme;
        _settingsStore.Save(settings);
    }

    /// <summary>Set by the view so the VM can show the Preferences window.</summary>
    public Func<SettingsViewModel, Task>? SettingsDialogRequested { get; set; }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        if (SettingsDialogRequested is null)
        {
            return;
        }

        await SettingsDialogRequested(_settingsDialogFactory());
    }

    [RelayCommand]
    private void Exit()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    /// <summary>Set by the view so the VM can show the Query Log window.</summary>
    public Func<QueryLogViewModel, Task>? QueryLogRequested { get; set; }

    [RelayCommand]
    private async Task OpenQueryLogAsync()
    {
        if (QueryLogRequested is null)
        {
            return;
        }

        await QueryLogRequested(_queryLogFactory());
    }

    /// <summary>Set by the view so the VM can show the Plugin Store window.</summary>
    public Func<PluginStoreViewModel, Task>? PluginStoreRequested { get; set; }

    [RelayCommand]
    private async Task OpenPluginStoreAsync()
    {
        if (PluginStoreRequested is null)
        {
            return;
        }

        await PluginStoreRequested(_pluginStoreFactory());

        // The store may have staged installs/removes/toggles — refresh the main-window banner.
        EvaluatePluginRestart();
    }

    /// <summary>Set by the view so the VM can show the About window.</summary>
    public Func<Task>? AboutRequested { get; set; }

    [RelayCommand]
    private async Task ShowAboutAsync()
    {
        if (AboutRequested is not null)
        {
            await AboutRequested();
        }
    }

    private DocumentViewModel NewDocument()
    {
        var document = new DocumentViewModel(_providers, _connections, _formatter, _history, _queryLog, _schemaCache, _settingsStore, Loc);
        // Surface every execution outcome (row counts, cancellations, failures) in the shared Output panel.
        document.Reported += (level, message) => ReportOutput(level, document.Connection?.Name, message);
        // A query auto-connects outside the tree's connect flow, so reflect that on the connection's status dot.
        document.ConnectionActivity += SetConnectionState;
        return document;
    }

    // Colour a connection's status dot from query activity. Property-only: setting State recolours the LED
    // (via ConnectionStateBrushConverter) and drives the schema-cache observer — it never refreshes/reloads
    // the node, so the tree keeps its expanded state (only the dot changes).
    private void SetConnectionState(string connectionId, ConnectionState state)
    {
        if (FindConnectionNode(connectionId) is { } node && node.State != state)
        {
            node.State = state;
        }
    }

    private void AddDocument(DocumentViewModel document)
    {
        Documents.Add(document);
        SelectedDocument = document;
    }
}
