namespace SqlExplorer.Sdk.Extensibility;

/// <summary>
/// Plugin-scoped persistent key/value storage the host owns, on <see cref="IPluginRuntimeContext.Storage"/>
/// when the plugin declared the <see cref="PluginCapabilities.Storage"/> capability. Values are
/// JSON-serialised; the host writes atomically and degrades to empty on a missing/corrupt file (same scheme
/// as its own stores). The store lives outside the plugin's install directory, so it survives updates, and
/// is removed when the plugin is uninstalled. Distinct from the host's own <c>IPluginStateStore</c> (which
/// tracks install state: enabled/disabled/pending) — a different concept, hence the different name.
/// </summary>
public interface IPluginStorage
{
    /// <summary>Deserialise the value stored under <paramref name="key"/>, or <c>default</c> when it is
    /// absent or can't be read.</summary>
    T? Load<T>(string key);

    /// <summary>Serialise and persist <paramref name="value"/> under <paramref name="key"/> (insert or replace).</summary>
    void Save<T>(string key, T value);

    /// <summary>Remove the value stored under <paramref name="key"/> (no-op if absent).</summary>
    void Delete(string key);
}
