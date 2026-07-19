using SqlExplorer.Core.Plugins;
using SqlExplorer.Sdk;
using SqlExplorer.Sdk.Mcp;
using SqlExplorer.Sdk.Tools;

namespace SqlExplorer.Core.Store;

/// <summary>
/// The host-side acceptance window for a plugin build. A plugin only declares the version it was built
/// against (<c>minHostApiVersion</c>); whether it is compatible is the host's call — a build is loadable
/// when its version falls in [<see cref="MinSupported"/>, <see cref="Current"/>], because additive host-API
/// bumps stay binary-compatible (new default-interface members, enum values, DTOs). Mirrors the loader gate
/// (<see cref="ProviderHostApi.IsCompatible"/>), so the Store never offers a plugin the loader would refuse.
/// </summary>
public readonly record struct HostApiCompat(int Current, int MinSupported)
{
    public bool Accepts(int pluginMinHostApiVersion) =>
        pluginMinHostApiVersion >= MinSupported && pluginMinHostApiVersion <= Current;
}

/// <summary>
/// Resolves the host API acceptance window a store entry must be judged against. The plugin kinds version
/// independently (<see cref="ProviderHostApi"/> vs <see cref="ToolHostApi"/> vs <see cref="McpHostApi"/>), so
/// the plugin's <c>type</c> picks which contract's window applies. <c>tool</c> and <c>extension</c> (SE-164)
/// share the <see cref="ToolHostApi"/> contract — same as their loader gate. Must stay in step with the
/// loaders (<see cref="ProviderHostApi.IsCompatible"/> / <see cref="ToolHostApi.IsCompatible"/> /
/// <see cref="McpHostApi.IsCompatible"/>) so the Store never judges a plugin against the wrong contract.
/// </summary>
public static class HostApiVersions
{
    public static HostApiCompat CompatFor(string? pluginType) => pluginType switch
    {
        // Tools and standing-subsystem extensions both load via ToolHostApi (see SubsystemPluginLoader).
        PluginManifest.Types.Tool or PluginManifest.Types.Extension => new(ToolHostApi.Version, ToolHostApi.MinimumSupported),
        PluginManifest.Types.Mcp => new(McpHostApi.Version, McpHostApi.MinimumSupported),
        _ => new(ProviderHostApi.Version, ProviderHostApi.MinimumSupported) // provider, or unspecified
    };
}
