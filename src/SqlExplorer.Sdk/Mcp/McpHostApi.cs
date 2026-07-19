namespace SqlExplorer.Sdk.Mcp;

/// <summary>
/// Versioning gate between the host and <c>mcp</c> plugins, separate from ProviderHostApi/ToolHostApi so
/// the three plugin kinds evolve independently. An MCP plugin's <c>plugin.json</c> declares the version it
/// was built for; the loader refuses one this host cannot satisfy.
/// </summary>
public static class McpHostApi
{
    // v1 (2026-07-14): initial IMcpPlugin/IMcpHost contract — list_connections/get_schema/run_query/
    //                  explain_query, host-side authz (AiAccess + SQL classification + caps + audit).
    // v2 (2026-07-19): additive — list_providers/create_connection/delete_connection for MCP-driven
    //                  connection creation (SE-155). Purely additive, so v1 plugins still load.
    public const int Version = 2;

    /// <summary>Oldest MCP ABI this host still loads. v2 is additive over v1, so this stays at 1 — an
    /// existing v1 plugin keeps working; a breaking change would raise it.</summary>
    public const int MinimumSupported = 1;

    public static bool IsCompatible(int pluginVersion) =>
        pluginVersion >= MinimumSupported && pluginVersion <= Version;
}
