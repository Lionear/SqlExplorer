using SqlExplorer.Sdk.Localization;

namespace SqlExplorer.Core.Plugins;

/// <summary>A no-op <see cref="IPluginLocalizer"/> (every lookup returns the key) used when a plugin ships
/// no translations — so <see cref="Sdk.Extensibility.IPluginRuntimeContext.Localizer"/> is always non-null.</summary>
public sealed class NullPluginLocalizer : IPluginLocalizer
{
    public static readonly NullPluginLocalizer Instance = new();

    public string this[string key] => key;

    public bool Contains(string key) => false;

    public string Get(string key, params object[] args) => key;
}
