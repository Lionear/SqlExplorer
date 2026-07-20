using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SqlExplorer.Sdk.Provisioning;

namespace SqlExplorer.Backends.Docker;

/// <summary>The snippet flavour to emit for a container recipe.</summary>
public enum SnippetFormat
{
    /// <summary>A <c>docker-compose.yaml</c> (a file you commit / <c>docker compose up</c>).</summary>
    Compose,

    /// <summary>A copy-paste <c>docker run</c> one-liner (no file needed).</summary>
    Run
}

/// <summary>
/// The inputs for one container recipe. <see cref="Values"/> are a provider's parsed connection values
/// (from <c>IDbProvider.ParseConnectionString</c>, keyed by <c>ConnectionField.Key</c>) — the builder reads
/// the conventional keys (<c>host</c>/<c>port</c>/<c>username</c>/<c>password</c>/<c>database</c>, or
/// <c>url</c> for Elasticsearch) and falls back to each engine's sensible default when a key is absent.
/// </summary>
public sealed record ContainerSpec(
    string ProviderId,
    IReadOnlyDictionary<string, string?> Values,
    string? Database = null,
    string? Tag = null,
    string? ContainerName = null,
    int? HostPort = null);

/// <summary>
/// Turns a live connection's engine + values into a <c>docker-compose</c> / <c>docker run</c> snippet for an
/// <em>empty</em> local instance that matches the connection (engine, version, port, credentials) — schema
/// and data stay out (that's Universal Backup's job). Pure and deterministic: no I/O, no Docker, no clock —
/// so it's exhaustively unit-testable and the CLI/regie layers (<c>IDockerCli</c>, <c>ContainerService</c>)
/// build on top of it. One <see cref="ContainerRecipe"/> per containerisable engine; SQLite and unknown ids
/// are unsupported (no server to run).
/// <para>
/// The recipe table is a merge: a built-in fallback for the seven first-party engines, overlaid with any
/// recipes a provider declared and the host handed in via <c>IProviderCatalog</c> (SE-166). A provider-declared
/// recipe <em>wins</em> over the built-in fallback for the same id (so a provider owns its own provisioning),
/// and a third-party engine that ships a recipe becomes containerisable with no change here.
/// </para>
/// </summary>
public sealed class DockerComposeBuilder
{
    private readonly IReadOnlyDictionary<string, ContainerRecipe> _recipes;

    /// <param name="external">Provider-declared recipes read from the host (via <c>IProviderCatalog</c>); each
    /// overrides the built-in fallback for its id. Null/empty = built-in table only (the capability wasn't
    /// granted, or nothing declared a recipe) — every first-party engine still works.</param>
    public DockerComposeBuilder(IEnumerable<ProviderRecipe>? external = null)
    {
        var merged = new Dictionary<string, ContainerRecipe>(BuiltIn, StringComparer.Ordinal);
        if (external is not null)
        {
            foreach (var pr in external)
            {
                merged[pr.ProviderId] = pr.Recipe;
            }
        }

        _recipes = merged;
    }

    /// <summary>The engine ids that can be spun up as a container (every engine with a recipe) — the choices
    /// the "New container" dialog offers.</summary>
    public IReadOnlyList<string> SupportedProviderIds => _recipes.Keys.ToList();

    /// <summary>True when this engine can be spun up as a container.</summary>
    public bool Supports(string providerId) => _recipes.ContainsKey(providerId);

    /// <summary>The base image for an engine (e.g. <c>postgres</c>), or null if unsupported.</summary>
    public string? ImageName(string providerId) => _recipes.TryGetValue(providerId, out var e) ? e.Image : null;

    /// <summary>The default image tag for an engine (e.g. <c>16</c>), or null if unsupported.</summary>
    public string? DefaultTag(string providerId) => _recipes.TryGetValue(providerId, out var e) ? e.DefaultTag : null;

    /// <summary>The in-container port an engine listens on (e.g. 5432), or null if unsupported.</summary>
    public int? ContainerPort(string providerId) => _recipes.TryGetValue(providerId, out var e) ? e.ContainerPort : null;

    /// <summary>The engine's default admin user (e.g. <c>postgres</c>), or null if unsupported / not applicable.</summary>
    public string? DefaultUser(string providerId) =>
        _recipes.TryGetValue(providerId, out var e) && e.DefaultUser.Length > 0 ? e.DefaultUser : null;

    /// <summary>The engine's default admin password prefill (e.g. <c>changeme</c>), or null if unsupported.</summary>
    public string? DefaultPassword(string providerId) =>
        _recipes.TryGetValue(providerId, out var e) && e.DefaultPassword.Length > 0 ? e.DefaultPassword : null;

    /// <summary>The host port this connection would publish (the connection's own port, or Elasticsearch's
    /// URL port), falling back to the engine default — the pre-fill for the create dialog's port field.</summary>
    public int? DefaultHostPort(string providerId, IReadOnlyDictionary<string, string?> values) =>
        _recipes.TryGetValue(providerId, out var e) ? ResolvePort(e, values) : null;

    public string Build(ContainerSpec spec, SnippetFormat format)
    {
        if (!_recipes.TryGetValue(spec.ProviderId, out var engine))
        {
            throw new NotSupportedException($"No container recipe for provider '{spec.ProviderId}'.");
        }

        var values = spec.Values;
        var tag = Blank(spec.Tag) ? engine.DefaultTag : spec.Tag!.Trim();
        var name = Blank(spec.ContainerName) ? $"{spec.ProviderId}-local" : spec.ContainerName!.Trim();
        var hostPort = spec.HostPort ?? ResolvePort(engine, values);

        // A numeric-index "database" (Redis/Dragonfly, NamedDatabase == false) is never a create-on-boot database.
        var namedDb = engine.NamedDatabase
            ? Blank(spec.Database) ? NullIfBlank(Get(values, "database")) : spec.Database!.Trim()
            : null;

        var ctx = new ContainerEnvInput(
            values,
            namedDb,
            Value(values, "username", engine.DefaultUser),
            Value(values, "password", engine.DefaultPassword));

        var env = engine.Environment(ctx);
        var command = engine.Command?.Invoke(ctx) ?? [];
        var image = $"{engine.Image}:{tag}";

        return format == SnippetFormat.Compose
            ? RenderCompose(engine, name, image, hostPort, env, command)
            : RenderRun(engine, name, image, hostPort, env, command);
    }

    /// <summary>The host port to publish: the recipe's own override (e.g. Elasticsearch's URL port) if any,
    /// else the connection's <c>port</c> value, falling back to the engine's default container port.</summary>
    internal static int ResolvePort(ContainerRecipe recipe, IReadOnlyDictionary<string, string?> values)
    {
        if (recipe.HostPortOverride?.Invoke(values) is { } overridden && overridden > 0)
        {
            return overridden;
        }

        return int.TryParse(Get(values, "port"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port > 0
            ? port
            : recipe.ContainerPort;
    }

    // ---- rendering --------------------------------------------------------------------------------

    private static string RenderCompose(
        ContainerRecipe engine, string name, string image, int hostPort,
        IReadOnlyList<KeyValuePair<string, string>> env, IReadOnlyList<string> command)
    {
        var sb = new StringBuilder();
        sb.Append("services:\n  db:\n");
        sb.Append($"    image: {image}\n");
        sb.Append($"    container_name: {name}\n");
        sb.Append("    restart: unless-stopped\n");

        if (env.Count > 0)
        {
            sb.Append("    environment:\n");
            foreach (var (key, value) in env)
            {
                sb.Append($"      {key}: {YamlScalar(value)}\n");
            }
        }

        if (command.Count > 0)
        {
            sb.Append($"    command: [{string.Join(", ", command.Select(a => YamlScalar(a, quoteAlways: true)))}]\n");
        }

        sb.Append("    ports:\n");
        sb.Append($"      - \"{hostPort}:{engine.ContainerPort}\"\n");
        sb.Append("    volumes:\n");
        sb.Append($"      - {name}-data:{engine.DataPath}\n");

        if (engine.Memlock)
        {
            sb.Append("    ulimits:\n      memlock: -1\n");
        }

        sb.Append($"volumes:\n  {name}-data:\n");
        return sb.ToString();
    }

    private static string RenderRun(
        ContainerRecipe engine, string name, string image, int hostPort,
        IReadOnlyList<KeyValuePair<string, string>> env, IReadOnlyList<string> command)
    {
        var sb = new StringBuilder();
        sb.Append("docker run -d \\\n");
        sb.Append($"  --name {name} \\\n");
        foreach (var (key, value) in env)
        {
            sb.Append($"  -e {ShellArg($"{key}={value}")} \\\n");
        }

        sb.Append($"  -p {hostPort}:{engine.ContainerPort} \\\n");
        sb.Append($"  -v {name}-data:{engine.DataPath} \\\n");
        if (engine.Memlock)
        {
            sb.Append("  --ulimit memlock=-1 \\\n");
        }

        var tail = command.Count > 0 ? $"{image} {string.Join(' ', command.Select(ShellArg))}" : image;
        sb.Append($"  {tail}\n");
        return sb.ToString();
    }

    // ---- built-in fallback recipe table -----------------------------------------------------------

    // The first-party engines. Kept as a fallback (SE-166): a provider that declares its own ContainerRecipe
    // overrides its entry here, and this table covers every engine whose provider does not (or when the
    // "providers" capability isn't granted). Postgres is dogfooded from the Postgres provider, so its entry
    // here is only reached when that recipe is absent — it stays for backwards-compatibility.
    private static readonly IReadOnlyDictionary<string, ContainerRecipe> BuiltIn = new Dictionary<string, ContainerRecipe>(StringComparer.Ordinal)
    {
        ["postgres"] = new("postgres", "16", 5432, "/var/lib/postgresql/data", "postgres", "changeme",
            e => Env(
                ("POSTGRES_DB", e.Database ?? "postgres"),
                ("POSTGRES_USER", e.User),
                ("POSTGRES_PASSWORD", e.Password))),

        ["mysql"] = new("mysql", "8", 3306, "/var/lib/mysql", "root", "changeme", MySqlEnv),

        // SQL Server takes no MSSQL_DATABASE env; a named database is created after the server starts
        // (DatabaseAfterStart) by the regie layer, not here. ACCEPT_EULA is mandatory.
        ["sqlserver"] = new("mcr.microsoft.com/mssql/server", "2022-latest", 1433, "/var/opt/mssql", "sa", "Str0ng!Passw0rd",
            e => Env(
                ("ACCEPT_EULA", "Y"),
                ("MSSQL_SA_PASSWORD", e.Password),
                ("MSSQL_PID", "Developer")),
            DatabaseAfterStart: true),

        ["mongodb"] = new("mongo", "7", 27017, "/data/db", "root", "changeme", MongoEnv),

        // Redis auth is a server flag, not an env var — omitted entirely when the connection has no password.
        // Its "database" is a numeric index, not a named database (NamedDatabase: false).
        ["redis"] = new("redis", "7", 6379, "/data", "", "",
            _ => [], Command: e => Blank(e.Password) ? [] : ["redis-server", "--requirepass", e.Password],
            NamedDatabase: false),

        // Dragonfly is Redis-wire-compatible; its entrypoint is the daemon, so command args are bare flags,
        // it wants an unlimited memlock ulimit, and its "database" is likewise a numeric index.
        ["dragonflydb"] = new("docker.dragonflydb.io/dragonflydb/dragonfly", "latest", 6379, "/data", "", "",
            _ => [], Command: e => Blank(e.Password) ? [] : ["--requirepass", e.Password],
            Memlock: true, NamedDatabase: false),

        // Elasticsearch keeps its port inside the connection's `url`, not a plain `port` field (HostPortOverride).
        ["elasticsearch"] = new("docker.elastic.co/elasticsearch/elasticsearch", "8.13.0", 9200, "/usr/share/elasticsearch/data", "elastic", "changeme",
            e => Env(
                ("discovery.type", "single-node"),
                ("xpack.security.enabled", "true"),
                ("ELASTIC_PASSWORD", e.Password)),
            HostPortOverride: values =>
                Uri.TryCreate(Get(values, "url"), UriKind.Absolute, out var uri) && !uri.IsDefaultPort ? uri.Port : null),
    };

    // MySQL's image always needs a root password; a non-root connection user gets MYSQL_USER/MYSQL_PASSWORD
    // on top (the image creates that user and grants it the initial database).
    private static IReadOnlyList<KeyValuePair<string, string>> MySqlEnv(ContainerEnvInput e)
    {
        var list = new List<KeyValuePair<string, string>> { new("MYSQL_ROOT_PASSWORD", e.Password) };
        if (e.Database is { Length: > 0 })
        {
            list.Add(new("MYSQL_DATABASE", e.Database));
        }

        if (!string.Equals(e.User, "root", StringComparison.OrdinalIgnoreCase) && !Blank(e.User))
        {
            list.Add(new("MYSQL_USER", e.User));
            list.Add(new("MYSQL_PASSWORD", e.Password));
        }

        return list;
    }

    // Mongo only enables auth when the connection actually carries a username; otherwise the container runs
    // open (matching a no-auth local connection).
    private static IReadOnlyList<KeyValuePair<string, string>> MongoEnv(ContainerEnvInput e)
    {
        var list = new List<KeyValuePair<string, string>>();
        if (!Blank(Get(e.Values, "username")))
        {
            list.Add(new("MONGO_INITDB_ROOT_USERNAME", e.User));
            list.Add(new("MONGO_INITDB_ROOT_PASSWORD", e.Password));
        }

        if (e.Database is { Length: > 0 })
        {
            list.Add(new("MONGO_INITDB_DATABASE", e.Database));
        }

        return list;
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static IReadOnlyList<KeyValuePair<string, string>> Env(params (string Key, string Value)[] pairs) =>
        pairs.Select(p => new KeyValuePair<string, string>(p.Key, p.Value)).ToList();

    private static string? Get(IReadOnlyDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var v) ? v : null;

    private static string Value(IReadOnlyDictionary<string, string?> values, string key, string fallback) =>
        Blank(Get(values, key)) ? fallback : Get(values, key)!.Trim();

    private static string? NullIfBlank(string? s) => Blank(s) ? null : s!.Trim();

    private static bool Blank(string? s) => string.IsNullOrWhiteSpace(s);

    private static readonly Regex SafeScalar = new(@"^[A-Za-z0-9_./@=+-]+$", RegexOptions.Compiled);

    // YAML 1.1 reads these bare tokens as bool/null, so an env value like "Y" (ACCEPT_EULA) must be quoted.
    private static readonly HashSet<string> YamlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "y", "yes", "n", "no", "true", "false", "on", "off", "null", "~"
    };

    private static string YamlScalar(string value, bool quoteAlways = false)
    {
        if (!quoteAlways && value.Length > 0 && SafeScalar.IsMatch(value) && !YamlKeywords.Contains(value))
        {
            return value;
        }

        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    // Bare when shell-safe, otherwise single-quoted (with the '\'' escape for embedded quotes).
    private static readonly Regex SafeShell = new(@"^[A-Za-z0-9_./@=:,-]+$", RegexOptions.Compiled);

    private static string ShellArg(string value) =>
        value.Length > 0 && SafeShell.IsMatch(value)
            ? value
            : "'" + value.Replace("'", "'\\''") + "'";
}
