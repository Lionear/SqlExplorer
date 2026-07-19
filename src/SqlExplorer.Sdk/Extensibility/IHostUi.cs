using Avalonia.Controls;

namespace SqlExplorer.Sdk.Extensibility;

/// <summary>
/// Host UI services a plugin's contribution controls get — currently showing a plugin-built control modally.
/// Deliberately lives in the SDK (Avalonia-aware), not on <see cref="IPluginRuntimeContext"/> which is in the
/// host's Avalonia-free Core: only the App layer, which owns the window, provides it. Handed to a panel when
/// it's built (<see cref="IPanelPlugin.CreatePanel"/>) and to a menu action when it runs
/// (<see cref="MenuContribution.InvokeAsync"/>), so both can open dialogs.
/// </summary>
public interface IHostUi
{
    /// <summary>Show <paramref name="content"/> modally over the main window with the given window
    /// <paramref name="title"/>; completes when the dialog closes.</summary>
    Task ShowDialogAsync(string title, Control content);

    /// <summary>Ask the user a yes/no question in a modal dialog over the main window. Returns true when
    /// they confirm, false when they decline or dismiss it. Use before a destructive plugin action.</summary>
    Task<bool> ConfirmAsync(string title, string message);
}
