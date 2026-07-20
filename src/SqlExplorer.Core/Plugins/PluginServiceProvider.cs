namespace SqlExplorer.Core.Plugins;

/// <summary>
/// An <see cref="IServiceProvider"/> handed to a plugin that granted the <c>services</c> capability. It
/// delegates to the host's root provider but only for the service types the plugin itself registered
/// (its marker-annotated concrete types and own-assembly interfaces); anything else resolves to
/// <c>null</c>. So a plugin can resolve its own wiring without gaining read access to the host container.
/// </summary>
public sealed class PluginServiceProvider(IServiceProvider inner, IReadOnlySet<Type> allowed) : IServiceProvider
{
    public object? GetService(Type serviceType) =>
        allowed.Contains(serviceType) ? inner.GetService(serviceType) : null;
}
