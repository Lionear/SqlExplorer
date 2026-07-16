using System.Text;

namespace SqlExplorer.Tools.UniversalBackup;

/// <summary>
/// Restore-side sink for a streamed v2 payload. Pure-scalar rows are batched into multi-row INSERTs (fast
/// path, unchanged from before); a row carrying a large LOB cell is spooled cell-by-cell to a temp file and
/// inserted on its own via <see cref="IDbProvider.InsertStreamingAsync"/>, so a multi-gigabyte value flows
/// backup-file → temp-file → database without ever sitting in a single .NET array.
/// </summary>
internal sealed class RestoreVisitor(
    ToolExecutionContext context,
    IProgress<ToolProgress> progress,
    IReadOnlyList<SelectionEntry>? selection,
    bool dropRecreate) : ILbakVisitor
{
    private const int BatchSize = 500;

    private BackupTable? _table;
    private string _qualified = string.Empty;
    private string _columnList = string.Empty;
    private int[] _keep = [];
    private readonly List<object?[]> _batch = [];
    private readonly List<BackupObject> _objects = [];
    private long _inserted;
    private string? _activeItemKey; // the checklist row currently "Running"; flipped to Done on the next switch
    private bool _includeData; // Schema selected but Data unchecked → create the table, skip every row

    public async Task OnTableAsync(BackupTable header, CancellationToken ct)
    {
        await FlushBatchAsync(ct); // finish the previous table before switching state
        CompleteActiveItem();

        _table = header;
        var itemKey = $"table:{header.SchemaName}.{header.TableName}";

        // null = table not selected at all; false = schema only; true = schema + data — the same
        // [Schema][Data] semantics the Backup dialog's tree uses, shared via BackupSelection.
        var dataChoice = BackupSelection.TableDataChoice(selection, header.SchemaName, header.TableName);
        if (dataChoice is null)
        {
            _keep = []; // not selected — OnRowAsync no-ops below, the reader still drains the row bytes
            progress.Report(new ToolProgress(
                context.Localizer.Get("restore.progress.tableSkipped", header.TableName),
                ItemKey: itemKey, ItemStatus: ToolItemStatus.Skipped));
            return;
        }

        if (dropRecreate)
        {
            await UniversalRestoreTool.DropTableIfExistsAsync(context, header, ct);
        }

        _includeData = dataChoice == true;
        _keep = await UniversalRestoreTool.CreateTableAsync(context, header, ct);
        var dialect = context.Provider.Dialect;
        var schema = string.IsNullOrEmpty(header.SchemaName) ? null : header.SchemaName;
        _qualified = dialect.QualifyName(null, schema, header.TableName);
        _columnList = string.Join(", ", _keep.Select(k => dialect.QuoteIdentifier(header.Columns[k].Name)));
        _inserted = 0;
        _activeItemKey = itemKey;
        progress.Report(new ToolProgress(
            context.Localizer.Get("restore.progress.createdTable", header.TableName),
            ItemKey: itemKey, ItemStatus: ToolItemStatus.Running));
    }

    public async Task OnRowAsync(ILbakRow row, CancellationToken ct)
    {
        if (_keep.Length == 0 || !_includeData)
        {
            return; // nothing insertable, or schema-only restore — ReadStreamingAsync drains the row's cells
        }

        var values = new object?[_keep.Length];
        List<LobSpool>? lobs = null;
        try
        {
            for (var k = 0; k < _keep.Length; k++)
            {
                var i = _keep[k];
                if (row.IsNull(i))
                {
                    values[k] = null;
                }
                else if (!row.IsStreamed(i))
                {
                    values[k] = row.GetValue(i);
                }
                else
                {
                    var isText = row.IsTextStream(i);
                    var temp = Path.GetTempFileName();
                    await using (var fs = File.Create(temp))
                    {
                        await row.OpenStream(i).CopyToAsync(fs, ct);
                    }

                    (lobs ??= []).Add(new LobSpool(k, temp, isText));
                }
            }

            if (lobs is null)
            {
                _batch.Add(values);
                if (_batch.Count >= BatchSize)
                {
                    await FlushBatchAsync(ct);
                }
            }
            else
            {
                await FlushBatchAsync(ct); // keep insertion order
                await InsertStreamingRowAsync(values, lobs, ct);
                _inserted++;
                Report();
            }
        }
        finally
        {
            if (lobs is not null)
            {
                foreach (var lob in lobs)
                {
                    TryDelete(lob.TempPath);
                }
            }
        }
    }

    // Buffer selected non-table objects; they're replayed after all table data (they may reference tables).
    public Task OnObjectAsync(BackupObject obj, CancellationToken ct)
    {
        if (BackupSelection.IsIncluded(selection, obj.Kind.ToString().ToLowerInvariant(), obj.SchemaName, obj.Name))
        {
            _objects.Add(obj);
        }
        else
        {
            var itemKey = $"{obj.Kind.ToString().ToLowerInvariant()}:{obj.SchemaName}.{obj.Name}";
            progress.Report(new ToolProgress(
                context.Localizer.Get("restore.progress.objectSkipped", obj.Name),
                ItemKey: itemKey, ItemStatus: ToolItemStatus.Skipped));
        }

        return Task.CompletedTask;
    }

    public async Task FinishAsync(CancellationToken ct)
    {
        await FlushBatchAsync(ct);
        CompleteActiveItem();
        await ReplayObjectsAsync(ct);
    }

    private void CompleteActiveItem()
    {
        if (_activeItemKey is not { } key)
        {
            return;
        }

        progress.Report(new ToolProgress(
            context.Localizer.Get("restore.progress.tableDone", _table?.TableName ?? string.Empty, _inserted),
            ItemKey: key, ItemStatus: ToolItemStatus.Done));
        _activeItemKey = null;
    }

    // Replay object DDL best-effort in dependency-friendly order (views → functions → procedures →
    // triggers). No topological sort within a kind (a view-on-view chain can still fail) — an object that
    // fails (already exists, unmet dependency) is logged and skipped, the rest continue.
    private async Task ReplayObjectsAsync(CancellationToken ct)
    {
        foreach (var obj in _objects.OrderBy(ReplayOrder))
        {
            ct.ThrowIfCancellationRequested();
            var itemKey = $"{obj.Kind.ToString().ToLowerInvariant()}:{obj.SchemaName}.{obj.Name}";
            try
            {
                await context.Provider.ExecuteDdlAsync(context.Profile, obj.Definition, ct);
                progress.Report(new ToolProgress(
                    context.Localizer.Get("restore.progress.objectDone", obj.Name),
                    ItemKey: itemKey, ItemStatus: ToolItemStatus.Done));
            }
            catch (Exception ex)
            {
                progress.Report(new ToolProgress(
                    context.Localizer.Get("restore.progress.objectFailed", obj.Name, ex.Message),
                    ItemKey: itemKey, ItemStatus: ToolItemStatus.Error));
            }
        }
    }

    private static int ReplayOrder(BackupObject obj) => obj.Kind switch
    {
        LbakObjectKind.View => 0,
        LbakObjectKind.Function => 1,
        LbakObjectKind.Procedure => 2,
        _ => 3 // Trigger last (depends on tables/views existing)
    };

    private async Task InsertStreamingRowAsync(object?[] values, List<LobSpool> lobs, CancellationToken ct)
    {
        var placeholders = string.Join(", ", Enumerable.Range(0, _keep.Length).Select(k => $"@p{k}"));
        var sql = $"INSERT INTO {_qualified} ({_columnList}) VALUES ({placeholders})";

        var pars = new List<StreamingParam>(_keep.Length);
        var open = new List<IDisposable>();
        try
        {
            for (var k = 0; k < _keep.Length; k++)
            {
                var lob = lobs.FirstOrDefault(l => l.ParamIndex == k);
                if (lob is null)
                {
                    pars.Add(new StreamingParam($"p{k}", StreamingValue.Of(values[k])));
                }
                else if (lob.IsText)
                {
                    var reader = new StreamReader(File.OpenRead(lob.TempPath), Encoding.UTF8);
                    open.Add(reader);
                    pars.Add(new StreamingParam($"p{k}", StreamingValue.Text(reader)));
                }
                else
                {
                    var stream = File.OpenRead(lob.TempPath);
                    open.Add(stream);
                    pars.Add(new StreamingParam($"p{k}", StreamingValue.Bytes(stream)));
                }
            }

            await context.Provider.InsertStreamingAsync(context.Profile, sql, pars, ct);
        }
        finally
        {
            foreach (var d in open)
            {
                d.Dispose();
            }
        }
    }

    private async Task FlushBatchAsync(CancellationToken ct)
    {
        if (_batch.Count == 0)
        {
            return;
        }

        var statements = _batch.Select(v => UniversalRestoreTool.BuildInsert(_qualified, _columnList, v)).ToList();
        await context.Provider.ExecuteBatchAsync(context.Profile, statements, ct);
        _inserted += _batch.Count;
        _batch.Clear();
        Report();
    }

    private void Report() =>
        progress.Report(new ToolProgress(
            $"{_table?.TableName}: inserted {_inserted} row(s)",
            ItemKey: _activeItemKey, ItemStatus: _activeItemKey is null ? null : ToolItemStatus.Running));

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }

    private sealed record LobSpool(int ParamIndex, string TempPath, bool IsText);
}
