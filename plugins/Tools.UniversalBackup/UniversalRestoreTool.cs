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

        IReadOnlyList<BackupTable> tables;
        try
        {
            tables = LbakFormat.ReadPayload(filePath, inputs.GetValueOrDefault("passphrase"));
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("Wrong passphrase or corrupted backup file.");
        }

        var dialect = context.Provider.Dialect;
        foreach (var table in tables)
        {
            ct.ThrowIfCancellationRequested();
            var schema = string.IsNullOrEmpty(table.SchemaName) ? null : table.SchemaName;

            var spec = new CreateObjectSpec(
                DbObjectKind.Table, table.TableName, schema,
                table.Columns.Select(c => new NewColumnSpec(c.Name, c.DeclaredType, c.Nullable, c.PrimaryKey, AutoIncrement: false)).ToList());
            var create = context.Provider.BuildCreateStatement(spec);
            await context.Provider.ExecuteDdlAsync(context.Profile, create.Text, ct);
            progress.Report(new ToolProgress($"Created table {table.TableName}"));

            await InsertRowsAsync(context, dialect, schema, table, progress, ct);
        }

        progress.Report(new ToolProgress("Restore complete. Reconnect or refresh the tree to see the data."));
    }

    private static async Task InsertRowsAsync(
        ToolExecutionContext context, Sdk.ISqlDialect dialect, string? schema, BackupTable table,
        IProgress<ToolProgress> progress, CancellationToken ct)
    {
        if (table.Rows.Count == 0)
        {
            return;
        }

        var qualified = dialect.QualifyName(null, schema, table.TableName);
        var columnList = string.Join(", ", table.Columns.Select(c => dialect.QuoteIdentifier(c.Name)));

        var inserted = 0;
        foreach (var chunk in Chunk(table.Rows, InsertChunkSize))
        {
            ct.ThrowIfCancellationRequested();
            var statements = chunk.Select(row => BuildInsert(qualified, columnList, row)).ToList();
            await context.Provider.ExecuteBatchAsync(context.Profile, statements, ct);
            inserted += chunk.Count;
            progress.Report(new ToolProgress($"{table.TableName}: inserted {inserted}/{table.Rows.Count} row(s)"));
        }
    }

    private static SqlStatement BuildInsert(string qualified, string columnList, object?[] row)
    {
        var placeholders = string.Join(", ", row.Select((_, i) => $"@p{i}"));
        var parameters = row.Select((value, i) => new SqlParam($"p{i}", value)).ToList();
        return new SqlStatement($"INSERT INTO {qualified} ({columnList}) VALUES ({placeholders})", parameters);
    }

    private static IEnumerable<List<T>> Chunk<T>(IReadOnlyList<T> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
        {
            yield return source.Skip(i).Take(size).ToList();
        }
    }
}
