using SqlExplorer.Backends.Docker;
using SqlExplorer.Sdk.Extensibility;
using SqlExplorer.Sdk.Localization;
using SqlExplorer.Sdk.Provisioning;

namespace SqlExplorer.Backends.Docker.Tests;

/// <summary>
/// The plugin dogfoods the SE-171 <c>services</c> capability: it marks <see cref="DockerCli"/> as a
/// singleton and resolves <see cref="IDockerCli"/> through <see cref="IPluginRuntimeContext.Services"/>.
/// These cover the plugin side end-to-end; the host-side scan/guardrail/gating live in Core.Tests.
/// </summary>
public class ServiceDogfoodTests
{
    private sealed class FakeLocalizer : IPluginLocalizer
    {
        public string this[string key] => key;
        public bool Contains(string key) => false;
        public string Get(string key, params object[] args) => key;
    }

    private sealed class FakeProvider(Func<Type, object?> resolve) : IServiceProvider
    {
        public object? GetService(Type serviceType) => resolve(serviceType);
    }

    private sealed class FakeContext(IServiceProvider? services) : IPluginRuntimeContext
    {
        public string PluginId => "local-containers";
        public IPluginStorage? Storage => null;
        public IManagedConnections? Connections => null;
        public IServiceProvider? Services { get; } = services;
        public IProviderCatalog? Providers => null;
        public IPluginLocalizer Localizer { get; } = new FakeLocalizer();
        public void Log(string message) { }
    }

    [Fact] // The marker + own-assembly guardrail on the real plugin assembly: DockerCli registers as a
           // singleton under IDockerCli (both plugin-owned), never under a host contract.
    public void Plugin_marks_DockerCli_as_a_singleton_under_IDockerCli()
    {
        var registration = ServiceRegistrationScanner
            .Scan(typeof(DockerSubsystem).Assembly)
            .Single(r => r.ImplementationType == typeof(DockerCli))
            .WithOwnAssemblyServiceTypesOnly();

        Assert.Equal(ServiceScope.Singleton, registration.Scope);
        Assert.Contains(typeof(IDockerCli), registration.ServiceTypes);
    }

    [Fact]
    public void Resolves_the_host_provided_Docker_CLI_when_services_is_granted()
    {
        var hostCli = new DockerCli();
        var context = new FakeContext(new FakeProvider(t => t == typeof(IDockerCli) ? hostCli : null));

        Assert.Same(hostCli, DockerSubsystem.ResolveDockerCli(context));
    }

    [Fact]
    public void Falls_back_to_a_direct_instance_when_services_is_not_granted()
    {
        var context = new FakeContext(services: null);

        Assert.IsType<DockerCli>(DockerSubsystem.ResolveDockerCli(context));
    }
}
