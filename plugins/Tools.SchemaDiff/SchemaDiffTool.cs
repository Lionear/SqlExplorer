namespace SqlExplorer.Tools.SchemaDiff;

/// <summary>
/// Compares this database against a second one the user picks (another connection + one of its databases)
/// and produces the migration — an ALTER script that would make <i>this</i> database match the other. It
/// doesn't run any DDL itself: it opens the script in a new query tab on this connection/database, so the
/// user reviews and runs it in the normal editor (with its own safety and editing).
///
/// <para>Route A: the pickers (<see cref="ToolFieldType.ConnectionPicker"/> / <see cref="ToolFieldType.DatabasePicker"/>)
/// are host-filtered to same-provider connections, so both sides are read and rendered with one dialect. The
/// heavy lifting — reading each schema (<see cref="SchemaReader"/>), diffing (<see cref="SchemaDiffer"/>), and
/// rendering (<see cref="AlterScriptWriter"/>) — is separated out and unit-tested.</para>
/// </summary>
public sealed class SchemaDiffTool : IToolPlugin
{
    private static readonly string[] SupportedProviders = ["postgres", "mysql", "sqlserver", "sqlite"];

    public string Id => "schema-diff";
    public string Title => "Schema Diff";
    public string? TitleKey => "diff.title";
    public string DialogTitle => "Schema Diff";
    public string? DialogTitleKey => "diff.dialogTitle";

    public string? Description =>
        "Builds a migration that changes the database above to match the one you pick below. " +
        "It opens as a script in a new query tab to review and run — nothing changes automatically.";
    public string? DescriptionKey => "diff.description";

    public ToolTarget Target { get; } = new(
        ProviderIds: SupportedProviders,
        NodeKinds: [DbNodeKind.Database],
        IncludeConnectionRoot: true,
        ConnectionRootProviderIds: SupportedProviders);

    public IReadOnlyList<ToolField> Fields { get; } =
    [
        new("compareTo", "Compare against connection", ToolFieldType.ConnectionPicker,
            Required: true, LabelKey: "diff.field.compareTo"),
        new("database", "Database on that connection", ToolFieldType.DatabasePicker,
            Required: true, LabelKey: "diff.field.database")
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
            progress.Report(new ToolProgress(loc.Get("diff.error.unsupported", context.ProviderId)));
            return;
        }

        var target = inputs.GetValueOrDefault("compareTo");
        var targetDatabase = inputs.GetValueOrDefault("database");
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(targetDatabase))
        {
            progress.Report(new ToolProgress(loc.Get("diff.error.noTarget")));
            return;
        }

        var other = context.Host.OpenConnection(target, targetDatabase);
        if (other is null)
        {
            progress.Report(new ToolProgress(loc.Get("diff.error.openFailed")));
            return;
        }

        var thisLabel = $"{context.Profile.Name} / {context.Profile.Database}";
        var otherLabel = $"{other.Profile.Name} / {targetDatabase}";

        progress.Report(new ToolProgress(loc.Get("diff.progress.readingThis", thisLabel)));
        var thisSchema = await new SchemaReader(context.Provider).ReadAsync(context.Profile, context.ProviderId, ct);

        progress.Report(new ToolProgress(loc.Get("diff.progress.readingOther", otherLabel)));
        var otherSchema = await new SchemaReader(other.Provider).ReadAsync(other.Profile, other.ProviderId, ct);

        // Transform *this* schema into the picked one — the script (and any apply) targets this connection.
        var changes = SchemaDiffer.Diff(thisSchema, otherSchema);
        if (changes.Count == 0)
        {
            progress.Report(new ToolProgress(loc.Get("diff.result.identical", otherLabel)));
            return;
        }

        // Header naming the database the script targets — the one missing/differing from the other.
        var header = loc.Get("diff.script.header", thisLabel, otherLabel);
        var script = header + "\n\n" + new AlterScriptWriter(SqlDialect.For(context.ProviderId)).Script(changes);

        progress.Report(new ToolProgress(
            loc.Get("diff.result.summary", thisLabel, otherLabel, changes.Count)));

        // Hand the migration to a query tab on this connection/database — the user reviews and runs it there.
        context.Host.OpenQueryEditor(script);
        progress.Report(new ToolProgress(loc.Get("diff.result.opened", thisLabel)));
    }
}
