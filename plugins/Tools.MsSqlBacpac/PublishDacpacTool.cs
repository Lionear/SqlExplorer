using Microsoft.SqlServer.Dac;
using SqlExplorer.Sdk.Branding;

namespace SqlExplorer.Tools.MsSqlBacpac;

/// <summary>
/// Publish a <c>.dacpac</c> against the selected (existing) database, updating its schema to match.
/// Destructive — the host confirms first, and <see cref="DacDeployOptions.BlockOnPossibleDataLoss"/> is on
/// by default so a change that would drop data aborts before running. Route B:
/// <see cref="PublishDacpacView"/> picks the file and the deploy toggles; the target is the node itself.
/// </summary>
public sealed class PublishDacpacTool : IToolPlugin, ICustomToolUi
{
    public Avalonia.Controls.Control CreateView(IToolUiContext context) => new PublishDacpacView(context);

    public string Id => "mssql-publish-dacpac";

    public string Title => "Publish DACPAC";
    public string? TitleKey => "bacpac.publish.title";
    public string? DialogTitleKey => "bacpac.publish.title";
    public string DialogTitle => "Publish DACPAC";

    public IReadOnlyList<string> MenuPath => ["Data-tier Application"];

    public ProviderIcon? Icon { get; } = ProviderIconLoader.Load(typeof(PublishDacpacTool), "📦");

    public ToolTarget Target { get; } = new(ProviderIds: ["sqlserver"], NodeKinds: [DbNodeKind.Database]);

    public IReadOnlyList<ToolField> Fields { get; } = [];

    public bool IsDestructive => true;

    public Task<string?> PreviewAsync(string filePath, CancellationToken ct)
    {
        try
        {
            return Task.FromResult<string?>(DacFxPreview.Dacpac(filePath));
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
        var target = context.Node?.Name ?? context.Profile.Database
            ?? throw new InvalidOperationException(context.Localizer["bacpac.error.noDatabase"]);

        var filePath = inputs.GetValueOrDefault("filePath");
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new InvalidOperationException(context.Localizer["bacpac.publish.error.noFile"]);
        }

        // Defaults mirror the view's initial checkbox state (data-loss guard + single transaction on).
        var options = new DacDeployOptions
        {
            BlockOnPossibleDataLoss = !IsFalse(inputs.GetValueOrDefault("blockDataLoss")),
            IncludeTransactionalScripts = !IsFalse(inputs.GetValueOrDefault("singleTransaction")),
            DropObjectsNotInSource = IsTrue(inputs.GetValueOrDefault("dropObjects"))
        };

        var runner = new DacFxRunner(context.Profile, context.Localizer, progress);
        await runner.PublishDacpacAsync(filePath, target, options, ct);
    }

    private static bool IsTrue(string? v) => string.Equals(v, "true", StringComparison.Ordinal);
    private static bool IsFalse(string? v) => string.Equals(v, "false", StringComparison.Ordinal);
}
