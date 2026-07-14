using Avalonia.Controls;

namespace Lionear.SqlExplorer.Tools.MsSqlAdmin;

/// <summary>
/// SSMS "Shrink File" (Tasks ▸ Shrink ▸ Files): shrink or empty one specific data/log file. Route B:
/// <see cref="ShrinkFileView"/> cascades File type → Filegroup → File name from live
/// <c>sys.database_files</c> data and offers the three SSMS shrink actions; <see cref="ExecuteAsync"/>
/// runs the matching <c>DBCC SHRINKFILE</c>.
/// </summary>
public sealed class ShrinkFileTool : IToolPlugin, ICustomToolUi
{
    public const string LogicalNameKey = "logicalName";
    public const string ActionKey = "action";
    public const string TargetMbKey = "targetMb";

    public const string ActionRelease = "release";
    public const string ActionReorganize = "reorganize";
    public const string ActionEmpty = "empty";

    public string Id => "mssql-shrink-file";

    public string Title => "Files…";

    public string DialogTitle => "Shrink Files";

    // SSMS-style: Tools ▸ Shrink ▸ Files.
    public IReadOnlyList<string> MenuPath => ["Shrink"];

    public ToolTarget Target { get; } = new(ProviderIds: ["sqlserver"], NodeKinds: [DbNodeKind.Database]);

    public IReadOnlyList<ToolField> Fields { get; } = [];

    public bool IsDestructive => false;

    public Control CreateView(IToolUiContext context) => new ShrinkFileView(context);

    public async Task ExecuteAsync(
        ToolExecutionContext context,
        IReadOnlyDictionary<string, string?> inputs,
        IProgress<ToolProgress> progress,
        CancellationToken ct)
    {
        if (!inputs.TryGetValue(LogicalNameKey, out var name) || string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Select a file to shrink.");
        }

        var literal = name.Replace("'", "''");
        var action = inputs.TryGetValue(ActionKey, out var a) ? a : ActionRelease;
        var sql = action switch
        {
            ActionEmpty => $"DBCC SHRINKFILE (N'{literal}', EMPTYFILE)",
            ActionReorganize when inputs.TryGetValue(TargetMbKey, out var t) && int.TryParse(t, out var mb) =>
                $"DBCC SHRINKFILE (N'{literal}', {Math.Max(mb, 0)})",
            ActionReorganize => $"DBCC SHRINKFILE (N'{literal}')",
            _ => $"DBCC SHRINKFILE (N'{literal}', TRUNCATEONLY)"
        };

        progress.Report(new ToolProgress($"Running: {sql}"));
        await context.Provider.ExecuteDdlAsync(context.Profile, sql, ct);
        progress.Report(new ToolProgress($"Shrink complete for file {name}.", 1.0));
    }
}
