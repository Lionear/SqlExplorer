namespace SqlExplorer.Core.Mcp;

/// <summary>
/// The runtime policy for MCP-driven connection creation (SE-155). The host reads this live (via a
/// <c>Func&lt;McpConnectionPolicy&gt;</c>) on every create call, so toggling the setting or editing the
/// allowlist takes effect without restarting the server. Fail-closed: <see cref="Allow"/> defaults off.
/// </summary>
public sealed record McpConnectionPolicy(bool Allow, IReadOnlyList<string> AllowedHosts, string Folder)
{
    /// <summary>Loopback hosts are always permitted, and are the <em>only</em> hosts a Sandbox (DDL)
    /// connection may ever target.</summary>
    public static readonly IReadOnlyList<string> Loopback = ["localhost", "127.0.0.1", "::1"];

    /// <summary>Whether <paramref name="host"/> is a loopback address.</summary>
    public bool IsLoopback(string host) => Loopback.Contains(host, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="host"/> may be targeted at all: loopback always, plus any configured
    /// allowlist entry.</summary>
    public bool IsHostAllowed(string host) =>
        IsLoopback(host) || AllowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase);
}
