using SqlExplorer.Sdk.Branding;

namespace SqlExplorer.Tools.MsSqlBacpac;

/// <summary>
/// Import a <c>.bacpac</c> as a brand-new database on the connected server. Destructive in that it creates
/// a database object (and fails rather than overwrite if the name is taken), so the host confirms first.
/// Offered on the "Databases" folder node. Route B: <see cref="ImportBacpacView"/> picks the file and the
/// new database name.
/// </summary>
public sealed class ImportBacpacTool : IToolPlugin, ICustomToolUi
{
    public Avalonia.Controls.Control CreateView(IToolUiContext context) => new ImportBacpacView(context);

    public string Id => "mssql-import-bacpac";

    public string Title => "Import BACPAC as new database";
    public string? TitleKey => "bacpac.import.title";
    public string? DialogTitleKey => "bacpac.import.title";
    public string DialogTitle => "Import BACPAC as new database";

    public IReadOnlyList<string> MenuPath => ["Data-tier Application"];

    public ProviderIcon? Icon { get; } = ProviderIconLoader.Load(typeof(ImportBacpacTool), "📦");

    // The "Databases" folder node (a new database is created under it).
    public ToolTarget Target { get; } = new(ProviderIds: ["sqlserver"], NodeKinds: [DbNodeKind.DatabaseFolder]);

    public IReadOnlyList<ToolField> Fields { get; } = [];

    public bool IsDestructive => true;

    // Header shown once a file is chosen (Route A fallback; the view renders the same inline).
    public Task<string?> PreviewAsync(string filePath, CancellationToken ct)
    {
        try
        {
            return Task.FromResult<string?>(DacFxPreview.Bacpac(filePath));
        }
        catch (Exception ex)
        {
            return Task.FromResult<string?>(ex.Message);
        }
    }

    public async Task ExecuteAsync(
        ToolExecutionContext context,
        IReadOnlyDictionary<string, string?> inputs,
        IProgress<ToolProgress> progress,
        CancellationToken ct)
    {
        var filePath = inputs.GetValueOrDefault("filePath");
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new InvalidOperationException(context.Localizer["bacpac.import.error.noFile"]);
        }

        var targetDatabase = inputs.GetValueOrDefault("targetDatabase");
        if (string.IsNullOrWhiteSpace(targetDatabase))
        {
            throw new InvalidOperationException(context.Localizer["bacpac.import.error.noName"]);
        }

        var runner = new DacFxRunner(context.Profile, context.Localizer, progress);
        await runner.ImportBacpacAsync(filePath, targetDatabase.Trim(), ct);
    }
}
