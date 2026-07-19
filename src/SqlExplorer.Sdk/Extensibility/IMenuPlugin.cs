using Avalonia.Controls;

namespace SqlExplorer.Sdk.Extensibility;

/// <summary>
/// Host services a menu action gets when it runs — notably the ability to show a modal dialog hosting a
/// plugin-built control. Kept off <see cref="IPluginRuntimeContext"/> (which lives in the host's Avalonia-free
/// Core) so only the App layer, which actually owns the window, provides it. The plugin supplies the control;
/// the host shows it modally over the main window and returns when it closes.
/// </summary>
public interface IMenuActionContext
{
    /// <summary>Show <paramref name="content"/> modally over the main window with the given window
    /// <paramref name="title"/>; completes when the dialog closes.</summary>
    Task ShowDialogAsync(string title, Control content);
}

/// <summary>One item a plugin adds to the host's Tools menu: a stable <see cref="Id"/>, a localised
/// <see cref="Title"/>, and the action to run when it's clicked (handed an <see cref="IMenuActionContext"/> so
/// it can open a dialog). The plugin already holds its <see cref="IPluginRuntimeContext"/> from Initialize for
/// everything else (storage, connections, its own services).</summary>
public sealed record MenuContribution(string Id, string Title, Func<IMenuActionContext, Task> InvokeAsync);

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
