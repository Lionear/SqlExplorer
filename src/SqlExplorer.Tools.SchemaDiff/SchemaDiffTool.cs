namespace SqlExplorer.Tools.SchemaDiff;

/// <summary>
/// Compares this connection's schema to a second one the user picks and reports the migration — an ALTER
/// script that would make <i>this</i> connection match the picked one. With "Apply" ticked it runs that
/// script against this connection; otherwise it only reports it (preview).
///
/// <para>Route A: the picker (<see cref="ToolFieldType.ConnectionPicker"/>) is host-filtered to same-provider
/// connections, so both sides are read and rendered with one dialect. The heavy lifting — reading each
/// schema (<see cref="SchemaReader"/>), diffing (<see cref="SchemaDiffer"/>), and rendering
/// (<see cref="AlterScriptWriter"/>) — is separated out and unit-tested.</para>
/// </summary>
public sealed class SchemaDiffTool : IToolPlugin
{
    private static readonly string[] SupportedProviders = ["postgres", "mysql", "sqlserver"];

    public string Id => "schema-diff";
    public string Title => "Schema Diff…";
    public string? TitleKey => "diff.title";
    public string DialogTitle => "Schema Diff";
    public string? DialogTitleKey => "diff.dialogTitle";

    // Can run DDL (Apply), so the host gates the run behind its destructive-action confirmation.
    public bool IsDestructive => true;

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
            Required: true, LabelKey: "diff.field.database"),
        new("apply", "Apply the changes to this connection", ToolFieldType.Bool,
            Default: "false", LabelKey: "diff.field.apply")
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

        var statements = new AlterScriptWriter(SqlDialect.For(context.ProviderId)).Statements(changes);

        progress.Report(new ToolProgress(
            loc.Get("diff.result.summary", thisLabel, otherLabel, changes.Count)));
        progress.Report(new ToolProgress(string.Empty));
        foreach (var statement in statements)
        {
            progress.Report(new ToolProgress(statement));
        }

        progress.Report(new ToolProgress(string.Empty));

        if (!ParseBool(inputs.GetValueOrDefault("apply")))
        {
            progress.Report(new ToolProgress(loc.Get("diff.result.previewOnly")));
            return;
        }

        await ApplyAsync(context, statements, progress, ct);
    }

    private static async Task ApplyAsync(
        ToolExecutionContext context,
        IReadOnlyList<string> statements,
        IProgress<ToolProgress> progress,
        CancellationToken ct)
    {
        var loc = context.Localizer;
        // Comment lines (SQL Server default notes, unsupported-op notes) are reported, not executed.
        var runnable = statements.Where(s => !s.TrimStart().StartsWith("--", StringComparison.Ordinal)).ToList();

        progress.Report(new ToolProgress(loc.Get("diff.apply.start", runnable.Count)));

        var done = 0;
        foreach (var statement in runnable)
        {
            ct.ThrowIfCancellationRequested();
            var key = statement;
            progress.Report(new ToolProgress(statement, ItemKey: key, ItemStatus: ToolItemStatus.Running));
            try
            {
                await context.Provider.ExecuteDdlAsync(context.Profile, statement, ct);
                done++;
                progress.Report(new ToolProgress(
                    statement, (double)done / runnable.Count, key, ToolItemStatus.Done));
            }
            catch (Exception ex)
            {
                progress.Report(new ToolProgress(
                    loc.Get("diff.apply.failed", ex.Message), ItemKey: key, ItemStatus: ToolItemStatus.Error));
                throw;
            }
        }

        progress.Report(new ToolProgress(loc.Get("diff.apply.done", done)));
    }

    private static bool ParseBool(string? value) => bool.TryParse(value, out var b) && b;
}
