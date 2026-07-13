namespace Lionear.SqlExplorer.Sdk.Settings;

/// <summary>How a <see cref="PluginSettingField"/> is captured — the host renders one control per type,
/// mirroring the connection dialog's field rendering.</summary>
public enum PluginSettingFieldType
{
    Text,
    Bool,

    /// <summary>A closed set of options rendered as a dropdown; the picked value is stored verbatim.
    /// Populate <see cref="PluginSettingField.Choices"/> with the allowed values.</summary>
    Choice,

    /// <summary>A filesystem path with a Browse… button (e.g. a path to an external binary like
    /// <c>mysqldump</c>) — the exact use case the plugin-settings pane was designed for.</summary>
    File
}

/// <summary>
/// One field in a plugin's settings pane (Route A). A plugin declares these via
/// <see cref="IPluginSettings"/>; the host renders a generic form from them — the same declarative,
/// ALC-boundary-safe shape as <c>ConnectionField</c>, but for plugin-wide, persistent settings
/// (set once, applied to every use of the plugin) rather than per-connection values.
/// </summary>
/// <param name="Group">Optional section label; fields sharing a group render together under a header.
/// Null groups the field with the other ungrouped fields. Lets one pane carry multiple sections without
/// needing multiple top-level panes.</param>
/// <param name="Choices">Allowed values for a <see cref="PluginSettingFieldType.Choice"/> field; ignored
/// for other types.</param>
public sealed record PluginSettingField(
    string Key,
    string Label,
    PluginSettingFieldType Type = PluginSettingFieldType.Text,
    string? Default = null,
    string? Placeholder = null,
    string? Group = null,
    IReadOnlyList<string>? Choices = null);
