namespace SqlExplorer.Sdk.Provisioning;

/// <summary>
/// The inputs a <see cref="ContainerRecipe"/>'s env/command builders read: a connection's parsed values
/// (from <c>IDbProvider.ParseConnectionString</c>, keyed by <c>ConnectionField.Key</c>) plus the resolved
/// admin identity. <see cref="Database"/> is the named database to create on first boot (already null when
/// the engine has none, e.g. a Redis numeric index — see <see cref="ContainerRecipe.NamedDatabase"/>);
/// <see cref="User"/>/<see cref="Password"/> fall back to the recipe's declared defaults when the connection
/// omits them.
/// </summary>
public sealed record ContainerEnvInput(
    IReadOnlyDictionary<string, string?> Values,
    string? Database,
    string User,
    string Password);

/// <summary>
/// How to spin up an <em>empty</em> local container that matches a connection's engine — image, version,
/// port, data path, and the environment/command that carry the credentials. A provider declares one from
/// <see cref="IDbProvider.ContainerRecipe"/> so its engine becomes containerisable by the Docker plugin;
/// <c>null</c> (the default) means "not containerisable" (e.g. file-based SQLite). The set of engines stays
/// open: a third-party provider ships a recipe and needs no host change.
/// </summary>
/// <remarks>
/// The env/command are <em>delegates</em> rather than pure data on purpose — the conditional cases
/// (MySQL's non-root <c>MYSQL_USER</c>, Redis's <c>--requirepass</c> server flag, SQL Server's mandatory
/// <c>ACCEPT_EULA</c>) can't be expressed as a static map. A trivial engine just returns a fixed list.
/// They run in-process (the plugin invokes the provider's delegate across the shared SDK type identity),
/// so no serialisation is involved.
/// </remarks>
/// <param name="Image">The base image, e.g. <c>postgres</c> or <c>mcr.microsoft.com/mssql/server</c>.</param>
/// <param name="DefaultTag">The image tag prefilled in the create dialog, e.g. <c>16</c>.</param>
/// <param name="ContainerPort">The port the engine listens on inside the container, e.g. 5432.</param>
/// <param name="DataPath">The in-container data directory to persist as a named volume.</param>
/// <param name="DefaultUser">The engine's default admin user prefill (empty when the engine has none, e.g. Redis).</param>
/// <param name="DefaultPassword">The default admin password prefill (empty when auth is off by default).</param>
/// <param name="Environment">Builds the container's environment variables from a connection's values + identity.</param>
/// <param name="Command">Optional entrypoint command/args (e.g. Redis <c>--requirepass</c>); null = the image default.</param>
/// <param name="Memlock">True to publish <c>ulimits.memlock: -1</c> (Dragonfly needs it).</param>
/// <param name="DatabaseAfterStart">True when a named database is provisioned after the server starts (SQL Server),
/// by the regie layer rather than the compose file.</param>
/// <param name="NamedDatabase">False when the connection's "database" is a numeric index, not a named database
/// (Redis/Dragonfly): the builder then never surfaces it as a create-on-boot database.</param>
/// <param name="HostPortOverride">Optional override for the host port to publish, given the connection's raw
/// values — for engines that carry the port somewhere other than a plain <c>port</c> field (Elasticsearch keeps
/// it inside its <c>url</c>). Return null to fall back to the <c>port</c> value, then <see cref="ContainerPort"/>.</param>
public sealed record ContainerRecipe(
    string Image,
    string DefaultTag,
    int ContainerPort,
    string DataPath,
    string DefaultUser,
    string DefaultPassword,
    Func<ContainerEnvInput, IReadOnlyList<KeyValuePair<string, string>>> Environment,
    Func<ContainerEnvInput, IReadOnlyList<string>>? Command = null,
    bool Memlock = false,
    bool DatabaseAfterStart = false,
    bool NamedDatabase = true,
    Func<IReadOnlyDictionary<string, string?>, int?>? HostPortOverride = null);
