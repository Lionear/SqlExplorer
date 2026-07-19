namespace SqlExplorer.Sdk.Extensibility;

/// <summary>
/// A plugin that is a <em>standing subsystem</em> rather than a one-shot task. Unlike <c>IToolPlugin</c>
/// (invoked once and forgotten), it receives an <see cref="IPluginRuntimeContext"/> once at startup —
/// restart-gated activation, mirroring how installs/enables apply — through which it reaches its
/// capability-gated host services and (in later phases) registers panel / background / menu contributions.
/// This is the base seam of the extensibility family; the panel/background/menu interfaces are separate and
/// the same class may implement several. Discovery mirrors the other loaders (scan the entry assembly for
/// implementors); a plugin activates only if it declared the matching capabilities and the user consented.
/// </summary>
public interface ISubsystemPlugin
{
    /// <summary>Called once after the plugin loads (and again on enable, after the applying restart). The
    /// <paramref name="context"/> stays valid for the app's lifetime — hold onto it.</summary>
    void Initialize(IPluginRuntimeContext context);

    /// <summary>Release whatever <see cref="Initialize"/> acquired (stop loops, dispose handles). Called at
    /// shutdown / on disable. Best-effort — must not throw.</summary>
    void Deactivate() { }
}
