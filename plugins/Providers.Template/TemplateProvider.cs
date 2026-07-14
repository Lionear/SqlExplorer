using System.Diagnostics;
using Lionear.SqlExplorer.Sdk;
using Lionear.SqlExplorer.Sdk.Settings;
using Lionear.SqlExplorer.Sdk.Shortcuts;

namespace Lionear.SqlExplorer.Providers.Template;

/// <summary>
/// A reference / example provider. It does not talk to any database — its purpose is to be a readable
/// template for third-party provider authors and to exercise the host's plugin plumbing during
/// development. It is staged into <c>plugins/</c> in Debug builds only (see the conditional reference in
/// App.csproj + Desktop.csproj), so it never ships in a release/MVP.
/// </summary>
/// <remarks>
/// It also demonstrates <see cref="IPluginSettings"/> (Route A): the fields below appear in
/// Settings ▸ Plugins, rendered by the host and persisted to <c>plugin-settings.json</c> under this
/// plugin's id — the exact shape a real tool (e.g. one needing a path to <c>mysqldump</c>) would use.
/// </remarks>
public sealed class TemplateProvider : IDbProvider, IPluginSettings, IShortcutContributor
{
    public string DisplayName => "Template (example)";

    public ProviderIcon? Icon { get; } = ProviderIconLoader.Load(typeof(TemplateProvider), "🧩");

    public ISqlDialect Dialect { get; } = new TemplateDialect();

    // A single illustrative connection field — the example provider is not actually connectable.
    public IReadOnlyList<ConnectionField> ConnectionFields { get; } =
    [
        new("endpoint", "Endpoint", ConnectionFieldType.Text, Placeholder: "example.internal:1234")
    ];

    // --- Route A plugin settings (the point of this template): every field type + two groups. ---
    public IReadOnlyList<PluginSettingField> SettingsFields { get; } =
    [
        new("binaryPath", "Executable path", PluginSettingFieldType.File,
            Placeholder: "/usr/local/bin/example-tool", Group: "Paths"),
        new("configDir", "Config directory", PluginSettingFieldType.File, Group: "Paths"),
        new("logLevel", "Log level", PluginSettingFieldType.Choice,
            Default: "info", Choices: ["debug", "info", "warn", "error"], Group: "Behaviour"),
        new("verbose", "Verbose output", PluginSettingFieldType.Bool, Group: "Behaviour"),
        new("extraArgs", "Extra arguments", PluginSettingFieldType.Text,
            Placeholder: "--flag value", Group: "Behaviour")
    ];

    // --- Route: IShortcutContributor. Two example shortcuts: one with a suggested default (Mod = Cmd on
    //     macOS, Ctrl elsewhere) and one shipped unbound for the user to assign. Both just write a line to
    //     the debug output — a real plugin would kick off its own work here. ---
    public IReadOnlyList<ShortcutContribution> Shortcuts { get; } =
    [
        new("ping", "Template: ping", "Mod+Alt+P", ct =>
        {
            Debug.WriteLine("[Template] ping shortcut fired");
            return Task.CompletedTask;
        }),
        new("secondary", "Template: secondary action", DefaultGesture: null, ct =>
        {
            Debug.WriteLine("[Template] secondary shortcut fired");
            return Task.CompletedTask;
        })
    ];

    public string BuildConnectionString(IReadOnlyDictionary<string, string?> values) =>
        values.TryGetValue("endpoint", out var endpoint) ? endpoint ?? string.Empty : string.Empty;

    // "Connects" but exposes nothing — keeps the example out of the way (no error popups, empty tree).
    public Task<bool> TestConnectionAsync(ConnectionProfile profile, CancellationToken ct) => Task.FromResult(true);

    public Task<IReadOnlyList<DbTreeNode>> GetChildNodesAsync(
        ConnectionProfile profile, IReadOnlyList<DbNodeRef> ancestors, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DbTreeNode>>([]);

    public IReadOnlyList<CreateCapability> CreateCapabilities { get; } = [];

    public IReadOnlyList<string> ColumnTypes { get; } = [];

    public Task<IReadOnlyList<string>> GetDatabasesAsync(ConnectionProfile profile, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    // Execution is out of scope for the example — a real provider implements these against its driver.
    private static NotSupportedException NotAConnectableProvider() =>
        new("The template provider is a reference example and does not execute SQL.");

    public SqlStatement BuildCreateStatement(CreateObjectSpec spec) => throw NotAConnectableProvider();

    public Task ExecuteDdlAsync(ConnectionProfile profile, string sql, CancellationToken ct) => throw NotAConnectableProvider();

    public Task<QueryResult> ExecuteQueryAsync(ConnectionProfile profile, string sql, CancellationToken ct) => throw NotAConnectableProvider();

    public Task<IReadOnlyList<QueryResult>> ExecuteScriptAsync(ConnectionProfile profile, string sql, CancellationToken ct) => throw NotAConnectableProvider();

    public Task<QueryResult> ExplainAsync(ConnectionProfile profile, string sql, CancellationToken ct) => throw NotAConnectableProvider();

    public Task<int> ExecuteBatchAsync(ConnectionProfile profile, IReadOnlyList<SqlStatement> statements, CancellationToken ct) => throw NotAConnectableProvider();
}
