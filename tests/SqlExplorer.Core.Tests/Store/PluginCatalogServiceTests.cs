using System.Collections.Generic;
using SqlExplorer.Core.Plugins;

namespace SqlExplorer.Core.Tests.Store;

public class PluginCatalogServiceTests
{
    private const string Dir = "/plugins/local-containers";

    // Regression (SE-164): the catalog was only fed provider + tool load outcomes, so a "type: extension"
    // plugin (loaded by SubsystemPluginLoader) had no outcome and read as loaded=false. With Enabled=true
    // that tripped the "enabled but not loaded" clause, pinning the "restart to apply" banner forever.
    [Fact]
    public void Extension_that_loaded_is_not_a_pending_change()
    {
        var catalog = Catalog(
            discovered: Extension(),
            outcomes: new PluginLoadOutcome(Dir, Succeeded: true, Error: null));

        Assert.False(catalog.HasPendingChanges);
        Assert.True(catalog.Installed[0].Loaded);
    }

    // The other half of the same fix: if we ever forget a loader's outcomes again the plugin looks
    // unloaded, and this is the symptom the user sees. Locks in that a missing outcome == pending.
    [Fact]
    public void Enabled_plugin_with_no_load_outcome_is_pending()
    {
        var catalog = Catalog(discovered: Extension() /* no outcomes */);

        Assert.True(catalog.HasPendingChanges);
    }

    [Fact]
    public void Failed_load_is_not_pending_it_is_an_error()
    {
        var catalog = Catalog(
            discovered: Extension(),
            outcomes: new PluginLoadOutcome(Dir, Succeeded: false, Error: "boom"));

        Assert.False(catalog.HasPendingChanges);
        Assert.Equal("boom", catalog.Installed[0].LoadError);
    }

    [Fact]
    public void Pending_install_marker_is_a_pending_change()
    {
        var store = new FakeStateStore();
        store.Save("local-containers", new PluginStateEntry { Enabled = true, Pending = PluginPendingAction.Install });

        var catalog = new PluginCatalogService(store, [Extension()],
            [new PluginLoadOutcome(Dir, Succeeded: true, Error: null)]);

        Assert.True(catalog.HasPendingChanges);
    }

    private static DiscoveredPlugin Extension() => new(
        Dir,
        PluginOrigin.UserInstalled,
        new PluginManifest
        {
            Id = "local-containers",
            Type = PluginManifest.Types.Extension,
            Name = "Local Containers",
            HostApiVersion = 1,
            EntryAssembly = "LocalContainers.dll"
        },
        ManifestError: null);

    private static PluginCatalogService Catalog(DiscoveredPlugin discovered, params PluginLoadOutcome[] outcomes) =>
        new(new FakeStateStore(), [discovered], outcomes);

    private sealed class FakeStateStore : IPluginStateStore
    {
        private readonly Dictionary<string, PluginStateEntry> _entries = new();

        public IReadOnlyDictionary<string, PluginStateEntry> GetAll() => _entries;
        public PluginStateEntry Get(string id) => _entries.TryGetValue(id, out var e) ? e : new PluginStateEntry();
        public void Save(string id, PluginStateEntry entry) => _entries[id] = entry;
        public void Remove(string id) => _entries.Remove(id);
    }
}
