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

/// <summary>A provider (database driver) an MCP client may target with <c>create_connection</c>, plus the
/// fields it needs (SE-155). The <see cref="Description"/> spells out that a provider is a driver so the AI
/// uses the right term. Backs <c>list_providers</c> — metadata only, never any field <em>values</em>.</summary>
public sealed record McpProviderInfo(string Id, string DisplayName, string Description, IReadOnlyList<McpProviderField> Fields);

/// <summary>One connection field a provider declares, as an MCP client needs to understand it: which values
/// to supply for <c>create_connection</c>, which are <see cref="Required"/> vs optional, which are
/// <see cref="Secret"/> (passwords — the host routes these to the keychain and never echoes them back),
/// which are <see cref="Advanced"/>, and the allowed <see cref="Choices"/> for a choice field.</summary>
public sealed record McpProviderField(
    string Key,
    string Label,
    string Type,
    bool Required,
    bool Secret,
    string? Default,
    bool Advanced,
    IReadOnlyList<string>? Choices);

/// <summary>An MCP client's request to create a connection (SE-155). <see cref="Persistent"/> false = an
/// in-memory, session-only connection wiped on shutdown; true = saved to the config file/keychain.
/// <see cref="Access"/> is the requested AI-access level ("readonly"/"readwrite"/"sandbox"; null = the host's
/// default) — the host may cap it (persistent can't get Sandbox; non-loopback can't get Sandbox).</summary>
public sealed record McpCreateConnectionRequest(
    string ProviderId,
    string Name,
    IReadOnlyDictionary<string, string?> Values,
    bool Persistent,
    string? Access);

/// <summary>Result of a <c>create_connection</c> call: the new connection's id (use it with the other tools)
/// plus the access level actually granted, which may be lower than requested.</summary>
public sealed record McpCreateConnectionResult(
    string ConnectionId,
    string Name,
    string ProviderId,
    bool Persistent,
    string Access);
