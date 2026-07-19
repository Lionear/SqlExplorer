using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SqlExplorer.Core.Connections;
using SqlExplorer.Core.Plugins;
using SqlExplorer.Core.Providers;
using SqlExplorer.Infrastructure.Extensibility;

namespace SqlExplorer.Core.Tests.Extensibility;

/// <summary>
/// End-to-end proof of the SE-164 seams: the REAL <see cref="SubsystemPluginLoader"/> discovers and ALC-loads
/// the built <c>Backends.Docker</c> extension plugin, and the REAL <see cref="SubsystemActivator"/> — the same
/// post-build path the app uses — builds its capability-gated context and Initialize()s it against live host
/// services (plugin-scoped <see cref="JsonPluginStorage"/> and a <see cref="ConnectionService"/>). No host UI,
/// no mocks of the plugin. The plugin is a build dependency of this test project, so its output is present.
/// </summary>
public class DockerSubsystemIntegrationTests
{
    // The plugin builds to plugins/Backends.Docker/bin/<config>/net10.0; Discover scans a root's subfolders,
    // so we point it at bin/<config> (which holds the net10.0 folder with plugin.json).
    private static string PluginBinRoot()
    {
        var config = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            ? "Release"
            : "Debug";

        // BaseDirectory = <repo>/tests/SqlExplorer.Core.Tests/bin/<config>/net10.0 → up 5 to the repo root.
        var repo = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
        return Path.Combine(repo, "plugins", "Backends.Docker", "bin", config);
    }

    // A ConnectionService over in-memory fakes: no keychain, no file I/O. A MissingDbProvider stands in for
    // the container's engine (empty ConnectionFields, no Avalonia) — enough for ConnectionService.Save to run.
    private static (ConnectionService Service, FakeConnectionStore Store) NewConnectionService()
    {
        var providers = new DbProviderRegistry([new ProviderRegistration("test", new MissingDbProvider("test"))]);
        var store = new FakeConnectionStore();
        return (new ConnectionService(store, new NoopSecretStore(), providers), store);
    }

    private static SubsystemActivation LoadDockerActivation()
    {
        var discovered = PluginDiscovery.Discover(PluginBinRoot(), string.Empty);
        var result = new SubsystemPluginLoader().Load(discovered).SingleOrDefault(r => r.Id == "local-containers");

        Assert.NotNull(result);
        Assert.True(result!.Succeeded, result.Error);
        return result.Activation!;
    }

    // Seed the plugin's migrated container registry (containers.json) with one container — structural JSON
    // matching the plugin's ManagedContainer shape, which the test can't reference (build-only, ALC-loaded).
    // ConnectionId is null so the reconcile step links a connection and writes the link back.
    private static void SeedContainer(string storageRoot) =>
        new JsonPluginStorage("local-containers", storageRoot).Save("containers", new[]
        {
            new
            {
                Id = "pg-1", Name = "pg-1", ProviderId = "test", Image = "postgres", Tag = "16",
                HostPort = 5432, ComposeDir = "", ConnectionId = (string?)null, CreatedAtUtc = "2024-01-01T00:00:00Z"
            }
        });

    private static string? ReadFirstContainerConnectionId(string storageRoot)
    {
        var json = File.ReadAllText(Path.Combine(storageRoot, "local-containers", "containers.json"));
        using var doc = JsonDocument.Parse(json);
        var first = doc.RootElement[0];
        return first.TryGetProperty("ConnectionId", out var cid) && cid.ValueKind == JsonValueKind.String
            ? cid.GetString()
            : null;
    }

    [Fact] // Storage seam end-to-end: seed a container, activate, and the plugin reads it, links a connection,
           // and persists the ConnectionId back through plugin-scoped storage — proving both directions.
    public void Activator_round_trips_the_container_registry_through_storage()
    {
        var activation = LoadDockerActivation();
        var storageRoot = Path.Combine(Path.GetTempPath(), "se164-int-" + Guid.NewGuid().ToString("N"));
        var (service, _) = NewConnectionService();
        try
        {
            SeedContainer(storageRoot);

            var activator = new SubsystemActivator(
                [activation],
                id => new JsonPluginStorage(id, storageRoot),
                id => new ManagedConnections(id, service));

            var result = activator.ActivateAll();

            Assert.Single(result.Registry.All);
            Assert.False(
                string.IsNullOrEmpty(ReadFirstContainerConnectionId(storageRoot)),
                "the plugin did not persist the linked connection id back through storage");

            result.Registry.DeactivateAll();
        }
        finally
        {
            if (Directory.Exists(storageRoot))
            {
                Directory.Delete(storageRoot, recursive: true);
            }
        }
    }

    [Fact] // Seed the plugin's registry, activate for real, and prove it created an origin-tagged connection
           // through the live ConnectionService — the connections seam, wired end-to-end via the activator.
    public void Activator_wires_connections_so_the_plugin_creates_an_origin_tagged_connection()
    {
        var activation = LoadDockerActivation();
        var storageRoot = Path.Combine(Path.GetTempPath(), "se164-int-" + Guid.NewGuid().ToString("N"));
        var (service, store) = NewConnectionService();
        try
        {
            SeedContainer(storageRoot);

            var activator = new SubsystemActivator(
                [activation],
                id => new JsonPluginStorage(id, storageRoot),
                id => new ManagedConnections(id, service));

            activator.ActivateAll();

            var saved = store.GetAll().SingleOrDefault(c => c.Origin == "local-containers");
            Assert.NotNull(saved);
            Assert.Equal("pg-1", saved!.Name);
            Assert.Equal("test", saved.ProviderId);
            Assert.Equal("localhost", saved.Values["host"]);
            Assert.Equal("5432", saved.Values["port"]);
        }
        finally
        {
            if (Directory.Exists(storageRoot))
            {
                Directory.Delete(storageRoot, recursive: true);
            }
        }
    }

    [Fact] // The plugin declares the `panel` capability and implements IPanelPlugin, so the activator surfaces
           // it as a panel contribution. We assert the metadata only — building the control would pull
           // Avalonia.Controls into the test runtime (the known gotcha), which the host, not the test, owns.
    public void Activator_surfaces_the_docker_panel_contribution()
    {
        var activation = LoadDockerActivation();
        var storageRoot = Path.Combine(Path.GetTempPath(), "se164-int-" + Guid.NewGuid().ToString("N"));
        var (service, _) = NewConnectionService();
        try
        {
            var activator = new SubsystemActivator(
                [activation],
                id => new JsonPluginStorage(id, storageRoot),
                id => new ManagedConnections(id, service));

            var panel = activator.ActivateAll().Panels.SingleOrDefault();

            Assert.NotNull(panel);
            Assert.Equal("containers", panel!.PanelId);
            Assert.Equal("Containers", panel.Title);
        }
        finally
        {
            if (Directory.Exists(storageRoot))
            {
                Directory.Delete(storageRoot, recursive: true);
            }
        }
    }

    [Fact] // The plugin declares `menu` + implements IMenuPlugin, so the activator surfaces its Tools-menu
           // contribution. Metadata only — invoking it needs a live window, which the host, not the test, owns.
    public void Activator_surfaces_the_docker_menu_contribution()
    {
        var activation = LoadDockerActivation();
        var storageRoot = Path.Combine(Path.GetTempPath(), "se164-int-" + Guid.NewGuid().ToString("N"));
        var (service, _) = NewConnectionService();
        try
        {
            var activator = new SubsystemActivator(
                [activation],
                id => new JsonPluginStorage(id, storageRoot),
                id => new ManagedConnections(id, service));

            var menu = activator.ActivateAll().Menus.SingleOrDefault();

            Assert.NotNull(menu);
            var item = Assert.Single(menu!.MenuItems);
            Assert.Equal("new-container", item.Id);
            Assert.Equal("New Local Container…", item.Title);
        }
        finally
        {
            if (Directory.Exists(storageRoot))
            {
                Directory.Delete(storageRoot, recursive: true);
            }
        }
    }

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
}
