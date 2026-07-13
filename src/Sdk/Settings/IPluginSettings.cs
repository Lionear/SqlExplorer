namespace Lionear.SqlExplorer.Sdk.Settings;

/// <summary>
/// Optional capability a plugin may implement to declare persistent, plugin-wide settings (Route A):
/// values the user sets once and that apply to every use of the plugin (e.g. a tool's path to an
/// external binary). The host renders a generic form from <see cref="SettingsFields"/> and persists the
/// values by plugin id. A plugin implementing neither this nor <c>ICustomPluginSettingsUi</c> gets no
/// entry in the Settings ▸ Plugins tree.
/// </summary>
/// <remarks>
/// Purely declarative, so it crosses the plugin ALC boundary cleanly. Applies to any plugin type: today
/// only providers load, so a provider may implement this; the identical contract serves future tool
/// plugins unchanged. If a plugin ever needs several distinct top-level panes, that is an additive
/// future interface — this stays the single-pane common case (use <c>PluginSettingField.Group</c> to
/// section a single pane).
/// </remarks>
public interface IPluginSettings
{
    /// <summary>The settings fields this plugin exposes; an empty list means "no settings" (no tree entry).</summary>
    IReadOnlyList<PluginSettingField> SettingsFields { get; }
}
