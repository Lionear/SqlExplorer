using Avalonia.Controls;

namespace Lionear.SqlExplorer.Sdk.Ui;

/// <summary>
/// Optional capability a plugin may implement to supply its own Avalonia view for its settings pane
/// (Route B), instead of the host-generated form built from declared <c>PluginSettingField</c>s. Useful
/// when settings are interdependent (e.g. an auth mode that shows/hides other fields). Mirrors
/// <see cref="ICustomConnectionUi"/>.
/// </summary>
/// <remarks>
/// Values still flow through <see cref="IPluginSettingsContext"/>, so the host persists them the same way
/// regardless of route. This assembly and Avalonia are shared across the plugin ALC boundary, so the
/// returned control has a single type identity with the host. A plugin may implement this, the
/// declarative <c>IPluginSettings</c>, or both (the host prefers the custom view when present).
/// </remarks>
public interface ICustomPluginSettingsUi
{
    /// <summary>Build the settings-pane view. Read and write values through <paramref name="context"/>.</summary>
    Control CreateSettingsView(IPluginSettingsContext context);
}
