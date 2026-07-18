using System;
using System.IO;
using System.Linq;
using SqlExplorer.Core.Plugins;
using SqlExplorer.Infrastructure.Extensibility;

namespace SqlExplorer.Core.Tests.Extensibility;

/// <summary>
/// End-to-end proof of the SE-164 <c>storage</c> seam: the REAL <see cref="SubsystemPluginLoader"/> discovers
/// and ALC-loads the built <c>Backends.Docker</c> extension plugin, hands it a capability-gated context, and
/// its <c>Initialize</c> round-trips through plugin-scoped <see cref="JsonPluginStorage"/> — no host, no
/// mocks. The plugin is a build dependency of this test project, so its output is present.
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

    [Fact]
    public void Real_loader_activates_the_docker_extension_and_round_trips_storage()
    {
        var binRoot = PluginBinRoot();
        Assert.True(Directory.Exists(binRoot), $"plugin build output not found: {binRoot}");

        var discovered = PluginDiscovery.Discover(binRoot, string.Empty);
        var storageRoot = Path.Combine(Path.GetTempPath(), "se164-int-" + Guid.NewGuid().ToString("N"));
        try
        {
            var loader = new SubsystemPluginLoader(id => new JsonPluginStorage(id, storageRoot));
            var result = loader.Load(discovered).SingleOrDefault(r => r.Id == "local-containers");

            Assert.NotNull(result);
            Assert.True(result!.Succeeded, result.Error);

            // Activate for real: Initialize dogfoods the storage seam by round-tripping its registry.
            result.Plugin!.Initialize(result.Context!);

            Assert.True(
                File.Exists(Path.Combine(storageRoot, "local-containers", "containers.json")),
                "the plugin did not persist through the capability-gated storage");

            result.Plugin.Deactivate();
        }
        finally
        {
            if (Directory.Exists(storageRoot))
            {
                Directory.Delete(storageRoot, recursive: true);
            }
        }
    }
}
