namespace SqlExplorer.Backends.Docker;

/// <summary>
/// Persistent registry of the containers this plugin manages, surviving restarts. A <see cref="Changed"/>
/// event drives the Containers panel; the concrete impl (<see cref="PluginStorageContainerRegistry"/>) persists
/// through the host's plugin-scoped storage seam and degrades to empty on a missing/corrupt file.
/// </summary>
public interface IContainerRegistryStore
{
    /// <summary>Raised after any add/remove so the UI can refresh the container list.</summary>
    event Action? Changed;

    IReadOnlyList<ManagedContainer> GetAll();

    ManagedContainer? Get(string id);

    /// <summary>Insert or replace (keyed by <see cref="ManagedContainer.Id"/>).</summary>
    void Save(ManagedContainer container);

    void Remove(string id);
}
