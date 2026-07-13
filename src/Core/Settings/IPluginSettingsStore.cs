namespace Lionear.SqlExplorer.Core.Settings;

/// <summary>
/// Persists plugin-declared settings values, keyed by plugin id then setting key. Kept separate from
/// <see cref="IAppSettingsStore"/> (window geometry, theme…) so the two never clobber each other's saves.
/// Values are plain strings, exactly as the plugin's declared fields (or custom UI) produce them.
/// </summary>
public interface IPluginSettingsStore
{
    /// <summary>Stored values for one plugin (empty when the plugin has never been configured).</summary>
    IReadOnlyDictionary<string, string?> Get(string pluginId);

    /// <summary>Replace the stored values for one plugin.</summary>
    void Save(string pluginId, IReadOnlyDictionary<string, string?> values);
}
