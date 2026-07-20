using SqlExplorer.Core.Providers;
using SqlExplorer.Core.Tests.Mcp;
using SqlExplorer.Sdk.Provisioning;

namespace SqlExplorer.Core.Tests.Providers;

public class HostProviderCatalogTests
{
    private static ContainerRecipe Recipe(string image) =>
        new(image, "latest", 1234, "/data", "admin", "changeme", _ => []);

    [Fact] // Only providers that declared a recipe surface; the rest (e.g. file-based SQLite) are omitted.
    public void Lists_only_providers_that_declared_a_recipe()
    {
        var registry = new DbProviderRegistry(
        [
            new ProviderRegistration("cooldb", new FieldsProvider("CoolDB", Recipe("cooldb"))),
            new ProviderRegistration("filedb", new FieldsProvider("FileDB")), // no recipe
        ]);

        var catalog = new HostProviderCatalog(registry);
        var recipes = catalog.ContainerRecipes();

        var only = Assert.Single(recipes);
        Assert.Equal("cooldb", only.ProviderId);
        Assert.Equal("CoolDB", only.DisplayName);
        Assert.Equal("cooldb", only.Recipe.Image);
    }

    [Fact]
    public void Is_empty_when_no_provider_is_containerisable()
    {
        var registry = new DbProviderRegistry([new ProviderRegistration("filedb", new FieldsProvider("FileDB"))]);

        Assert.Empty(new HostProviderCatalog(registry).ContainerRecipes());
    }
}
