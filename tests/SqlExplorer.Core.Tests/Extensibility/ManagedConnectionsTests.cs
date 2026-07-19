using System.Collections.Generic;
using System.Linq;
using SqlExplorer.Core.Connections;
using SqlExplorer.Core.Providers;
using SqlExplorer.Sdk.Extensibility;

namespace SqlExplorer.Core.Tests.Extensibility;

// ManagedConnections is a thin, origin-stamping wrapper over ConnectionService (which already owns the
// secret→keychain split). These tests target its own behaviour: tagging origin and scoping Mine/Remove to
// this plugin's own connections. A MissingDbProvider stands in for the provider (empty ConnectionFields, no
// Avalonia) — the secret-routing itself is ConnectionService's concern, tested elsewhere.
public class ManagedConnectionsTests
{
    private sealed class FakeConnectionStore : IConnectionStore
    {
        private readonly List<SavedConnection> _items = new();
        public IReadOnlyList<SavedConnection> GetAll() => _items.ToList();
        public IReadOnlyDictionary<string, int> GetFolderOrder() => new Dictionary<string, int>();
        public void Save(SavedConnection c) { _items.RemoveAll(x => x.Id == c.Id); _items.Add(c); }
        public void Delete(string id) => _items.RemoveAll(x => x.Id == id);
        public void SaveAll(IReadOnlyList<SavedConnection> connections, IReadOnlyDictionary<string, int> folderOrder)
        { _items.Clear(); _items.AddRange(connections); }
    }

    private sealed class NoopSecretStore : ISecretStore
    {
        public void Set(string key, string secret) { }
        public string? Get(string key) => null;
        public void Delete(string key) { }
    }

    private static (ManagedConnections Managed, FakeConnectionStore Store) New(string origin = "local-containers")
    {
        var providers = new DbProviderRegistry([new ProviderRegistration("test", new MissingDbProvider("test"))]);
        var store = new FakeConnectionStore();
        var svc = new ConnectionService(store, new NoopSecretStore(), providers);
        return (new ManagedConnections(origin, svc), store);
    }

    private static NewConnectionSpec Spec() => new(
        "PG local", "test",
        new Dictionary<string, string?> { ["host"] = "localhost", ["port"] = "5432" },
        Folder: "Local containers");

    [Fact]
    public void Create_tags_the_plugin_origin_and_persists_the_connection()
    {
        var (managed, store) = New();

        var id = managed.Create(Spec());

        var saved = store.GetAll().Single();
        Assert.Equal(id, saved.Id);
        Assert.Equal("local-containers", saved.Origin);
        Assert.Equal("Local containers", saved.Folder);
        Assert.Equal("localhost", saved.Values["host"]);
    }

    [Fact]
    public void Mine_lists_only_this_plugins_connections()
    {
        var (managed, store) = New();
        managed.Create(Spec());
        store.Save(new SavedConnection { Id = "user1", Name = "u", ProviderId = "test", Values = new Dictionary<string, string?>() });
        store.Save(new SavedConnection { Id = "other1", Name = "o", ProviderId = "test", Origin = "some-other-plugin", Values = new Dictionary<string, string?>() });

        var mine = managed.Mine();

        Assert.Single(mine);
        Assert.DoesNotContain("user1", mine);
        Assert.DoesNotContain("other1", mine);
    }

    [Fact]
    public void All_reads_every_connection_with_non_secret_values()
    {
        var (managed, store) = New();
        managed.Create(Spec());
        store.Save(new SavedConnection
        {
            Id = "user1", Name = "Prod", ProviderId = "test", Folder = "Production",
            Values = new Dictionary<string, string?> { ["host"] = "db.example.com", ["port"] = "5432" }
        });

        var all = managed.All();

        Assert.Equal(2, all.Count);
        var prod = all.Single(c => c.Id == "user1");
        Assert.Equal("Prod", prod.Name);
        Assert.Equal("Production", prod.Folder);
        Assert.Equal("db.example.com", prod.Values["host"]);
        // The plugin's own connection is included too (All is not origin-scoped, unlike Mine).
        Assert.Contains(all, c => c.Name == "PG local");
    }

    [Fact]
    public void Remove_only_touches_this_plugins_own_connections()
    {
        var (managed, store) = New();
        var mine = managed.Create(Spec());
        store.Save(new SavedConnection { Id = "user1", Name = "u", ProviderId = "test", Values = new Dictionary<string, string?>() });

        managed.Remove("user1");                       // not ours → no-op
        Assert.NotNull(store.GetAll().SingleOrDefault(c => c.Id == "user1"));

        managed.Remove(mine);                           // ours → gone
        Assert.Null(store.GetAll().SingleOrDefault(c => c.Id == mine));
    }
}
