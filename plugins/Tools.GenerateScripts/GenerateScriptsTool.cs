namespace SqlExplorer.Tools.GenerateScripts;

/// <summary>
/// Scripts a whole database as <c>CREATE</c> DDL — SSMS's "Generate scripts", for every engine the schema
/// reader covers. The host already scripts a single table or view from the tree; this is the "everything, in
/// dependency order, as one file" case that has no equivalent.
///
/// <para>It is deliberately thin. Reading the schema (<c>SchemaReader</c>) and rendering DDL for a dialect
/// (<c>AlterScriptWriter</c>) already exist in <c>Shared.Schema</c>, shared with Schema Diff and Copy Table;
/// a whole-database script is that same renderer fed a <see cref="CreateTable"/> per table instead of a
/// diff. The one thing this tool owns is <b>ordering</b>: foreign keys must come last, which the writer
/// already guarantees, and tables are emitted in a stable order so re-running produces the same file.</para>
///
/// <para>Output goes to a query tab by default, so the script lands somewhere it can be read and run;
/// choosing a file writes it instead, for checking into a repository.</para>
/// </summary>
public sealed class GenerateScriptsTool : IToolPlugin
{
    private static readonly string[] SupportedProviders = ["postgres", "mysql", "sqlserver", "sqlite"];

    private const string WhatStructure = "Tables only";
    private const string WhatStructureIndexes = "Tables, indexes and foreign keys";
    private const string ToTab = "Open in a query tab";
    private const string ToFile = "Save to a file";

    public string Id => "generate-scripts";
    public string Title => "Generate Scripts";
    public string? TitleKey => "gen.title";
    public string DialogTitle => "Generate Scripts";
    public string? DialogTitleKey => "gen.dialogTitle";

    public string? Description =>
        "Scripts every table in this database as CREATE statements — optionally with its indexes and " +
        "foreign keys. Nothing is changed: the script opens in a query tab, or is written to a file.";
    public string? DescriptionKey => "gen.description";

    public ToolTarget Target { get; } = new(
        ProviderIds: SupportedProviders,
        NodeKinds: [DbNodeKind.Database],
        IncludeConnectionRoot: true,
        ConnectionRootProviderIds: SupportedProviders);

    public IReadOnlyList<ToolField> Fields { get; } =
    [
        new("what", "What to script", ToolFieldType.Choice, Default: WhatStructureIndexes,
            Choices: [WhatStructureIndexes, WhatStructure], LabelKey: "gen.field.what"),
        new("dropFirst", "Add DROP TABLE before each CREATE", ToolFieldType.Bool, Default: "false",
            LabelKey: "gen.field.dropFirst"),
        new("output", "Where to put it", ToolFieldType.Choice, Default: ToTab,
            Choices: [ToTab, ToFile], LabelKey: "gen.field.output"),
        new("file", "File", ToolFieldType.File, FileExtensions: ["sql"], SaveFile: true,
            LabelKey: "gen.field.file")
    ];

    public async Task ExecuteAsync(
        ToolExecutionContext context,
        IReadOnlyDictionary<string, string?> inputs,
        IProgress<ToolProgress> progress,
        CancellationToken ct)
    {
        var loc = context.Localizer;

        if (!SchemaReader.Supports(context.ProviderId))
        {
            progress.Report(new ToolProgress(loc.Get("gen.error.unsupported", context.ProviderId)));
            return;
        }

        var includeIndexes = (inputs.GetValueOrDefault("what") ?? WhatStructureIndexes) != WhatStructure;
        var dropFirst = inputs.GetValueOrDefault("dropFirst") == "true";
        var toFile = (inputs.GetValueOrDefault("output") ?? ToTab) == ToFile;

        var label = string.IsNullOrWhiteSpace(context.Profile.Database)
            ? context.Profile.Name
            : $"{context.Profile.Name} / {context.Profile.Database}";

        progress.Report(new ToolProgress(loc.Get("gen.progress.reading", label)));
        var snapshot = await new SchemaReader(context.Provider).ReadAsync(context.Profile, context.ProviderId, ct);

        if (snapshot.Tables.Count == 0)
        {
            progress.Report(new ToolProgress(loc.Get("gen.result.empty", label)));
            return;
        }

        var script = Build(snapshot, context.ProviderId, includeIndexes, dropFirst,
            loc.Get("gen.script.header", label, snapshot.Tables.Count));

        progress.Report(new ToolProgress(loc.Get("gen.progress.scripted", snapshot.Tables.Count)));

        if (!toFile)
        {
            context.Host.OpenQueryEditor(script);
            progress.Report(new ToolProgress(loc.Get("gen.result.opened", snapshot.Tables.Count, label)));
            return;
        }

        // A file field the user left empty still gets a picker, so "Save to a file" never silently no-ops.
        var path = inputs.GetValueOrDefault("file");
        if (string.IsNullOrWhiteSpace(path))
        {
            path = await context.Host.PickSaveFileAsync(SuggestedName(context.Profile), "sql");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            progress.Report(new ToolProgress(loc.Get("gen.result.cancelled")));
            return;
        }

        await File.WriteAllTextAsync(path, script, ct);
        progress.Report(new ToolProgress(loc.Get("gen.result.written", snapshot.Tables.Count, path)));
    }

    /// <summary>
    /// The whole schema as one script. Pure — the tests drive this directly.
    ///
    /// <para>Every table is emitted as a <see cref="CreateTable"/>, which is exactly what the writer already
    /// renders for a diff that creates a table: columns, primary key and unique constraints inline, then its
    /// indexes. Foreign keys are emitted separately <b>after every table exists</b> — a whole-database script
    /// otherwise fails on the first table that references one declared later in the file.</para>
    /// </summary>
    public static string Build(
        SchemaSnapshot snapshot, string providerId, bool includeIndexes, bool dropFirst, string header)
    {
        var dialect = SqlDialect.For(providerId);

        // Stable order, so re-running produces the same file and a diff of two runs shows real changes only.
        var tables = snapshot.Tables
            .OrderBy(t => t.Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var changes = new List<SchemaChange>();

        // Drops run in reverse dependency order — a table referenced by a foreign key can't go first.
        if (dropFirst)
        {
            changes.AddRange(Enumerable.Reverse(tables).Select(t => (SchemaChange)new DropTable(Bare(t, includeIndexes))));
        }

        changes.AddRange(tables.Select(t => (SchemaChange)new CreateTable(Bare(t, includeIndexes))));

        if (includeIndexes)
        {
            changes.AddRange(tables.SelectMany(t => t.ForeignKeys.Select(fk => (SchemaChange)new AddForeignKey(t, fk))));
        }

        var body = new AlterScriptWriter(dialect).Script(changes);
        return $"{header}\n\n{body}\n";
    }

    // The writer renders a table's own indexes with it; strip them (and the foreign keys, which are emitted
    // last) when the user asked for tables only.
    private static TableDef Bare(TableDef table, bool includeIndexes) =>
        includeIndexes
            ? table with { ForeignKeys = [] }
            : table with { ForeignKeys = [], Indexes = [] };

    private static string SuggestedName(ConnectionProfile profile) =>
        string.IsNullOrWhiteSpace(profile.Database) ? "schema.sql" : $"{profile.Database}.sql";
}
