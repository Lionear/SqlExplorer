using Lionear.SqlExplorer.App.Localization;
using Lionear.SqlExplorer.App.ViewModels;
using Lionear.SqlExplorer.Core.Connections;
using Lionear.SqlExplorer.Core.Formatting;
using Lionear.SqlExplorer.Core.Localization;
using Lionear.SqlExplorer.Core.Plugins;
using Lionear.SqlExplorer.Core.Providers;
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
        var pluginsDir = Path.Combine(AppContext.BaseDirectory, "plugins");
        foreach (var result in new ProviderPluginLoader().Load(pluginsDir))
        {
            if (result.Succeeded)
            {
                services.AddSingleton<IDbProvider>(result.Provider!);
            }
            else
            {
                Console.Error.WriteLine($"[plugin] skipped '{result.PluginDirectory}': {result.Error}");
            }
        }

        services.AddSingleton<IDbProviderRegistry, DbProviderRegistry>();

        // Connections: metadata in a JSON file, secrets in the OS-native keychain.
        services.AddSingleton<IConnectionStore>(new JsonConnectionStore());
        services.AddSingleton<ISecretStore>(SecretStores.CreateForCurrentOs());

        // UI preferences (window geometry, sidebar width) alongside connections.json.
        services.AddSingleton<IAppSettingsStore>(new JsonAppSettingsStore());
        services.AddSingleton<ConnectionService>();

        services.AddTransient<ConnectionDialogViewModel>();
        services.AddSingleton<Func<ConnectionDialogViewModel>>(sp => sp.GetRequiredService<ConnectionDialogViewModel>);
        services.AddTransient<MainViewModel>();

        return services.BuildServiceProvider();
    }
}
