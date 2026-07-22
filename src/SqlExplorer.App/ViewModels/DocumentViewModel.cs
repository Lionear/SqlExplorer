using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using SqlExplorer.Core.Completion;
using SqlExplorer.Core.Connections;
using SqlExplorer.Core.Editing;
using SqlExplorer.Core.Export;
using SqlExplorer.Sdk.Formatting;
using SqlExplorer.Core.History;
using SqlExplorer.Core.Localization;
using SqlExplorer.Core.Logging;
using SqlExplorer.Core.Providers;
using SqlExplorer.Core.Schema;
using SqlExplorer.Core.Settings;
using SqlExplorer.Core.Sql;
using SqlExplorer.Sdk;
using SqlExplorer.Sdk.Editing;
using SqlExplorer.Sdk.Ui;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SqlExplorer.App.ViewModels;

public enum DocumentMode
{
    Query,
    Browse,
    Monitor
}

/// <summary>One entry in the Activity Monitor's auto-refresh interval dropdown. <see cref="Seconds"/> 0
/// means "Off" (manual refresh only).</summary>
public sealed record RefreshOption(string Label, int Seconds);

/// <summary>Export text format — mirrors the three <see cref="ResultExporter"/> methods.</summary>
public enum ExportFormat
{
    Csv,
    Json,
    Sql,
    Markdown,
    Html
}

/// <summary>One column's inline browse-filter box; empty <see cref="Value"/> means "no filter".</summary>
public sealed partial class ColumnFilterEntry(string columnName) : ObservableObject
{
    public string ColumnName { get; } = columnName;

    [ObservableProperty]
    private string _value = string.Empty;
}

/// <summary>One tab in the multi-resultset strip: a display label ("Result 1 · 42 rows", "EXPLAIN")
/// plus the editable set it wraps. <see cref="IsSelected"/> is host-maintained (not part of identity)
/// so the tab strip can highlight which one the grid currently shows.</summary>
public sealed partial class ResultSetTab(string label, EditableResultSet set) : ObservableObject
{
    public string Label { get; } = label;

    public EditableResultSet Set { get; } = set;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// One editor tab. In <see cref="DocumentMode.Query"/> it is a free SQL pane; in
/// <see cref="DocumentMode.Browse"/> it pages through a single table without typing SQL
/// (Notes §5). Both modes share the editable result grid and the save-flow (Notes §8),
/// each tab bound to its own connection.
/// </summary>
public partial class DocumentViewModel : ViewModelBase
{
    private readonly IDbProviderRegistry _providers;
    private readonly ConnectionService _connections;
    private readonly ISqlFormatter _formatter;
    private readonly IQueryHistoryStore _history;
    private readonly IQueryLog _queryLog;
    private readonly ISchemaCache _schemaCache;
    private readonly IServerVersionCache _serverVersions;

    private string? _database;
    private string? _schema;
    private string _table = string.Empty;
    private int _lastRowCount;

    // Cursor paging (providers with SupportsCursorPaging, e.g. Elasticsearch beyond its 10k window):
    // instead of LIMIT/OFFSET, each page is fetched with an opaque forward cursor. _cursorStack[p] is the
    // cursor that produces page p (index 0 = null, the first page), grown as we page forward so PrevPage can
    // re-fetch an earlier page; _lastNextCursor is the token the just-loaded page handed back (null = no more).
    private bool _cursorMode;
    private readonly List<string?> _cursorStack = [null];
    private string? _lastNextCursor;

    // Browse-mode server-side sort (base column name + direction); null = unsorted.
    private string? _sortColumn;
    private bool _sortDescending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabLabel))]
    private string _title = "Query";

    [ObservableProperty]
    private string _sql = string.Empty;

    // ── Query file backing (SE-154) ──────────────────────────────────────────────────────────────────
    // A query tab may be backed by a .sql file on disk. FilePath is null for an untitled tab; IsDirty
    // tracks unsaved editor changes relative to the last open/save (independent of HasChanges, which is
    // pending grid-row edits). While loading text programmatically (open/restore/duplicate) dirtying is
    // suppressed so only genuine user edits raise the marker.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabLabel))]
    private string? _filePath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabLabel))]
    private bool _isDirty;

    private bool _loadingText;

    /// <summary>Tab-strip label: the title, prefixed with a dot when the query has unsaved edits — the
    /// SSMS/DataGrip "●" convention (SE-154).</summary>
    public string TabLabel => IsDirty ? $"● {Title}" : Title;

    // Only genuine user edits dirty the tab; programmatic loads set _loadingText first (see LoadContent).
    partial void OnSqlChanged(string value)
    {
        if (!_loadingText)
        {
            IsDirty = true;
        }
    }

    /// <summary>Load query text without dirtying the tab, optionally associating a <c>.sql</c> file (open
    /// from disk, or a restored session tab). With a file path the title becomes the file name; the tab is
    /// left clean.</summary>
    public void LoadContent(string sql, string? filePath)
    {
        _loadingText = true;
        Sql = sql;
        _loadingText = false;

        if (filePath is not null)
        {
            FilePath = filePath;
            Title = System.IO.Path.GetFileName(filePath);
        }

        IsDirty = false;
    }

    /// <summary>Record that the tab's text was just written to <paramref name="filePath"/>: adopt it as the
    /// backing file, retitle to its name, and clear the dirty marker.</summary>
    public void MarkSaved(string filePath)
    {
        FilePath = filePath;
        Title = System.IO.Path.GetFileName(filePath);
        IsDirty = false;
    }

    /// <summary>Raised after an execution completes, so the host can surface the outcome — success row
    /// counts, cancellations, and failures — in the shared Output panel. The host pops the panel open on
    /// a failure so it's never missed.</summary>
    public event Action<OutputLevel, string>? Reported;

    private void Report(OutputLevel level, string message) => Reported?.Invoke(level, message);

    private void ReportFailure(string message) => Report(OutputLevel.Error, message);

    /// <summary>Raised (connection id + new state) when running a query touches the connection, so the host
    /// can colour that connection's status dot even though the query auto-connected outside the tree's own
    /// connect flow. The handler only sets the node's State — it never reloads the tree.</summary>
    public event Action<string, ConnectionState>? ConnectionActivity;

    private void SignalConnection(ConnectionState state)
    {
        if (Connection is { } connection)
        {
            ConnectionActivity?.Invoke(connection.Id, state);
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsResultEditable))]
    [NotifyPropertyChangedFor(nameof(ReadOnlyReason))]
    private EditableResultSet? _editable;

    [ObservableProperty]
    private EditableRow? _selectedRow;

    // Multi-resultset support: a script/selection with several statements (or a driver that returns
    // several result sets for one) lands here, one tab per set. Editable always mirrors
    // ResultSets[SelectedResultSetIndex].Set — SetResult keeps doing the row/pending-state wiring it
    // always did, just called on a selection change too, not only on a fresh execute.
    public ObservableCollection<ResultSetTab> ResultSets { get; } = [];

    [ObservableProperty]
    private int _selectedResultSetIndex;

    public bool HasMultipleResultSets => ResultSets.Count > 1;

    /// <summary>Caret offset and current selection in the SQL editor, kept in sync by the view so
    /// Run/Run-at-cursor/Explain know what text to act on without reaching back into AvaloniaEdit.</summary>
    [ObservableProperty]
    private int _caretOffset;

    [ObservableProperty]
    private string _selectionText = string.Empty;

    [ObservableProperty]
    private bool _hasChanges;

    [ObservableProperty]
    private string _pendingSummary = string.Empty;

    // Browse-mode paging + filtering.
    [ObservableProperty]
    private string _filterText = string.Empty;

    /// <summary>One inline filter box per browse-grid column (rebuilt only when the column set itself
    /// changes, so typed values survive a page/sort reload). Combined with <see cref="FilterText"/> in
    /// <see cref="LoadPageAsync"/>'s WHERE clause.</summary>
    public ObservableCollection<ColumnFilterEntry> ColumnFilters { get; } = [];

    [ObservableProperty]
    private int _page;

    [ObservableProperty]
    private int _pageSize = 200;

    // Query-result paging (SE-178), read once per tab like the browse page size. When a run is a single
    // pageable SELECT, _pagedQueryBase holds it so next/prev can re-run it at another offset (_pagedQueryOrdered
    // tells the dialect whether it already has an ORDER BY). _pageQueries/_queryPageSize are the settings.
    private bool _pageQueries;
    private int _queryPageSize;
    private string? _pagedQueryBase;
    private bool _pagedQueryOrdered;

    [ObservableProperty]
    private string _rowRange = string.Empty;

    // Status-bar stats for the last run (SE-123): "6 rows · 18 ms · PostgreSQL 16.2". Set once per
    // execution; the main window's status bar binds to StatusLine on the active document.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLine))]
    private int? _lastRunRows;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLine))]
    private double? _lastRunMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLine))]
    private string? _engineLabel;

    // Stamp the status-bar stats after a run. Engine is the provider's DisplayName plus the server version
    // the host cached at connect (host-API v25) — "PostgreSQL 16.2" — falling back to the name alone when
    // no version is known.
    private void SetRunStats(int rows, double ms)
    {
        LastRunRows = rows;
        LastRunMs = ms;
        EngineLabel = _providers.TryGet(Connection.ProviderId, out var provider)
            ? ProviderLabel.Engine(provider.DisplayName, _serverVersions.Get(Connection.Id))
            : null;
    }

    /// <summary>"{rows} rows · {ms} ms · {engine}" for the status bar; empty until this tab has run
    /// something. Parts are dropped when unknown, so a fresh tab shows just the engine (or nothing).</summary>
    public string StatusLine
    {
        get
        {
            var parts = new List<string>(3);
            if (LastRunRows is { } rows)
            {
                parts.Add(Loc.Get("StatusBarRows", rows));
            }

            if (LastRunMs is { } ms)
            {
                parts.Add(Loc.Get("StatusBarMs", (int)ms));
            }

            if (!string.IsNullOrEmpty(EngineLabel))
            {
                parts.Add(EngineLabel!);
            }

            return string.Join(" · ", parts);
        }
    }

    // Query-tab connection/database switcher (DBeaver-style): query-mode only, browse tabs stay pinned
    // to their tree-node's connection/database. Backs the two toolbar ComboBoxes in DocumentView.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsResultEditable))]
    [NotifyPropertyChangedFor(nameof(ReadOnlyReason))]
    [NotifyPropertyChangedFor(nameof(TabTooltip))]
    private SavedConnection _connection = null!;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabTooltip))]
    private string? _selectedDatabase;

    /// <summary>Tab hover text: which connection and database this tab runs against.</summary>
    public string TabTooltip => Connection is { } c
        ? $"{c.Name} · {_database ?? c.Values.GetValueOrDefault("database") ?? "—"}"
        : Title;

    private readonly IAppSettingsStore _settingsStore;

    public DocumentViewModel(
        IDbProviderRegistry providers,
        ConnectionService connections,
        ISqlFormatter formatter,
        IQueryHistoryStore history,
        IQueryLog queryLog,
        ISchemaCache schemaCache,
        IServerVersionCache serverVersions,
        IAppSettingsStore settingsStore,
        ILocalizer localizer)
    {
        _providers = providers;
        _connections = connections;
        _formatter = formatter;
        _history = history;
        _queryLog = queryLog;
        _schemaCache = schemaCache;
        _serverVersions = serverVersions;
        _settingsStore = settingsStore;
        Loc = localizer;

        var settings = _settingsStore.Load();
        EditorFontSize = settings.EditorFontSize;
        EditorWordWrap = settings.EditorWordWrap;
        // Browse page size is a global preference read once per tab (like the editor font size); a changed
        // value applies to newly opened browse tabs. Guard against a zero/negative stored value.
        _pageSize = settings.BrowsePageSize > 0 ? settings.BrowsePageSize : 200;
        _pageQueries = settings.PageQueryResults;
        _queryPageSize = settings.QueryPageSize > 0 ? settings.QueryPageSize : 200;
    }

    /// <summary>SQL editor font size/word-wrap, read once from settings at document creation
    /// (Notes: no live-binding infrastructure needed for a per-tab cosmetic default like this).</summary>
    public double? EditorFontSize { get; }

    public bool EditorWordWrap { get; }

    /// <summary>Persist a live editor-zoom change as the global font size, so it survives a restart and
    /// applies to newly opened tabs.</summary>
    public void PersistEditorFontSize(double size)
    {
        var settings = _settingsStore.Load();
        settings.EditorFontSize = size;
        _settingsStore.Save(settings);
    }

    /// <summary>Quick-open-ranked completions (1.3) for the SQL text at <paramref name="caret"/>: alias
    /// columns after "alias.", tables after FROM/JOIN, a broad table+column+keyword mix elsewhere.</summary>
    public CompletionResult GetCompletions(string sql, int caret)
    {
        var dialect = _providers.Get(Connection.ProviderId).Dialect;
        var snapshot = _schemaCache.Get(Connection.Id) ?? SchemaSnapshot.Empty;
        return SqlCompletionProvider.Suggest(sql, caret, snapshot, dialect.Keywords, dialect.Functions);
    }

    public ILocalizer Loc { get; }

    /// <summary>Every saved connection, for the query-tab connection switcher. Snapshotted once at
    /// <see cref="InitQuery"/> — a connection added while the tab is open won't retroactively appear.</summary>
    public IReadOnlyList<SavedConnection> AvailableConnections { get; private set; } = [];

    /// <summary>Databases/catalogs on the current <see cref="Connection"/>, for the database switcher.
    /// Empty (and the picker hidden, see <see cref="HasDatabasePicker"/>) for engines with no database
    /// layer, e.g. SQLite.</summary>
    public ObservableCollection<string> AvailableDatabases { get; } = [];

    public bool HasDatabasePicker => AvailableDatabases.Count > 0;

    public DocumentMode Mode { get; private set; }

    public bool IsQueryMode => Mode == DocumentMode.Query;

    public bool IsBrowseMode => Mode == DocumentMode.Browse;

    public bool IsMonitorMode => Mode == DocumentMode.Monitor;

    /// <summary>Vector glyph shown in the tab-strip; matches the document mode so a query/browse/monitor
    /// tab is recognisable at a glance (SE-123, mockup icon-per-tabtype).</summary>
    public Avalonia.Media.Geometry TabIcon => Mode switch
    {
        DocumentMode.Browse => NodeIcons.TabBrowse,
        DocumentMode.Monitor => NodeIcons.TabMonitor,
        _ => NodeIcons.TabQuery
    };

    // A connection flagged read-only (safe mode) blocks the editable-grid save-flow entirely, even when
    // the result would otherwise map back to a single keyed table — this guards against accidental writes
    // (e.g. on production). Free DML typed in a query tab is out of scope for the MVP.
    public bool IsResultEditable => Connection is not { ReadOnly: true } && Editable?.IsEditable == true;

    public string? ReadOnlyReason => Connection is { ReadOnly: true }
        ? Loc["ReadOnlyConnection"]
        : Editable?.ReadOnlyReason;

    /// <summary>The Add/Delete/Save/Discard/Export edit toolbar shows for a real result set, but never in
    /// monitor mode — those are write actions, and the live sessions grid is read-only.</summary>
    public bool ShowEditToolbar => !IsMonitorMode && Editable is not null;

    /// <summary>True while a query tab is showing a single SELECT's results one page at a time (SE-178) —
    /// drives the under-grid prev/next/row-range nav.</summary>
    public bool IsQueryPaged => _pagedQueryBase is not null;

    // Enter/leave paged-query mode, notifying the derived paging state so the nav and buttons update.
    private void SetPagedQuery(string? baseSql, bool ordered)
    {
        _pagedQueryBase = baseSql;
        _pagedQueryOrdered = ordered;
        OnPropertyChanged(nameof(IsQueryPaged));
        PrevPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
    }

    public bool CanPrevPage => (IsBrowseMode || IsQueryPaged) && Page > 0;

    // Cursor providers know "is there a next page?" from the token they returned; offset providers (including
    // paged queries) infer it from a full last page (a short page means the end).
    public bool CanNextPage => (IsBrowseMode || IsQueryPaged) && PageSize > 0 &&
        (_cursorMode ? _lastNextCursor is not null : _lastRowCount == PageSize);

    /// <summary>
    /// Render the active result set as text. <paramref name="rows"/> exports just those rows
    /// (the view passes the grid's current selection); null exports every row.
    /// </summary>
    public string BuildExportText(ExportFormat format, IReadOnlyList<EditableRow>? rows = null)
    {
        if (Editable is not { } editable)
        {
            return string.Empty;
        }

        var source = rows ?? editable.Rows;
        var raw = source.Select(r => Enumerable.Range(0, editable.Columns.Count).Select(r.CurrentAt).ToArray());

        return format switch
        {
            ExportFormat.Csv => ResultExporter.ToCsv(editable.Columns, raw),
            ExportFormat.Json => ResultExporter.ToJson(editable.Columns, raw),
            ExportFormat.Sql => ResultExporter.ToSqlInserts(editable.Columns, raw, _providers.Get(Connection.ProviderId).Dialect, Connection.ProviderId, ExportTableName(editable)),
            ExportFormat.Markdown => ResultExporter.ToMarkdown(editable.Columns, raw),
            ExportFormat.Html => ResultExporter.ToHtml(editable.Columns, raw),
            _ => string.Empty
        };
    }

    /// <summary>Tab-separated text for the plain "Copy"/"Copy with headers" grid actions.</summary>
    public string BuildClipboardTsv(bool includeHeaders, IReadOnlyList<EditableRow>? rows = null)
    {
        if (Editable is not { } editable)
        {
            return string.Empty;
        }

        var source = rows ?? editable.Rows;
        var raw = source.Select(r => Enumerable.Range(0, editable.Columns.Count).Select(r.CurrentAt).ToArray());
        return ResultExporter.ToTsv(editable.Columns, raw, includeHeaders);
    }

    // A join/computed result has no single target table; fall back to a generic placeholder name
    // rather than guessing wrong — same "known limitation" spirit as CrudStatementBuilder.IsWritable.
    private string ExportTableName(EditableResultSet editable)
    {
        var dialect = _providers.Get(Connection.ProviderId).Dialect;
        var bases = editable.Columns
            .Where(c => c.BaseTable is not null)
            .Select(c => (c.BaseSchema, c.BaseTable))
            .Distinct()
            .ToList();

        if (bases.Count != 1)
        {
            return dialect.QuoteIdentifier("export");
        }

        var (schema, table) = bases[0];
        return schema is { Length: > 0 }
            ? $"{dialect.QuoteIdentifier(schema)}.{dialect.QuoteIdentifier(table!)}"
            : dialect.QuoteIdentifier(table!);
    }

    /// <summary>Set by the view so the document can show the generated SQL for review before saving.</summary>
    public Func<string, Task<bool>>? SaveReviewRequested { get; set; }

    public void InitQuery(SavedConnection connection, string? database = null)
    {
        // Mode must land before the Connection assignment: OnConnectionChanged branches on IsQueryMode
        // to decide whether to touch Title/AvailableDatabases at all (browse tabs manage their own).
        Mode = DocumentMode.Query;
        // Seed the target database (e.g. a restored session tab) before Connection triggers the database
        // refresh, which re-selects it once the list has loaded.
        _database = database;
        AvailableConnections = _connections.List();

        // Re-resolve to the AvailableConnections instance with the same id rather than using `connection`
        // as-is: it usually comes from a different ConnectionService.List() snapshot (e.g. the tree's own
        // copy), and SavedConnection's record equality is broken by its Values dictionary (two loads of
        // the same connection never compare equal). The ComboBox's SelectedItem binding needs a genuine
        // match against ItemsSource, or it silently shows nothing selected.
        Connection = AvailableConnections.FirstOrDefault(c => c.Id == connection.Id) ?? connection;
    }

    public void InitBrowse(SavedConnection connection, string? database, string? schema, string table)
    {
        Mode = DocumentMode.Browse;
        _database = database;
        _schema = schema;
        _table = table;
        _cursorMode = _providers.Get(connection.ProviderId).SupportsCursorPaging;
        // The connection-switcher ComboBox (query toolbar) two-way-binds SelectedItem to Connection. It is
        // collapsed in browse mode but still realized, so if Connection isn't among its ItemsSource the
        // ComboBox coerces SelectedItem to null and writes that null straight back into Connection — the
        // browse tab then NREs on Connection.ProviderId in LoadPageAsync. Seed AvailableConnections with the
        // exact instance we assign (same pattern InitQuery already uses) so the item matches and no null is
        // ever coerced back.
        AvailableConnections = [connection];
        Connection = connection;
        Title = table;
    }

    // ── Activity Monitor ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Which session-list column identifies a row for Kill/Cancel (provider-declared), and the
    /// session id of the monitor's own polling connection — used to leave that row visible but with
    /// Kill/Cancel disabled. Both captured at <see cref="InitMonitor"/>.</summary>
    private string _sessionIdColumn = string.Empty;

    private string? _currentSessionId;

    private bool _monitorSupportsCancel;

    private bool _monitorRefreshing;

    private DispatcherTimer? _refreshTimer;

    // The latest sessions snapshot, kept so a click-sort (and the persisted sort across auto-refreshes)
    // can re-render without another round-trip. Monitor sort is client-side and reuses the browse
    // _sortColumn/_sortDescending fields — a document is only ever one mode, never both.
    private QueryResult? _lastSessions;

    /// <summary>True when this provider offers the soft "Cancel Query…" action (Postgres/MySQL) — false on
    /// SQL Server, whose KILL is always hard. Drives the Cancel row-action's visibility.</summary>
    public bool MonitorSupportsCancel => _monitorSupportsCancel;

    /// <summary>The auto-refresh interval choices for the monitor toolbar. Off = manual refresh only.</summary>
    public IReadOnlyList<RefreshOption> RefreshOptions { get; private set; } = [];

    [ObservableProperty]
    private RefreshOption? _selectedRefreshOption;

    public void InitMonitor(SavedConnection connection)
    {
        Mode = DocumentMode.Monitor;
        // Same "seed AvailableConnections with the exact instance" guard as InitBrowse: the query
        // connection-switcher combo is collapsed here but still realized, and would otherwise coerce
        // Connection to null.
        AvailableConnections = [connection];
        Connection = connection;
        Title = Loc.Get("ActivityMonitorTab", connection.Name);

        var provider = _providers.Get(connection.ProviderId);
        _sessionIdColumn = provider.SessionIdColumn;
        _monitorSupportsCancel = provider.SupportsCancelQuery;
        OnPropertyChanged(nameof(MonitorSupportsCancel));

        RefreshOptions =
        [
            new RefreshOption(Loc["RefreshOff"], 0),
            new RefreshOption("5s", 5),
            new RefreshOption("10s", 10),
            new RefreshOption("30s", 30)
        ];
        OnPropertyChanged(nameof(RefreshOptions));
        // Rick's decision #2: auto-refresh on by default at 5s. Setting the property starts the timer.
        SelectedRefreshOption = RefreshOptions[1];
    }

    /// <summary>True when this is the monitor tab for the given connection (avoids duplicate tabs).</summary>
    public bool MatchesMonitor(string connectionId) => IsMonitorMode && Connection.Id == connectionId;

    // Reconfigure the auto-refresh timer whenever the interval changes: Off stops it, any interval
    // (re)starts it. Never touched outside monitor mode (the property is only ever set in InitMonitor).
    partial void OnSelectedRefreshOptionChanged(RefreshOption? value)
    {
        if (!IsMonitorMode)
        {
            return;
        }

        _refreshTimer ??= new DispatcherTimer();
        _refreshTimer.Stop();
        _refreshTimer.Tick -= OnRefreshTick;

        if (value is { Seconds: > 0 } option)
        {
            _refreshTimer.Interval = TimeSpan.FromSeconds(option.Seconds);
            _refreshTimer.Tick += OnRefreshTick;
            _refreshTimer.Start();
        }

        // Load immediately on the first configuration (tab open) and on every interval change, so the
        // grid never sits empty waiting for the first tick.
        _ = RefreshMonitorAsync();
    }

    private void OnRefreshTick(object? sender, EventArgs e) => _ = RefreshMonitorAsync();

    [RelayCommand]
    private Task RefreshMonitor() => RefreshMonitorAsync();

    // One monitor refresh: pull the live sessions as an ordinary result set (rendered by the shared grid)
    // and remember which row is our own connection. Guarded against overlap so a slow query can't stack up
    // behind the timer. Auto-refresh stays silent in the Output panel (a report every 5s would spam it);
    // only genuine failures surface.
    private async Task RefreshMonitorAsync(CancellationToken ct = default)
    {
        if (_monitorRefreshing)
        {
            return;
        }

        _monitorRefreshing = true;
        try
        {
            var provider = _providers.Get(Connection.ProviderId);
            var profile = _connections.Resolve(Connection, _database);
            var snapshot = await provider.GetActiveSessionsAsync(profile, ct);
            _currentSessionId = snapshot.CurrentSessionId;
            _lastRowCount = snapshot.Sessions.Rows.Count;
            _lastSessions = snapshot.Sessions;
            RenderSessions();
        }
        catch (OperationCanceledException)
        {
            // A newer refresh (or a close) superseded this one — nothing to report.
        }
        catch (Exception ex)
        {
            ReportFailure(ex.Message);
        }
        finally
        {
            _monitorRefreshing = false;
        }
    }

    // Re-render the current sessions, applying the active client-side sort. Called after every refresh
    // (so the sort persists across auto-refreshes) and on a header click (so it re-sorts with no round-trip).
    private void RenderSessions()
    {
        if (_lastSessions is not { } sessions)
        {
            return;
        }

        var sorted = new QueryResult
        {
            Columns = sessions.Columns,
            Rows = SortSessionRows(sessions),
            RecordsAffected = sessions.RecordsAffected,
            Elapsed = sessions.Elapsed
        };
        SetResultSets([new ResultSetTab("Sessions", EditableResultSet.From(sorted))]);
    }

    private IReadOnlyList<object?[]> SortSessionRows(QueryResult sessions)
    {
        if (_sortColumn is null)
        {
            return sessions.Rows;
        }

        var index = -1;
        for (var i = 0; i < sessions.Columns.Count; i++)
        {
            if (sessions.Columns[i].Name == _sortColumn)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return sessions.Rows;
        }

        var ordered = sessions.Rows.OrderBy(row => row[index], MonitorCellComparer.Instance).ToList();
        if (_sortDescending)
        {
            ordered.Reverse();
        }

        return ordered;
    }

    /// <summary>Cycle the client-side monitor sort on a column (unsorted → asc → desc → unsorted) and
    /// re-render in place — the session list is already materialised, so no query runs.</summary>
    public void SortMonitorBy(string column)
    {
        if (!IsMonitorMode)
        {
            return;
        }

        if (_sortColumn != column)
        {
            _sortColumn = column;
            _sortDescending = false;
        }
        else if (!_sortDescending)
        {
            _sortDescending = true;
        }
        else
        {
            _sortColumn = null;
            _sortDescending = false;
        }

        RenderSessions();
    }

    // Sort session cells numerically when both parse as numbers (session_id/pid/cpu_time), else as text;
    // nulls sort last. Ascending order; the caller reverses for descending.
    private sealed class MonitorCellComparer : IComparer<object?>
    {
        public static readonly MonitorCellComparer Instance = new();

        public int Compare(object? x, object? y)
        {
            if (x is null or DBNull)
            {
                return y is null or DBNull ? 0 : 1;
            }

            if (y is null or DBNull)
            {
                return -1;
            }

            var sx = Convert.ToString(x, CultureInfo.InvariantCulture) ?? string.Empty;
            var sy = Convert.ToString(y, CultureInfo.InvariantCulture) ?? string.Empty;
            if (decimal.TryParse(sx, NumberStyles.Any, CultureInfo.InvariantCulture, out var nx)
                && decimal.TryParse(sy, NumberStyles.Any, CultureInfo.InvariantCulture, out var ny))
            {
                return nx.CompareTo(ny);
            }

            return string.Compare(sx, sy, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>The session id of <paramref name="row"/> (the provider-declared id column), or null when the
    /// column can't be resolved — used by the view for the Kill/Cancel row actions.</summary>
    public string? SessionIdOf(EditableRow row)
    {
        if (Editable is not { } editable || _sessionIdColumn.Length == 0)
        {
            return null;
        }

        for (var i = 0; i < editable.Columns.Count; i++)
        {
            if (editable.Columns[i].Name == _sessionIdColumn)
            {
                return Convert.ToString(row.CurrentAt(i), CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    /// <summary>True when <paramref name="row"/> is the monitor's own polling connection — Kill/Cancel are
    /// disabled on it so you can't shoot down the tab that's doing the polling.</summary>
    public bool IsOwnSession(EditableRow row) =>
        _currentSessionId is { } current && SessionIdOf(row) == current;

    /// <summary>Hard kill (close the whole connection) of the session under <paramref name="row"/>, after a
    /// destructive confirm. No-op on the monitor's own row.</summary>
    public Task KillSessionAsync(EditableRow row) => ActOnSessionAsync(row, hard: true);

    /// <summary>Soft cancel (abort just the running statement) of the session under <paramref name="row"/>,
    /// after a confirm. Only meaningful when <see cref="MonitorSupportsCancel"/> is true.</summary>
    public Task CancelQueryAsync(EditableRow row) => ActOnSessionAsync(row, hard: false);

    /// <summary>Set by the view so a destructive Kill/Cancel can confirm first (title, message → proceed?).</summary>
    public Func<string, string, Task<bool>>? ConfirmRequested { get; set; }

    private async Task ActOnSessionAsync(EditableRow row, bool hard)
    {
        if (!IsMonitorMode || IsOwnSession(row) || SessionIdOf(row) is not { Length: > 0 } id)
        {
            return;
        }

        if (!hard && !_monitorSupportsCancel)
        {
            return;
        }

        var title = hard ? Loc["ConfirmKillSessionTitle"] : Loc["ConfirmCancelQueryTitle"];
        var message = Loc.Get(hard ? "ConfirmKillSession" : "ConfirmCancelQuery", id);
        if (ConfirmRequested is not null && !await ConfirmRequested(title, message))
        {
            return;
        }

        try
        {
            var provider = _providers.Get(Connection.ProviderId);
            var profile = _connections.Resolve(Connection, _database);
            if (hard)
            {
                await provider.KillSessionAsync(profile, id, CancellationToken.None);
                Report(OutputLevel.Info, Loc.Get("SessionKilled", id));
            }
            else
            {
                await provider.CancelQueryAsync(profile, id, CancellationToken.None);
                Report(OutputLevel.Info, Loc.Get("QueryCancelledSession", id));
            }

            await RefreshMonitorAsync();
        }
        catch (Exception ex)
        {
            ReportFailure(ex.Message);
        }
    }

    // ── Cell actions (provider-owned dialogs on a recognised cell) ───────────────────────────────────

    /// <summary>Set by the view so a cell action can show its provider-owned dialog in the shared chrome.</summary>
    public Func<NodeInfoDialogViewModel, Task>? CellActionRequested { get; set; }

    /// <summary>True when the provider recognises the cell at <paramref name="columnIndex"/> in
    /// <paramref name="row"/> as actionable (e.g. MSSQL's blocking_session_id &gt; 0) — the view renders it
    /// as a link. Cheap and side-effect-free; called per cell while building the grid.</summary>
    // Cheap, value-independent check the grid makes ONCE per column when building it: a column that can
    // never carry an action gets a plain text cell template with no per-cell provider call, keeping the
    // scroll path fast. Only columns that pass here fall through to the per-cell HasCellAction below.
    public bool ColumnMayHaveCellActions(string columnName) =>
        _providers.Get(Connection.ProviderId) is ICustomCellActionUi ui && ui.ColumnMayHaveCellActions(columnName);

    public bool HasCellAction(int columnIndex, EditableRow row)
    {
        if (_providers.Get(Connection.ProviderId) is not ICustomCellActionUi ui
            || BuildCellActionContext(columnIndex, row) is not { } context)
        {
            return false;
        }

        try
        {
            return ui.HasCellAction(context);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Open the provider-owned dialog for the cell at <paramref name="columnIndex"/> in
    /// <paramref name="row"/>. The dialog queries (and may act on) its own live data; when it closes, a
    /// monitor tab refreshes so a kill done inside it is reflected.</summary>
    public async Task OpenCellActionAsync(int columnIndex, EditableRow row)
    {
        if (_providers.Get(Connection.ProviderId) is not ICustomCellActionUi ui
            || BuildCellActionContext(columnIndex, row) is not { } context
            || CellActionRequested is null)
        {
            return;
        }

        try
        {
            if (!ui.HasCellAction(context))
            {
                return;
            }

            var view = ui.CreateCellActionView(context);
            await CellActionRequested(new NodeInfoDialogViewModel(ui.CellActionTitle(context), view, Loc));
        }
        catch (Exception ex)
        {
            ReportFailure(ex.Message);
        }

        if (IsMonitorMode)
        {
            await RefreshMonitorAsync();
        }
    }

    private CellActionContext? BuildCellActionContext(int columnIndex, EditableRow row)
    {
        if (Editable is not { } editable || columnIndex < 0 || columnIndex >= editable.Columns.Count)
        {
            return null;
        }

        var values = new Dictionary<string, object?>(editable.Columns.Count);
        for (var i = 0; i < editable.Columns.Count; i++)
        {
            values[editable.Columns[i].Name] = row.CurrentAt(i);
        }

        var provider = _providers.Get(Connection.ProviderId);
        var profile = _connections.Resolve(Connection, _database);
        return new CellActionContext(profile, provider, editable.Columns[columnIndex].Name, row.CurrentAt(columnIndex), values);
    }

    /// <summary>Stop and release the auto-refresh timer — called when the monitor tab closes so it doesn't
    /// keep polling in the background.</summary>
    public void StopMonitor()
    {
        if (_refreshTimer is not null)
        {
            _refreshTimer.Stop();
            _refreshTimer.Tick -= OnRefreshTick;
            _refreshTimer = null;
        }
    }

    // Query-tab connection switch (InitQuery counts as the first one): refresh the tab title and the
    // database list for the newly-selected connection. Browse tabs are pinned to their tree-node's
    // connection/database and never touch either — this is a no-op there.
    partial void OnConnectionChanged(SavedConnection value)
    {
        if (!IsQueryMode)
        {
            return;
        }

        // A file-backed tab keeps its file name as the title across a connection switch (SE-154); only an
        // untitled query tab tracks the connection name.
        if (FilePath is null)
        {
            Title = $"{Loc["QueryTab"]} · {value.Name}";
        }

        _ = RefreshDatabasesAsync(value);
    }

    // The database dropdown writes straight through to the same _database field ExecuteAsync/SaveAsync
    // already resolve with — browse tabs set it once via InitBrowse and never touch this property.
    partial void OnSelectedDatabaseChanged(string? value) => _database = value;

    private async Task RefreshDatabasesAsync(SavedConnection connection)
    {
        // Preserve the intended database (a restored/current tab's) across the reset below — clearing
        // SelectedDatabase writes null back through to _database.
        var target = _database;

        AvailableDatabases.Clear();
        SelectedDatabase = null;
        OnPropertyChanged(nameof(HasDatabasePicker));

        try
        {
            var provider = _providers.Get(connection.ProviderId);
            var profile = _connections.Resolve(connection);
            var databases = await provider.GetDatabasesAsync(profile, CancellationToken.None);

            // A later connection switch may have raced ahead of this one — don't let a stale response
            // repopulate the picker for a connection that's no longer selected.
            if (connection != Connection)
            {
                return;
            }

            foreach (var database in databases)
            {
                AvailableDatabases.Add(database);
            }

            // Show the database this tab actually runs against instead of a blank picker: the preserved
            // target if set, otherwise the connection's configured default (e.g. "master"/"postgres").
            // Include it in the list even when the engine's database query omits it (SQL Server hides the
            // system databases), so the picker can always reflect and re-select the current database.
            var current = target ?? connection.Values.GetValueOrDefault("database");
            if (current is { Length: > 0 } db)
            {
                if (!AvailableDatabases.Contains(db))
                {
                    AvailableDatabases.Insert(0, db);
                }

                SelectedDatabase = db;
            }

            OnPropertyChanged(nameof(HasDatabasePicker));
        }
        catch
        {
            // Best-effort: the picker just stays empty/hidden for this connection.
        }
    }

    /// <summary>True when this is the browse tab for the given connection + table (avoids duplicate tabs).
    /// Database is part of the identity so same-named tables in different databases open as separate tabs.</summary>
    public bool MatchesBrowse(string connectionId, string? database, string? schema, string table) =>
        IsBrowseMode && Connection.Id == connectionId && _database == database && _schema == schema && _table == table;

    /// <summary>Current browse sort column (base name), or null when unsorted.</summary>
    public string? SortColumn => _sortColumn;

    public bool SortDescending => _sortDescending;

    /// <summary>
    /// Cycle the browse sort on a column: unsorted → ascending → descending → unsorted. Re-runs from
    /// page 0 with a server-side ORDER BY so the ordering spans the whole table, not just this page.
    /// </summary>
    public async Task SortByAsync(string baseColumn, CancellationToken ct = default)
    {
        if (!IsBrowseMode)
        {
            return;
        }

        if (_sortColumn != baseColumn)
        {
            _sortColumn = baseColumn;
            _sortDescending = false;
        }
        else if (!_sortDescending)
        {
            _sortDescending = true;
        }
        else
        {
            _sortColumn = null;
            _sortDescending = false;
        }

        Page = 0;
        await LoadPageAsync(ct);
    }

    /// <summary>Load the current browse page (also the initial load right after <see cref="InitBrowse"/>).</summary>
    public async Task LoadPageAsync(CancellationToken ct = default)
    {
        // Building the paged query (dialect/quoting/filters) runs before ExecuteAsync's own try/catch, so
        // an unexpected failure here — a provider without a dialect, a null field, a cancelled token — used
        // to escape unguarded and take the whole app down (it surfaces as an unhandled NRE from the browse
        // command). Wrap the entire load so a browse failure degrades to an Output-panel error, exactly like
        // a failed query does, and the real cause is visible instead of a crash.
        try
        {
            var dialect = _providers.Get(Connection.ProviderId).Dialect;
            var qualified = _schema is { } schema
                ? $"{dialect.QuoteIdentifier(schema)}.{dialect.QuoteIdentifier(_table)}"
                : dialect.QuoteIdentifier(_table);

            var conditions = new List<string>();
            if (!string.IsNullOrWhiteSpace(FilterText))
            {
                conditions.Add(FilterText);
            }

            foreach (var filter in ColumnFilters)
            {
                if (!string.IsNullOrWhiteSpace(filter.Value))
                {
                    // Cast to text first: LIKE against a non-text column (uuid, int, bit, timestamp, …)
                    // fails outright on strict-typed engines like Postgres ("operator does not exist").
                    var column = TextCast(Connection.ProviderId, dialect.QuoteIdentifier(filter.ColumnName));
                    conditions.Add($"{column} LIKE '%{SqlLiteralFormatter.EscapeString(filter.Value)}%'");
                }
            }

            var where = conditions.Count == 0 ? string.Empty : $" WHERE {string.Join(" AND ", conditions)}";
            var orderBy = _sortColumn is null
                ? null
                : $"{dialect.QuoteIdentifier(_sortColumn)} {(_sortDescending ? "DESC" : "ASC")}";
            var baseSql = $"SELECT * FROM {qualified}{where}";
            if (_cursorMode)
            {
                // Page 0 is always a fresh browse (InitBrowse / ApplyFilter / SortBy all reset Page to 0):
                // restart the cursor chain so a new filter/sort re-opens the provider's point-in-time.
                if (Page == 0)
                {
                    _cursorStack.Clear();
                    _cursorStack.Add(null);
                }

                var cursor = Page < _cursorStack.Count ? _cursorStack[Page] : null;
                var order = orderBy is null ? string.Empty : $" ORDER BY {orderBy}";
                var cursorSql = baseSql + order;
                await ExecuteAsync(cursorSql, ct,
                    (profile, token) => _providers.Get(Connection.ProviderId)
                        .ExecuteCursorPageAsync(profile, cursorSql, PageSize, cursor, token));

                // Record the token that reaches the next page, so NextPage/PrevPage can navigate the chain.
                if (_lastNextCursor is not null)
                {
                    if (Page + 1 < _cursorStack.Count) _cursorStack[Page + 1] = _lastNextCursor;
                    else _cursorStack.Add(_lastNextCursor);
                }
            }
            else
            {
                var paged = dialect.Paginate(baseSql, PageSize, Page * PageSize, orderBy);
                await ExecuteAsync(paged, ct);
            }

            SyncColumnFilters();

            var offset = Page * PageSize;
            RowRange = _lastRowCount == 0
                ? Loc.Get("RowRangeEmpty")
                : Loc.Get("RowRange", offset + 1, offset + _lastRowCount);
            PrevPageCommand.NotifyCanExecuteChanged();
            NextPageCommand.NotifyCanExecuteChanged();
        }
        catch (OperationCanceledException)
        {
            // A newer browse/reload superseded this one — nothing to report.
        }
        catch (Exception ex)
        {
            ReportFailure(ex.Message);
        }
    }

    // Universal-ish text cast for the per-column filter's LIKE — CAST target names aren't portable:
    // MySQL's CAST has no VARCHAR target (only CHAR), and SQL Server's CAST(x AS VARCHAR) silently
    // truncates to 30 chars without an explicit length. Postgres/SQLite both accept VARCHAR fine
    // (SQLite infers TEXT affinity from any type name containing "CHAR").
    private static string TextCast(string providerId, string quotedColumn) => providerId switch
    {
        "sqlserver" => $"CAST({quotedColumn} AS NVARCHAR(MAX))",
        "mysql" => $"CAST({quotedColumn} AS CHAR)",
        _ => $"CAST({quotedColumn} AS VARCHAR)"
    };

    // Rebuild the per-column filter boxes only when the column set actually changed (a different
    // table) — reloading the same table (page nav, sort, Apply) must not wipe what the user typed.
    private void SyncColumnFilters()
    {
        if (Editable is not { } editable)
        {
            return;
        }

        var baseColumns = editable.Columns.Where(c => c.BaseColumn is not null).Select(c => c.BaseColumn!).Distinct().ToList();
        if (ColumnFilters.Select(f => f.ColumnName).SequenceEqual(baseColumns))
        {
            return;
        }

        ColumnFilters.Clear();
        foreach (var column in baseColumns)
        {
            ColumnFilters.Add(new ColumnFilterEntry(column));
        }
    }

    // True while a query/script/browse load is in flight; drives the Stop button and its guard.
    [ObservableProperty]
    private bool _isRunning;

    // The active run's cancellation source, so the Stop button can cancel it (null when idle).
    private CancellationTokenSource? _runCts;

    [RelayCommand]
    private void Stop() => _runCts?.Cancel();

    // Wrap a run with cancellation + the IsRunning flag: a linked source lets Stop cancel it, and the
    // token is threaded down to the provider (ADO.NET honours it), so long queries can be aborted.
    private async Task RunTracked(CancellationToken outerCt, Func<CancellationToken, Task> body)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        // Optional global query timeout (Settings › Query): 0 = no limit. CancelAfter on the linked source
        // aborts the provider call (ADO.NET honours the token) — the same path the Stop button uses.
        if (_settingsStore.Load().QueryTimeoutSeconds is > 0 and var timeout)
        {
            cts.CancelAfter(TimeSpan.FromSeconds(timeout));
        }
        _runCts = cts;
        IsRunning = true;
        try
        {
            await body(cts.Token);
        }
        finally
        {
            IsRunning = false;
            _runCts = null;
        }
    }

    [RelayCommand]
    private async Task RunAsync(CancellationToken ct) =>
        await RunScriptAsync(SelectionText.Length > 0 ? SelectionText : Sql, ct);

    [RelayCommand]
    private async Task RunAtCursorAsync(CancellationToken ct)
    {
        var text = SelectionText.Length > 0 ? SelectionText : SqlStatementSplitter.StatementAtCursor(Sql, CaretOffset);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        await RunScriptAsync(text, ct);
    }

    [RelayCommand]
    private async Task ExplainAsync(CancellationToken ct)
    {
        var text = SelectionText.Length > 0 ? SelectionText : SqlStatementSplitter.StatementAtCursor(Sql, CaretOffset);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        SetPagedQuery(null, false); // an EXPLAIN result isn't pageable — drop any prior paged-query bar

        await RunTracked(ct, async token =>
        {
            try
            {
                var profile = _connections.Resolve(Connection, _database);
                var result = await _providers.Get(Connection.ProviderId).ExplainAsync(profile, text, token);
                SetResultSets([new ResultSetTab("EXPLAIN", EditableResultSet.From(result))]);
                Report(OutputLevel.Info, Loc.Get("StatusRows", result.Rows.Count, result.Elapsed.TotalMilliseconds));
            }
            catch (OperationCanceledException)
            {
                Report(OutputLevel.Info, Loc["QueryCancelled"]);
            }
            catch (Exception ex)
            {
                ReportFailure(ex.Message);
            }
        });
    }

    // Query-mode "Run"/"Run at cursor": the text may be one statement or a whole script, and the driver
    // may return zero, one, or several result sets — the host never needs to know which up front.
    // GO is not real T-SQL (a client-side batch separator only), so on SQL Server the text is split into
    // GO-batches first and each is executed separately; every other engine sends the text through as-is.
    // "Run"/"Run at cursor": a single unbounded SELECT is shown paged (SE-178) — first page now, next/prev to
    // walk the rest; anything else (a script, a non-SELECT, an already-bounded/limited query) runs as-is through
    // the multi-result-set path below.
    private Task RunScriptAsync(string sql, CancellationToken ct)
    {
        if (_pageQueries && _queryPageSize > 0 && QueryPaging.TryGetPageableSelect(sql, out var statement, out var ordered))
        {
            SetPagedQuery(statement, ordered);
            PageSize = _queryPageSize;
            Page = 0;
            return LoadQueryPageAsync(ct, announce: true);
        }

        SetPagedQuery(null, false);
        return RunScriptCoreAsync(sql, ct);
    }

    private Task RunScriptCoreAsync(string sql, CancellationToken ct) => RunTracked(ct, async token =>
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var profile = _connections.Resolve(Connection, _database);
            var provider = _providers.Get(Connection.ProviderId);

            var batches = Connection.ProviderId == "sqlserver"
                ? SqlStatementSplitter.SplitGoBatches(sql)
                : [sql];

            // A script can't be paged — its statements aren't all result sets, and one prev/next bar can't
            // drive several of them — but "SELECT * FROM a; SELECT * FROM b;" shouldn't pull both tables in
            // full either. Each unbounded SELECT is bounded to one page's worth server-side; everything else
            // runs as written. Governed by the same setting as query paging, and reported below, because a
            // silently shortened result set is worse than a slow one.
            var capLimit = _pageQueries ? _queryPageSize : 0;
            var cappedStatements = 0;
            if (capLimit > 0)
            {
                batches = [.. batches.Select(b =>
                {
                    var text = QueryPaging.CapPageableStatements(b, provider.Dialect, capLimit, out var capped);
                    cappedStatements += capped;
                    return text;
                })];
            }

            var results = new List<QueryResult>();
            foreach (var batch in batches)
            {
                results.AddRange(await provider.ExecuteScriptAsync(profile, batch, token));
            }

            stopwatch.Stop();
            // The run auto-connected: light the connection's status dot in the tree.
            SignalConnection(ConnectionState.Connected);
            var totalRows = results.Sum(r => r.Rows.Count);
            SetResultSets(BuildResultTabs(results, cappedStatements > 0 ? capLimit : 0));
            Report(OutputLevel.Info, DescribeOutcome(results, stopwatch.Elapsed.TotalMilliseconds));
            if (cappedStatements > 0 && results.Any(r => r.Rows.Count >= capLimit))
            {
                Report(OutputLevel.Info, Loc.Get("StatusScriptCapped", capLimit));
            }
            SetRunStats(totalRows, stopwatch.Elapsed.TotalMilliseconds);
            if (IsQueryMode)
            {
                AppendHistory(sql, QueryHistoryKind.Query, stopwatch.ElapsedMilliseconds, totalRows, success: true, error: null);
            }
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            Report(OutputLevel.Info, Loc["QueryCancelled"]);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            SignalConnection(ConnectionState.Error);
            ReportFailure(ex.Message);
            if (IsQueryMode)
            {
                AppendHistory(sql, QueryHistoryKind.Query, stopwatch.ElapsedMilliseconds, 0, success: false, error: ex.Message);
            }
        }
    });

    // capLimit > 0 means the script's SELECTs were bounded to that many rows: a result set that came back
    // exactly full is the one that was cut off, and its tab says "first N" rather than claiming N is all there is.
    private static List<ResultSetTab> BuildResultTabs(IReadOnlyList<QueryResult> results, int capLimit = 0) =>
        results.Count <= 1
            ? [new ResultSetTab("Result", EditableResultSet.From(results.Count == 1 ? results[0] : EmptyResult()))]
            : results.Select((r, i) => new ResultSetTab(
                capLimit > 0 && r.Rows.Count >= capLimit
                    ? $"Result {i + 1} · first {r.Rows.Count} rows"
                    : $"Result {i + 1} · {r.Rows.Count} rows",
                EditableResultSet.From(r))).ToList();

    private static QueryResult EmptyResult() => new() { Columns = [], Rows = [], RecordsAffected = 0, Elapsed = TimeSpan.Zero };

    // Outcome line for the Output panel: a result-returning statement reports its row count, a non-SELECT
    // (INSERT/UPDATE/DELETE — no columns) reports affected rows instead of a misleading "0 rows".
    private string DescribeOutcome(IReadOnlyList<QueryResult> results, double elapsedMs) =>
        results.Any(r => r.Columns.Count > 0)
            ? Loc.Get("StatusRows", results.Sum(r => r.Rows.Count), elapsedMs)
            : Loc.Get("StatusAffected", Math.Max(0, results.Sum(r => r.RecordsAffected)), elapsedMs);

    [RelayCommand(CanExecute = nameof(CanPrevPage))]
    private async Task PrevPageAsync(CancellationToken ct)
    {
        if (Page == 0)
        {
            return;
        }

        Page--;
        await ReloadPageAsync(ct);
    }

    [RelayCommand(CanExecute = nameof(CanNextPage))]
    private async Task NextPageAsync(CancellationToken ct)
    {
        Page++;
        await ReloadPageAsync(ct);
    }

    // A page-flip reloads the browse table or re-runs the paged query at the new offset (never announcing).
    private Task ReloadPageAsync(CancellationToken ct) =>
        IsQueryPaged ? LoadQueryPageAsync(ct, announce: false) : LoadPageAsync(ct);

    [RelayCommand]
    private async Task ApplyFilterAsync(CancellationToken ct)
    {
        Page = 0;
        await LoadPageAsync(ct);
    }

    [RelayCommand]
    private void Format()
    {
        // The provider may ship its own dialect-specialised formatter (SE-148); fall back to the host's
        // generic one (_formatter) when it doesn't. Options come from Settings (casing + indent width).
        var provider = _providers.Get(Connection.ProviderId);
        var formatter = provider.Formatter ?? _formatter;
        var settings = _settingsStore.Load();
        var options = new SqlFormatOptions
        {
            KeywordCasing = settings.FormatKeywordCasing,
            IndentSize = settings.FormatIndentSize > 0 ? settings.FormatIndentSize : SqlFormatOptions.Default.IndentSize
        };
        Sql = formatter.Format(Sql, provider.Dialect, options);
    }

    // Shared execution path for both a typed query and a browse page. Only typed queries are logged to
    // history — browse paging (same path, IsBrowseMode) would just clutter it.
    // A non-null runner overrides the default ExecuteQueryAsync call (cursor-paged browse fetches one page
    // via ExecuteCursorPageAsync) while reusing all the result-population, output, and error handling here.
    // Shared single-result-set path for browse paging and query-result paging. <paramref name="announce"/>
    // reports to the Output panel + query history — true for a fresh query run, false for a page-flip (browse or
    // query), so flipping pages never spams the log (the RowRange bar already shows the count).
    private Task ExecuteAsync(string sql, CancellationToken ct,
        Func<ConnectionProfile, CancellationToken, Task<QueryResult>>? runner = null, bool announce = false,
        string? historySql = null) => RunTracked(ct, async token =>
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var profile = _connections.Resolve(Connection, _database);
            var result = runner is null
                ? await _providers.Get(Connection.ProviderId).ExecuteQueryAsync(profile, sql, token)
                : await runner(profile, token);
            stopwatch.Stop();
            // The query auto-connected: reflect that on the connection's status dot in the tree.
            SignalConnection(ConnectionState.Connected);
            _lastRowCount = result.Rows.Count;
            _lastNextCursor = result.NextCursor;
            SetResultSets([new ResultSetTab("Result", EditableResultSet.From(result))]);
            if (announce)
            {
                Report(OutputLevel.Info, DescribeOutcome([result], result.Elapsed.TotalMilliseconds));
                AppendHistory(historySql ?? sql, QueryHistoryKind.Query, stopwatch.ElapsedMilliseconds, result.Rows.Count, success: true, error: null);
            }

            // Browse/query page-flips update the status bar too — it's a stat, not a log line.
            SetRunStats(result.Rows.Count, result.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            if (announce)
            {
                Report(OutputLevel.Info, Loc["QueryCancelled"]);
            }
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            SignalConnection(ConnectionState.Error);
            ReportFailure(ex.Message);
            if (announce)
            {
                AppendHistory(historySql ?? sql, QueryHistoryKind.Query, stopwatch.ElapsedMilliseconds, 0, success: false, error: ex.Message);
            }
        }
    });

    // Run one page of the active paged query (SE-178) through the shared ExecuteAsync path and refresh the
    // prev/next/row-range bar. announce is true only for the initial run, not for a page-flip.
    private async Task LoadQueryPageAsync(CancellationToken ct, bool announce)
    {
        if (_pagedQueryBase is not { } baseSql)
        {
            return;
        }

        var dialect = _providers.Get(Connection.ProviderId).Dialect;
        var paged = dialect.PageQuery(baseSql, PageSize, Page * PageSize, _pagedQueryOrdered);
        await ExecuteAsync(paged, ct, announce: announce, historySql: baseSql);

        var offset = Page * PageSize;
        RowRange = _lastRowCount == 0
            ? Loc.Get("RowRangeEmpty")
            : Loc.Get("RowRange", offset + 1, offset + _lastRowCount);
        PrevPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
    }

    private void AppendHistory(string sql, QueryHistoryKind kind, long durationMs, int rowCount, bool success, string? error)
    {
        var entry = new QueryHistoryEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            TimestampUtc = DateTime.UtcNow,
            ConnectionId = Connection.Id,
            ConnectionName = Connection.Name,
            Kind = kind,
            Sql = sql,
            DurationMs = durationMs,
            RowCount = rowCount,
            Success = success,
            Error = error
        };
        _history.Append(entry);
        _queryLog.Record(entry); // No-op unless logging + the application source are enabled.
    }

    /// <summary>Render a cell value for the double-click value window: NULL for null, pretty-printed JSON when
    /// the text parses as an object/array, otherwise the raw string.</summary>
    public static string FormatCellValue(object? value)
    {
        if (value is null or DBNull)
        {
            return "NULL";
        }

        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                return JsonSerializer.Serialize(doc.RootElement, PrettyJson);
            }
            catch (JsonException)
            {
                // not actually JSON — fall through to raw text
            }
        }

        return text;
    }

    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    // Replace the whole result-set list (fresh execute) and select the first tab. Comes after every Run/
    // Run-at-cursor/Explain/browse-page-load — never appended to, always a full swap.
    private void SetResultSets(IReadOnlyList<ResultSetTab> tabs)
    {
        ResultSets.Clear();
        foreach (var tab in tabs)
        {
            ResultSets.Add(tab);
        }

        OnPropertyChanged(nameof(HasMultipleResultSets));

        if (SelectedResultSetIndex == 0)
        {
            // The setter no-ops when the value doesn't change, so the very common "still on tab 0" case
            // needs an explicit SetResult call — OnSelectedResultSetIndexChanged won't fire for it.
            MarkSelected(0);
            SetResult(tabs.Count > 0 ? tabs[0].Set : null);
        }
        else
        {
            SelectedResultSetIndex = 0;
        }
    }

    partial void OnSelectedResultSetIndexChanged(int value)
    {
        MarkSelected(value);
        if (value >= 0 && value < ResultSets.Count)
        {
            SetResult(ResultSets[value].Set);
        }
    }

    // Drives the tab strip's highlight — the Button in DocumentView.axaml binds Classes.Accent to IsSelected.
    private void MarkSelected(int index)
    {
        for (var i = 0; i < ResultSets.Count; i++)
        {
            ResultSets[i].IsSelected = i == index;
        }
    }

    [RelayCommand]
    private void SelectResultSet(ResultSetTab tab)
    {
        var index = ResultSets.IndexOf(tab);
        if (index >= 0)
        {
            SelectedResultSetIndex = index;
        }
    }

    private void SetResult(EditableResultSet? editable)
    {
        if (Editable is not null)
        {
            Editable.Rows.CollectionChanged -= OnRowsChanged;
            foreach (var row in Editable.Rows)
            {
                row.PropertyChanged -= OnRowChanged;
            }
        }

        SelectedRow = null;
        Editable = editable;
        if (editable is not null)
        {
            editable.Rows.CollectionChanged += OnRowsChanged;
            foreach (var row in editable.Rows)
            {
                row.PropertyChanged += OnRowChanged;
            }
        }

        RefreshPending();
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var row in e.OldItems?.OfType<EditableRow>() ?? [])
        {
            row.PropertyChanged -= OnRowChanged;
        }

        foreach (var row in e.NewItems?.OfType<EditableRow>() ?? [])
        {
            row.PropertyChanged += OnRowChanged;
        }

        RefreshPending();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e) => RefreshPending();

    private void RefreshPending()
    {
        var counts = Editable?.Pending ?? default;
        HasChanges = counts.Total > 0;
        PendingSummary = counts.Total == 0
            ? string.Empty
            : Loc.Get("PendingSummary", counts.Modified, counts.Added, counts.Deleted);
        SaveCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(IsResultEditable))]
    private void AddRow()
    {
        var row = Editable?.AddRow();
        if (row is not null)
        {
            SelectedRow = row;
        }
    }

    private bool CanDeleteRow => IsResultEditable && SelectedRow is not null;

    [RelayCommand(CanExecute = nameof(CanDeleteRow))]
    private void DeleteRow()
    {
        if (Editable is null || SelectedRow is null)
        {
            return;
        }

        Editable.DeleteRow(SelectedRow);
        SelectedRow = null;
    }

    private bool CanSave => HasChanges && IsResultEditable;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync(CancellationToken ct)
    {
        if (Editable is null)
        {
            return;
        }

        var provider = _providers.Get(Connection.ProviderId);
        if (provider.IsSqlBased)
        {
            await SaveSqlAsync(provider, ct);
        }
        else
        {
            await SaveChangeSetAsync(provider, ct);
        }
    }

    // The SQL path: dialect-quoted INSERT/UPDATE/DELETE, run as one transaction (Notes §8).
    private async Task SaveSqlAsync(IDbProvider provider, CancellationToken ct)
    {
        var statements = CrudStatementBuilder.Build(Editable!, provider.Dialect);
        if (statements.Count == 0)
        {
            Report(OutputLevel.Info, Loc.Get("SaveNothing"));
            return;
        }

        var preview = BuildPreview(statements);
        if (!await ConfirmSaveAsync(preview))
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var profile = _connections.Resolve(Connection, _database);
            var affected = await provider.ExecuteBatchAsync(profile, statements, ct);
            stopwatch.Stop();
            // Re-read so DB-assigned values (auto-increment ids, defaults) and a clean baseline show up.
            await ReloadAsync(ct);
            Report(OutputLevel.Info, Loc.Get("SaveOk", affected));
            AppendHistory(preview, QueryHistoryKind.Save, stopwatch.ElapsedMilliseconds, affected, success: true, error: null);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            ReportFailure(ex.Message);
            AppendHistory(preview, QueryHistoryKind.Save, stopwatch.ElapsedMilliseconds, 0, success: false, error: ex.Message);
        }
    }

    // The non-SQL path (SE-114): a structured ChangeSet, applied via the provider's own write ops
    // (IDbProvider.ApplyChangesAsync) instead of generated SQL text.
    private async Task SaveChangeSetAsync(IDbProvider provider, CancellationToken ct)
    {
        var changes = ChangeSetBuilder.Build(Editable!);
        if (changes is null || changes.Rows.Count == 0)
        {
            Report(OutputLevel.Info, Loc.Get("SaveNothing"));
            return;
        }

        var preview = BuildPreview(changes);
        if (!await ConfirmSaveAsync(preview))
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var profile = _connections.Resolve(Connection, _database);
            var result = await provider.ApplyChangesAsync(profile, changes, ct);
            stopwatch.Stop();
            await ReloadAsync(ct);
            Report(OutputLevel.Info, Loc.Get("SaveOk", result.AffectedCount));
            if (!result.IsAtomic && result.RowErrors.Count > 0)
            {
                Report(OutputLevel.Error, string.Join(Environment.NewLine, result.RowErrors));
            }

            AppendHistory(preview, QueryHistoryKind.Save, stopwatch.ElapsedMilliseconds, result.AffectedCount,
                success: result.RowErrors.Count == 0, error: result.RowErrors.Count == 0 ? null : string.Join("; ", result.RowErrors));
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            ReportFailure(ex.Message);
            AppendHistory(preview, QueryHistoryKind.Save, stopwatch.ElapsedMilliseconds, 0, success: false, error: ex.Message);
        }
    }

    // Read fresh (not cached at construction) so a Settings-window change takes effect on the very next
    // save, no new tab required.
    private async Task<bool> ConfirmSaveAsync(string preview)
    {
        if (!_settingsStore.Load().ConfirmBeforeSave)
        {
            return true;
        }

        return SaveReviewRequested is not null && await SaveReviewRequested(preview);
    }

    private bool CanDiscard => HasChanges;

    [RelayCommand(CanExecute = nameof(CanDiscard))]
    private async Task DiscardAsync(CancellationToken ct) => await ReloadAsync(ct);

    // Reload the current view: the browse page in Browse mode, the typed query in Query mode. A reload
    // always re-runs the whole editor text, never a stale selection from before the save.
    private Task ReloadAsync(CancellationToken ct) =>
        IsBrowseMode ? LoadPageAsync(ct) : RunScriptAsync(Sql, ct);

    partial void OnEditableChanged(EditableResultSet? value)
    {
        OnPropertyChanged(nameof(ShowEditToolbar));
        AddRowCommand.NotifyCanExecuteChanged();
        DeleteRowCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedRowChanged(EditableRow? value) =>
        DeleteRowCommand.NotifyCanExecuteChanged();

    private static string BuildPreview(IReadOnlyList<SqlStatement> statements)
    {
        var builder = new StringBuilder();
        foreach (var statement in statements)
        {
            builder.AppendLine(statement.Text + ";");
            foreach (var parameter in statement.Parameters)
            {
                builder.AppendLine($"  @{parameter.Name} = {parameter.Value ?? "NULL"}");
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildPreview(ChangeSet changes)
    {
        var builder = new StringBuilder();
        foreach (var row in changes.Rows)
        {
            builder.Append(row.Kind).Append(' ').Append(changes.Table);
            if (row.Identity.Count > 0)
            {
                builder.Append(" where ").Append(string.Join(", ", row.Identity.Select(kv => $"{kv.Key} = {kv.Value ?? "NULL"}")));
            }

            builder.AppendLine();
            foreach (var cell in row.Cells)
            {
                builder.AppendLine($"  {cell.Column} = {cell.Value ?? "NULL"}");
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }
}
