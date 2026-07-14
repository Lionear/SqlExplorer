using System.Security.Cryptography;
using Lionear.SqlExplorer.Sdk.Branding;

namespace Lionear.SqlExplorer.Tools.UniversalBackup;

/// <summary>
/// Restores a <c>.lbak</c> file into the connected database: recreates each table and inserts its rows
/// through the host's provider. Destructive (creates objects + writes data), so the host confirms first.
/// A separate tool from Backup — same reason Backup/Restore are split everywhere: Backup is safe, Restore
/// is not, and each deserves its own menu item. MVP: same-engine only (cross-engine is blocked).
/// </summary>
public sealed class UniversalRestoreTool : IToolPlugin
{
    private const int InsertChunkSize = 500;

    public string Id => "universal-restore";

    public string Title => "Restore from backup…";

    public IReadOnlyList<string> MenuPath => ["Backup & Restore"];

    public ProviderIcon? Icon { get; } = ProviderIconLoader.Load(typeof(UniversalRestoreTool), "♻");

    public ToolTarget Target { get; } = new(NodeKinds: [DbNodeKind.Database]);

    public bool IsDestructive => true;

    public IReadOnlyList<ToolField> Fields { get; } =
    [
        new("filePath", "Backup file", ToolFieldType.File, Required: true, FileExtensions: ["lbak"], SaveFile: false),
        new("passphrase", "Passphrase (only if the backup is encrypted)", ToolFieldType.Password)
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
            throw new InvalidOperationException("Choose a backup file to restore.");
        }

        var meta = LbakFormat.ReadMeta(filePath);
        progress.Report(new ToolProgress($"Backup from {meta.EngineDisplayName} · \"{meta.DatabaseName}\" · {meta.TableCount} table(s)."));

        // Cross-engine restore is intentionally blocked in v1 (dialect-specific column types).
        if (!string.Equals(meta.ProviderId, context.ProviderId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"This backup is from {meta.EngineDisplayName}; restoring to a different engine isn't supported yet.");
        }

        var passphrase = inputs.GetValueOrDefault("passphrase");

        // Streaming CPU/IO (decompress + decrypt + LOB spooling) off the UI thread.
        await Task.Run(async () =>
        {
            try
            {
                if (meta.FormatVersion >= 2)
                {
                    var visitor = new RestoreVisitor(context, progress);
                    await LbakFormat.ReadStreamingAsync(filePath, passphrase, visitor, ct);
                    await visitor.FinishAsync(ct);
                }
                else
                {
                    await RestoreV1Async(context, filePath, passphrase, progress, ct);
                }
            }
            catch (CryptographicException)
            {
                throw new InvalidOperationException("Wrong passphrase or corrupted backup file.");
            }
        }, ct);

        progress.Report(new ToolProgress("Restore complete. Reconnect or refresh the tree to see the data.", 1.0));
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
        ToolExecutionContext context, string filePath, string? passphrase, IProgress<ToolProgress> progress, CancellationToken ct)
    {
        var tables = LbakFormat.ReadPayloadV1(filePath, passphrase);
        var dialect = context.Provider.Dialect;
        for (var i = 0; i < tables.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var table = tables[i];
            var fraction = (double)(i + 1) / tables.Count;
            var keep = await CreateTableAsync(context, table, ct);
            progress.Report(new ToolProgress($"Created table {table.TableName}", fraction));
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
                progress.Report(new ToolProgress($"{table.TableName}: inserted {inserted}/{table.Rows.Count} row(s)"));
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
