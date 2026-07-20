namespace SqlExplorer.Sdk.Tools;

/// <summary>
/// Versioning gate between the host and non-provider plugins (tools + standing-subsystem extensions),
/// separate from <c>ProviderHostApi</c> so the plugin kinds evolve independently. A plugin's
/// <c>plugin.json</c> declares the version it was built for; the loader refuses one this host cannot satisfy.
/// </summary>
public static class ToolHostApi
{
    // v2 (2026-07-14): added IToolPlugin.MenuPath (default []) — tools can declare a nested submenu path
    //                  (Tools ▸ Shrink ▸ Database) instead of only appearing flat under Tools. Also
    //                  IToolUiContext.QueryAsync (Route-B live-data hook). Both additive.
    // v3 (2026-07-19): the extensibility family (SE-164) — the standing-subsystem plugin type
    //                  (type: "extension"), loaded via this same contract. Adds, in SqlExplorer.Sdk.Extensibility:
    //                  ISubsystemPlugin + IPluginRuntimeContext + IPluginStorage + the capability model
    //                  (PluginCapabilities), IManagedConnections (incl. All()) + ManagedConnectionInfo, IHostUi,
    //                  and the contribution seams IPanelPlugin / IMenuPlugin / IBackgroundPlugin /
    //                  IConnectionMenuPlugin. Additive: classic tools are untouched.
    // v4 (2026-07-20): the "services" capability (SE-171) — a plugin that declares it gets its
    //                  marker-annotated services (ISingletonService/ITransientService/IScopedService, in
    //                  SqlExplorer.Sdk.Extensibility) auto-registered in the host container, and a scoped
    //                  resolver on IPluginRuntimeContext.Services (new member). Additive for existing plugins;
    //                  a plugin that *uses* the seam must declare v4 so an older host refuses it rather than
    //                  crashing on the missing member.
    public const int Version = 4;

    /// <summary>Oldest plugin ABI this host still loads. Every bump has been additive (v2 tool defaults, v3
    /// extensibility seams, v4 the services capability), so older tools keep loading on a newer host.</summary>
    public const int MinimumSupported = 1;

    public static bool IsCompatible(int pluginVersion) =>
        pluginVersion >= MinimumSupported && pluginVersion <= Version;
}
