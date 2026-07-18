using SqlExplorer.Sdk.Localization;

namespace SqlExplorer.Sdk.Extensibility;

/// <summary>
/// A standing plugin's window onto the host: the capability-gated services it may use while the app runs,
/// plus its localisation and an Output-log hook. Handed to <see cref="ISubsystemPlugin.Initialize"/> (and,
/// in later phases, to panel/background contributions) and stays valid for the app's lifetime. A service
/// property is <c>null</c> when the plugin did not declare — and the user did not consent to — the matching
/// capability, so a plugin can never silently use a power it wasn't granted.
/// </summary>
public interface IPluginRuntimeContext
{
    /// <summary>The plugin's id (its <c>plugin.json</c> id) — for log channels and labels.</summary>
    string PluginId { get; }

    /// <summary>Plugin-scoped persistent storage; <c>null</c> without the
    /// <see cref="PluginCapabilities.Storage"/> capability.</summary>
    IPluginStorage? Storage { get; }

    /// <summary>Localisation backed by the plugin's embedded <c>Lang/strings*.json</c> (as tools get).</summary>
    IPluginLocalizer Localizer { get; }

    /// <summary>Write a line to the host's Output panel under this plugin's channel.</summary>
    void Log(string message);
}
