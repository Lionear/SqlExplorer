using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Lionear.SqlExplorer.Core.Connections;
using Lionear.SqlExplorer.Core.Editing;
using Lionear.SqlExplorer.Core.Formatting;
using Lionear.SqlExplorer.Core.History;
using Lionear.SqlExplorer.Core.Localization;
using Lionear.SqlExplorer.Core.Providers;
using Lionear.SqlExplorer.Sdk;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Lionear.SqlExplorer.App.ViewModels;

public enum DocumentMode
{
    Query,
    Browse
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

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsResultEditable))]
    [NotifyPropertyChangedFor(nameof(ReadOnlyReason))]
    private EditableResultSet? _editable;

    [ObservableProperty]
    private EditableRow? _selectedRow;

    [ObservableProperty]
    private bool _hasChanges;

    [ObservableProperty]
    private string _pendingSummary = string.Empty;

    // Browse-mode paging + filtering.
    [ObservableProperty]
    private string _filterText = string.Empty;

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

    public DocumentViewModel(
        IDbProviderRegistry providers,
        ConnectionService connections,
        ISqlFormatter formatter,
        IQueryHistoryStore history,
        ILocalizer localizer)
    {
        _providers = providers;
        _connections = connections;
        _formatter = formatter;
        _history = history;
        Loc = localizer;
    }

    public ILocalizer Loc { get; }

    public SavedConnection Connection { get; private set; } = null!;

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

    /// <summary>Set by the view so the document can show the generated SQL for review before saving.</summary>
    public Func<string, Task<bool>>? SaveReviewRequested { get; set; }

    public void InitQuery(SavedConnection connection)
    {
        Connection = connection;
        Mode = DocumentMode.Query;
        Title = $"{Loc["QueryTab"]} · {connection.Name}";
    }

    public void InitBrowse(SavedConnection connection, string? database, string? schema, string table)
    {
        Connection = connection;
        Mode = DocumentMode.Browse;
        _database = database;
        _schema = schema;
        _table = table;
        Title = table;
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
        var dialect = _providers.Get(Connection.ProviderId).Dialect;
        var qualified = _schema is { } schema
            ? $"{dialect.QuoteIdentifier(schema)}.{dialect.QuoteIdentifier(_table)}"
            : dialect.QuoteIdentifier(_table);

        var where = string.IsNullOrWhiteSpace(FilterText) ? string.Empty : $" WHERE {FilterText}";
        var orderBy = _sortColumn is null
            ? null
            : $"{dialect.QuoteIdentifier(_sortColumn)} {(_sortDescending ? "DESC" : "ASC")}";
        var paged = dialect.Paginate($"SELECT * FROM {qualified}{where}", PageSize, Page * PageSize, orderBy);
        await ExecuteAsync(paged, ct);

        var offset = Page * PageSize;
        RowRange = _lastRowCount == 0
            ? Loc.Get("RowRangeEmpty")
            : Loc.Get("RowRange", offset + 1, offset + _lastRowCount);
        PrevPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task RunAsync(CancellationToken ct) => await ExecuteAsync(Sql, ct);

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
    private async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var profile = _connections.Resolve(Connection, _database);
            var result = await _providers.Get(Connection.ProviderId).ExecuteQueryAsync(profile, sql, ct);
            stopwatch.Stop();
            _lastRowCount = result.Rows.Count;
            SetResult(EditableResultSet.From(result));
            Status = Loc.Get("StatusRows", result.Rows.Count, result.Elapsed.TotalMilliseconds);
            if (IsQueryMode)
            {
                AppendHistory(sql, QueryHistoryKind.Query, stopwatch.ElapsedMilliseconds, result.Rows.Count, success: true, error: null);
            }
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Status = ex.Message;
            if (IsQueryMode)
            {
                AppendHistory(sql, QueryHistoryKind.Query, stopwatch.ElapsedMilliseconds, 0, success: false, error: ex.Message);
            }
        }
    }

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

    private void SetResult(EditableResultSet editable)
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
        editable.Rows.CollectionChanged += OnRowsChanged;
        foreach (var row in editable.Rows)
        {
            row.PropertyChanged += OnRowChanged;
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
        if (Editable is null || SaveReviewRequested is null)
        {
            return;
        }

        var dialect = _providers.Get(Connection.ProviderId).Dialect;
        var statements = CrudStatementBuilder.Build(Editable, dialect);
        if (statements.Count == 0)
        {
            Status = Loc.Get("SaveNothing");
            return;
        }

        var preview = BuildPreview(statements);
        if (!await SaveReviewRequested(preview))
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var profile = _connections.Resolve(Connection, _database);
            var affected = await _providers.Get(Connection.ProviderId).ExecuteBatchAsync(profile, statements, ct);
            stopwatch.Stop();
            // Re-read so DB-assigned values (auto-increment ids, defaults) and a clean baseline show up.
            await ReloadAsync(ct);
            Status = Loc.Get("SaveOk", affected);
            AppendHistory(preview, QueryHistoryKind.Save, stopwatch.ElapsedMilliseconds, affected, success: true, error: null);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Status = ex.Message;
            AppendHistory(preview, QueryHistoryKind.Save, stopwatch.ElapsedMilliseconds, 0, success: false, error: ex.Message);
        }
    }

    private bool CanDiscard => HasChanges;

    [RelayCommand(CanExecute = nameof(CanDiscard))]
    private async Task DiscardAsync(CancellationToken ct) => await ReloadAsync(ct);

    // Reload the current view: the browse page in Browse mode, the typed query in Query mode.
    private Task ReloadAsync(CancellationToken ct) =>
        IsBrowseMode ? LoadPageAsync(ct) : ExecuteAsync(Sql, ct);

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
