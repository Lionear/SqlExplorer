using Lionear.SqlExplorer.Sdk.Branding;
using Lionear.SqlExplorer.Sdk.Connections;
using Lionear.SqlExplorer.Sdk.Ddl;
using Lionear.SqlExplorer.Sdk.Query;
using Lionear.SqlExplorer.Sdk.Schema;

namespace Lionear.SqlExplorer.Sdk;

/// <summary>
/// The single seam every database engine plugs into — and the public contract
/// third-party providers implement. UI and view-models depend only on this
/// abstraction, never on a concrete driver.
/// </summary>
/// <remarks>
/// A provider carries no identity of its own: which engine it is comes from the
/// <c>id</c> in its <c>plugin.json</c> manifest, attached by the loader. That keeps the
/// set of engines open — a third party ships a new provider with a new manifest id,
/// no host enum to extend.
/// </remarks>
public interface IDbProvider
{
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

    /// <summary>
    /// Inverse of <see cref="BuildConnectionString"/>: parse a pasted connection string into field
    /// values (keyed by <see cref="ConnectionField.Key"/>) so the dialog can prefill itself — the
    /// "import from connection string" flow. Each provider uses its own
    /// <see cref="System.Data.Common.DbConnectionStringBuilder"/> to map keys back to its fields.
    /// Returns <c>null</c> when the provider does not support import (the default); an empty map means
    /// "supported, but nothing recognised". Unknown keys are simply dropped.
    /// </summary>
    IReadOnlyDictionary<string, string?>? ParseConnectionString(string connectionString) => null;

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

    /// <summary>What DDL Create can build for this provider, and which tree-node kind each "New …"
    /// action appears under. Empty = nothing creatable (host hides every DDL Create menu item).</summary>
    IReadOnlyList<CreateCapability> CreateCapabilities { get; }

    /// <summary>Column-type suggestions offered in the DDL Create table dialog's type dropdown.</summary>
    IReadOnlyList<string> ColumnTypes { get; }

    /// <summary>Render <paramref name="spec"/> as dialect-correct DDL for the host to preview (and let
    /// the user edit) before running it via <see cref="ExecuteDdlAsync"/>.</summary>
    SqlStatement BuildCreateStatement(CreateObjectSpec spec);

    /// <summary>
    /// Run one DDL statement (typically the — possibly user-edited — text from
    /// <see cref="BuildCreateStatement"/>) outside any transaction: some engines forbid statements like
    /// <c>CREATE DATABASE</c> inside one, which rules out reusing <see cref="ExecuteBatchAsync"/>.
    /// </summary>
    Task ExecuteDdlAsync(ConnectionProfile profile, string sql, CancellationToken ct);

    /// <summary>The databases/catalogs reachable on this connection, for the query-tab database
    /// switcher. Empty for engines with no database layer (e.g. SQLite).</summary>
    Task<IReadOnlyList<string>> GetDatabasesAsync(ConnectionProfile profile, CancellationToken ct);

    /// <summary>
    /// Run raw SQL text (one or more statements) and return every result set the driver produces
    /// (via <c>NextResult</c>), not just the first. Powers "Run"/"Run at cursor": the host never
    /// needs to know up front whether the text is one statement or a script. A statement that
    /// produces no rows (DDL/DML) still yields one <see cref="QueryResult"/> entry carrying
    /// <see cref="QueryResult.RecordsAffected"/> so the UI always has at least one tab to show.
    /// </summary>
    Task<IReadOnlyList<QueryResult>> ExecuteScriptAsync(ConnectionProfile profile, string sql, CancellationToken ct);

    /// <summary>
    /// Run this provider's EXPLAIN-equivalent for <paramref name="sql"/> without executing the
    /// query itself, and return the plan as a normal <see cref="QueryResult"/> so it renders in the
    /// existing grid infrastructure.
    /// </summary>
    Task<QueryResult> ExplainAsync(ConnectionProfile profile, string sql, CancellationToken ct);
}
