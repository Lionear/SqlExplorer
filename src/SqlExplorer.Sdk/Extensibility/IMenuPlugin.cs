namespace SqlExplorer.Sdk.Extensibility;

/// <summary>One item a plugin adds to the host's Tools menu: a stable <see cref="Id"/>, a localised
/// <see cref="Title"/>, and the action to run when it's clicked (handed an <see cref="IHostUi"/> so it can
/// open a dialog). The plugin already holds its <see cref="IPluginRuntimeContext"/> from Initialize for
/// everything else (storage, connections, its own services).</summary>
public sealed record MenuContribution(string Id, string Title, Func<IHostUi, Task> InvokeAsync);

/// <summary>
/// Optional contribution a standing-subsystem plugin (<see cref="ISubsystemPlugin"/>) may implement to add
/// items to the host's Tools menu. Gated by the <see cref="PluginCapabilities.Menu"/> capability — the host
/// only surfaces the items when the plugin declared it (and the user consented at install).
/// </summary>
public interface IMenuPlugin
{
    /// <summary>The Tools-menu items this plugin contributes (host namespaces their ids with the plugin id).</summary>
    IReadOnlyList<MenuContribution> MenuItems { get; }
}
