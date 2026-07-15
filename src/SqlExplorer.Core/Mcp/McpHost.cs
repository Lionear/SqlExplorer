using System.Diagnostics;
using SqlExplorer.Core.Connections;
using SqlExplorer.Core.History;
using SqlExplorer.Core.Logging;
using SqlExplorer.Core.Providers;
using SqlExplorer.Sdk.Mcp;
using SqlExplorer.Sdk.Schema;

namespace SqlExplorer.Core.Mcp;

/// <summary>
/// The host-side <see cref="IMcpHost"/> — the single place every MCP authorization decision is made,
/// against the live connection/provider instances. The MCP plugin owns only the transport; it calls in
/// here, and here is where reachability (opt-in + not-excluded), the SQL read/write/DDL classification for
/// a connection's access mode, the row/timeout caps, the AI-sourced history entry, and the audit log all
/// happen. Nothing here trusts the caller: every method re-checks reachability from the id, so knowing a
/// connection id is never enough to reach an excluded connection (checklist item 2, fail-closed).
/// </summary>
public sealed class McpHost(
    ConnectionService connections,
    IDbProviderRegistry providers,
    IQueryHistoryStore history,
    IQueryLog queryLog,
    Func<string, string?> getSetting,
    Action<string> audit) : IMcpHost
{
    private const int DefaultMaxRows = 200;
    private const int DefaultTimeoutSeconds = 30;

    // Host settings are top-level (not per-plugin); the host constructs this with a reader over them.
    private bool RequireAuthOn => getSetting("requireAuth") is not "false"; // default on

    public IReadOnlyList<McpConnectionInfo> ListConnections() =>
        connections.List()
            .Where(c => c.IsMcpReachable)
            .Select(c => new McpConnectionInfo(c.Id, c.Name, c.ProviderId, c.ReadOnly, c.AiAccess.ToString()))
            .ToList();

    // Resolve a reachable connection by id or throw — the shared fail-closed gate for every tool. A missing
    // or non-reachable id is refused identically (no distinction that could confirm an excluded id exists).
    private SavedConnection RequireReachable(string connectionId, string tool)
    {
        var connection = connections.List().FirstOrDefault(c => c.Id == connectionId);
        if (connection is not { IsMcpReachable: true })
        {
            LogAudit(tool, connectionId, allowed: false, "connection not AI-accessible", RequireAuthOn);
            throw new McpAccessException("Connection is not available to the MCP server.");
        }

        return connection;
    }

    public async Task<IReadOnlyList<McpSchemaEntry>> GetSchemaAsync(string connectionId, IReadOnlyList<string>? path, CancellationToken ct)
    {
        var connection = RequireReachable(connectionId, "get_schema");
        var provider = providers.Get(connection.ProviderId);
        var profile = connections.Resolve(connection);

        // Walk the lazy tree by name, discovering each level's real DbNodeRef kinds as we go — the client
        // only ever needs to know names.
        var ancestors = new List<DbNodeRef>();
        var children = await provider.GetChildNodesAsync(profile, ancestors, ct);
        foreach (var name in path ?? [])
        {
            var node = children.FirstOrDefault(n => n.Name == name)
                ?? throw new McpAccessException($"Schema path segment '{name}' not found.");
            ancestors.Add(new DbNodeRef(node.Kind, node.Name));
            children = await provider.GetChildNodesAsync(profile, ancestors, ct);
        }

        LogAudit("get_schema", connectionId, allowed: true, null, RequireAuthOn);
        return children.Select(n => new McpSchemaEntry(n.Name, n.Kind.ToString(), n.Detail)).ToList();
    }

    public async Task<McpQueryResult> RunQueryAsync(string connectionId, string sql, int? maxRows, CancellationToken ct)
    {
        var connection = RequireReachable(connectionId, "run_query");

        // The write-guard: classify host-side and refuse anything the access mode doesn't permit — before
        // the SQL ever reaches the driver (CRIT-2). DDL/multi-statement are refused for every mode.
        if (!McpSqlClassifier.IsAllowed(sql, connection.AiAccess))
        {
            var kind = McpSqlClassifier.Classify(sql);
            LogAudit("run_query", connectionId, allowed: false, $"statement '{kind}' not permitted for {connection.AiAccess}", RequireAuthOn);
            throw new McpAccessException($"This statement is not permitted for a {connection.AiAccess} connection.");
        }

        var provider = providers.Get(connection.ProviderId);
        var profile = connections.Resolve(connection);
        var cap = ResolveMaxRows(maxRows);

        var stopwatch = Stopwatch.StartNew();
        using var timeout = TimeoutSource(ct);
        try
        {
            var result = await provider.ExecuteQueryAsync(profile, sql, timeout.Token);
            stopwatch.Stop();
            var mapped = Map(result, cap, stopwatch.Elapsed.TotalMilliseconds);
            AppendHistory(connection, sql, mapped.RowCount, success: true, error: null, stopwatch.ElapsedMilliseconds);
            LogAudit("run_query", connectionId, allowed: true, mapped.Truncated ? "ok (row-capped)" : "ok", RequireAuthOn);
            return mapped;
        }
        catch (McpAccessException)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            AppendHistory(connection, sql, 0, success: false, error: ex.Message, stopwatch.ElapsedMilliseconds);
            LogAudit("run_query", connectionId, allowed: true, $"error: {ex.Message}", RequireAuthOn);
            throw;
        }
    }

    public async Task<McpQueryResult> ExplainAsync(string connectionId, string sql, CancellationToken ct)
    {
        var connection = RequireReachable(connectionId, "explain_query");
        var provider = providers.Get(connection.ProviderId);
        var profile = connections.Resolve(connection);

        var stopwatch = Stopwatch.StartNew();
        using var timeout = TimeoutSource(ct);
        var result = await provider.ExplainAsync(profile, sql, timeout.Token);
        stopwatch.Stop();
        LogAudit("explain_query", connectionId, allowed: true, null, RequireAuthOn);
        return Map(result, ResolveMaxRows(null), stopwatch.Elapsed.TotalMilliseconds);
    }

    public void LogAudit(string tool, string? connectionId, bool allowed, string? reason, bool requireAuthOn)
    {
        var verdict = allowed ? "ALLOW" : "DENY";
        var authState = requireAuthOn ? "auth" : "NO-AUTH";
        audit($"[MCP {verdict}] {tool} conn={connectionId ?? "-"} ({authState}){(reason is null ? "" : $": {reason}")}");
    }

    // Row cap: the caller may only ever request FEWER rows than the server cap, never more (HIGH-1 — the AI
    // can't raise the limit itself).
    private int ResolveMaxRows(int? requested)
    {
        var serverCap = int.TryParse(getSetting("maxRows"), out var m) && m > 0 ? m : DefaultMaxRows;
        return requested is { } r && r > 0 ? Math.Min(r, serverCap) : serverCap;
    }

    private CancellationTokenSource TimeoutSource(CancellationToken ct)
    {
        var seconds = int.TryParse(getSetting("timeoutSeconds"), out var s) && s > 0 ? s : DefaultTimeoutSeconds;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(seconds));
        return cts;
    }

    private static McpQueryResult Map(Sdk.Query.QueryResult result, int cap, double durationMs)
    {
        var columns = result.Columns.Select(c => c.Name).ToList();
        var truncated = result.Rows.Count > cap;
        var rows = result.Rows.Take(cap)
            .Select(r => (IReadOnlyList<object?>)r.ToList())
            .ToList();
        return new McpQueryResult(columns, rows, rows.Count, durationMs, truncated);
    }

    private void AppendHistory(SavedConnection connection, string sql, int rowCount, bool success, string? error, long durationMs)
    {
        var entry = new QueryHistoryEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            TimestampUtc = DateTime.UtcNow,
            ConnectionId = connection.Id,
            ConnectionName = connection.Name,
            Kind = QueryHistoryKind.Query,
            Sql = sql,
            DurationMs = durationMs,
            RowCount = rowCount,
            Success = success,
            Error = error,
            Source = QueryHistorySource.Ai
        };
        history.Append(entry);
        queryLog.Record(entry); // No-op unless logging + the AI/MCP source are enabled.
    }
}
