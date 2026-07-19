namespace SqlExplorer.Sdk.Mcp;

/// <summary>
/// The host services an MCP plugin drives, and — crucially — the security boundary. The plugin owns only
/// the transport (a loopback HTTP listener + the MCP protocol); every authorization decision lives behind
/// this interface, implemented by the host against its live connection/provider instances. So the plugin
/// never sees a connection string, never decides what SQL is allowed, and never reaches a driver directly:
/// it calls these methods and the host enforces reachability (opt-in + not-excluded), the read/write/DDL
/// classification for the connection's access mode, the row/timeout caps, and the audit log.
/// </summary>
public interface IMcpHost
{
    /// <summary>The MCP-reachable connections (already filtered to opted-in + not-excluded), with no secrets.
    /// Backs <c>list_connections</c>.</summary>
    IReadOnlyList<McpConnectionInfo> ListConnections();

    /// <summary>The schema entries under <paramref name="path"/> (ancestor names from the tree root; null/empty
    /// = top level) of <paramref name="connectionId"/>. Throws <see cref="McpAccessException"/> when the
    /// connection is not MCP-reachable. Backs <c>get_schema</c>.</summary>
    Task<IReadOnlyList<McpSchemaEntry>> GetSchemaAsync(string connectionId, IReadOnlyList<string>? path, CancellationToken ct);

    /// <summary>Run <paramref name="sql"/> on <paramref name="connectionId"/>, capped to
    /// <paramref name="maxRows"/> (or the host default, whichever is lower). The host classifies the SQL and
    /// throws <see cref="McpAccessException"/> unless it is permitted for the connection's access mode
    /// (reads need ReadOnly+, DML needs ReadWrite, DDL/multi-statement always refused). Logs to history as an
    /// AI-sourced entry. Backs <c>run_query</c>.</summary>
    Task<McpQueryResult> RunQueryAsync(string connectionId, string sql, int? maxRows, CancellationToken ct);

    /// <summary>Run this engine's EXPLAIN for <paramref name="sql"/> (read-only; needs only reachability).
    /// Backs <c>explain_query</c>.</summary>
    Task<McpQueryResult> ExplainAsync(string connectionId, string sql, CancellationToken ct);

    /// <summary>Recent entries from the query log, newest first, capped by <paramref name="limit"/> and
    /// optionally filtered by <paramref name="source"/> ("app"/"user" or "mcp"/"ai"; null = both). Only
    /// entries for MCP-reachable connections are returned — the same fail-closed rule as the rest of the
    /// surface, so the log never leaks connections or SQL the AI could not otherwise see. Empty when query
    /// logging is disabled. Backs <c>get_query_log</c>.</summary>
    IReadOnlyList<McpQueryLogEntry> GetQueryLog(int? limit, string? source);

    /// <summary>Record one MCP transport call for the audit trail — including refused/unauthorized/excluded
    /// ones, and whether auth was required at the time — so an unauthenticated window is recognisable after
    /// the fact (plan §8 / CRIT-3).</summary>
    void LogAudit(string tool, string? connectionId, bool allowed, string? reason, bool requireAuthOn);

    /// <summary>The providers (database drivers) available to <c>create_connection</c>, each with its
    /// connection fields (required/optional/secret/choices) so an AI can assemble a valid request (SE-155).
    /// Metadata only — never any secret or field value. Read-only and always available (it creates nothing),
    /// so it is not gated by the connection-create setting. Backs <c>list_providers</c>.</summary>
    IReadOnlyList<McpProviderInfo> ListProviders();

    /// <summary>Create a connection on the user's behalf (SE-155), subject to the host's fail-closed policy:
    /// creation must be enabled, the provider must exist, required fields must be present, the target host must
    /// be on the allowlist (loopback always), and secrets can't be stored while the master password is locked.
    /// Persistent connections are capped at ReadWrite; only transient loopback connections may get Sandbox
    /// (DDL). Throws <see cref="McpAccessException"/> when refused. Backs <c>create_connection</c>.</summary>
    Task<McpCreateConnectionResult> CreateConnectionAsync(McpCreateConnectionRequest request, CancellationToken ct);

    /// <summary>Delete a connection the AI created — a transient one, or a persisted one whose origin is the
    /// MCP server. Never touches the user's or another plugin's connections. Throws
    /// <see cref="McpAccessException"/> otherwise. Backs <c>delete_connection</c>.</summary>
    void DeleteConnection(string connectionId);
}
