using System.Security.Cryptography;
using SqlExplorer.Sdk.Mcp;

namespace SqlExplorer.Mcp.Hosting;

/// <summary>
/// Host-level coordinator for the MCP server: reads the current top-level MCP settings, generates the
/// bearer token on first enable, and starts/stops the <see cref="McpServer"/> accordingly. The app calls
/// <see cref="ApplyAsync"/> at startup and whenever the MCP settings change (enable/disable, port, auth,
/// regenerate token), so a settings change takes effect immediately — no restart. Holds the tools
/// contributed by enabled MCP plugins, discovered once at load.
/// </summary>
public sealed class McpService(
    McpServer server,
    IReadOnlyList<McpToolDefinition> tools,
    Func<McpServerOptions> readOptions,
    Action<string> persistToken)
{
    public IReadOnlyList<McpToolDefinition> Tools => tools;

    /// <summary>Whether the underlying server is currently listening. Mirrors <see cref="McpServer.IsRunning"/>.</summary>
    public bool IsRunning => server.IsRunning;

    /// <summary>Raised after the running-state may have changed (start, stop, or restart). Lets the UI reflect
    /// startup auto-start, Save-driven restarts, and the manual Start/Stop toggle. May fire off the UI thread
    /// (both apply/stop run on a background task) — subscribers must marshal to their own thread.</summary>
    public event Action? StateChanged;

    /// <summary>Apply the current settings: (re)start the server when enabled with tools present, stop it
    /// otherwise. Generates and persists a token the first time it's needed.</summary>
    public async Task ApplyAsync(CancellationToken ct = default)
    {
        var options = readOptions();

        // Generate a token the first time the server is enabled with auth on and none stored yet.
        if (options is { Enabled: true, RequireAuth: true, Token: null or "" })
        {
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            persistToken(token);
            options = options with { Token = token };
        }

        await server.StartAsync(options, tools, ct);
        StateChanged?.Invoke();
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await server.StopAsync(ct);
        StateChanged?.Invoke();
    }
}
