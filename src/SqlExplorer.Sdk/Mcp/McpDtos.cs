namespace SqlExplorer.Sdk.Mcp;

/// <summary>A connection as an MCP client may see it — identity + engine + coarse access flags only,
/// never a connection string or secret. The host only ever returns connections that are MCP-reachable
/// (opted in and not hard-excluded), so a client never learns an excluded connection exists.</summary>
public sealed record McpConnectionInfo(string Id, string Name, string ProviderId, bool ReadOnly, string AiAccess);

/// <summary>One schema-tree entry for <c>get_schema</c>: a table/view/column/folder with its child-count
/// hint and (for a column) its type. Deliberately flat — the client walks it with the optional path arg.</summary>
public sealed record McpSchemaEntry(string Name, string Kind, string? Type);

/// <summary>The result of a query/explain run over MCP: column names + row-major values (already row-capped),
/// with the true counts and whether the cap truncated the set. <see cref="RedactedCount"/> reports how many
/// cell values the host redacted as suspected secrets (SE-145) — a placeholder like <c>«redacted:token»</c>
/// stands in their place, so a client can tell those apart from real data.</summary>
public sealed record McpQueryResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    int RowCount,
    double DurationMs,
    bool Truncated,
    int RedactedCount = 0);

/// <summary>One entry from the query log as an MCP client may see it. Only entries for MCP-reachable
/// connections are ever returned (same fail-closed rule as the rest of the surface), and only the SQL and
/// timing/outcome — never result data or secrets. <see cref="Source"/> is "User" (app) or "Ai" (MCP).</summary>
public sealed record McpQueryLogEntry(
    DateTime TimestampUtc,
    string Source,
    string ConnectionName,
    string Sql,
    long DurationMs,
    int RowCount,
    bool Success,
    string? Error);

/// <summary>Thrown by <see cref="IMcpHost"/> when a call is refused by the host-side guards (connection not
/// MCP-reachable, statement not permitted for the access mode, DDL/multi-statement, missing connection).
/// The plugin maps it to an MCP error — the refusal happens in the host, never at the driver.</summary>
public sealed class McpAccessException(string message) : Exception(message);
