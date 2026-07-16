using SqlExplorer.Sdk;
using SqlExplorer.Sdk.Branding;
using SqlExplorer.Sdk.Connections;
using SqlExplorer.Sdk.Ddl;
using SqlExplorer.Sdk.Query;
using SqlExplorer.Sdk.Schema;

namespace SqlExplorer.Core.Providers;

/// <summary>
/// Stand-in for a saved connection whose provider plugin isn't installed (e.g. a non-default provider
/// like MongoDB left out of a build). Keeps the connection node visible in the tree — every action
/// fails with a clear message, driving the node to its normal connect-failure (Error) state — instead
/// of the host crashing while resolving the provider at startup.
/// </summary>
public sealed class MissingDbProvider(string providerId) : IDbProvider
{
    private Exception NotInstalled() =>
        new InvalidOperationException($"The '{providerId}' provider is not installed. Install it via the Plugin Store.");

    public string DisplayName => $"Unknown provider ({providerId})";
    public ProviderIcon? Icon => null;
    public ISqlDialect Dialect => throw NotInstalled();
    public bool IsSqlBased => false;
    public IReadOnlyList<ConnectionField> ConnectionFields => [];

    public string BuildConnectionString(IReadOnlyDictionary<string, string?> values) => throw NotInstalled();

    public Task<bool> TestConnectionAsync(ConnectionProfile profile, CancellationToken ct) => throw NotInstalled();

    public Task<IReadOnlyList<DbTreeNode>> GetChildNodesAsync(
        ConnectionProfile profile, IReadOnlyList<DbNodeRef> ancestors, CancellationToken ct) => throw NotInstalled();

    public Task<QueryResult> ExecuteQueryAsync(ConnectionProfile profile, string sql, CancellationToken ct) => throw NotInstalled();

    public Task<int> ExecuteBatchAsync(
        ConnectionProfile profile, IReadOnlyList<SqlStatement> statements, CancellationToken ct) => throw NotInstalled();

    public IReadOnlyList<CreateCapability> CreateCapabilities => [];
    public IReadOnlyList<string> ColumnTypes => [];

    public SqlStatement BuildCreateStatement(CreateObjectSpec spec) => throw NotInstalled();

    public Task ExecuteDdlAsync(ConnectionProfile profile, string sql, CancellationToken ct) => throw NotInstalled();

    public Task<IReadOnlyList<string>> GetDatabasesAsync(ConnectionProfile profile, CancellationToken ct) => throw NotInstalled();

    public Task<IReadOnlyList<QueryResult>> ExecuteScriptAsync(ConnectionProfile profile, string sql, CancellationToken ct) => throw NotInstalled();

    public Task<QueryResult> ExplainAsync(ConnectionProfile profile, string sql, CancellationToken ct) => throw NotInstalled();
}
