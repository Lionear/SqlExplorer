using System.Collections.Generic;
using SqlExplorer.Backends.Docker;
using SqlExplorer.Sdk.Extensibility;

namespace SqlExplorer.Backends.Docker.Tests;

public class PluginStorageContainerRegistryTests
{
    // In-memory IPluginStorage — the registry's only dependency. JSON fidelity is JsonPluginStorage's concern
    // (and is covered by the ALC integration test in Core.Tests), so this keeps the value object as-is.
    private sealed class FakeStorage : IPluginStorage
    {
        private readonly Dictionary<string, object?> _data = new();
        public T? Load<T>(string key) => _data.TryGetValue(key, out var v) && v is T t ? t : default;
        public void Save<T>(string key, T value) => _data[key] = value;
        public void Delete(string key) => _data.Remove(key);
    }

    private static ManagedContainer Container(string id, string? connectionId = null) =>
        new(id, id, "postgres", "postgres", "16", 5432, "/tmp/" + id, connectionId, "2024-01-01T00:00:00Z");

    [Fact]
    public void Save_persists_so_a_fresh_registry_reads_it_back()
    {
        var storage = new FakeStorage();
        new PluginStorageContainerRegistry(storage).Save(Container("pg-1"));

        // A second instance over the same storage stands in for a restart.
        var reloaded = new PluginStorageContainerRegistry(storage);
        Assert.Single(reloaded.GetAll());
        Assert.Equal("pg-1", reloaded.Get("pg-1")!.Id);
    }

    [Fact]
    public void Save_replaces_by_id()
    {
        var registry = new PluginStorageContainerRegistry(new FakeStorage());
        registry.Save(Container("pg-1"));
        registry.Save(Container("pg-1", connectionId: "conn-42"));

        Assert.Single(registry.GetAll());
        Assert.Equal("conn-42", registry.Get("pg-1")!.ConnectionId);
    }

    [Fact]
    public void Remove_drops_the_entry_and_persists()
    {
        var storage = new FakeStorage();
        var registry = new PluginStorageContainerRegistry(storage);
        registry.Save(Container("pg-1"));
        registry.Save(Container("pg-2"));

        registry.Remove("pg-1");

        Assert.Null(registry.Get("pg-1"));
        Assert.Single(new PluginStorageContainerRegistry(storage).GetAll());
    }

    [Fact]
    public void Changed_fires_on_save_and_remove()
    {
        var registry = new PluginStorageContainerRegistry(new FakeStorage());
        var count = 0;
        registry.Changed += () => count++;

        registry.Save(Container("pg-1"));
        registry.Remove("pg-1");

        Assert.Equal(2, count);
    }

    [Fact]
    public void GetAll_is_empty_when_nothing_is_stored()
    {
        Assert.Empty(new PluginStorageContainerRegistry(new FakeStorage()).GetAll());
    }
}
