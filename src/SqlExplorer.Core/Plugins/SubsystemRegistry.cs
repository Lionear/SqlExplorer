using SqlExplorer.Sdk.Extensibility;

namespace SqlExplorer.Core.Plugins;

/// <summary>Holds the subsystem plugins the host activated at startup, so they can be deactivated on
/// shutdown. <see cref="DeactivateAll"/> is best-effort — one plugin's failure never blocks the others.</summary>
public sealed class SubsystemRegistry(IReadOnlyList<ISubsystemPlugin> plugins)
{
    public IReadOnlyList<ISubsystemPlugin> All => plugins;

    public void DeactivateAll()
    {
        foreach (var plugin in plugins)
        {
            try
            {
                plugin.Deactivate();
            }
            catch (Exception)
            {
                // Best-effort teardown; a plugin that throws on Deactivate must not stop the rest.
            }
        }
    }
}
