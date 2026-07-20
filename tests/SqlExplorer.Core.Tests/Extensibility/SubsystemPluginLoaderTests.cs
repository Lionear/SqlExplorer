using SqlExplorer.Core.Plugins;
using SqlExplorer.Sdk.Extensibility;

namespace SqlExplorer.Core.Tests.Extensibility;

public class SubsystemPluginLoaderTests
{
    private sealed class FakeStorage : IPluginStorage
    {
        public T? Load<T>(string key) => default;
        public void Save<T>(string key, T value) { }
        public void Delete(string key) { }
    }

    [Fact] // No "storage" capability → context.Storage is null, so a plugin can't use a power it didn't declare.
    public void Storage_is_gated_off_without_the_capability()
    {
        var ctx = SubsystemPluginLoader.CreateContext("docker", [], _ => new FakeStorage(), localizer: null, log: null);

        Assert.Null(ctx.Storage);
        Assert.Equal("docker", ctx.PluginId);
    }

    [Fact]
    public void Storage_is_present_with_the_capability()
    {
        var ctx = SubsystemPluginLoader.CreateContext(
            "docker", [PluginCapabilities.Storage], _ => new FakeStorage(), localizer: null, log: null);

        Assert.NotNull(ctx.Storage);
    }

    [Fact] // A plugin without translations still gets a non-null localizer (returns the key).
    public void Localizer_falls_back_to_a_no_op_when_none_supplied()
    {
        var ctx = SubsystemPluginLoader.CreateContext("x", [], _ => new FakeStorage(), localizer: null, log: null);

        Assert.Equal("some.key", ctx.Localizer["some.key"]);
        Assert.False(ctx.Localizer.Contains("some.key"));
    }

    private sealed class FakeServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    [Fact] // No "services" capability → context.Services is null even when a resolver was supplied.
    public void Services_is_gated_off_without_the_capability()
    {
        var ctx = SubsystemPluginLoader.CreateContext(
            "x", [], _ => new FakeStorage(), localizer: null, log: null, services: new FakeServices());

        Assert.Null(ctx.Services);
    }

    [Fact]
    public void Services_is_present_with_the_capability()
    {
        var resolver = new FakeServices();
        var ctx = SubsystemPluginLoader.CreateContext(
            "x", [PluginCapabilities.Services], _ => new FakeStorage(), localizer: null, log: null, services: resolver);

        Assert.Same(resolver, ctx.Services);
    }
}
