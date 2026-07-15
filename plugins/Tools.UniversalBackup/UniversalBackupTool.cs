using System.IO;
using SqlExplorer.Sdk.Branding;
using SqlExplorer.Sdk.Settings;

namespace SqlExplorer.Tools.UniversalBackup;

/// <summary>
/// Backs up a database to a single <c>.lbak</c> file, engine-agnostically: it walks the schema, reads
/// each table and writes the rows — all through the host's <see cref="SqlExplorer.Sdk.IDbProvider"/>,
/// so it works for every current and future provider without bundling any driver. Read-only, hence not
/// destructive. Also declares a persistent "default backup folder" setting (Settings ▸ Plugins) so the
/// file field can be left empty for an automatic <c>&lt;database&gt;-&lt;timestamp&gt;.lbak</c> name there.
/// </summary>
public sealed class UniversalBackupTool : IToolPlugin, IPluginSettings, ICustomToolUi
{
    // Route B: the object-selection tree replaces the generic form. Fields stay declared as a graceful
    // fallback but are unused while this view is supplied.
    public Avalonia.Controls.Control CreateView(IToolUiContext context) => new BackupSelectionView(context);

    private const string DefaultFolderKey = "defaultFolder";

    public string Id => "universal-backup";

    public string Title => "Backup";
    public string? TitleKey => "backup.title";
    public string? DialogTitleKey => "backup.title";

    public IReadOnlyList<string> MenuPath => ["Backup & Restore"];

    public ProviderIcon? Icon { get; } = ProviderIconLoader.Load(typeof(UniversalBackupTool), "💾");

    // Offered on a Database node — plus the SQLite connection root, whose single file IS the database
    // (SQLite has no Database node). Not other providers' roots, where it would mean "every database".
    public ToolTarget Target { get; } = new(NodeKinds: [DbNodeKind.Database], ConnectionRootProviderIds: ["sqlite"]);

    public IReadOnlyList<ToolField> Fields { get; } =
    [
        new("filePath", "Backup file (optional if a default folder is set)", ToolFieldType.File,
            Required: false, Placeholder: "Leave empty to use the default backup folder", FileExtensions: ["lbak"], SaveFile: true,
            LabelKey: "backup.field.file.label", PlaceholderKey: "backup.field.file.placeholder"),
        new("passphrase", "Passphrase (optional — leave empty for an unencrypted backup)", ToolFieldType.Password,
            LabelKey: "backup.field.passphrase.label")
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

        progress.Report(new ToolProgress(context.Localizer["backup.progress.readingSchema"]));
        var tableRefs = await SchemaReader.CollectTablesAsync(context.Provider, context.Profile, context.Node, ct);
        progress.Report(new ToolProgress(context.Localizer.Get("backup.progress.foundTables", tableRefs.Count)));

        // Non-table objects (views/procedures/functions/triggers) — names only here (fast); each selected
        // object's DDL is resolved at write time. Best-effort: a failure here must never break the (working)
        // table backup, so fall back to no objects.
        IReadOnlyList<SchemaReader.BackupObjectRef> objectRefs = [];
        try
        {
            objectRefs = await SchemaReader.CollectObjectRefsAsync(context.Provider, context.Profile, context.Node, ct);
            if (objectRefs.Count > 0)
            {
                progress.Report(new ToolProgress(context.Localizer.Get("backup.progress.foundObjects", objectRefs.Count)));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            progress.Report(new ToolProgress(context.Localizer.Get("backup.progress.objectsSkipped", ex.Message)));
        }

        // Per-object selection (Route B). Absent = back everything up (Route A / no view interaction).
        var selection = BackupSelection.Parse(inputs.GetValueOrDefault("selection"));
        var selectedRefs = objectRefs.Where(r => BackupSelection.IsObjectSelected(selection, r.Kind, r.Schema, r.Name)).ToList();

        var meta = new LbakMeta(
            context.ProviderId,
            context.Provider.DisplayName,
            databaseName,
            DateTime.UtcNow.ToString("o"),
            AppVersion: "1.0.0",
            tableRefs.Count,
            Encrypted: !string.IsNullOrEmpty(passphrase),
            FormatVersion: 3,
            ViewCount: selectedRefs.Count(r => r.Kind == LbakObjectKind.View),
            RoutineCount: selectedRefs.Count(r => r.Kind is LbakObjectKind.Procedure or LbakObjectKind.Function),
            TriggerCount: selectedRefs.Count(r => r.Kind == LbakObjectKind.Trigger));

        // The write pipeline (gzip + chunked-GCM + file) does synchronous CPU/IO work; run it off the UI
        // thread while the DB reader feeds it. Streaming means a huge LOB cell never has to fit in memory.
        var dialect = context.Provider.Dialect;
        var objectsWritten = 0;
        var written = await Task.Run(async () =>
        {
            using var writer = LbakFormat.CreateWriter(filePath, meta, passphrase);
            var tablesWritten = 0;
            for (var i = 0; i < tableRefs.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var tableRef = tableRefs[i];
                var fraction = (double)(i + 1) / tableRefs.Count;

                var dataChoice = BackupSelection.TableDataChoice(selection, tableRef.Schema, tableRef.Table);
                if (dataChoice is null)
                {
                    continue; // table not selected
                }

                var itemKey = $"table:{tableRef.Schema}.{tableRef.Table}";
                progress.Report(new ToolProgress(
                    context.Localizer.Get("backup.progress.tableRunning", tableRef.Table), fraction,
                    ItemKey: itemKey, ItemStatus: ToolItemStatus.Running));

                var columns = await SchemaReader.ReadColumnsAsync(context.Provider, context.Profile, tableRef.Path, ct);
                if (columns.Count == 0)
                {
                    progress.Report(new ToolProgress(
                        context.Localizer.Get("backup.progress.skippedTable", tableRef.Table), fraction,
                        ItemKey: itemKey, ItemStatus: ToolItemStatus.Skipped));
                    continue;
                }

                writer.BeginTable(tableRef.Schema ?? string.Empty, tableRef.Table, columns);
                long rowCount = 0;
                if (dataChoice == true) // include data; false = schema-only (header written, no rows)
                {
                    var qualified = dialect.QualifyName(null, tableRef.Schema, tableRef.Table);
                    var columnList = string.Join(", ", columns.Select(c => dialect.QuoteIdentifier(c.Name)));
                    var visitor = new BackupRowVisitor(writer);
                    await context.Provider.StreamQueryAsync(context.Profile, $"SELECT {columnList} FROM {qualified}", visitor, ct);
                    rowCount = visitor.RowCount;
                }

                tablesWritten++;
                progress.Report(new ToolProgress(
                    context.Localizer.Get("backup.progress.table", i + 1, tableRefs.Count, tableRef.Table, rowCount), fraction,
                    ItemKey: itemKey, ItemStatus: ToolItemStatus.Done));
            }

            // Resolve + append the selected non-table objects. An object whose DDL the provider can't supply
            // (encrypted/CLR routine, …) is logged as skipped rather than silently dropped.
            foreach (var objectRef in selectedRefs)
            {
                ct.ThrowIfCancellationRequested();
                var itemKey = $"{objectRef.Kind.ToString().ToLowerInvariant()}:{objectRef.Schema}.{objectRef.Name}";
                var definition = await SchemaReader.ResolveDefinitionAsync(context.Provider, context.Profile, objectRef, ct);
                if (string.IsNullOrWhiteSpace(definition))
                {
                    progress.Report(new ToolProgress(
                        context.Localizer.Get("backup.progress.objectNoDdl", objectRef.Name),
                        ItemKey: itemKey, ItemStatus: ToolItemStatus.Skipped));
                    continue;
                }

                writer.AddObject(new BackupObject(objectRef.Kind, objectRef.Schema, objectRef.Name, objectRef.ParentTable, definition));
                objectsWritten++;
                progress.Report(new ToolProgress(
                    context.Localizer.Get("backup.progress.objectItem", objectRef.Name),
                    ItemKey: itemKey, ItemStatus: ToolItemStatus.Done));
            }

            return tablesWritten;
        }, ct);

        if (objectsWritten > 0)
        {
            progress.Report(new ToolProgress(context.Localizer.Get("backup.progress.savedObjects", objectsWritten)));
        }

        progress.Report(new ToolProgress(context.Localizer.Get("backup.progress.saved", written, filePath), 1.0));
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
            throw new InvalidOperationException(context.Localizer["backup.error.noFile"]);
        }

        var fileName = $"{Sanitize(databaseName)}-{DateTime.Now:yyyyMMdd-HHmmss}.lbak";
        var path = Path.Combine(folder, fileName);
        progress.Report(new ToolProgress(context.Localizer.Get("backup.progress.defaultFolder", path)));
        return path;
    }

    private static string Sanitize(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
