using SqlExplorer.Sdk.Scripting;

namespace SqlExplorer.Tools.CopyTable;

/// <summary>
/// Copies the selected table to another connection and database the user picks. Reads the table's shape
/// (columns, primary key, identity, unique constraints, secondary indexes and foreign keys) and its rows
/// from the source, then either <b>runs the copy</b> — creating and filling the table on the target with a
/// live checklist — or <b>opens it as a script</b> in a new query tab on the target to review and run.
/// Same-provider only (the picker enforces it), so one dialect renders both sides.
///
/// <para>The read comes from <c>Shared.Schema</c>, the same reader Schema Diff uses, narrowed to the one
/// clicked table — which is what brings SQLite along with Postgres, MySQL and SQL Server. The tool only
/// knows the table's <em>name</em> (the clicked node), not its schema, so it resolves the schema from the
/// read; when the name is ambiguous across schemas it takes the first and says so.</para>
///
/// <para>The mode is remembered per session and persisted, so the tool re-opens on the choice you used last
/// (first run: Run the copy). Identity/auto-increment columns are handled by the "Keep identity values"
/// toggle, indexes and foreign keys by their own switch — see <see cref="CreateTableWriter"/>.</para>
/// </summary>
public sealed class CopyTableTool : IToolPlugin, ICustomToolUi
{
    private static readonly string[] SupportedProviders = ["postgres", "mysql", "sqlserver", "sqlite"];

    private const string ModeRun = "Run the copy";
    private const string ModeScript = "Open as script";
    private const string LastModeKey = "copy.lastMode";

    // Session-remembered dialog choice (persisted via SetPluginSetting too). Starts on "Run the copy".
    private string _lastMode = ModeRun;

    // The live dialog view, and what the last run landed — the view owns the whole dialog lifecycle
    // (IToolDialogLifecycle), so the tool hands it the run's summary and answers its "Open target table".
    private CopyTableView? _view;
    private IToolHost? _host;
    private string? _openConnectionId;
    private string? _openDatabase;
    private string? _openSelect;

    // Rows per INSERT batch: small enough that the progress bar moves on a large table, big enough that a
    // copy isn't dominated by round-trips.
    private const int BatchRows = 500;

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
        new("includeIndexes", "Include indexes & foreign keys", ToolFieldType.Bool, Default: "true",
            LabelKey: "copy.field.includeIndexes"),
        new("dropExisting", "Drop target table if it exists", ToolFieldType.Bool, Default: "false",
            LabelKey: "copy.field.dropExisting"),
        new("mode", "How", ToolFieldType.Choice, Default: _lastMode,
            Choices: [ModeRun, ModeScript], LabelKey: "copy.field.mode")
    ];

    private const string WhatBoth = "Structure + data";
    private const string WhatStructure = "Structure only";
    private const string WhatData = "Data only";

    // Route B: supply the tailored dialog (From → To pickers, segmented options, mode cards) instead of the
    // host's generic field form. Values still flow back through IToolUiContext under the same field keys.
    public Avalonia.Controls.Control CreateView(IToolUiContext context)
    {
        var view = new CopyTableView(context, _lastMode, context.Node?.Name ?? "table");
        view.OpenTargetRequested += () =>
        {
            if (_host is { } host && _openConnectionId is { } id && _openSelect is { } sql)
            {
                host.OpenQueryEditorOn(id, _openDatabase, sql);
            }
        };
        _view = view;
        return view;
    }

    public async Task ExecuteAsync(
        ToolExecutionContext context,
        IReadOnlyDictionary<string, string?> inputs,
        IProgress<ToolProgress> progress,
        CancellationToken ct)
    {
        var loc = context.Localizer;

        if (!SchemaReader.Supports(context.ProviderId))
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
        var includeIndexes = inputs.GetValueOrDefault("includeIndexes") != "false";
        var dropExisting = inputs.GetValueOrDefault("dropExisting") == "true";
        var mode = inputs.GetValueOrDefault("mode") ?? ModeRun;
        int? limit = int.TryParse(inputs.GetValueOrDefault("rows"), out var n) ? n : null;

        // Remember the chosen mode for next time (session + persisted).
        _lastMode = mode;
        context.Host.SetPluginSetting(LastModeKey, mode);

        var started = DateTime.UtcNow;

        // Remember what the view's "Open target table" link should open once the copy lands.
        _host = context.Host;
        _openConnectionId = toConnection;
        _openDatabase = toDatabase;
        _openSelect = null;

        progress.Report(new ToolProgress(loc.Get("copy.progress.reading", tableName),
            ItemKey: "schema", ItemStatus: ToolItemStatus.Running));

        // One narrowed read covers both questions: the table's shape, and whether the name is ambiguous —
        // the snapshot carries a table per schema that has one.
        var snapshot = await new SchemaReader(context.Provider)
            .ReadAsync(context.Profile, context.ProviderId, ct, onlyTable: tableName);

        if (snapshot.Tables is not [var model, ..])
        {
            progress.Report(new ToolProgress(loc.Get("copy.error.notFound", tableName),
                ItemKey: "schema", ItemStatus: ToolItemStatus.Error));
            return;
        }

        var ambiguous = snapshot.Tables.Count > 1;

        progress.Report(new ToolProgress(loc.Get("copy.progress.read", tableName, model.Columns.Count),
            ItemKey: "schema", ItemStatus: ToolItemStatus.Done,
            Detail: model.PrimaryKey is { Columns.Count: > 0 }
                ? loc.Get("copy.detail.columnsPk", model.Columns.Count)
                : loc.Get("copy.detail.columns", model.Columns.Count)));

        var dialect = context.Provider.Dialect;
        var literalDialect = SqlValueLiteral.DialectFor(context.ProviderId);
        var writer = new CreateTableWriter(SqlDialect.For(context.ProviderId));
        var quotedTarget = writer.QuoteTable(model);

        // The insert columns depend on whether we keep the identity; the source SELECT must match so the read
        // rows and the insert line up.
        var insertColumns = CreateTableWriter.ColumnsForInsert(model, keepIdentity);
        QueryResult? data = null;
        if (copyData)
        {
            var columnList = string.Join(", ", insertColumns.Select(c => writer.Quote(c.Name)));
            var sourceQualified = dialect.QualifyName(context.Profile.Database, model.Schema, model.Name);
            var select = $"SELECT {columnList} FROM {sourceQualified}";
            var dataSql = limit is { } lim ? dialect.Paginate(select, lim, 0) : $"{select};";
            progress.Report(new ToolProgress(loc.Get("copy.progress.readingRows",
                limit is { } l ? l.ToString() : loc.Get("copy.all"))));
            data = await context.Provider.ExecuteQueryAsync(context.Profile, dataSql, ct);
        }

        if (mode == ModeScript)
        {
            progress.Report(new ToolProgress(loc.Get("copy.progress.scripting"),
                ItemKey: "script", ItemStatus: ToolItemStatus.Running));
            OpenAsScript(context, model, data, dropExisting, copyStructure, copyData, keepIdentity, includeIndexes,
                dialect, literalDialect, writer, quotedTarget, toConnection!, toDatabase!, ambiguous, progress, loc);
            _view?.SetSummary(new CopySummary(tableName, $"{toDatabase}.{model.Name}", data?.Rows.Count ?? 0,
                DateTime.UtcNow - started, Scripted: true));
        }
        else
        {
            await RunCopyAsync(context, model, data, dropExisting, copyStructure, copyData, keepIdentity,
                includeIndexes, dialect, literalDialect, writer, quotedTarget, toConnection!, toDatabase!,
                progress, loc, ct);
            _openSelect = $"SELECT * FROM {quotedTarget};";
            _view?.SetSummary(new CopySummary(tableName, $"{toDatabase}.{model.Name}", data?.Rows.Count ?? 0,
                DateTime.UtcNow - started, Scripted: false));
        }
    }

    private async Task RunCopyAsync(
        ToolExecutionContext context, TableDef model, QueryResult? data, bool dropExisting, bool copyStructure,
        bool copyData, bool keepIdentity, bool includeIndexes, ISqlDialect dialect, SqlLiteralDialect literalDialect,
        CreateTableWriter writer, string quotedTarget, string toConnection, string toDatabase,
        IProgress<ToolProgress> progress, IPluginLocalizer loc, CancellationToken ct)
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
                    await target.Provider.ExecuteDdlAsync(target.Profile, writer.DropIfExists(model), ct);
                }

                await target.Provider.ExecuteDdlAsync(target.Profile,
                    writer.Build(model, keepIdentity, includeIndexes), ct);
            }
            catch (Exception ex)
            {
                progress.Report(new ToolProgress(ex.Message, ItemKey: "create", ItemStatus: ToolItemStatus.Error));
                throw;
            }

            progress.Report(new ToolProgress(loc.Get("copy.progress.created"),
                ItemKey: "create", ItemStatus: ToolItemStatus.Done,
                Detail: dropExisting ? loc.Get("copy.detail.replaced") : loc.Get("copy.detail.created")));
        }

        if (copyData && data is not null)
        {
            var total = data.Rows.Count;
            progress.Report(new ToolProgress(loc.Get("copy.progress.copyingRows", total),
                ItemKey: "rows", ItemStatus: ToolItemStatus.Running, Fraction: total == 0 ? 1 : 0,
                Detail: loc.Get("copy.detail.rowsOf", 0, total)));

            // Insert in batches rather than one giant script: the dialog can show real progress, and a huge
            // table doesn't have to be serialised into a single statement blob first.
            var copied = 0;
            try
            {
                while (copied < total)
                {
                    ct.ThrowIfCancellationRequested();
                    var batch = data.Rows.Skip(copied).Take(BatchRows).ToList();
                    var inserts = InsertScripter.Build(quotedTarget, data.Columns, batch, dialect, literalDialect);
                    await target.Provider.ExecuteScriptAsync(target.Profile, inserts, ct);
                    copied += batch.Count;
                    progress.Report(new ToolProgress(loc.Get("copy.progress.copyingRows", total),
                        ItemKey: "rows", ItemStatus: ToolItemStatus.Running, Fraction: (double)copied / total,
                        Detail: loc.Get("copy.detail.rowsOf", copied, total)));
                }
            }
            catch (OperationCanceledException)
            {
                progress.Report(new ToolProgress(loc.Get("copy.progress.copiedRows", copied),
                    ItemKey: "rows", ItemStatus: ToolItemStatus.Error,
                    Detail: loc.Get("copy.detail.rowsOf", copied, total)));
                throw;
            }
            catch (Exception ex)
            {
                progress.Report(new ToolProgress(ex.Message, ItemKey: "rows", ItemStatus: ToolItemStatus.Error,
                    Detail: loc.Get("copy.detail.rowsOf", copied, total)));
                throw;
            }

            progress.Report(new ToolProgress(loc.Get("copy.progress.copiedRows", copied),
                ItemKey: "rows", ItemStatus: ToolItemStatus.Done,
                Detail: loc.Get("copy.detail.rowsCopied", copied)));
        }

        if (copyStructure && includeIndexes)
        {
            await AddIndexesAndForeignKeysAsync(target, model, writer, progress, loc, ct);
        }

        progress.Report(new ToolProgress(loc.Get("copy.result.ran", model.Name, toDatabase),
            ItemKey: "done", ItemStatus: ToolItemStatus.Done));
    }

    /// <summary>
    /// Creates the copied table's secondary indexes and foreign keys, once its rows are in — indexing an
    /// empty table and then filling it is the slower way round, and a foreign key can only be checked
    /// against rows that exist.
    ///
    /// <para>Each statement runs on its own and a failure is counted rather than thrown: a foreign key points
    /// at a table this copy did not bring along, so it may legitimately not exist on the target. Losing a
    /// constraint is worth reporting; it is not worth discarding a copy that otherwise landed, and the step's
    /// detail says how many of each made it.</para>
    /// </summary>
    private static async Task AddIndexesAndForeignKeysAsync(
        ToolConnection target, TableDef model, CreateTableWriter writer, IProgress<ToolProgress> progress,
        IPluginLocalizer loc, CancellationToken ct)
    {
        var indexes = writer.Indexes(model);
        var foreignKeys = writer.ForeignKeys(model);

        // Reported even when there is nothing to create: the dialog planned this step from the same two
        // conditions the caller checked, and a step nobody reports would sit there pending after a clean run.
        if (indexes.Count == 0 && foreignKeys.Count == 0)
        {
            progress.Report(new ToolProgress(loc.Get("copy.progress.indexesDone", 0, 0),
                ItemKey: "indexes", ItemStatus: ToolItemStatus.Done, Detail: loc.Get("copy.detail.indexesNone")));
            return;
        }

        progress.Report(new ToolProgress(loc.Get("copy.progress.indexes"),
            ItemKey: "indexes", ItemStatus: ToolItemStatus.Running));

        var indexesMade = await RunEachAsync(target, indexes, ct);
        var keysMade = await RunEachAsync(target, foreignKeys, ct);

        var complete = indexesMade == indexes.Count && keysMade == foreignKeys.Count;
        progress.Report(new ToolProgress(
            loc.Get("copy.progress.indexesDone", indexesMade, keysMade),
            ItemKey: "indexes",
            // Not an error — the table and its rows are there. A partial result says so in its detail.
            ItemStatus: ToolItemStatus.Done,
            Detail: complete
                ? loc.Get("copy.detail.indexes", indexesMade, keysMade)
                : loc.Get("copy.detail.indexesPartial", indexesMade, indexes.Count, keysMade, foreignKeys.Count)));
    }

    private static async Task<int> RunEachAsync(
        ToolConnection target, IReadOnlyList<string> statements, CancellationToken ct)
    {
        var made = 0;
        foreach (var statement in statements)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await target.Provider.ExecuteDdlAsync(target.Profile, statement, ct);
                made++;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Counted, not thrown — see AddIndexesAndForeignKeysAsync.
            }
        }

        return made;
    }

    private static void OpenAsScript(
        ToolExecutionContext context, TableDef model, QueryResult? data, bool dropExisting, bool copyStructure,
        bool copyData, bool keepIdentity, bool includeIndexes, ISqlDialect dialect, SqlLiteralDialect literalDialect,
        CreateTableWriter writer, string quotedTarget, string toConnection, string toDatabase, bool ambiguous,
        IProgress<ToolProgress> progress, IPluginLocalizer loc)
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
                parts.Add(writer.DropIfExists(model));
            }

            parts.Add(writer.Build(model, keepIdentity, includeIndexes));
        }

        if (copyData && data is not null)
        {
            parts.Add(InsertScripter.Build(quotedTarget, data.Columns, data.Rows, dialect, literalDialect));
        }

        // Indexes and foreign keys come after the inserts here for the same reason the run does them last:
        // the script is meant to be run top to bottom, and a foreign key can only be checked against rows
        // that exist. A key whose referenced table isn't on the target will fail — visibly, in a script the
        // user is reviewing anyway.
        if (copyStructure && includeIndexes)
        {
            parts.AddRange(writer.Indexes(model));
            parts.AddRange(writer.ForeignKeys(model));
        }

        context.Host.OpenQueryEditorOn(toConnection, toDatabase, string.Join("\n\n", parts));
        progress.Report(new ToolProgress(loc.Get("copy.result.opened", toDatabase, data?.Rows.Count ?? 0),
            ItemKey: "script", ItemStatus: ToolItemStatus.Done,
            Detail: loc.Get("copy.detail.rowsCopied", data?.Rows.Count ?? 0)));
    }
}
