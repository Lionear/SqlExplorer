using SqlExplorer.Sdk.Scripting;

namespace SqlExplorer.Tools.CopyTable;

/// <summary>
/// Copies the selected table to another connection and database the user picks. Reads the table's shape
/// (columns, primary key, identity) and its rows from the source, then either <b>runs the copy</b> —
/// creating and filling the table on the target with a live checklist — or <b>opens it as a script</b> in a
/// new query tab on the target to review and run. Same-provider only (the picker enforces it), so one
/// dialect renders both sides; reads via <c>information_schema</c> → Postgres, MySQL and SQL Server.
///
/// <para>The mode is remembered per session and persisted, so the tool re-opens on the choice you used last
/// (first run: Run the copy). Identity/auto-increment columns are handled by the "Keep identity values"
/// toggle — see <see cref="CreateTableWriter"/>.</para>
/// </summary>
public sealed class CopyTableTool : IToolPlugin
{
    private static readonly string[] SupportedProviders = ["postgres", "mysql", "sqlserver"];

    private const string ModeRun = "Run the copy";
    private const string ModeScript = "Open as script";
    private const string LastModeKey = "copy.lastMode";

    // Session-remembered dialog choice (persisted via SetPluginSetting too). Starts on "Run the copy".
    private string _lastMode = ModeRun;

    public string Id => "copy-table";
    public string Title => "Copy Table";
    public string? TitleKey => "copy.title";
    public string DialogTitle => "Copy Table";
    public string? DialogTitleKey => "copy.dialogTitle";

    public string? Description =>
        "Copies the table above to the connection and database you pick below. Run the copy to create and fill " +
        "the table on the target, or open it as a script to review the SQL first.";
    public string? DescriptionKey => "copy.description";

    public ToolTarget Target { get; } = new(
        ProviderIds: SupportedProviders,
        NodeKinds: [DbNodeKind.Table]);

    public IReadOnlyList<ToolField> Fields =>
    [
        new("toConnection", "Copy to connection", ToolFieldType.ConnectionPicker,
            Required: true, LabelKey: "copy.field.toConnection"),
        new("toDatabase", "Database on that connection", ToolFieldType.DatabasePicker,
            Required: true, LabelKey: "copy.field.toDatabase"),
        new("what", "What to copy", ToolFieldType.Choice, Default: WhatBoth,
            Choices: [WhatBoth, WhatStructure, WhatData], LabelKey: "copy.field.what"),
        new("rows", "Rows", ToolFieldType.Choice, Default: "All",
            Choices: ["All", "100", "1000", "5000"], LabelKey: "copy.field.rows"),
        new("keepIdentity", "Keep identity / sequence values", ToolFieldType.Bool, Default: "true",
            LabelKey: "copy.field.keepIdentity"),
        new("dropExisting", "Drop target table if it exists", ToolFieldType.Bool, Default: "false",
            LabelKey: "copy.field.dropExisting"),
        new("mode", "How", ToolFieldType.Choice, Default: _lastMode,
            Choices: [ModeRun, ModeScript], LabelKey: "copy.field.mode")
    ];

    private const string WhatBoth = "Structure + data";
    private const string WhatStructure = "Structure only";
    private const string WhatData = "Data only";

    public async Task ExecuteAsync(
        ToolExecutionContext context,
        IReadOnlyDictionary<string, string?> inputs,
        IProgress<ToolProgress> progress,
        CancellationToken ct)
    {
        var loc = context.Localizer;

        if (!TableReader.Supports(context.ProviderId))
        {
            progress.Report(new ToolProgress(loc.Get("copy.error.unsupported", context.ProviderId)));
            return;
        }

        if (context.Node is not { Kind: DbNodeKind.Table, Name: { Length: > 0 } tableName })
        {
            progress.Report(new ToolProgress(loc.Get("copy.error.noTable")));
            return;
        }

        var toConnection = inputs.GetValueOrDefault("toConnection");
        var toDatabase = inputs.GetValueOrDefault("toDatabase");
        if (string.IsNullOrWhiteSpace(toConnection) || string.IsNullOrWhiteSpace(toDatabase))
        {
            progress.Report(new ToolProgress(loc.Get("copy.error.noTarget")));
            return;
        }

        var what = inputs.GetValueOrDefault("what") ?? WhatBoth;
        var copyStructure = what != WhatData;
        var copyData = what != WhatStructure;
        var keepIdentity = inputs.GetValueOrDefault("keepIdentity") != "false";
        var dropExisting = inputs.GetValueOrDefault("dropExisting") == "true";
        var mode = inputs.GetValueOrDefault("mode") ?? ModeRun;
        int? limit = int.TryParse(inputs.GetValueOrDefault("rows"), out var n) ? n : null;

        // Remember the chosen mode for next time (session + persisted).
        _lastMode = mode;
        context.Host.SetPluginSetting(LastModeKey, mode);

        var reader = new TableReader(context.Provider);
        progress.Report(new ToolProgress(loc.Get("copy.progress.reading", tableName),
            ItemKey: "schema", ItemStatus: ToolItemStatus.Running));

        var model = await reader.ReadAsync(context.Profile, context.ProviderId, tableName, ct);
        if (model is null)
        {
            progress.Report(new ToolProgress(loc.Get("copy.error.notFound", tableName),
                ItemKey: "schema", ItemStatus: ToolItemStatus.Error));
            return;
        }

        progress.Report(new ToolProgress(loc.Get("copy.progress.read", tableName, model.Columns.Count),
            ItemKey: "schema", ItemStatus: ToolItemStatus.Done));

        var dialect = context.Provider.Dialect;
        var literalDialect = SqlValueLiteral.DialectFor(context.ProviderId);
        var quotedTarget = CreateTableWriter.QuoteTable(context.ProviderId, model);

        // The insert columns depend on whether we keep the identity; the source SELECT must match so the read
        // rows and the insert line up.
        var insertColumns = CreateTableWriter.ColumnsForInsert(model, keepIdentity);
        QueryResult? data = null;
        if (copyData)
        {
            var columnList = string.Join(", ", insertColumns.Select(c => CreateTableWriter.QuoteId(context.ProviderId, c.Name)));
            var sourceQualified = dialect.QualifyName(context.Profile.Database, model.Schema, model.Name);
            var select = $"SELECT {columnList} FROM {sourceQualified}";
            var dataSql = limit is { } lim ? dialect.Paginate(select, lim, 0) : $"{select};";
            progress.Report(new ToolProgress(loc.Get("copy.progress.readingRows",
                limit is { } l ? l.ToString() : loc.Get("copy.all"))));
            data = await context.Provider.ExecuteQueryAsync(context.Profile, dataSql, ct);
        }

        var ambiguous = await reader.IsAmbiguousAsync(context.Profile, context.ProviderId, tableName, ct);

        if (mode == ModeScript)
        {
            await OpenAsScriptAsync(context, model, data, dropExisting, copyStructure, copyData, keepIdentity,
                dialect, literalDialect, quotedTarget, toConnection!, toDatabase!, ambiguous, tableName, progress, loc);
        }
        else
        {
            await RunCopyAsync(context, model, data, dropExisting, copyStructure, copyData, keepIdentity,
                dialect, literalDialect, quotedTarget, toConnection!, toDatabase!, progress, loc, ct);
        }
    }

    private async Task RunCopyAsync(
        ToolExecutionContext context, TableModel model, QueryResult? data, bool dropExisting, bool copyStructure,
        bool copyData, bool keepIdentity, ISqlDialect dialect, SqlLiteralDialect literalDialect, string quotedTarget,
        string toConnection, string toDatabase, IProgress<ToolProgress> progress, IPluginLocalizer loc,
        CancellationToken ct)
    {
        if (context.Host.OpenConnection(toConnection, toDatabase) is not { } target)
        {
            progress.Report(new ToolProgress(loc.Get("copy.error.noTarget"),
                ItemKey: "create", ItemStatus: ToolItemStatus.Error));
            return;
        }

        if (copyStructure)
        {
            progress.Report(new ToolProgress(loc.Get("copy.progress.creating"),
                ItemKey: "create", ItemStatus: ToolItemStatus.Running));
            try
            {
                if (dropExisting)
                {
                    await target.Provider.ExecuteDdlAsync(target.Profile,
                        CreateTableWriter.DropIfExists(context.ProviderId, model), ct);
                }

                await target.Provider.ExecuteDdlAsync(target.Profile,
                    CreateTableWriter.Build(context.ProviderId, model, keepIdentity), ct);
            }
            catch (Exception ex)
            {
                progress.Report(new ToolProgress(ex.Message, ItemKey: "create", ItemStatus: ToolItemStatus.Error));
                throw;
            }

            progress.Report(new ToolProgress(loc.Get("copy.progress.created"),
                ItemKey: "create", ItemStatus: ToolItemStatus.Done));
        }

        if (copyData && data is not null)
        {
            var inserts = InsertScripter.Build(quotedTarget, data.Columns, data.Rows, dialect, literalDialect);
            progress.Report(new ToolProgress(loc.Get("copy.progress.copyingRows", data.Rows.Count),
                ItemKey: "rows", ItemStatus: ToolItemStatus.Running));
            try
            {
                if (data.Rows.Count > 0)
                {
                    await target.Provider.ExecuteScriptAsync(target.Profile, inserts, ct);
                }
            }
            catch (Exception ex)
            {
                progress.Report(new ToolProgress(ex.Message, ItemKey: "rows", ItemStatus: ToolItemStatus.Error));
                throw;
            }

            progress.Report(new ToolProgress(loc.Get("copy.progress.copiedRows", data.Rows.Count),
                ItemKey: "rows", ItemStatus: ToolItemStatus.Done));
        }

        progress.Report(new ToolProgress(loc.Get("copy.result.ran", model.Name, toDatabase),
            ItemKey: "done", ItemStatus: ToolItemStatus.Done));
    }

    private async Task OpenAsScriptAsync(
        ToolExecutionContext context, TableModel model, QueryResult? data, bool dropExisting, bool copyStructure,
        bool copyData, bool keepIdentity, ISqlDialect dialect, SqlLiteralDialect literalDialect, string quotedTarget,
        string toConnection, string toDatabase, bool ambiguous, string tableName, IProgress<ToolProgress> progress,
        IPluginLocalizer loc)
    {
        var parts = new List<string>();
        var header = loc.Get("copy.script.header", $"{context.Profile.Name} / {context.Profile.Database}",
            toDatabase, data?.Rows.Count ?? 0);
        if (ambiguous)
        {
            header += "\n" + loc.Get("copy.script.ambiguous", model.Schema);
        }

        parts.Add(header);

        if (copyStructure)
        {
            if (dropExisting)
            {
                parts.Add(CreateTableWriter.DropIfExists(context.ProviderId, model));
            }

            parts.Add(CreateTableWriter.Build(context.ProviderId, model, keepIdentity));
        }

        if (copyData && data is not null)
        {
            parts.Add(InsertScripter.Build(quotedTarget, data.Columns, data.Rows, dialect, literalDialect));
        }

        context.Host.OpenQueryEditorOn(toConnection, toDatabase, string.Join("\n\n", parts));
        progress.Report(new ToolProgress(loc.Get("copy.result.opened", toDatabase, data?.Rows.Count ?? 0)));
    }
}
