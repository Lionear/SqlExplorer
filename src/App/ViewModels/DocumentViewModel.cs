using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;
using Lionear.SqlExplorer.Core.Connections;
using Lionear.SqlExplorer.Core.Editing;
using Lionear.SqlExplorer.Core.Formatting;
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

    public DocumentViewModel(
        IDbProviderRegistry providers,
        ConnectionService connections,
        ISqlFormatter formatter,
        ILocalizer localizer)
    {
        _providers = providers;
        _connections = connections;
        _formatter = formatter;
        Loc = localizer;
    }

    public ILocalizer Loc { get; }

    public SavedConnection Connection { get; private set; } = null!;

    public DocumentMode Mode { get; private set; }

    public bool IsQueryMode => Mode == DocumentMode.Query;

    public bool IsBrowseMode => Mode == DocumentMode.Browse;

    public bool IsResultEditable => Editable?.IsEditable == true;

    public string? ReadOnlyReason => Editable?.ReadOnlyReason;

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

    public void InitBrowse(SavedConnection connection, string? schema, string table)
    {
        Connection = connection;
        Mode = DocumentMode.Browse;
        _schema = schema;
        _table = table;
        Title = table;
    }

    /// <summary>True when this is the browse tab for the given connection + table (avoids duplicate tabs).</summary>
    public bool MatchesBrowse(string connectionId, string? schema, string table) =>
        IsBrowseMode && Connection.Id == connectionId && _schema == schema && _table == table;

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
        var dialect = _providers.Get(Connection.Kind).Dialect;
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
        var dialect = _providers.Get(Connection.Kind).Dialect;
        Sql = _formatter.Format(Sql, dialect, SqlFormatOptions.Default);
    }

    // Shared execution path for both a typed query and a browse page.
    private async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        try
        {
            var profile = _connections.Resolve(Connection);
            var result = await _providers.Get(Connection.Kind).ExecuteQueryAsync(profile, sql, ct);
            _lastRowCount = result.Rows.Count;
            SetResult(EditableResultSet.From(result));
            Status = Loc.Get("StatusRows", result.Rows.Count, result.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

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

        var dialect = _providers.Get(Connection.Kind).Dialect;
        var statements = CrudStatementBuilder.Build(Editable, dialect);
        if (statements.Count == 0)
        {
            Status = Loc.Get("SaveNothing");
            return;
        }

        if (!await SaveReviewRequested(BuildPreview(statements)))
        {
            return;
        }

        try
        {
            var profile = _connections.Resolve(Connection);
            var affected = await _providers.Get(Connection.Kind).ExecuteBatchAsync(profile, statements, ct);
            // Re-read so DB-assigned values (auto-increment ids, defaults) and a clean baseline show up.
            await ReloadAsync(ct);
            Status = Loc.Get("SaveOk", affected);
        }
        catch (Exception ex)
        {
            Status = ex.Message;
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
