using SqlExplorer.Sdk.Extensibility;

namespace SqlExplorer.Core.Plugins;

/// <summary>What activation produced: the registry for shutdown teardown, plus the capability-gated
/// contributions the host wires up — panels mounted as tool-windows, menu items added to the Tools menu, and
/// background loops the host starts under the shutdown token.</summary>
public sealed record SubsystemActivationResult(
    SubsystemRegistry Registry,
    IReadOnlyList<IPanelPlugin> Panels,
    IReadOnlyList<IMenuPlugin> Menus,
    IReadOnlyList<IBackgroundPlugin> Background);

/// <summary>
/// Activates the loaded subsystem plugins (SE-164) <em>after</em> the host's ServiceProvider is built — the
/// only point at which the services a plugin's context leans on actually exist. Storage is available earlier,
/// but <see cref="IManagedConnections"/> is backed by <c>ConnectionService</c>, which is registered late in
/// <c>AppServices.Build</c>; building the context and calling <c>Initialize</c> during the build would hand
/// plugins a dead connections seam. So the loader only produces <see cref="SubsystemActivation"/>s and this
/// activator — resolved post-build in App startup — builds each capability-gated
/// <see cref="PluginRuntimeContext"/>, calls <c>Initialize</c>, and returns a <see cref="SubsystemRegistry"/>
/// so the host can <c>Deactivate</c> them at shutdown. Activation is best-effort: one plugin throwing on
/// Initialize never blocks the others.
/// </summary>
public sealed class SubsystemActivator
{
    private readonly IReadOnlyList<SubsystemActivation> _activations;
    private readonly Func<string, IPluginStorage> _storageProvider;
    private readonly Func<string, IManagedConnections> _connectionsProvider;
    private readonly Action<string>? _log;

    /// <param name="storageProvider">Builds the plugin-scoped storage for a plugin id; wired into the context
    /// only when the plugin declared <see cref="PluginCapabilities.Storage"/>.</param>
    /// <param name="connectionsProvider">Builds the origin-scoped managed-connections facade for a plugin id
    /// (over the live <c>ConnectionService</c>); wired in only when the plugin declared
    /// <see cref="PluginCapabilities.Connections"/>.</param>
    public SubsystemActivator(
        IReadOnlyList<SubsystemActivation> activations,
        Func<string, IPluginStorage> storageProvider,
        Func<string, IManagedConnections> connectionsProvider,
        Action<string>? log = null)
    {
        _activations = activations;
        _storageProvider = storageProvider;
        _connectionsProvider = connectionsProvider;
        _log = log;
    }

    /// <summary>Build each plugin's context and Initialize it; returns the registry of the ones that came up
    /// plus their capability-gated panel contributions (collected after Initialize, so a panel's control can
    /// rely on the plugin already holding its context).</summary>
    public SubsystemActivationResult ActivateAll()
    {
        var active = new List<ISubsystemPlugin>();
        var panels = new List<IPanelPlugin>();
        var menus = new List<IMenuPlugin>();
        var background = new List<IBackgroundPlugin>();
        foreach (var activation in _activations)
        {
            try
            {
                var context = SubsystemPluginLoader.CreateContext(
                    activation.Id, activation.Capabilities, _storageProvider,
                    activation.Localizer, _log, _connectionsProvider);
                activation.Plugin.Initialize(context);
                active.Add(activation.Plugin);

                // Pull-model contribution checks, capability-gated like the runtime services: only surface a
                // contribution the plugin both declared (consent) and actually implements.
                if (activation.Capabilities.Contains(PluginCapabilities.Panel)
                    && activation.Plugin is IPanelPlugin panel)
                {
                    panels.Add(panel);
                }

                if (activation.Capabilities.Contains(PluginCapabilities.Menu)
                    && activation.Plugin is IMenuPlugin menu)
                {
                    menus.Add(menu);
                }

                if (activation.Capabilities.Contains(PluginCapabilities.Background)
                    && activation.Plugin is IBackgroundPlugin bg)
                {
                    background.Add(bg);
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"{activation.Id} Initialize failed: {ex.Message}");
            }
        }

        return new SubsystemActivationResult(new SubsystemRegistry(active), panels, menus, background);
    }
}
