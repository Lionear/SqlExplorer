using SqlExplorer.Sdk.Extensibility;
using SqlExplorer.Sdk.Localization;

namespace SqlExplorer.Core.Plugins;

/// <summary>
/// The host's <see cref="IPluginRuntimeContext"/> handed to a subsystem plugin. Already capability-gated by
/// the loader: a service (e.g. <see cref="Storage"/>) is null unless the plugin declared — and the user
/// consented to — its capability. <see cref="Log"/> routes to a host-provided sink.
/// </summary>
public sealed class PluginRuntimeContext : IPluginRuntimeContext
{
    private readonly Action<string>? _log;

    public PluginRuntimeContext(
        string pluginId, IPluginStorage? storage, IManagedConnections? connections, IPluginLocalizer localizer, Action<string>? log)
    {
        PluginId = pluginId;
        Storage = storage;
        Connections = connections;
        Localizer = localizer;
        _log = log;
    }

    public string PluginId { get; }

    public IPluginStorage? Storage { get; }

    public IManagedConnections? Connections { get; }

    public IPluginLocalizer Localizer { get; }

    public void Log(string message) => _log?.Invoke(message);
}
