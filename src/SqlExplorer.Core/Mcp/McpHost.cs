using System.Diagnostics;
using SqlExplorer.Core.Connections;
using SqlExplorer.Core.History;
using SqlExplorer.Core.Logging;
using SqlExplorer.Core.Security;
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
    MasterPasswordService masterPassword,
    Func<string, string?> getSetting,
    Func<McpConnectionPolicy> connectionPolicy,
    McpActivityLog activity,
    Action<string> audit) : IMcpHost
{
    private const int DefaultMaxRows = 200;
    private const int DefaultTimeoutSeconds = 30;

    /// <summary>Origin stamp on connections the MCP server creates (SE-155), so <c>delete_connection</c> can
    /// scope deletes to only the AI's own persisted connections and the tree can badge them.</summary>
    public const string CreatedByOrigin = "mcp";

    // Field keys that carry the target host, in priority order — used to enforce the create allowlist.
    // File-based providers (e.g. SQLite) declare none, so a create with no host counts as local.
    private static readonly string[] HostKeys = ["host", "server", "hostname", "endpoint"];

    // Persisted connections plus in-memory transient ones — the MCP surface treats both alike, so the AI can
    // list, query and delete a connection it just created transiently.
    private IEnumerable<SavedConnection> AllConnections() => connections.List().Concat(connections.ListTransient());

    // Host settings are top-level (not per-plugin); the host constructs this with a reader over them.
    private bool RequireAuthOn => getSetting("requireAuth") is not "false"; // default on

    // Redact suspected secrets out of result cells before they reach the AI (SE-145). Default on: any
    // connection that reaches a result is already MCP-reachable, i.e. AI-facing.
    private bool ScrubSecretsOn => getSetting("scrubSecrets") is not "false";
    private readonly SecretScrubber scrubber = new();

    public IReadOnlyList<McpConnectionInfo> ListConnections() =>
        AllConnections()
            .Where(c => c.IsMcpReachable)
            .Select(c => new McpConnectionInfo(c.Id, c.Name, c.ProviderId, c.ReadOnly, c.AiAccess.ToString()))
            .ToList();

    public IReadOnlyList<McpQueryLogEntry> GetQueryLog(int? limit, string? source)
    {
        // Fail-closed: only surface log entries for connections that are MCP-reachable right now, so the log
        // never reveals a connection (or the SQL run against it) the AI could not otherwise reach.
        var reachable = AllConnections().Where(c => c.IsMcpReachable).Select(c => c.Id).ToHashSet();

        var want = Math.Clamp(limit ?? 100, 1, 1000);
        var filter = new QueryLogFilter
        {
            Source = source?.ToLowerInvariant() switch
            {
                "app" or "user" => QueryHistorySource.User,
                "mcp" or "ai" => QueryHistorySource.Ai,
                _ => null
            },
            // Read a generous window, then narrow to reachable connections and take the requested count.
            Limit = 5000
        };

        return queryLog.Read(filter)
            .Where(e => reachable.Contains(e.ConnectionId))
            .Take(want)
            .Select(e => new McpQueryLogEntry(
                e.TimestampUtc, e.Source.ToString(), e.ConnectionName, e.Sql, e.DurationMs, e.RowCount, e.Success, e.Error))
            .ToList();
    }

    // Resolve a reachable connection by id or throw — the shared fail-closed gate for every tool. A missing
    // or non-reachable id is refused identically (no distinction that could confirm an excluded id exists).
    private SavedConnection RequireReachable(string connectionId, string tool)
    {
        var connection = AllConnections().FirstOrDefault(c => c.Id == connectionId);
        if (connection is not { IsMcpReachable: true })
        {
            LogAudit(tool, connectionId, allowed: false, "connection not AI-accessible", RequireAuthOn);
            throw new McpAccessException("Connection is not available to the MCP server.");
        }

        // Master-password gate: when the app's secrets are locked (idle-locked, or not yet unlocked in the
        // GUI) a connection that needs a stored password can't be decrypted from the background MCP server.
        // Refuse with a clear, actionable message instead of letting the driver fail later with a confusing
        // "login failed" error. Connections without a secret field (e.g. SQLite, trusted auth) are unaffected.
        if (masterPassword.IsEnabled && !masterPassword.IsUnlocked
            && providers.Get(connection.ProviderId).ConnectionFields.Any(f => f.IsSecret))
        {
            LogAudit(tool, connectionId, allowed: false, "master password locked", RequireAuthOn);
            throw new McpAccessException(
                "The connection's credentials are locked by the SQL Explorer master password. " +
                "Open SQL Explorer and enter the master password to unlock, then retry.");
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
            var outcome = mapped.Truncated ? "ok (row-capped)" : "ok";
            if (mapped.RedactedCount > 0) outcome += $", redacted={mapped.RedactedCount}";
            LogAudit("run_query", connectionId, allowed: true, outcome, RequireAuthOn);
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
        // Structured mirror for the in-app "AI activity" panel (SE-159), alongside the Trace line above.
        activity.Record(new McpActivityEntry(DateTime.UtcNow, tool, connectionId, allowed, reason));
    }

    public IReadOnlyList<McpProviderInfo> ListProviders()
    {
        var result = providers.All
            .Select(r => new McpProviderInfo(
                r.Id,
                r.Provider.DisplayName,
                $"Database driver (provider) for {r.Provider.DisplayName}. Supply the fields below to create a connection with create_connection.",
                r.Provider.ConnectionFields
                    .Select(f => new McpProviderField(
                        f.Key, f.Label, f.Type.ToString(), f.Required, f.IsSecret, f.Default, f.Advanced, f.Choices))
                    .ToList()))
            .ToList();

        // Read-only schema discovery — creates nothing, so it is never gated by the create policy. Audited
        // for visibility so the activity panel shows the AI enumerating drivers.
        LogAudit("list_providers", null, allowed: true, $"{result.Count} providers", RequireAuthOn);
        return result;
    }

    public Task<McpCreateConnectionResult> CreateConnectionAsync(McpCreateConnectionRequest request, CancellationToken ct)
    {
        var policy = connectionPolicy();

        // (1) Feature gate — fail-closed. Even with the server on, creation is off until the user opts in.
        if (!policy.Allow)
        {
            LogAudit("create_connection", null, allowed: false, "connection creation disabled", RequireAuthOn);
            throw new McpAccessException("Creating connections over MCP is disabled. Enable it in SQL Explorer settings.");
        }

        // (2) Provider must exist.
        if (!providers.TryGet(request.ProviderId, out var provider))
        {
            LogAudit("create_connection", null, allowed: false, $"unknown provider '{request.ProviderId}'", RequireAuthOn);
            throw new McpAccessException($"Unknown provider '{request.ProviderId}'. Call list_providers for the available drivers.");
        }

        // (3) Every required field must be supplied.
        var missing = provider.ConnectionFields
            .Where(f => f.Required && string.IsNullOrEmpty(Value(request.Values, f.Key)))
            .Select(f => f.Key)
            .ToList();
        if (missing.Count > 0)
        {
            LogAudit("create_connection", null, allowed: false, $"missing required: {string.Join(", ", missing)}", RequireAuthOn);
            throw new McpAccessException(
                $"Missing required field(s): {string.Join(", ", missing)}. Call list_providers for this provider's fields.");
        }

        // (4) Host allowlist — loopback always; anything else must be configured. File-based providers (no
        // host field) skip this and count as local.
        var host = HostValue(request.Values);
        if (host is not null && !policy.IsHostAllowed(host))
        {
            LogAudit("create_connection", null, allowed: false, $"host '{host}' not allowed", RequireAuthOn);
            throw new McpAccessException($"Host '{host}' is not in the MCP allowed-hosts list. Add it in SQL Explorer settings.");
        }

        // (5) Master-password gate: persisting a secret while locked would strand it un-decryptable.
        var hasSecret = provider.ConnectionFields.Any(f => f.IsSecret && !string.IsNullOrEmpty(Value(request.Values, f.Key)));
        if (request.Persistent && hasSecret && masterPassword.IsEnabled && !masterPassword.IsUnlocked)
        {
            LogAudit("create_connection", null, allowed: false, "master password locked", RequireAuthOn);
            throw new McpAccessException(
                "The master password is locked; unlock SQL Explorer before creating a connection with a stored secret.");
        }

        // (6) Resolve the access level actually granted (may be capped below the request).
        var isLoopback = host is null || policy.IsLoopback(host);
        var granted = ResolveAccess(ParseAccess(request.Access), request.Persistent, isLoopback);

        var id = Guid.NewGuid().ToString("N");
        if (request.Persistent)
        {
            connections.Save(id, request.Name, request.ProviderId, request.Values,
                folder: policy.Folder, aiAccess: granted, origin: CreatedByOrigin);
        }
        else
        {
            connections.CreateTransient(id, request.Name, request.ProviderId, request.Values,
                folder: policy.Folder, aiAccess: granted, origin: CreatedByOrigin);
        }

        LogAudit("create_connection", id, allowed: true, $"{(request.Persistent ? "persistent" : "transient")} {granted}", RequireAuthOn);
        return Task.FromResult(new McpCreateConnectionResult(id, request.Name, request.ProviderId, request.Persistent, granted.ToString()));
    }

    public void DeleteConnection(string connectionId)
    {
        // Transient connections are always MCP-created — drop from the in-memory overlay first.
        if (connections.RemoveTransient(connectionId))
        {
            LogAudit("delete_connection", connectionId, allowed: true, "transient removed", RequireAuthOn);
            return;
        }

        // Persisted: only ever delete a connection the MCP server itself created (origin match) — never the
        // user's or another plugin's.
        var connection = connections.List().FirstOrDefault(c => c.Id == connectionId);
        if (connection is null || connection.Origin != CreatedByOrigin)
        {
            LogAudit("delete_connection", connectionId, allowed: false, "not an MCP-created connection", RequireAuthOn);
            throw new McpAccessException("Only connections created over MCP can be deleted this way.");
        }

        connections.Delete(connectionId);
        LogAudit("delete_connection", connectionId, allowed: true, "persistent removed", RequireAuthOn);
    }

    // Resolve the granted access level from what was requested, capping per the security model: persisted
    // connections never get Sandbox (DDL) — capped at ReadWrite; transient connections get Sandbox only on
    // loopback. Unspecified defaults to Sandbox for a transient loopback connection (so the AI can build and
    // test a schema) and ReadWrite otherwise.
    private static AiAccessMode ResolveAccess(AiAccessMode? requested, bool persistent, bool isLoopback)
    {
        if (persistent)
        {
            return requested switch
            {
                AiAccessMode.ReadOnly => AiAccessMode.ReadOnly,
                _ => AiAccessMode.ReadWrite // None/unspecified/ReadWrite/Sandbox → cap at ReadWrite
            };
        }

        return requested switch
        {
            AiAccessMode.ReadOnly => AiAccessMode.ReadOnly,
            AiAccessMode.ReadWrite => AiAccessMode.ReadWrite,
            AiAccessMode.Sandbox => isLoopback ? AiAccessMode.Sandbox : AiAccessMode.ReadWrite,
            _ => isLoopback ? AiAccessMode.Sandbox : AiAccessMode.ReadWrite // unspecified
        };
    }

    private static AiAccessMode? ParseAccess(string? access) => access?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "readonly" or "read" => AiAccessMode.ReadOnly,
        "readwrite" or "write" => AiAccessMode.ReadWrite,
        "sandbox" => AiAccessMode.Sandbox,
        "none" => AiAccessMode.None,
        _ => null
    };

    private static string? Value(IReadOnlyDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var v) ? v : null;

    private static string? HostValue(IReadOnlyDictionary<string, string?> values) =>
        HostKeys.Select(k => Value(values, k)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

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

    private McpQueryResult Map(Sdk.Query.QueryResult result, int cap, double durationMs)
    {
        var columns = result.Columns.Select(c => c.Name).ToList();
        var truncated = result.Rows.Count > cap;
        IReadOnlyList<IReadOnlyList<object?>> rows = result.Rows.Take(cap)
            .Select(r => (IReadOnlyList<object?>)r.ToList())
            .ToList();

        var redacted = 0;
        if (ScrubSecretsOn)
        {
            var outcome = scrubber.Scrub(columns, rows);
            rows = outcome.Rows;
            redacted = outcome.RedactedCount;
        }

        return new McpQueryResult(columns, rows, rows.Count, durationMs, truncated, redacted);
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
