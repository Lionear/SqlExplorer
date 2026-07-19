using System;
using System.Collections.Generic;
using System.IO;
using SqlExplorer.Infrastructure.Extensibility;
using SqlExplorer.Sdk.Extensibility;

namespace SqlExplorer.Core.Tests.Extensibility;

public class JsonPluginStorageTests
{
    public sealed record Box(string Name, int Count);

    private static (IPluginStorage Store, string Root) New(string pluginId = "docker")
    {
        var root = Path.Combine(Path.GetTempPath(), "se164-" + Guid.NewGuid().ToString("N"));
        return (new JsonPluginStorage(pluginId, root), root);
    }

    [Fact]
    public void Round_trips_a_value()
    {
        var (store, root) = New();
        try
        {
            store.Save("containers", new List<Box> { new("a", 1), new("b", 2) });
            var loaded = store.Load<List<Box>>("containers");

            Assert.NotNull(loaded);
            Assert.Equal(2, loaded!.Count);
            Assert.Equal(new Box("b", 2), loaded[1]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Missing_key_returns_default()
    {
        var (store, root) = New();
        try
        {
            Assert.Null(store.Load<List<Box>>("nope"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Save_replaces_and_delete_removes()
    {
        var (store, root) = New();
        try
        {
            store.Save("k", new Box("first", 1));
            store.Save("k", new Box("second", 2));
            Assert.Equal(new Box("second", 2), store.Load<Box>("k"));

            store.Delete("k");
            Assert.Null(store.Load<Box>("k"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact] // Two plugins sharing a root never see each other's state.
    public void State_is_isolated_per_plugin()
    {
        var root = Path.Combine(Path.GetTempPath(), "se164-" + Guid.NewGuid().ToString("N"));
        try
        {
            var docker = new JsonPluginStorage("docker", root);
            var other = new JsonPluginStorage("other", root);

            docker.Save("k", new Box("docker", 1));
            Assert.Null(other.Load<Box>("k"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact] // A corrupt file degrades to default instead of throwing.
    public void Corrupt_file_degrades_to_default()
    {
        var (store, root) = New("docker");
        try
        {
            var file = Path.Combine(root, "docker", "k.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "{ not valid json ][");

            Assert.Null(store.Load<Box>("k"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
