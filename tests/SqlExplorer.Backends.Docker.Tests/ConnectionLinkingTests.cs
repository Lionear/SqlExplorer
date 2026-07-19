using System.Collections.Generic;
using System.Linq;
using SqlExplorer.Backends.Docker;
using SqlExplorer.Sdk.Extensibility;
using Xunit;

namespace SqlExplorer.Backends.Docker.Tests;

// The two connections-seam paths that only run behind UI callbacks (create dialog → link, panel Remove →
// release), extracted to testable helpers. Guards that a created container gets a fully-credentialled
// connection stamped back onto it (idempotently), and that removing it releases exactly that connection.
public class ConnectionLinkingTests
{
    private sealed class FakeStorage : IPluginStorage
    {
        private readonly Dictionary<string, object?> _data = new();
        public T? Load<T>(string key) => _data.TryGetValue(key, out var v) && v is T t ? t : default;
        public void Save<T>(string key, T value) => _data[key] = value;
        public void Delete(string key) => _data.Remove(key);
    }

    // Records what the plugin asked the host to create/remove; origin-scoping is ConnectionService's concern.
    private sealed class FakeManagedConnections : IManagedConnections
    {
        public readonly List<NewConnectionSpec> Created = new();
        public readonly List<string> Removed = new();
        private int _seq;

        public string Create(NewConnectionSpec spec)
        {
            Created.Add(spec);
            return $"conn-{++_seq}";
        }

        public void Remove(string connectionId) => Removed.Add(connectionId);
        public IReadOnlyList<string> Mine() => Created.Select((_, i) => $"conn-{i + 1}").ToList();
        public IReadOnlyList<ManagedConnectionInfo> All() => [];
    }

    private static ManagedContainer Container(string id, string? connectionId = null) =>
        new(id, id, "postgres", "postgres", "16", 5455, "/tmp/" + id, connectionId, "2024-01-01T00:00:00Z");

    private static CreateContainerRequest Request() =>
        new(
            ProviderId: "postgres",
            Values: new Dictionary<string, string?> { ["username"] = "admin", ["password"] = "s3cret" },
            ContainerName: "pg-local",
            HostPort: 5455,
            Tag: "16",
            Database: "shop");

    [Fact]
    public void LinkContainer_creates_a_credentialled_connection_and_stamps_the_id()
    {
        var registry = new PluginStorageContainerRegistry(new FakeStorage());
        registry.Save(Container("pg-local"));
        var connections = new FakeManagedConnections();

        DockerSubsystem.LinkContainer(connections, registry, Request());

        var spec = Assert.Single(connections.Created);
        Assert.Equal("Local Containers", spec.Folder);
        Assert.Equal("localhost", spec.Values["host"]);
        Assert.Equal("5455", spec.Values["port"]);
        Assert.Equal("admin", spec.Values["username"]);
        Assert.Equal("s3cret", spec.Values["password"]);
        Assert.Equal("shop", spec.Values["database"]);
        // The new connection id round-trips onto the container so a restart won't link it twice.
        Assert.Equal("conn-1", registry.Get("pg-local")!.ConnectionId);
    }

    [Fact]
    public void LinkContainer_is_idempotent_once_linked()
    {
        var registry = new PluginStorageContainerRegistry(new FakeStorage());
        registry.Save(Container("pg-local", connectionId: "conn-existing"));
        var connections = new FakeManagedConnections();

        DockerSubsystem.LinkContainer(connections, registry, Request());

        Assert.Empty(connections.Created);
        Assert.Equal("conn-existing", registry.Get("pg-local")!.ConnectionId);
    }

    [Fact]
    public void ReleaseContainerConnection_removes_the_linked_connection()
    {
        var connections = new FakeManagedConnections();

        DockerSubsystem.ReleaseContainerConnection(connections, Container("pg-local", connectionId: "conn-7"));

        Assert.Equal("conn-7", Assert.Single(connections.Removed));
    }

    [Fact]
    public void ReleaseContainerConnection_is_a_noop_for_an_unlinked_container()
    {
        var connections = new FakeManagedConnections();

        DockerSubsystem.ReleaseContainerConnection(connections, Container("pg-local", connectionId: null));

        Assert.Empty(connections.Removed);
    }
}
