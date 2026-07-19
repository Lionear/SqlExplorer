using Avalonia.Controls;

namespace SqlExplorer.Sdk.Extensibility;

/// <summary>
/// Optional contribution a standing-subsystem plugin (<see cref="ISubsystemPlugin"/>) may also implement to
/// add a docked, long-lived panel — a third tool-window beside the host's Output and History. Gated by the
/// <see cref="PluginCapabilities.Panel"/> capability: the host only surfaces the panel when the plugin
/// declared it (and the user consented at install). The plugin owns an Avalonia <see cref="Control"/> that
/// reads its own live data (through the <see cref="IPluginRuntimeContext"/> it captured at
/// <see cref="ISubsystemPlugin.Initialize"/>); the host renders it in the panel region with a toggle,
/// header and resize handle, the same chrome Output/History get.
/// </summary>
/// <remarks>
/// Same ALC/type-identity contract as the other UI seams (<see cref="Ui.ICustomNodeInfoUi"/> et al.): this
/// assembly and Avalonia are shared across the plugin's <c>ProviderLoadContext</c>, so the returned control
/// has a single type identity with the host. v1 panels dock at the bottom; an explicit edge is added when
/// the host generalises the right-hand region too. Additive optional-interface check — no host API bump.
/// </remarks>
public interface IPanelPlugin
{
    /// <summary>Stable id for this panel (used to key its toggle/visibility). Namespaced by the host with
    /// the plugin id, so two plugins can reuse the same local id without clashing.</summary>
    string PanelId { get; }

    /// <summary>Localised title shown on the panel's toggle and header (localise via the plugin's own
    /// <see cref="IPluginRuntimeContext.Localizer"/> before returning it).</summary>
    string Title { get; }

    /// <summary>Build the panel's content control. Called once by the host after the plugin's
    /// <see cref="ISubsystemPlugin.Initialize"/> has run, so the plugin already holds its runtime context.
    /// <paramref name="hostUi"/> lets the panel open modal dialogs (e.g. a container's logs).</summary>
    Control CreatePanel(IHostUi hostUi);
}
