namespace SqlExplorer.Sdk.Provisioning;

/// <summary>One containerisable provider the host exposes to a plugin: its manifest id, display name, and the
/// <see cref="ContainerRecipe"/> it declared.</summary>
public sealed record ProviderRecipe(string ProviderId, string DisplayName, ContainerRecipe Recipe);

/// <summary>
/// The read-seam a standing-subsystem plugin uses to discover which installed providers can be containerised —
/// every registered <c>IDbProvider</c> whose <see cref="IDbProvider.ContainerRecipe"/> is non-null. Handed to a
/// plugin on <see cref="Extensibility.IPluginRuntimeContext.Providers"/> only when it declared the
/// <see cref="Extensibility.PluginCapabilities.Providers"/> capability. Read-only host metadata: the plugin
/// learns what engines exist and how to provision them, but gains no control over them.
/// </summary>
public interface IProviderCatalog
{
    /// <summary>Every installed provider that declared a container recipe, in registration order.</summary>
    IReadOnlyList<ProviderRecipe> ContainerRecipes();
}
