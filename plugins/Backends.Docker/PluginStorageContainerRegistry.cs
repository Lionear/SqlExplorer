namespace SqlExplorer.Backends.Docker;

/// <summary>
/// <see cref="IContainerRegistryStore"/> backed by the host's plugin-scoped storage seam
/// (<see cref="IPluginStorage"/>) — the replacement for the old host-owned JSON store now that Docker is a
/// plugin. Keeps an in-memory cache guarded by a lock and rewrites the whole list on every change (the set is
/// tiny); reads degrade to empty when nothing is stored yet. <see cref="Changed"/> drives the Containers panel.
/// </summary>
public sealed class PluginStorageContainerRegistry : IContainerRegistryStore
{
    private const string Key = "containers";

    private readonly IPluginStorage _storage;
    private readonly object _gate = new();
    private List<ManagedContainer> _cache;

    public event Action? Changed;

    public PluginStorageContainerRegistry(IPluginStorage storage)
    {
        _storage = storage;
        _cache = storage.Load<List<ManagedContainer>>(Key) ?? [];
    }

    public IReadOnlyList<ManagedContainer> GetAll()
    {
        lock (_gate)
        {
            return _cache.ToList();
        }
    }

    public ManagedContainer? Get(string id)
    {
        lock (_gate)
        {
            return _cache.FirstOrDefault(c => c.Id == id);
        }
    }

    public void Save(ManagedContainer container)
    {
        lock (_gate)
        {
            _cache = _cache.Where(c => c.Id != container.Id).Append(container).ToList();
            _storage.Save(Key, _cache);
        }

        Changed?.Invoke();
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            _cache = _cache.Where(c => c.Id != id).ToList();
            _storage.Save(Key, _cache);
        }

        Changed?.Invoke();
    }
}
