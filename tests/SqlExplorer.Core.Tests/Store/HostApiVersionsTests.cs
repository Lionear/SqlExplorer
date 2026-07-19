using SqlExplorer.Core.Plugins;
using SqlExplorer.Core.Store;
using SqlExplorer.Sdk.Tools;

namespace SqlExplorer.Core.Tests.Store;

public class HostApiVersionsTests
{
    // Regression (SE-164): the "extension" and "mcp" plugin types were known to HostApiVersions.CompatFor
    // but had been forgotten by the installer's compat gate, so installing an extension failed with
    // "Unknown plugin type 'extension'". These lock in that every shipped type is recognised.
    [Theory]
    [InlineData(PluginManifest.Types.Provider)]
    [InlineData(PluginManifest.Types.Tool)]
    [InlineData(PluginManifest.Types.Mcp)]
    [InlineData(PluginManifest.Types.Extension)]
    public void IsKnown_true_for_shipped_types(string type) =>
        Assert.True(PluginManifest.Types.IsKnown(type));

    [Theory]
    [InlineData("nonsense")]
    [InlineData(null)]
    public void IsKnown_false_for_unknown_types(string? type) =>
        Assert.False(PluginManifest.Types.IsKnown(type));

    // Extensions are standing subsystems that load through the tool host-API contract (SubsystemPluginLoader),
    // so their acceptance window must equal the tool window — not the provider default.
    [Fact]
    public void Extension_uses_the_tool_host_api_window()
    {
        var extension = HostApiVersions.CompatFor(PluginManifest.Types.Extension);
        var tool = HostApiVersions.CompatFor(PluginManifest.Types.Tool);

        Assert.Equal(ToolHostApi.Version, extension.Current);
        Assert.Equal(ToolHostApi.MinimumSupported, extension.MinSupported);
        Assert.Equal(tool, extension);
    }
}
