using Lionear.SqlExplorer.App.Localization;
using Lionear.SqlExplorer.App.ViewModels;
using Lionear.SqlExplorer.Core.Connections;
using Lionear.SqlExplorer.Core.Formatting;
using Lionear.SqlExplorer.Core.History;
using Lionear.SqlExplorer.Core.Localization;
using Lionear.SqlExplorer.Core.Plugins;
using Lionear.SqlExplorer.Core.Providers;
using Lionear.SqlExplorer.Core.Schema;
using Lionear.SqlExplorer.Core.Settings;
using Lionear.SqlExplorer.Infrastructure.Persistence;
using Lionear.SqlExplorer.Infrastructure.Secrets;
using Lionear.SqlExplorer.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace Lionear.SqlExplorer.App.DependencyInjection;

public static class AppServices
{
    public static IServiceProvider Build()
    {
        var services = new ServiceCollection();

        services.AddSingleton<ISqlFormatter, BasicSqlFormatter>();
        services.AddSingleton<ILocalizer, ResxLocalizer>();

        // Providers are discovered as plugins from plugins/ (staged there at build time),
        // loaded in isolated AssemblyLoadContexts — the same path a third party would use.
        // Each provider's engine identity is its manifest id, paired with the instance here.
        var pluginsDir = Path.Combine(AppContext.BaseDirectory, "plugins");
        var registrations = new List<ProviderRegistration>();
        foreach (var result in new ProviderPluginLoader().Load(pluginsDir))
        {
            if (result is { Succeeded: true, Id: { } id })
            {
                registrations.Add(new ProviderRegistration(id, result.Provider!));
            }
            else
            {
                Console.Error.WriteLine($"[plugin] skipped '{result.PluginDirectory}': {result.Error}");
            }
        }

        services.AddSingleton<IDbProviderRegistry>(new DbProviderRegistry(registrations));

        // Connections: metadata in a JSON file, secrets in the OS-native keychain.
        // Migrate pre-v10 configs (legacy "Kind" enum) to the manifest "ProviderId" once at startup.
        var connectionStore = new JsonConnectionStore();
        connectionStore.MigrateLegacyProviderIds();
        services.AddSingleton<IConnectionStore>(connectionStore);
        services.AddSingleton<ISecretStore>(SecretStores.CreateForCurrentOs());

        // UI preferences (window geometry, sidebar width) alongside connections.json.
        services.AddSingleton<IAppSettingsStore>(new JsonAppSettingsStore());

        // Query history (searchable, re-runnable) beside connections.json.
        services.AddSingleton<IQueryHistoryStore>(new JsonQueryHistoryStore());
        services.AddSingleton<ConnectionService>();

        // Per-connection schema snapshot (tables/views/columns), built by walking the lazy tree at
        // connect. Powers object-search and schema-aware completion.
        services.AddSingleton<ISchemaCache, SchemaCache>();

        services.AddTransient<ConnectionDialogViewModel>();
        services.AddSingleton<Func<ConnectionDialogViewModel>>(sp => sp.GetRequiredService<ConnectionDialogViewModel>);
        services.AddTransient<MainViewModel>();

        return services.BuildServiceProvider();
    }
}
