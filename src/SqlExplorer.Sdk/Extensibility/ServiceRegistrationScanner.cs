using System.Reflection;

namespace SqlExplorer.Sdk.Extensibility;

/// <summary>
/// One service the host should register: the concrete <see cref="ImplementationType"/>, the
/// <see cref="ServiceTypes"/> it should be resolvable as (its non-marker interfaces — possibly empty), and
/// the <see cref="Scope"/> its marker asks for. Container-neutral by design; the host adapter turns this into
/// its own registrations.
/// </summary>
public sealed record ServiceRegistration(
    Type ImplementationType,
    IReadOnlyList<Type> ServiceTypes,
    ServiceScope Scope);

/// <summary>
/// Reflects over assemblies for concrete classes that implement exactly one lifetime marker
/// (<see cref="ISingletonService"/>/<see cref="ITransientService"/>/<see cref="IScopedService"/>) and yields
/// a <see cref="ServiceRegistration"/> for each. Pure and DI-free so it can be unit-tested and reused for the
/// (later) plugin-side scan; the host owns the actual <c>IServiceCollection</c> wiring.
/// </summary>
public static class ServiceRegistrationScanner
{
    private static readonly (Type Marker, ServiceScope Scope)[] Markers =
    [
        (typeof(ISingletonService), ServiceScope.Singleton),
        (typeof(ITransientService), ServiceScope.Transient),
        (typeof(IScopedService), ServiceScope.Scoped),
    ];

    /// <inheritdoc cref="Scan(IEnumerable{Type})"/>
    public static IReadOnlyList<ServiceRegistration> Scan(params Assembly[] assemblies) =>
        Scan(assemblies.SelectMany(a => a.GetTypes()));

    /// <inheritdoc cref="Scan(IEnumerable{Type})"/>
    public static IReadOnlyList<ServiceRegistration> Scan(IEnumerable<Assembly> assemblies) =>
        Scan(assemblies.SelectMany(a => a.GetTypes()));

    /// <summary>
    /// Scans the given types. Skips interfaces, abstract classes and open generics; ignores the marker
    /// interfaces themselves as service types. Throws <see cref="InvalidOperationException"/> when a class
    /// implements more than one marker — that is a developer mistake worth failing fast on at startup.
    /// </summary>
    public static IReadOnlyList<ServiceRegistration> Scan(IEnumerable<Type> types)
    {
        var registrations = new List<ServiceRegistration>();

        foreach (var type in types)
        {
            if (type is not { IsClass: true, IsAbstract: false } || type.IsGenericTypeDefinition)
                continue;

            var scope = ScopeOf(type);
            if (scope is null)
                continue;

            registrations.Add(new ServiceRegistration(type, ServiceTypesFor(type), scope.Value));
        }

        return registrations;
    }

    // The single marker a type opts into, or null when it declares none. More than one is rejected so an
    // ambiguous lifetime can never silently resolve to whichever marker happens to be checked first.
    private static ServiceScope? ScopeOf(Type type)
    {
        ServiceScope? found = null;

        foreach (var (marker, scope) in Markers)
        {
            if (!marker.IsAssignableFrom(type))
                continue;

            if (found is not null)
                throw new InvalidOperationException(
                    $"{type.FullName} implements more than one service-lifetime marker; declare exactly one.");

            found = scope;
        }

        return found;
    }

    // Every interface the type implements except the markers. Empty means "register under the concrete type
    // only" — the host still makes the type itself resolvable.
    private static IReadOnlyList<Type> ServiceTypesFor(Type type) =>
        type.GetInterfaces()
            .Where(i => Markers.All(m => m.Marker != i))
            .ToArray();
}
