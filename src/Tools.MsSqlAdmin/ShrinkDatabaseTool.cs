using Avalonia.Controls;

namespace Lionear.SqlExplorer.Tools.MsSqlAdmin;

/// <summary>
/// SSMS "Shrink Database" (Tasks ▸ Shrink ▸ Database), General page. Reclaims free space across the
/// database's data files. Route B: <see cref="ShrinkDatabaseView"/> shows live allocated/free space and
/// the reorganize toggle; <see cref="ExecuteAsync"/> runs the matching <c>DBCC SHRINKDATABASE</c>.
/// Always-present core tool (not a Store install) — see the plan's §3.2 staging note.
/// </summary>
public sealed class ShrinkDatabaseTool : IToolPlugin, ICustomToolUi
{
    public const string ReorganizeKey = "reorganize";
    public const string TargetPercentKey = "targetPercent";

    public string Id => "mssql-shrink-database";

    public string Title => "Database…";

    public string DialogTitle => "Shrink Database";

    // SSMS-style: Tools ▸ Shrink ▸ Database.
    public IReadOnlyList<string> MenuPath => ["Shrink"];

    // Offered on a SQL Server Database node only.
    public ToolTarget Target { get; } = new(ProviderIds: ["sqlserver"], NodeKinds: [DbNodeKind.Database]);

    // Route B: no declared fields, the custom view drives everything.
    public IReadOnlyList<ToolField> Fields { get; } = [];

    // SSMS does not confirm a shrink; mirror that (Rick can flip this at review, plan §7).
    public bool IsDestructive => false;

    public Control CreateView(IToolUiContext context) => new ShrinkDatabaseView(context);

    public async Task ExecuteAsync(
        ToolExecutionContext context,
        IReadOnlyDictionary<string, string?> inputs,
        IProgress<ToolProgress> progress,
        CancellationToken ct)
    {
        var database = context.Node?.Name ?? context.Profile.Database
            ?? throw new InvalidOperationException("Shrink Database needs a target database.");
        var literal = database.Replace("'", "''");

        var reorganize = inputs.TryGetValue(ReorganizeKey, out var r) && r == "true";
        string sql;
        if (reorganize)
        {
            // Reorganize + release: leave targetPercent free space at the end (0 = shrink to minimum).
            var percent = inputs.TryGetValue(TargetPercentKey, out var p) && int.TryParse(p, out var v) ? Math.Clamp(v, 0, 99) : 0;
            sql = $"DBCC SHRINKDATABASE (N'{literal}', {percent})";
        }
        else
        {
            // Release only end-of-file free space, no page reorganization (fast, TRUNCATEONLY). The 0 is
            // the ignored target_percent placeholder DBCC requires before the option keyword.
            sql = $"DBCC SHRINKDATABASE (N'{literal}', 0, TRUNCATEONLY)";
        }

        progress.Report(new ToolProgress($"Running: {sql}"));
        await context.Provider.ExecuteDdlAsync(context.Profile, sql, ct);
        progress.Report(new ToolProgress($"Shrink complete for {database}.", 1.0));
    }
}
