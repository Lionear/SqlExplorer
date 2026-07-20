namespace SqlExplorer.Sdk.Extensibility;

/// <summary>
/// The capability strings a standing-subsystem plugin declares in its <c>plugin.json</c> (and the store
/// index) and the user consents to at install. Each gates one host power: the contribution interfaces
/// (<see cref="ISubsystemPlugin"/> + panel/background/menu, added per phase) and the runtime services on
/// <see cref="IPluginRuntimeContext"/>. Consent/disclosure, not a hard sandbox — a plugin in its ALC stays
/// semi-trusted; the value is transparency at install plus host convenience-APIs handed out only on consent.
/// </summary>
public static class PluginCapabilities
{
    /// <summary>Contributes a docked, long-lived panel (a tool-window beside Output/History).</summary>
    public const string Panel = "panel";

    /// <summary>Runs a background service (a loop) while the app is open.</summary>
    public const string Background = "background";

    /// <summary>Adds items to the top-bar menus.</summary>
    public const string Menu = "menu";

    /// <summary>Uses plugin-scoped persistent storage (<see cref="IPluginStorage"/>).</summary>
    public const string Storage = "storage";

    /// <summary>Creates host-managed connections tagged with the plugin as origin.</summary>
    public const string Connections = "connections";

    /// <summary>
    /// Auto-registers the plugin's own marker-annotated services into the host container and hands the
    /// plugin a resolver (<see cref="IPluginRuntimeContext.Services"/>) scoped to just those services. The
    /// plugin can never register under — or resolve — a host contract; only its own types.
    /// </summary>
    public const string Services = "services";

    /// <summary>Starts external processes (e.g. <c>docker</c>). Disclosure only — no host API to gate.</summary>
    public const string Process = "process";

    /// <summary>
    /// Reads host metadata about installed providers that can be containerised — their
    /// <see cref="Provisioning.ContainerRecipe"/>, via <see cref="IPluginRuntimeContext.Providers"/>. Read-only:
    /// the plugin learns what engines exist and how to provision them, but gains no control over them.
    /// </summary>
    public const string Providers = "providers";
}
