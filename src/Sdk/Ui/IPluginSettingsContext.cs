namespace Lionear.SqlExplorer.Sdk.Ui;

/// <summary>
/// Read/write access to a plugin's stored settings values, handed to a plugin's custom settings view
/// (<see cref="ICustomPluginSettingsUi"/>). Mirrors <see cref="IConnectionUiContext"/>: the view reads
/// and writes values by key so the host persists exactly what the plugin's own UI collects.
/// </summary>
public interface IPluginSettingsContext
{
    /// <summary>Current value for a setting key, or null if unset.</summary>
    string? GetValue(string key);

    /// <summary>Write a setting value; the host persists it under this plugin's id on Apply.</summary>
    void SetValue(string key, string? value);
}
