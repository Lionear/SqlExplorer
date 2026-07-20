namespace SqlExplorer.Sdk.Extensibility;

/// <summary>
/// The lifetime a service auto-registered through a marker interface is created with. Kept container-neutral
/// (no dependency on any DI package) so the scanner can live in the SDK and stay unit-testable; the host maps
/// each value onto its container's own lifetime when it turns the scan result into registrations.
/// </summary>
public enum ServiceScope
{
    /// <summary>One instance shared for the whole application.</summary>
    Singleton,

    /// <summary>A fresh instance every time the service is resolved.</summary>
    Transient,

    /// <summary>One instance per DI scope.</summary>
    Scoped,
}

/// <summary>
/// Marker interfaces that opt a class into auto-registration. A class implementing exactly one of these is
/// discovered by <see cref="ServiceRegistrationScanner"/> and registered with the matching
/// <see cref="ServiceScope"/> — under each non-marker interface it implements plus its own concrete type.
/// They live in the SDK so plugin authors can reference them too; wiring plugin services into the host
/// container is a later, capability-gated phase (SE-171). Implementing more than one marker is a
/// configuration error the scanner rejects.
/// </summary>
public interface ISingletonService;

/// <inheritdoc cref="ISingletonService"/>
public interface ITransientService;

/// <inheritdoc cref="ISingletonService"/>
public interface IScopedService;
