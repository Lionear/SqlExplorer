using SqlExplorer.Sdk.Branding;

namespace SqlExplorer.Tools.MsSqlBacpac;

/// <summary>
/// Export a SQL Server database to a portable <c>.bacpac</c> (schema + selected data). Read-only against
/// the source, hence not destructive. Route B: <see cref="ExportBacpacView"/> picks the file and the
/// per-table data set; <see cref="DacFxRunner.ExportBacpacAsync"/> drives DacFx.
/// </summary>
public sealed class ExportBacpacTool : IToolPlugin, ICustomToolUi
{
    public Avalonia.Controls.Control CreateView(IToolUiContext context) => new ExportBacpacView(context);

    public string Id => "mssql-export-bacpac";

    public string Title => "Export to BACPAC";
    public string? TitleKey => "bacpac.export.title";
    public string? DialogTitleKey => "bacpac.export.title";
    public string DialogTitle => "Export to BACPAC";

    // SSMS-style: Tasks ▸ Data-tier Application ▸ Export.
    public IReadOnlyList<string> MenuPath => ["Data-tier Application"];

    public ProviderIcon? Icon { get; } = ProviderIconLoader.Load(typeof(ExportBacpacTool), "📦");

    // A SQL Server Database node only.
    public ToolTarget Target { get; } = new(ProviderIds: ["sqlserver"], NodeKinds: [DbNodeKind.Database]);

    public IReadOnlyList<ToolField> Fields { get; } = [];

    public bool IsDestructive => false;

    public async Task ExecuteAsync(
        ToolExecutionContext context,
        IReadOnlyDictionary<string, string?> inputs,
        IProgress<ToolProgress> progress,
        CancellationToken ct)
    {
        var database = context.Node?.Name ?? context.Profile.Database
            ?? throw new InvalidOperationException(context.Localizer["bacpac.error.noDatabase"]);

        var filePath = inputs.GetValueOrDefault("filePath");
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new InvalidOperationException(context.Localizer["bacpac.export.error.noFile"]);
        }

        // "All data" maps to DacFx's null (every table's data). Anything less is an explicit table list —
        // an empty list is legal and yields a schema-only bacpac.
        IEnumerable<Tuple<string, string>>? dataTables = null;
        if (string.Equals(inputs.GetValueOrDefault("includeAllData"), "false", StringComparison.Ordinal))
        {
            dataTables = ParseTables(inputs.GetValueOrDefault("dataTables"));
        }

        var runner = new DacFxRunner(context.Profile, context.Localizer, progress);
        await runner.ExportBacpacAsync(database, filePath, dataTables, ct);
    }

    // The view serializes selected data tables as "schema\tname" lines. Blank/whitespace lines are dropped.
    internal static List<Tuple<string, string>> ParseTables(string? serialized) =>
        (serialized ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('\t'))
            .Where(parts => parts.Length == 2)
            .Select(parts => Tuple.Create(parts[0], parts[1]))
            .ToList();
}
