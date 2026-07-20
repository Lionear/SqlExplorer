using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SqlExplorer.Sdk.Extensibility;

namespace SqlExplorer.App.DependencyInjection;

/// <summary>
/// Bridges the container-neutral <see cref="ServiceRegistrationScanner"/> onto Microsoft.Extensions.DI:
/// discovers classes marked with a lifetime interface and registers each under its concrete type plus every
/// non-marker interface it implements, with a single instance shared across all of them for singletons.
/// Additive — it registers only opt-in types, so existing manual registrations are untouched.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMarkedServices(this IServiceCollection services, params Assembly[] assemblies)
    {
        foreach (var registration in ServiceRegistrationScanner.Scan(assemblies))
        {
            var lifetime = ToLifetime(registration.Scope);

            // Register the concrete type once, then forward each interface to that same registration so a
            // singleton resolved via any of its interfaces is the very same instance (not one per interface).
            services.Add(new ServiceDescriptor(registration.ImplementationType, registration.ImplementationType, lifetime));

            foreach (var serviceType in registration.ServiceTypes)
                services.Add(new ServiceDescriptor(
                    serviceType,
                    sp => sp.GetRequiredService(registration.ImplementationType),
                    lifetime));
        }

        return services;
    }

    private static ServiceLifetime ToLifetime(ServiceScope scope) => scope switch
    {
        ServiceScope.Singleton => ServiceLifetime.Singleton,
        ServiceScope.Transient => ServiceLifetime.Transient,
        ServiceScope.Scoped => ServiceLifetime.Scoped,
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown service scope."),
    };
}
