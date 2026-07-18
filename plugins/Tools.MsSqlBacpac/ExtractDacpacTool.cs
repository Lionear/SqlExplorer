using Microsoft.SqlServer.Dac;
using SqlExplorer.Sdk.Branding;

namespace SqlExplorer.Tools.MsSqlBacpac;

/// <summary>
/// Extract a SQL Server database's schema (no data) to a <c>.dacpac</c> for CI/CD or schema compare.
/// Read-only, not destructive. Route B: <see cref="ExtractDacpacView"/> supplies the file, application
/// name/version and a few <see cref="DacExtractOptions"/> toggles.
/// </summary>
public sealed class ExtractDacpacTool : IToolPlugin, ICustomToolUi
{
    public Avalonia.Controls.Control CreateView(IToolUiContext context) => new ExtractDacpacView(context);

    public string Id => "mssql-extract-dacpac";

    public string Title => "Extract to DACPAC";
    public string? TitleKey => "bacpac.extract.title";
    public string? DialogTitleKey => "bacpac.extract.title";
    public string DialogTitle => "Extract to DACPAC";

    public IReadOnlyList<string> MenuPath => ["Data-tier Application"];

    public ProviderIcon? Icon { get; } = ProviderIconLoader.Load(typeof(ExtractDacpacTool), "📦");

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
            throw new InvalidOperationException(context.Localizer["bacpac.extract.error.noFile"]);
        }

        var appName = inputs.GetValueOrDefault("appName");
        if (string.IsNullOrWhiteSpace(appName))
        {
            appName = database;
        }

        var version = Version.TryParse(inputs.GetValueOrDefault("version"), out var parsed) ? parsed : new Version(1, 0, 0, 0);

        // Defaults match the view's initial checkbox state; a missing key falls back to that default.
        var options = new DacExtractOptions
        {
            ExtractApplicationScopedObjectsOnly = !IsFalse(inputs.GetValueOrDefault("appScopedOnly")),
            VerifyExtraction = IsTrue(inputs.GetValueOrDefault("verifyExtraction")),
            IgnorePermissions = !IsTrue(inputs.GetValueOrDefault("includePermissions"))
        };

        var runner = new DacFxRunner(context.Profile, context.Localizer, progress);
        await runner.ExtractDacpacAsync(database, filePath, appName!, version, options, ct);
    }

    private static bool IsTrue(string? v) => string.Equals(v, "true", StringComparison.Ordinal);
    private static bool IsFalse(string? v) => string.Equals(v, "false", StringComparison.Ordinal);
}
