using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SqlExplorer.Sdk.Extensibility;

namespace SqlExplorer.App.DependencyInjection;

/// <summary>
/// Bridges the container-neutral <see cref="ServiceRegistrationScanner"/> onto Microsoft.Extensions.DI:
/// discovers classes marked with a lifetime interface and registers each under its concrete type plus its
/// non-marker interfaces, with a single instance shared across all of them for singletons.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Host-side registration: trusted assemblies register under every non-marker interface they implement.
    /// Additive — only opt-in types are touched, so existing manual registrations stay as they are.
    /// </summary>
    public static IServiceCollection AddMarkedServices(this IServiceCollection services, params Assembly[] assemblies)
    {
        foreach (var registration in ServiceRegistrationScanner.Scan(assemblies))
            Register(services, registration);

        return services;
    }

    // (see AddMarkedPluginServices for the plugin-scoped, guard-railed variant)

    /// <summary>
    /// Plugin-side registration for a single plugin assembly: same mechanism, but service interfaces are
    /// narrowed to the plugin's own assembly (<see cref="ServiceRegistration.WithOwnAssemblyServiceTypesOnly"/>)
    /// so a plugin can add services but never replace a host/SDK contract. Returns every service type it
    /// registered — the allow-list for the plugin's scoped <see cref="System.IServiceProvider"/>.
    /// </summary>
    public static IReadOnlySet<Type> AddMarkedPluginServices(this IServiceCollection services, Assembly pluginAssembly)
    {
        var registered = new HashSet<Type>();

        foreach (var registration in ServiceRegistrationScanner.Scan(pluginAssembly))
            registered.UnionWith(Register(services, registration.WithOwnAssemblyServiceTypesOnly()));

        return registered;
    }

    // Registers the concrete type once, then forwards each interface to that same registration so a singleton
    // resolved via any of its interfaces is the very same instance. Returns every service type it registered.
    private static IReadOnlyList<Type> Register(IServiceCollection services, ServiceRegistration registration)
    {
        var lifetime = ToLifetime(registration.Scope);
        var registered = new List<Type> { registration.ImplementationType };

        services.Add(new ServiceDescriptor(registration.ImplementationType, registration.ImplementationType, lifetime));

        foreach (var serviceType in registration.ServiceTypes)
        {
            services.Add(new ServiceDescriptor(
                serviceType,
                sp => sp.GetRequiredService(registration.ImplementationType),
                lifetime));
            registered.Add(serviceType);
        }

        return registered;
    }

    private static ServiceLifetime ToLifetime(ServiceScope scope) => scope switch
    {
        ServiceScope.Singleton => ServiceLifetime.Singleton,
        ServiceScope.Transient => ServiceLifetime.Transient,
        ServiceScope.Scoped => ServiceLifetime.Scoped,
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown service scope."),
    };
}
