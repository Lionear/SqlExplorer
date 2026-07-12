using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Lionear.SqlExplorer.Core.Connections;
using Lionear.SqlExplorer.Core.Editing;
using Lionear.SqlExplorer.Core.Formatting;
using Lionear.SqlExplorer.Core.History;
using Lionear.SqlExplorer.Core.Localization;
using Lionear.SqlExplorer.Core.Providers;
using Lionear.SqlExplorer.Core.Schema;
using Lionear.SqlExplorer.Sdk;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Lionear.SqlExplorer.App.ViewModels;

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
    private readonly ISchemaCache _schemaCache;
    private readonly Func<ConnectionDialogViewModel> _dialogFactory;

    // Selected tree node drives the active connection: any node knows its owning connection.
    [ObservableProperty]
    private TreeNodeViewModel? _selectedNode;

    [ObservableProperty]
    private SavedConnection? _selectedConnection;

    [ObservableProperty]
    private DocumentViewModel? _selectedDocument;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _isHistoryVisible;

    [ObservableProperty]
    private string _historySearch = string.Empty;

    [ObservableProperty]
    private bool _isSearchVisible;

    [ObservableProperty]
    private string _searchText = string.Empty;

    public MainViewModel(
        IDbProviderRegistry providers,
        ISqlFormatter formatter,
        ConnectionService connections,
        IQueryHistoryStore history,
        ISchemaCache schemaCache,
        Func<ConnectionDialogViewModel> dialogFactory,
        ILocalizer localizer)
    {
        _providers = providers;
        _formatter = formatter;
        _connections = connections;
        _history = history;
        _schemaCache = schemaCache;
        _dialogFactory = dialogFactory;
        Loc = localizer;

        _history.Changed += OnHistoryChanged;
        RefreshConnections();
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

    /// <summary>Quick-open ("go to table") hits for <see cref="SearchText"/>, newest ranked first.</summary>
    public ObservableCollection<SchemaSearchResult> SearchResults { get; } = [];

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

        var hits = new List<(int Rank, SchemaSearchResult Result)>();
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

        foreach (var hit in hits.OrderBy(h => h.Rank).ThenBy(h => h.Result.Display, StringComparer.OrdinalIgnoreCase).Take(50))
        {
            SearchResults.Add(hit.Result);
        }
    }

    // Quick-open result picked: jump straight to a browse tab (no tree-reveal — the tree is lazily
    // loaded per-node, so re-walking ancestors to select a node deep in it isn't worth it for MVP).
    [RelayCommand]
    private async Task OpenSearchResultAsync(SchemaSearchResult? result, CancellationToken ct)
    {
        if (result is null)
        {
            return;
        }

        IsSearchVisible = false;
        SearchText = string.Empty;
        await OpenBrowseTabAsync(result.Connection, result.Database, result.Schema, result.Name, ct);
    }

    public ILocalizer Loc { get; }

    /// <summary>The sidebar tree: one root node per saved connection, children loaded lazily.</summary>
    public ObservableCollection<TreeNodeViewModel> ConnectionNodes { get; } = [];

    /// <summary>Open editor tabs (query panes and table-browse panes).</summary>
    public ObservableCollection<DocumentViewModel> Documents { get; } = [];

    /// <summary>Set by the view so the VM can request a modal connection dialog.</summary>
    public Func<ConnectionDialogViewModel, Task<SavedConnection?>>? ConnectionDialogRequested { get; set; }

    /// <summary>Set by the view so the VM can copy text to the OS clipboard.</summary>
    public Func<string, Task>? ClipboardRequested { get; set; }

    partial void OnSelectedNodeChanged(TreeNodeViewModel? value) =>
        SelectedConnection = value?.Connection;

    // Full rebuild — used only at startup. Add/edit/delete go through the targeted helpers below so
    // that touching one connection never collapses the whole tree (loses every other node's expand +
    // loaded subtree). See Upsert/RemoveConnectionNode.
    private void RefreshConnections()
    {
        ConnectionNodes.Clear();
        foreach (var connection in _connections.List())
        {
            ConnectionNodes.Add(BuildConnectionNode(connection));
        }
    }

    private TreeNodeViewModel BuildConnectionNode(SavedConnection connection)
    {
        var node = TreeNodeViewModel.ForConnection(connection, ResolveIconImage(connection.ProviderId), LoadNodeChildrenAsync);
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
    private void UpsertConnectionNode(SavedConnection saved)
    {
        var node = BuildConnectionNode(saved);
        var index = IndexOfConnection(saved.Id);
        if (index >= 0)
        {
            ConnectionNodes[index] = node;
        }
        else
        {
            ConnectionNodes.Add(node);
        }

        SelectedNode = node;
    }

    private void RemoveConnectionNode(string id)
    {
        var index = IndexOfConnection(id);
        if (index < 0)
        {
            return;
        }

        if (SelectedNode == ConnectionNodes[index])
        {
            SelectedNode = null;
        }

        ConnectionNodes.RemoveAt(index);
    }

    private int IndexOfConnection(string id)
    {
        for (var i = 0; i < ConnectionNodes.Count; i++)
        {
            if (ConnectionNodes[i].Connection.Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    // A provider may ship a brand image and/or a glyph. We render the image when the host can decode
    // it; the tree otherwise falls back to a generic connection line-icon. The glyph is left to hosts
    // that can render emoji (Linux/Avalonia can't), so it is not used here. SVG needs an extra
    // renderer, so raster only for now.
    private IImage? ResolveIconImage(string providerId)
    {
        var icon = _providers.Get(providerId).Icon;
        if (icon?.ImageData is not { Length: > 0 } bytes || !CanRenderImage(icon.ImageMediaType))
        {
            return null;
        }

        try
        {
            return new Bitmap(new MemoryStream(bytes));
        }
        catch
        {
            return null;
        }
    }

    private static bool CanRenderImage(string? mediaType) =>
        mediaType is not null
        && mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        && !mediaType.Contains("svg", StringComparison.OrdinalIgnoreCase);

    // The lazy loader every tree node calls on expand: resolve secrets, ask the provider.
    private async Task<IReadOnlyList<DbTreeNode>> LoadNodeChildrenAsync(
        SavedConnection connection,
        IReadOnlyList<DbNodeRef> ancestors)
    {
        try
        {
            var profile = _connections.Resolve(connection);
            var nodes = await _providers.Get(connection.ProviderId).GetChildNodesAsync(profile, ancestors, CancellationToken.None);
            if (ancestors.Count == 0)
            {
                Status = Loc.Get("StatusConnected", nodes.Count);
            }

            return nodes;
        }
        catch (Exception ex)
        {
            // Surface the message and let the tree node mark itself errored (root shows a red status dot).
            Status = ex.Message;
            throw;
        }
    }

    [RelayCommand]
    private async Task NewConnectionAsync()
    {
        if (ConnectionDialogRequested is null)
        {
            return;
        }

        var saved = await ConnectionDialogRequested(_dialogFactory());
        if (saved is not null)
        {
            UpsertConnectionNode(saved);
        }
    }

    [RelayCommand]
    private async Task EditConnectionAsync()
    {
        if (SelectedConnection is null || ConnectionDialogRequested is null)
        {
            return;
        }

        var dialog = _dialogFactory();
        dialog.LoadForEdit(SelectedConnection);

        var saved = await ConnectionDialogRequested(dialog);
        if (saved is not null)
        {
            UpsertConnectionNode(saved);
        }
    }

    [RelayCommand]
    private void DeleteConnection()
    {
        if (SelectedConnection is null)
        {
            return;
        }

        var id = SelectedConnection.Id;
        _connections.Delete(id);
        _schemaCache.Invalidate(id);
        RemoveConnectionNode(id);
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

        var root = ConnectionNodes.FirstOrDefault(n => n.Connection.Id == SelectedConnection.Id);
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

        var root = ConnectionNodes.FirstOrDefault(n => n.Connection.Id == SelectedConnection.Id);
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

    [RelayCommand]
    private void CloseTab(DocumentViewModel? document)
    {
        if (document is null)
        {
            return;
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
        var dialect = _providers.Get(connection.ProviderId).Dialect;
        // Generated SQL opens in a free query tab with no database context, so qualify it fully — the
        // dialect decides how far (SQL Server: three-part [db].[schema].[table]; Postgres: schema.table).
        var qualified = dialect.QualifyName(node.DatabaseName, node.SchemaName, node.Name);

        try
        {
            var sql = kind switch
            {
                "Select" => $"SELECT * FROM {qualified};",
                "Count" => $"SELECT COUNT(*) FROM {qualified};",
                _ => SqlTemplateBuilder.Build(kind ?? "Select", qualified, dialect, await FetchColumnsAsync(connection, node.DatabaseName, qualified))
            };

            var document = NewDocument();
            document.InitQuery(connection);
            document.Sql = sql;
            AddDocument(document);
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

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
    private void ToggleLanguage()
    {
        var next = Loc.Culture.TwoLetterISOLanguageName == "nl"
            ? CultureInfo.GetCultureInfo("en")
            : CultureInfo.GetCultureInfo("nl");

        Loc.SetCulture(next);
    }

    private DocumentViewModel NewDocument() => new(_providers, _connections, _formatter, _history, _schemaCache, Loc);

    private void AddDocument(DocumentViewModel document)
    {
        Documents.Add(document);
        SelectedDocument = document;
    }
}
