using System.Security.Cryptography;
using SqlExplorer.Sdk.Branding;

namespace SqlExplorer.Tools.UniversalBackup;

/// <summary>
/// Restores a <c>.lbak</c> file into the connected database: recreates each table and inserts its rows
/// through the host's provider. Destructive (creates objects + writes data), so the host confirms first.
/// A separate tool from Backup — same reason Backup/Restore are split everywhere: Backup is safe, Restore
/// is not, and each deserves its own menu item. MVP: same-engine only (cross-engine is blocked).
/// </summary>
public sealed class UniversalRestoreTool : IToolPlugin, ICustomToolUi
{
    private const int InsertChunkSize = 500;

    // Route B: the object-selection tree (+ drop&recreate toggle) replaces the generic form. Fields stay
    // declared as a graceful fallback but are unused while this view is supplied.
    public Avalonia.Controls.Control CreateView(IToolUiContext context) => new RestoreSelectionView(context);

    public string Id => "universal-restore";

    public string Title => "Restore from backup";
    public string? TitleKey => "restore.title";
    public string? DialogTitleKey => "restore.title";

    public IReadOnlyList<string> MenuPath => ["Backup & Restore"];

    public ProviderIcon? Icon { get; } = ProviderIconLoader.Load(typeof(UniversalRestoreTool), "♻");

    public ToolTarget Target { get; } = new(NodeKinds: [DbNodeKind.Database]);

    public bool IsDestructive => true;

    public IReadOnlyList<ToolField> Fields { get; } =
    [
        new("filePath", "Backup file", ToolFieldType.File, Required: true, FileExtensions: ["lbak"], SaveFile: false,
            LabelKey: "restore.field.file.label"),
        new("passphrase", "Passphrase (only if the backup is encrypted)", ToolFieldType.Password,
            LabelKey: "restore.field.passphrase.label")
    ];

    // Read the always-plaintext header as soon as a file is chosen, so the dialog shows context first.
    public Task<string?> PreviewAsync(string filePath, CancellationToken ct)
    {
        try
        {
            var meta = LbakFormat.ReadMeta(filePath);
            var encrypted = meta.Encrypted ? "yes — passphrase required" : "no";
            return Task.FromResult<string?>(
                $"Source: {meta.EngineDisplayName} · database \"{meta.DatabaseName}\"\n" +
                $"Tables: {meta.TableCount} · created {meta.CreatedUtc}\n" +
                $"Encrypted: {encrypted}");
        }
        catch (Exception ex)
        {
            return Task.FromResult<string?>(ex.Message);
        }
    }

    public async Task ExecuteAsync(
        ToolExecutionContext context,
        IReadOnlyDictionary<string, string?> inputs,
        IProgress<ToolProgress> progress,
        CancellationToken ct)
    {
        var filePath = inputs.GetValueOrDefault("filePath");
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new InvalidOperationException(context.Localizer["restore.error.noFile"]);
        }

        var meta = LbakFormat.ReadMeta(filePath);
        progress.Report(new ToolProgress(
            context.Localizer.Get("restore.progress.backupInfo", meta.EngineDisplayName, meta.DatabaseName, meta.TableCount)));

        // Cross-engine restore is intentionally blocked in v1 (dialect-specific column types).
        if (!string.Equals(meta.ProviderId, context.ProviderId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(context.Localizer.Get("restore.error.crossEngine", meta.EngineDisplayName));
        }

        var passphrase = inputs.GetValueOrDefault("passphrase");
        var selection = BackupSelection.Parse(inputs.GetValueOrDefault("selection"));
        var dropRecreate = string.Equals(inputs.GetValueOrDefault("dropRecreate"), "true", StringComparison.OrdinalIgnoreCase);

        // Streaming CPU/IO (decompress + decrypt + LOB spooling) off the UI thread.
        await Task.Run(async () =>
        {
            try
            {
                if (meta.FormatVersion >= 2)
                {
                    var visitor = new RestoreVisitor(context, progress, selection, dropRecreate);
                    await LbakFormat.ReadStreamingAsync(filePath, passphrase, visitor, ct);
                    await visitor.FinishAsync(ct);
                }
                else
                {
                    await RestoreV1Async(context, filePath, passphrase, dropRecreate, progress, ct);
                }
            }
            catch (CryptographicException)
            {
                throw new InvalidOperationException(context.Localizer["restore.error.wrongPassphrase"]);
            }
        }, ct);

        progress.Report(new ToolProgress(context.Localizer["restore.progress.complete"], 1.0));
    }

    // SQL Server rowversion/timestamp columns are engine-generated: a table can hold at most one, and it
    // rejects any explicit INSERT value ("Cannot insert an explicit value into a timestamp column").
    // We recreate the column (so the shape matches) but must leave it out of the INSERT so SQL Server
    // fills it. Same-engine restore only, so keying off the target ProviderId is safe.
    internal static bool IsInsertable(string providerId, BackupColumn column)
    {
        if (!string.Equals(providerId, "sqlserver", StringComparison.Ordinal))
        {
            return true;
        }

        var type = column.DeclaredType.Trim();
        return !type.Equals("timestamp", StringComparison.OrdinalIgnoreCase)
            && !type.Equals("rowversion", StringComparison.OrdinalIgnoreCase);
    }

    // "Drop & recreate" toggle: DROP TABLE IF EXISTS is supported identically by all four engines, so this
    // needs no dialect-specific builder. Non-table objects aren't covered (v1 scope) — a CREATE that
    // collides with an existing view/procedure/function/trigger is still just logged and skipped, same as
    // before this feature (see ReplayObjectsAsync).
    internal static Task DropTableIfExistsAsync(ToolExecutionContext context, BackupTable table, CancellationToken ct)
    {
        var schema = string.IsNullOrEmpty(table.SchemaName) ? null : table.SchemaName;
        var qualified = context.Provider.Dialect.QualifyName(null, schema, table.TableName);
        return context.Provider.ExecuteDdlAsync(context.Profile, $"DROP TABLE IF EXISTS {qualified}", ct);
    }

    // Recreate a table from a backup header and return the insertable column indices (rowversion skipped).
    internal static async Task<int[]> CreateTableAsync(ToolExecutionContext context, BackupTable table, CancellationToken ct)
    {
        var schema = string.IsNullOrEmpty(table.SchemaName) ? null : table.SchemaName;
        var spec = new CreateObjectSpec(
            DbObjectKind.Table, table.TableName, schema,
            table.Columns.Select(c => new NewColumnSpec(c.Name, c.DeclaredType, c.Nullable, c.PrimaryKey, AutoIncrement: false)).ToList());
        var create = context.Provider.BuildCreateStatement(spec);
        await context.Provider.ExecuteDdlAsync(context.Profile, create.Text, ct);

        return Enumerable.Range(0, table.Columns.Count)
            .Where(i => IsInsertable(context.ProviderId, table.Columns[i]))
            .ToArray();
    }

    // ---- v1 (materialised, legacy files) ----
    private static async Task RestoreV1Async(
        ToolExecutionContext context, string filePath, string? passphrase, bool dropRecreate,
        IProgress<ToolProgress> progress, CancellationToken ct)
    {
        var tables = LbakFormat.ReadPayloadV1(filePath, passphrase);
        var dialect = context.Provider.Dialect;
        for (var i = 0; i < tables.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var table = tables[i];
            var fraction = (double)(i + 1) / tables.Count;
            if (dropRecreate)
            {
                await DropTableIfExistsAsync(context, table, ct);
            }

            var keep = await CreateTableAsync(context, table, ct);
            progress.Report(new ToolProgress(context.Localizer.Get("restore.progress.createdTable", table.TableName), fraction));
            if (keep.Length == 0 || table.Rows.Count == 0)
            {
                continue;
            }

            var schema = string.IsNullOrEmpty(table.SchemaName) ? null : table.SchemaName;
            var qualified = dialect.QualifyName(null, schema, table.TableName);
            var columnList = string.Join(", ", keep.Select(k => dialect.QuoteIdentifier(table.Columns[k].Name)));
            var inserted = 0;
            foreach (var chunk in Chunk(table.Rows, InsertChunkSize))
            {
                ct.ThrowIfCancellationRequested();
                var statements = chunk.Select(row => BuildInsert(qualified, columnList, keep.Select(k => row[k]).ToArray())).ToList();
                await context.Provider.ExecuteBatchAsync(context.Profile, statements, ct);
                inserted += chunk.Count;
                progress.Report(new ToolProgress(context.Localizer.Get("restore.progress.inserted", table.TableName, inserted, table.Rows.Count)));
            }
        }
    }

    internal static SqlStatement BuildInsert(string qualified, string columnList, object?[] row)
    {
        var placeholders = string.Join(", ", row.Select((_, i) => $"@p{i}"));
        var parameters = row.Select((value, i) => new SqlParam($"p{i}", value)).ToList();
        return new SqlStatement($"INSERT INTO {qualified} ({columnList}) VALUES ({placeholders})", parameters);
    }

    internal static IEnumerable<List<T>> Chunk<T>(IReadOnlyList<T> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
        {
            yield return source.Skip(i).Take(size).ToList();
        }
    }
}
