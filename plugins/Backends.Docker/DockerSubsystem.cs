namespace SqlExplorer.Backends.Docker;

/// <summary>
/// The Local Containers (Docker) subsystem plugin — the first consumer of the SE-164 extensibility platform,
/// and the target the host-built Docker regie (SE-113 fase-1 Core) migrates into. This first slice dogfoods
/// only the <c>storage</c> seam: it reads its persisted container registry through the capability-gated
/// <see cref="IPluginRuntimeContext.Storage"/>, proving the plugin can load, receive its context, and use a
/// host service. The panel / background / connections seams (and the migrated compose/CLI logic) land next.
/// </summary>
public sealed class DockerSubsystem : ISubsystemPlugin
{
    private const string RegistryKey = "containers";

    private IPluginRuntimeContext? _context;

    public void Initialize(IPluginRuntimeContext context)
    {
        _context = context;

        if (context.Storage is not { } storage)
        {
            context.Log("Local Containers: the 'storage' capability was not granted — the container registry is unavailable.");
            return;
        }

        // Read-then-write round-trip against plugin-scoped storage: proves both directions of the seam work
        // and that the registry persists across restarts. (The real registry payload arrives with the migration.)
        var containers = storage.Load<List<ManagedContainerRecord>>(RegistryKey) ?? [];
        storage.Save(RegistryKey, containers);
        context.Log($"Local Containers: {containers.Count} managed container(s) restored from storage.");
    }

    public void Deactivate() => _context = null;

    /// <summary>Placeholder registry row — replaced by the migrated SE-113 <c>ManagedContainer</c>.</summary>
    private sealed record ManagedContainerRecord(string Name, string ProviderId, int HostPort);
}
