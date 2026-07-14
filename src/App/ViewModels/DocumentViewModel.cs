using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Lionear.SqlExplorer.Core.Completion;
using Lionear.SqlExplorer.Core.Connections;
using Lionear.SqlExplorer.Core.Editing;
using Lionear.SqlExplorer.Core.Export;
using Lionear.SqlExplorer.Core.Formatting;
using Lionear.SqlExplorer.Core.History;
using Lionear.SqlExplorer.Core.Localization;
using Lionear.SqlExplorer.Core.Providers;
using Lionear.SqlExplorer.Core.Schema;
using Lionear.SqlExplorer.Core.Settings;
using Lionear.SqlExplorer.Core.Sql;
using Lionear.SqlExplorer.Sdk;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Lionear.SqlExplorer.App.ViewModels;

public enum DocumentMode
{
    Query,
    Browse
}

/// <summary>Export text format — mirrors the three <see cref="ResultExporter"/> methods.</summary>
public enum ExportFormat
{
    Csv,
    Json,
    Sql,
    Markdown
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
    private readonly ISchemaCache _schemaCache;

    private string? _database;
    private string? _schema;
    private string _table = string.Empty;
    private int _lastRowCount;

    // Browse-mode server-side sort (base column name + direction); null = unsorted.
    private string? _sortColumn;
    private bool _sortDescending;

    [ObservableProperty]
    private string _title = "Query";

    [ObservableProperty]
    private string _sql = string.Empty;

    /// <summary>Raised after an execution completes, so the host can surface the outcome — success row
    /// counts, cancellations, and failures — in the shared Output panel. The host pops the panel open on
    /// a failure so it's never missed.</summary>
    public event Action<OutputLevel, string>? Reported;

    private void Report(OutputLevel level, string message) => Reported?.Invoke(level, message);

    private void ReportFailure(string message) => Report(OutputLevel.Error, message);

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

    [ObservableProperty]
    private string _rowRange = string.Empty;

    // Cell-viewer panel: full value of the last-clicked cell (JSON pretty-printed when parseable).
    [ObservableProperty]
    private bool _isCellViewerVisible;

    [ObservableProperty]
    private string? _selectedCellColumn;

    [ObservableProperty]
    private string? _selectedCellValue;

    // Aggregation strip: count over the selected rows + sum/avg/min/max when the current column is numeric.
    [ObservableProperty]
    private string _aggregationSummary = string.Empty;

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
        ISchemaCache schemaCache,
        IAppSettingsStore settingsStore,
        ILocalizer localizer)
    {
        _providers = providers;
        _connections = connections;
        _formatter = formatter;
        _history = history;
        _schemaCache = schemaCache;
        _settingsStore = settingsStore;
        Loc = localizer;

        var settings = _settingsStore.Load();
        EditorFontSize = settings.EditorFontSize;
        EditorWordWrap = settings.EditorWordWrap;
    }

    /// <summary>SQL editor font size/word-wrap, read once from settings at document creation
    /// (Notes: no live-binding infrastructure needed for a per-tab cosmetic default like this).</summary>
    public double? EditorFontSize { get; }

    public bool EditorWordWrap { get; }

    /// <summary>Quick-open-ranked completions (1.3) for the SQL text at <paramref name="caret"/>: alias
    /// columns after "alias.", tables after FROM/JOIN, a broad table+column+keyword mix elsewhere.</summary>
    public CompletionResult GetCompletions(string sql, int caret)
    {
        var dialect = _providers.Get(Connection.ProviderId).Dialect;
        var snapshot = _schemaCache.Get(Connection.Id) ?? SchemaSnapshot.Empty;
        return SqlCompletionProvider.Suggest(sql, caret, snapshot, dialect.Keywords);
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

    // A connection flagged read-only (safe mode) blocks the editable-grid save-flow entirely, even when
    // the result would otherwise map back to a single keyed table — this guards against accidental writes
    // (e.g. on production). Free DML typed in a query tab is out of scope for the MVP.
    public bool IsResultEditable => Connection is not { ReadOnly: true } && Editable?.IsEditable == true;

    public string? ReadOnlyReason => Connection is { ReadOnly: true }
        ? Loc["ReadOnlyConnection"]
        : Editable?.ReadOnlyReason;

    public bool CanPrevPage => IsBrowseMode && Page > 0;

    public bool CanNextPage => IsBrowseMode && _lastRowCount == PageSize && PageSize > 0;

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

    // Query-tab connection switch (InitQuery counts as the first one): refresh the tab title and the
    // database list for the newly-selected connection. Browse tabs are pinned to their tree-node's
    // connection/database and never touch either — this is a no-op there.
    partial void OnConnectionChanged(SavedConnection value)
    {
        if (!IsQueryMode)
        {
            return;
        }

        Title = $"{Loc["QueryTab"]} · {value.Name}";
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
            var paged = dialect.Paginate($"SELECT * FROM {qualified}{where}", PageSize, Page * PageSize, orderBy);
            await ExecuteAsync(paged, ct);
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
    private Task RunScriptAsync(string sql, CancellationToken ct) => RunTracked(ct, async token =>
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var profile = _connections.Resolve(Connection, _database);
            var provider = _providers.Get(Connection.ProviderId);

            var batches = Connection.ProviderId == "sqlserver"
                ? SqlStatementSplitter.SplitGoBatches(sql)
                : [sql];

            var results = new List<QueryResult>();
            foreach (var batch in batches)
            {
                results.AddRange(await provider.ExecuteScriptAsync(profile, batch, token));
            }

            stopwatch.Stop();
            var totalRows = results.Sum(r => r.Rows.Count);
            SetResultSets(BuildResultTabs(results));
            Report(OutputLevel.Info, DescribeOutcome(results, stopwatch.Elapsed.TotalMilliseconds));
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
            ReportFailure(ex.Message);
            if (IsQueryMode)
            {
                AppendHistory(sql, QueryHistoryKind.Query, stopwatch.ElapsedMilliseconds, 0, success: false, error: ex.Message);
            }
        }
    });

    private static List<ResultSetTab> BuildResultTabs(IReadOnlyList<QueryResult> results) =>
        results.Count <= 1
            ? [new ResultSetTab("Result", EditableResultSet.From(results.Count == 1 ? results[0] : EmptyResult()))]
            : results.Select((r, i) => new ResultSetTab($"Result {i + 1} · {r.Rows.Count} rows", EditableResultSet.From(r))).ToList();

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
        await LoadPageAsync(ct);
    }

    [RelayCommand(CanExecute = nameof(CanNextPage))]
    private async Task NextPageAsync(CancellationToken ct)
    {
        Page++;
        await LoadPageAsync(ct);
    }

    [RelayCommand]
    private async Task ApplyFilterAsync(CancellationToken ct)
    {
        Page = 0;
        await LoadPageAsync(ct);
    }

    [RelayCommand]
    private void Format()
    {
        var dialect = _providers.Get(Connection.ProviderId).Dialect;
        Sql = _formatter.Format(Sql, dialect, SqlFormatOptions.Default);
    }

    // Shared execution path for both a typed query and a browse page. Only typed queries are logged to
    // history — browse paging (same path, IsBrowseMode) would just clutter it.
    private Task ExecuteAsync(string sql, CancellationToken ct) => RunTracked(ct, async token =>
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var profile = _connections.Resolve(Connection, _database);
            var result = await _providers.Get(Connection.ProviderId).ExecuteQueryAsync(profile, sql, token);
            stopwatch.Stop();
            _lastRowCount = result.Rows.Count;
            SetResultSets([new ResultSetTab("Result", EditableResultSet.From(result))]);
            // Browse paging shares this path; its own RowRange header already shows the count, so only a
            // typed query reports to the Output panel — otherwise every page-flip would spam it.
            if (IsQueryMode)
            {
                Report(OutputLevel.Info, DescribeOutcome([result], result.Elapsed.TotalMilliseconds));
                AppendHistory(sql, QueryHistoryKind.Query, stopwatch.ElapsedMilliseconds, result.Rows.Count, success: true, error: null);
            }
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            if (IsQueryMode)
            {
                Report(OutputLevel.Info, Loc["QueryCancelled"]);
            }
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            ReportFailure(ex.Message);
            if (IsQueryMode)
            {
                AppendHistory(sql, QueryHistoryKind.Query, stopwatch.ElapsedMilliseconds, 0, success: false, error: ex.Message);
            }
        }
    });

    private void AppendHistory(string sql, QueryHistoryKind kind, long durationMs, int rowCount, bool success, string? error) =>
        _history.Append(new QueryHistoryEntry
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
        });

    /// <summary>Show a clicked cell's full value in the viewer panel, resolving the column name by index.</summary>
    public void ShowCell(int columnIndex, object? value)
    {
        SelectedCellColumn = Editable is { } editable && columnIndex >= 0 && columnIndex < editable.Columns.Count
            ? editable.Columns[columnIndex].Name
            : null;
        SelectedCellValue = FormatCellValue(value);
        IsCellViewerVisible = true;
    }

    [RelayCommand]
    private void HideCellViewer() => IsCellViewerVisible = false;

    /// <summary>
    /// Recompute the selection aggregation over <paramref name="values"/> (the current column across the
    /// selected rows): always a count, plus sum/avg/min/max when every non-null value is numeric.
    /// </summary>
    public void UpdateAggregation(IReadOnlyList<object?> values)
    {
        if (values.Count == 0)
        {
            AggregationSummary = string.Empty;
            return;
        }

        var numbers = new List<decimal>();
        var numeric = true;
        foreach (var value in values)
        {
            if (value is null or DBNull)
            {
                continue; // nulls are ignored by the numeric aggregates, like SQL
            }

            if (decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
                    NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
            {
                numbers.Add(number);
            }
            else
            {
                numeric = false;
                break;
            }
        }

        var count = $"{Loc["AggCount"]} {values.Count}";
        AggregationSummary = numeric && numbers.Count > 0
            ? $"{count}  ·  {Loc["AggSum"]} {Num(numbers.Sum())}  ·  {Loc["AggAvg"]} {Num(numbers.Average())}" +
              $"  ·  {Loc["AggMin"]} {Num(numbers.Min())}  ·  {Loc["AggMax"]} {Num(numbers.Max())}"
            : count;
    }

    private static string Num(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    // Render a cell value for the viewer: NULL for null, pretty-printed JSON when the text parses as an
    // object/array, otherwise the raw string.
    private static string FormatCellValue(object? value)
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

        var dialect = _providers.Get(Connection.ProviderId).Dialect;
        var statements = CrudStatementBuilder.Build(Editable, dialect);
        if (statements.Count == 0)
        {
            Report(OutputLevel.Info, Loc.Get("SaveNothing"));
            return;
        }

        var preview = BuildPreview(statements);

        // Read fresh (not cached at construction) so a Settings-window change takes effect on the
        // very next save, no new tab required.
        if (_settingsStore.Load().ConfirmBeforeSave)
        {
            if (SaveReviewRequested is null || !await SaveReviewRequested(preview))
            {
                return;
            }
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var profile = _connections.Resolve(Connection, _database);
            var affected = await _providers.Get(Connection.ProviderId).ExecuteBatchAsync(profile, statements, ct);
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

    private bool CanDiscard => HasChanges;

    [RelayCommand(CanExecute = nameof(CanDiscard))]
    private async Task DiscardAsync(CancellationToken ct) => await ReloadAsync(ct);

    // Reload the current view: the browse page in Browse mode, the typed query in Query mode. A reload
    // always re-runs the whole editor text, never a stale selection from before the save.
    private Task ReloadAsync(CancellationToken ct) =>
        IsBrowseMode ? LoadPageAsync(ct) : RunScriptAsync(Sql, ct);

    partial void OnEditableChanged(EditableResultSet? value)
    {
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
}
