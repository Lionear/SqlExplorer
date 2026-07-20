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
    //   also in v4 (2026-07-20): the "providers" capability (SE-166) — a plugin that declares it gets a
    //                  read-only IProviderCatalog on IPluginRuntimeContext.Providers (new member) listing
    //                  installed providers that declared a container recipe (IDbProvider.ContainerRecipe). Lets
    //                  the Docker plugin containerise third-party engines. Folded into the still-unreleased v4
    //                  rather than opening v5: the whole 0.4.0 dev cycle accumulates additive subsystem surface
    //                  under one version, bumped once at release. Additive — a plugin without the capability
    //                  gets null and degrades to its built-in table.
    //   also in v4 (2026-07-20): the connection-picker seam (SE-99) — a new ToolFieldType.ConnectionPicker
    //                  lets a tool take a *second* saved connection, and IToolHost gains ListConnections() +
    //                  OpenConnection(id) (returning a runnable ToolConnection) so a cross-connection tool
    //                  (SchemaDiff, CopyTable) can open it. A companion ToolFieldType.DatabasePicker picks a
    //                  database on that connection (IToolHost.ListDatabasesAsync + OpenConnection's database
    //                  arg), since a server hosts many. IToolHost.OpenQueryEditor(sql) lets a tool hand its
    //                  generated SQL to a new query tab on the primary connection instead of running DDL itself
    //                  (SchemaDiff uses this). Default interface impls (empty/null) keep older hosts and
    //                  non-dialog IToolHost implementors compiling; folded into the unreleased v4.
    public const int Version = 4;

    /// <summary>Oldest plugin ABI this host still loads. Every bump has been additive (v2 tool defaults, v3
    /// extensibility seams, v4 the services + providers capabilities), so older tools keep loading on a newer
    /// host.</summary>
    public const int MinimumSupported = 1;

    public static bool IsCompatible(int pluginVersion) =>
        pluginVersion >= MinimumSupported && pluginVersion <= Version;
}
