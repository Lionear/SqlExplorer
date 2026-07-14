using Lionear.SqlExplorer.Sdk.Branding;
using Lionear.SqlExplorer.Sdk.Connections;
using Lionear.SqlExplorer.Sdk.Ddl;
using Lionear.SqlExplorer.Sdk.Query;
using Lionear.SqlExplorer.Sdk.Routines;
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
    /// Stream a result set row-by-row into <paramref name="visitor"/> instead of materialising every
    /// row (and every cell) up front — the read half of the streaming backup. LOB cells are exposed as
    /// forward-only streams so a multi-gigabyte value never has to fit in a single .NET array.
    /// The default implementation replays <see cref="ExecuteQueryAsync"/>'s materialised rows, so
    /// providers keep working unchanged; only engines that can genuinely stream (e.g. SQL Server via
    /// <c>SequentialAccess</c>) override it.
    /// </summary>
    async Task StreamQueryAsync(ConnectionProfile profile, string sql, IQueryRowVisitor visitor, CancellationToken ct)
    {
        var result = await ExecuteQueryAsync(profile, sql, ct);
        await visitor.OnColumnsAsync(result.Columns, ct);
        foreach (var row in result.Rows)
        {
            ct.ThrowIfCancellationRequested();
            await visitor.OnRowAsync(new MaterializedStreamedRow(row), ct);
        }
    }

    /// <summary>
    /// Run one INSERT whose parameters may be streams (the write half of the streaming restore), so a
    /// huge cell can be pushed from the backup file straight into the database without buffering it.
    /// The default implementation drains any streams into materialised values and defers to
    /// <see cref="ExecuteBatchAsync"/>; only providers with true parameter streaming override it.
    /// </summary>
    async Task InsertStreamingAsync(ConnectionProfile profile, string insertSql, IReadOnlyList<StreamingParam> parameters, CancellationToken ct)
    {
        var bound = new List<SqlParam>(parameters.Count);
        foreach (var p in parameters)
        {
            object? value = p.Value.Kind switch
            {
                StreamingValue.ValueKind.Null => null,
                StreamingValue.ValueKind.Scalar => p.Value.Scalar,
                StreamingValue.ValueKind.ByteStream => await ReadAllBytesAsync(p.Value.ByteStream!, ct),
                StreamingValue.ValueKind.TextStream => await p.Value.TextReader!.ReadToEndAsync(ct),
                _ => null
            };
            bound.Add(new SqlParam(p.Name, value));
        }

        await ExecuteBatchAsync(profile, [new SqlStatement(insertSql, bound)], ct);
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

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

    /// <summary>
    /// The CREATE/definition text of the object at <paramref name="ancestors"/> (a procedure, function,
    /// view or trigger), for the "View Definition" action that opens it in a normal editable query tab.
    /// Returns <c>null</c> when the provider cannot supply a definition for that node (the default) — the
    /// same "null = not supported" convention as <see cref="ParseConnectionString"/>.
    /// </summary>
    Task<string?> GetObjectDefinitionAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct) => Task.FromResult<string?>(null);

    /// <summary>
    /// The parameters of the procedure/function at <paramref name="ancestors"/>, for the routine
    /// "Execute…" dialog. Output/return values are surfaced as <see cref="RoutineParameter.IsOutput"/>
    /// rows. Empty (the default) means "no parameters" — the host then skips the dialog and generates
    /// the call directly.
    /// </summary>
    Task<IReadOnlyList<RoutineParameter>> GetRoutineParametersAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct) => Task.FromResult<IReadOnlyList<RoutineParameter>>([]);

    /// <summary>
    /// Build a dialect-specific script that calls the procedure/function at <paramref name="ancestors"/>.
    /// <paramref name="parameters"/> is the full parameter list from <see cref="GetRoutineParametersAsync"/>
    /// (the host already has it, so the provider need not re-introspect to know types/output flags);
    /// <paramref name="inputValues"/> holds the user-entered values for the IN parameters, keyed by
    /// <see cref="RoutineParameter.Name"/>. The script captures any OUT parameters / return value in a
    /// trailing SELECT so they appear as an ordinary result set when the user runs it — no separate
    /// execute machinery. The host opens the returned text in an editable tab; the user presses Run. Only
    /// reached for providers that expose Procedure/Function nodes, so the default throws.
    /// </summary>
    SqlStatement BuildCallStatement(
        IReadOnlyList<DbNodeRef> ancestors,
        IReadOnlyList<RoutineParameter> parameters,
        IReadOnlyDictionary<string, string?> inputValues) =>
        throw new NotSupportedException("This provider does not support executing routines.");

    /// <summary>
    /// True when this provider exposes an Activity Monitor (live server sessions/queries) — gates the
    /// "Activity Monitor…" action on a connection root. False (the default) for engines with no
    /// server-process model, e.g. SQLite. Same "false = not supported" convention as
    /// <see cref="ParseConnectionString"/>.
    /// </summary>
    bool SupportsActivityMonitor => false;

    /// <summary>
    /// One Activity-Monitor refresh: the engine's live sessions/queries as an ordinary result set plus
    /// the id of the session that produced this snapshot (see <see cref="ActiveSessionSnapshot"/>). The
    /// column set is the provider's own — the host doesn't interpret it beyond <see cref="SessionIdColumn"/>.
    /// Only reached when <see cref="SupportsActivityMonitor"/> is true, so the default throws.
    /// </summary>
    Task<ActiveSessionSnapshot> GetActiveSessionsAsync(ConnectionProfile profile, CancellationToken ct) =>
        throw new NotSupportedException("This provider does not support the activity monitor.");

    /// <summary>
    /// Which <see cref="GetActiveSessionsAsync"/> column identifies a row for Kill/Cancel — the host
    /// reads this cell's value and passes it back as <c>sessionId</c>, without needing to understand the
    /// rest of the (per-engine) column set. Empty when the monitor is unsupported.
    /// </summary>
    string SessionIdColumn => string.Empty;

    /// <summary>
    /// Hard kill: terminate the whole session/connection identified by <paramref name="sessionId"/>.
    /// Always present where <see cref="SupportsActivityMonitor"/> is true; the default throws.
    /// </summary>
    Task KillSessionAsync(ConnectionProfile profile, string sessionId, CancellationToken ct) =>
        throw new NotSupportedException("This provider does not support the activity monitor.");

    /// <summary>
    /// True when the engine can cancel just the running statement without dropping the connection
    /// (Postgres <c>pg_cancel_backend</c>, MySQL <c>KILL QUERY</c>). False (the default) — e.g. SQL
    /// Server, whose <c>KILL</c> is always hard — hides the "Cancel Query…" row action.
    /// </summary>
    bool SupportsCancelQuery => false;

    /// <summary>
    /// Soft cancel: abort only the running statement on <paramref name="sessionId"/>, leaving the
    /// connection open. Only reached when <see cref="SupportsCancelQuery"/> is true, so the default throws.
    /// </summary>
    Task CancelQueryAsync(ConnectionProfile profile, string sessionId, CancellationToken ct) =>
        throw new NotSupportedException("This provider does not support cancelling a query.");
}
