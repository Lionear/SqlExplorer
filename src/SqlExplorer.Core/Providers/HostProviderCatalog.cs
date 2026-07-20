using SqlExplorer.Sdk.Provisioning;

namespace SqlExplorer.Core.Providers;

/// <summary>
/// The host's <see cref="IProviderCatalog"/>: exposes every registered provider that declared a
/// <see cref="Sdk.IDbProvider.ContainerRecipe"/> to a plugin that holds the <c>providers</c> capability.
/// Read-only — it hands out the providers' own recipe metadata, not the providers themselves.
/// </summary>
public sealed class HostProviderCatalog : IProviderCatalog
{
    private readonly IDbProviderRegistry _registry;

    public HostProviderCatalog(IDbProviderRegistry registry) => _registry = registry;

    public IReadOnlyList<ProviderRecipe> ContainerRecipes() =>
        _registry.All
            .Where(r => r.Provider.ContainerRecipe is not null)
            .Select(r => new ProviderRecipe(r.Id, r.Provider.DisplayName, r.Provider.ContainerRecipe!))
            .ToList();
}
