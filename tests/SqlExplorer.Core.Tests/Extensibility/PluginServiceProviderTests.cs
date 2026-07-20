using SqlExplorer.Core.Plugins;

namespace SqlExplorer.Core.Tests.Extensibility;

public class PluginServiceProviderTests
{
    // Inner would happily resolve anything; the scoped provider must still gate on the allow-list.
    private sealed class AlwaysResolves : IServiceProvider
    {
        public object GetService(Type serviceType) => $"resolved:{serviceType.Name}";
    }

    [Fact]
    public void Resolves_allowed_types_through_the_inner_provider()
    {
        var sp = new PluginServiceProvider(new AlwaysResolves(), new HashSet<Type> { typeof(string) });

        Assert.Equal("resolved:String", sp.GetService(typeof(string)));
    }

    [Fact]
    public void Returns_null_for_types_outside_the_allow_list()
    {
        var sp = new PluginServiceProvider(new AlwaysResolves(), new HashSet<Type> { typeof(string) });

        Assert.Null(sp.GetService(typeof(int)));
    }
}
