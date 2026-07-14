using System.IO;
using Lionear.SqlExplorer.Sdk.Branding;
using Lionear.SqlExplorer.Sdk.Settings;

namespace Lionear.SqlExplorer.Tools.UniversalBackup;

/// <summary>
/// Backs up a database to a single <c>.lbak</c> file, engine-agnostically: it walks the schema, reads
/// each table and writes the rows — all through the host's <see cref="Lionear.SqlExplorer.Sdk.IDbProvider"/>,
/// so it works for every current and future provider without bundling any driver. Read-only, hence not
/// destructive. Also declares a persistent "default backup folder" setting (Settings ▸ Plugins) so the
/// file field can be left empty for an automatic <c>&lt;database&gt;-&lt;timestamp&gt;.lbak</c> name there.
/// </summary>
public sealed class UniversalBackupTool : IToolPlugin, IPluginSettings
{
    private const string DefaultFolderKey = "defaultFolder";

    public string Id => "universal-backup";

    public string Title => "Backup…";

    public IReadOnlyList<string> MenuPath => ["Backup & Restore"];

    public ProviderIcon? Icon { get; } = ProviderIconLoader.Load(typeof(UniversalBackupTool), "💾");

    // Offered on a Database node — plus the SQLite connection root, whose single file IS the database
    // (SQLite has no Database node). Not other providers' roots, where it would mean "every database".
    public ToolTarget Target { get; } = new(NodeKinds: [DbNodeKind.Database], ConnectionRootProviderIds: ["sqlite"]);

    public IReadOnlyList<ToolField> Fields { get; } =
    [
        new("filePath", "Backup file (optional if a default folder is set)", ToolFieldType.File,
            Required: false, Placeholder: "Leave empty to use the default backup folder", FileExtensions: ["lbak"], SaveFile: true),
        new("passphrase", "Passphrase (optional — leave empty for an unencrypted backup)", ToolFieldType.Password)
    ];

    public IReadOnlyList<PluginSettingField> SettingsFields { get; } =
    [
        new(DefaultFolderKey, "Default backup folder", PluginSettingFieldType.Folder,
            Placeholder: "Used when no file is chosen in the Backup dialog")
    ];

    public async Task ExecuteAsync(
        ToolExecutionContext context,
        IReadOnlyDictionary<string, string?> inputs,
        IProgress<ToolProgress> progress,
        CancellationToken ct)
    {
        // SQLite runs on the connection root (no Database node): fall back to the connection name.
        var databaseName = context.Profile.Database ?? context.Node?.Name ?? context.Profile.Name;
        var filePath = ResolveTargetPath(inputs.GetValueOrDefault("filePath"), context, databaseName, progress);
        var passphrase = inputs.GetValueOrDefault("passphrase");

        progress.Report(new ToolProgress("Reading schema…"));
        var tableRefs = await SchemaReader.CollectTablesAsync(context.Provider, context.Profile, context.Node, ct);
        progress.Report(new ToolProgress($"Found {tableRefs.Count} table(s)."));

        var meta = new LbakMeta(
            context.ProviderId,
            context.Provider.DisplayName,
            databaseName,
            DateTime.UtcNow.ToString("o"),
            AppVersion: "1.0.0",
            tableRefs.Count,
            Encrypted: !string.IsNullOrEmpty(passphrase),
            FormatVersion: 2);

        // The write pipeline (gzip + chunked-GCM + file) does synchronous CPU/IO work; run it off the UI
        // thread while the DB reader feeds it. Streaming means a huge LOB cell never has to fit in memory.
        var dialect = context.Provider.Dialect;
        var written = await Task.Run(async () =>
        {
            using var writer = LbakFormat.CreateWriter(filePath, meta, passphrase);
            var tablesWritten = 0;
            for (var i = 0; i < tableRefs.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var tableRef = tableRefs[i];
                var fraction = (double)(i + 1) / tableRefs.Count;

                var columns = await SchemaReader.ReadColumnsAsync(context.Provider, context.Profile, tableRef.Path, ct);
                if (columns.Count == 0)
                {
                    progress.Report(new ToolProgress($"Skipped {tableRef.Table} (no readable columns).", fraction));
                    continue;
                }

                writer.BeginTable(tableRef.Schema ?? string.Empty, tableRef.Table, columns);
                var qualified = dialect.QualifyName(null, tableRef.Schema, tableRef.Table);
                var columnList = string.Join(", ", columns.Select(c => dialect.QuoteIdentifier(c.Name)));
                var visitor = new BackupRowVisitor(writer);
                await context.Provider.StreamQueryAsync(context.Profile, $"SELECT {columnList} FROM {qualified}", visitor, ct);

                tablesWritten++;
                progress.Report(new ToolProgress($"Table {i + 1}/{tableRefs.Count}: {tableRef.Table} — {visitor.RowCount} row(s)", fraction));
            }

            return tablesWritten;
        }, ct);

        progress.Report(new ToolProgress($"Saved {written} table(s) to {filePath}", 1.0));
    }

    // Use the chosen file, or fall back to "<defaultFolder>/<database>-<timestamp>.lbak" from the setting.
    private static string ResolveTargetPath(string? chosen, ToolExecutionContext context, string databaseName, IProgress<ToolProgress> progress)
    {
        if (!string.IsNullOrWhiteSpace(chosen))
        {
            return chosen;
        }

        var folder = context.Host.GetPluginSetting(DefaultFolderKey);
        if (string.IsNullOrWhiteSpace(folder))
        {
            throw new InvalidOperationException("Choose a file to write to, or set a default backup folder in Settings ▸ Plugins.");
        }

        var fileName = $"{Sanitize(databaseName)}-{DateTime.Now:yyyyMMdd-HHmmss}.lbak";
        var path = Path.Combine(folder, fileName);
        progress.Report(new ToolProgress($"No file chosen — using the default folder: {path}"));
        return path;
    }

    private static string Sanitize(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
