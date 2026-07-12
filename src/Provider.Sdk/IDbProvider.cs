namespace Lionear.SqlExplorer.Sdk;

/// <summary>
/// The single seam every database engine plugs into — and the public contract
/// third-party providers implement. UI and view-models depend only on this
/// abstraction, never on a concrete driver.
/// </summary>
public interface IDbProvider
{
    DatabaseKind Kind { get; }

    /// <summary>Human-friendly provider name shown in the UI (e.g. "Microsoft SQL Server").</summary>
    string DisplayName { get; }

    /// <summary>Icon shown on this provider's connection nodes; null falls back to a host default.</summary>
    ProviderIcon? Icon { get; }

    ISqlDialect Dialect { get; }

    /// <summary>The fields this provider needs to build a connection — drives the connection dialog.</summary>
    IReadOnlyList<ConnectionField> ConnectionFields { get; }

    /// <summary>
    /// Compose a connection string from field values (keyed by <see cref="ConnectionField.Key"/>),
    /// including any secret just fetched from the keychain. The provider owns its own syntax/escaping.
    /// </summary>
    string BuildConnectionString(IReadOnlyDictionary<string, string?> values);

    Task<bool> TestConnectionAsync(ConnectionProfile profile, CancellationToken ct);

    /// <summary>
    /// Lazily list the children of a schema-tree node. <paramref name="ancestors"/> is the
    /// path from the connection root down to the node being expanded; an empty list means the
    /// connection's own top-level nodes. On-demand loading keeps large servers from being fully
    /// introspected up front (DBeaver-style). Each provider decides its own hierarchy shape.
    /// </summary>
    Task<IReadOnlyList<DbTreeNode>> GetChildNodesAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct);

    Task<QueryResult> ExecuteQueryAsync(ConnectionProfile profile, string sql, CancellationToken ct);

    /// <summary>
    /// Run a batch of parameterised statements inside a single transaction and return the
    /// total number of affected rows. Any failure rolls the whole batch back — this is the
    /// commit step of the editable-grid save-flow (see Notes.md §8). The host generates the
    /// statements (dialect-quoted INSERT/UPDATE/DELETE); the provider owns parameter binding.
    /// </summary>
    Task<int> ExecuteBatchAsync(
        ConnectionProfile profile,
        IReadOnlyList<SqlStatement> statements,
        CancellationToken ct);
}
