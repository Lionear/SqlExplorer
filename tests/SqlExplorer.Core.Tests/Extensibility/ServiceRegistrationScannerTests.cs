using SqlExplorer.Sdk.Extensibility;

namespace SqlExplorer.Core.Tests.Extensibility;

public class ServiceRegistrationScannerTests
{
    private interface IThing;

    private interface IOther;

    private sealed class SingletonWithInterface : IThing, ISingletonService;

    private sealed class TransientNoInterface : ITransientService;

    private sealed class ScopedThing : IThing, IScopedService;

    private sealed class MultiInterface : IThing, IOther, ISingletonService;

    private abstract class AbstractMarked : ISingletonService;

    private sealed class Unmarked : IThing;

    private sealed class DoubleMarked : ISingletonService, ITransientService;

    private static ServiceRegistration One<T>(IReadOnlyList<ServiceRegistration> regs) =>
        regs.Single(r => r.ImplementationType == typeof(T));

    [Fact]
    public void Registers_under_each_non_marker_interface_plus_self_scope()
    {
        var reg = One<SingletonWithInterface>(ServiceRegistrationScanner.Scan([typeof(SingletonWithInterface)]));

        Assert.Equal(ServiceScope.Singleton, reg.Scope);
        Assert.Contains(typeof(IThing), reg.ServiceTypes);
        // The markers are never service types themselves.
        Assert.DoesNotContain(typeof(ISingletonService), reg.ServiceTypes);
    }

    [Fact]
    public void A_marker_only_class_has_no_service_interfaces()
    {
        var reg = One<TransientNoInterface>(ServiceRegistrationScanner.Scan([typeof(TransientNoInterface)]));

        Assert.Equal(ServiceScope.Transient, reg.Scope);
        Assert.Empty(reg.ServiceTypes);
    }

    [Fact]
    public void Maps_each_marker_to_its_scope()
    {
        var scoped = One<ScopedThing>(ServiceRegistrationScanner.Scan([typeof(ScopedThing)]));

        Assert.Equal(ServiceScope.Scoped, scoped.Scope);
    }

    [Fact]
    public void Registers_all_implemented_interfaces()
    {
        var reg = One<MultiInterface>(ServiceRegistrationScanner.Scan([typeof(MultiInterface)]));

        Assert.Contains(typeof(IThing), reg.ServiceTypes);
        Assert.Contains(typeof(IOther), reg.ServiceTypes);
    }

    [Fact]
    public void Skips_abstract_classes_interfaces_and_unmarked_types()
    {
        var regs = ServiceRegistrationScanner.Scan([typeof(AbstractMarked), typeof(IThing), typeof(Unmarked)]);

        Assert.Empty(regs);
    }

    [Fact]
    public void Throws_when_a_class_declares_more_than_one_marker()
    {
        Assert.Throws<InvalidOperationException>(() => ServiceRegistrationScanner.Scan([typeof(DoubleMarked)]));
    }
}
