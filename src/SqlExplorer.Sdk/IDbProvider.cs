using SqlExplorer.Sdk.Branding;
using SqlExplorer.Sdk.Connections;
using SqlExplorer.Sdk.Ddl;
using SqlExplorer.Sdk.Editing;
using SqlExplorer.Sdk.Query;
using SqlExplorer.Sdk.Routines;
using SqlExplorer.Sdk.Schema;
using SqlExplorer.Sdk.Security;

namespace SqlExplorer.Sdk;

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

    /// <summary>
    /// False when this provider is not a SQL engine (e.g. a document store like MongoDB). The host then
    /// suppresses its built-in SQL scaffolds — the tree's "SQL commands" submenu (SELECT/INSERT/UPDATE/
    /// DELETE templates) — and relies on <see cref="BuildNodeQuery"/> for any node-action query text.
    /// True (the default) keeps the host's SQL generation. Same "flag defaults to the common case"
    /// convention as <see cref="SupportsActivityMonitor"/>.
    /// </summary>
    bool IsSqlBased => true;

    /// <summary>
    /// Build the query text for a table/collection node convenience action — the "Select top 1000" and
    /// "SQL commands" menu items. A non-SQL provider returns its own text here (e.g. a MongoDB
    /// <c>db.orders.find({}).limit(1000)</c> for <see cref="NodeQueryKind.SelectTop"/>); the host drops it
    /// into a new editable query tab (and runs it for <see cref="NodeQueryKind.SelectTop"/>). Return null
    /// to fall back to the host's built-in SQL generation (the default) — the same "null = not supported"
    /// convention as <see cref="ParseConnectionString"/>. <paramref name="nodePath"/> is the path from the
    /// connection root down to and including the node (as passed to <see cref="GetChildNodesAsync"/>);
    /// <paramref name="columns"/> is the node's column metadata when the host already has it, else null.
    /// <paramref name="profile"/> lets a provider make a cheap live call to decide the text — e.g. Redis
    /// needs a synchronous <c>TYPE key</c> round-trip to pick <c>GET</c>/<c>HGETALL</c>/<c>LRANGE</c>/
    /// <c>SMEMBERS</c>/<c>ZRANGE</c>, since a key's browse command depends on its (untyped-until-queried)
    /// value shape, unlike a SQL table or a Mongo collection (SE-114/SE-20 design note).
    /// </summary>
    string? BuildNodeQuery(
        NodeQueryKind kind,
        IReadOnlyList<DbNodeRef> nodePath,
        IReadOnlyList<ResultColumn>? columns,
        ConnectionProfile profile) => null;

    /// <summary>
    /// Build the statement for a DROP/TRUNCATE/ALTER tree action, letting a provider own that syntax (e.g.
    /// a MongoDB <c>db.orders.drop()</c>) instead of the host's built-in SQL builder. The host previews the
    /// returned statement — the user may edit it — then runs it via <see cref="ExecuteDdlAsync"/>. Return
    /// null for actions this provider does not handle (the default), and the host falls back to its own SQL
    /// generation — the same "null = not supported" convention as <see cref="ParseConnectionString"/>.
    /// </summary>
    SqlStatement? BuildAlterStatement(AlterSpec spec) => null;

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

    /// <summary>
    /// Apply a structured set of row changes — the write half of the editable-grid save-flow for a
    /// provider whose writes cannot be expressed as generated SQL text (<see cref="IsSqlBased"/> is
    /// false, e.g. a document or key-value store). The host builds <paramref name="changes"/> from the
    /// same <c>Base*</c>/<see cref="ResultColumn.IsKey"/> metadata that drives <see cref="ExecuteBatchAsync"/>
    /// for SQL providers, so a provider opts into an editable grid simply by populating that metadata
    /// (e.g. Mongo marking its <c>_id</c> field <c>IsKey</c>) and overriding this method — no SQL is ever
    /// generated for this path. SQL-based providers are unaffected: the host always uses
    /// <see cref="ExecuteBatchAsync"/> for them, so the default here throws. See <see cref="WritebackResult.IsAtomic"/>
    /// for how a non-transactional engine (e.g. a bulk API) reports partial failure.
    /// </summary>
    Task<WritebackResult> ApplyChangesAsync(ConnectionProfile profile, ChangeSet changes, CancellationToken ct) =>
        throw new NotSupportedException("This provider does not support saving grid edits.");

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

    /// <summary>
    /// True when this provider can create/drop database or server users (drives the "New User…"/"Delete"
    /// actions and whether the Users tree folder is offered). False (the default) — e.g. SQLite, which has
    /// no auth model. Same "false = not supported" convention as <see cref="ParseConnectionString"/>.
    /// </summary>
    bool CanManageUsers => false;

    /// <summary>The declarative inputs for the generic "New User…" form, on top of the always-present user
    /// name (MSSQL: a password; MySQL: password + host; Postgres: password + role attributes). Empty when
    /// user management is unsupported.</summary>
    IReadOnlyList<UserField> UserFields => [];

    /// <summary>
    /// Roles the new user can be granted, for the optional role checkbox list in the Create dialog — the
    /// database roles under <paramref name="ancestors"/> (MSSQL) or the cluster/server roles (Postgres/
    /// MySQL). Empty (the default) hides the role picker.
    /// </summary>
    Task<IReadOnlyList<string>> GetAssignableRolesAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>
    /// Build the dialect-correct statement(s) to create a user from the collected <paramref name="values"/>
    /// (keyed by <see cref="UserField.Key"/>, plus <c>"name"</c> for the user name) and the selected
    /// <paramref name="roles"/>. May be a multi-statement batch (CREATE USER + role grants); the host
    /// previews it and runs it via <see cref="ExecuteDdlAsync"/>. Only reached when
    /// <see cref="CanManageUsers"/> is true, so the default throws.
    /// </summary>
    SqlStatement BuildCreateUserStatement(
        IReadOnlyDictionary<string, string?> values,
        IReadOnlyList<string> roles) =>
        throw new NotSupportedException("This provider does not support creating users.");

    /// <summary>
    /// Build the dialect-correct DROP for the user at <paramref name="userNode"/>. MySQL user nodes are
    /// named <c>name@host</c> (one identity), so the provider parses that back out; MSSQL/Postgres use a
    /// plain name. The host shows the usual destructive confirm, then runs it via
    /// <see cref="ExecuteDdlAsync"/>. Default throws.
    /// </summary>
    SqlStatement BuildDropUserStatement(
        DbNodeRef userNode,
        IReadOnlyList<DbNodeRef> ancestors) =>
        throw new NotSupportedException("This provider does not support dropping users.");
}
