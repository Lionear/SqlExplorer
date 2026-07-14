using Lionear.SqlExplorer.App.Localization;
using Lionear.SqlExplorer.App.ViewModels;
using Lionear.SqlExplorer.Core.Connections;
using Lionear.SqlExplorer.Core.Formatting;
using Lionear.SqlExplorer.Core.History;
using Lionear.SqlExplorer.Core.Localization;
using Lionear.SqlExplorer.Core.Plugins;
using Lionear.SqlExplorer.Core.Providers;
using Lionear.SqlExplorer.Core.Schema;
using Lionear.SqlExplorer.Core.Session;
using Lionear.SqlExplorer.Core.Settings;
using Lionear.SqlExplorer.Core.Shortcuts;
using Lionear.SqlExplorer.Core.Store;
using Lionear.SqlExplorer.Core.Tools;
using Lionear.SqlExplorer.Sdk.Shortcuts;
using Lionear.SqlExplorer.Sdk.Tools;
using Lionear.SqlExplorer.Infrastructure.Persistence;
using Lionear.SqlExplorer.Infrastructure.Secrets;
using Lionear.SqlExplorer.Infrastructure.Store;
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

        // Plugins live in two roots: the read-only bundled folder staged beside the exe at build time,
        // and the writable per-user folder the Plugin Store installs into (user wins on id conflict).
        // Apply any install/remove the Store staged last run *before* loading, then discover both roots.
        var stateStore = new JsonPluginStateStore();
        PluginMaintenance.ApplyPending(stateStore, PluginPaths.UserRoot);

        var discovered = PluginDiscovery.Discover(PluginPaths.BundledRoot, PluginPaths.UserRoot);
        var state = stateStore.GetAll();

        // Disabled plugins are known to the catalog (Installed tab) but never handed to a loader.
        // A folder with an unreadable manifest has no id and defaults to enabled so its failure surfaces.
        var enabled = discovered
            .Where(p => p.Id is not { } id || !state.TryGetValue(id, out var s) || s.Enabled)
            .ToList();

        // Providers load in isolated AssemblyLoadContexts — the same path a third party would use.
        // Each provider's engine identity is its manifest id, paired with the instance here.
        var providerResults = new ProviderPluginLoader().Load(enabled);
        var registrations = new List<ProviderRegistration>();
        foreach (var result in providerResults)
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

        // Tool plugins (type: "tool") load from the same discovered set; one assembly may ship several.
        var toolResults = new ToolPluginLoader().Load(enabled);
        var tools = new List<IToolPlugin>();
        foreach (var result in toolResults)
        {
            if (result.Succeeded)
            {
                tools.AddRange(result.Tools);
            }
            else
            {
                Console.Error.WriteLine($"[plugin] skipped tool '{result.PluginDirectory}': {result.Error}");
            }
        }

        services.AddSingleton<IToolRegistry>(new ToolRegistry(tools));

        // Host-side view of everything installed (loaded or not, enabled or not) for the Plugin Store's
        // Installed tab. Enable/disable/uninstall stage a change here, applied on next startup.
        services.AddSingleton<IPluginStateStore>(stateStore);
        services.AddSingleton(new PluginCatalogService(stateStore, discovered, providerResults, toolResults));

        // Plugin Store: one shared HttpClient feeds the Discovery feed, the catalog merge and the
        // installer. The store window is opened from the menu, same factory-delegate pattern as the dialogs.
        services.AddSingleton(new HttpClient());
        services.AddSingleton<IStoreSourcesStore>(new JsonStoreSourcesStore());
        services.AddSingleton<IDiscoveryService>(sp => new HttpDiscoveryService(sp.GetRequiredService<HttpClient>()));
        services.AddSingleton<IStoreCatalog>(sp => new HttpStoreCatalog(
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<IDiscoveryService>(),
            sp.GetRequiredService<IStoreSourcesStore>()));
        services.AddSingleton<IPluginInstaller>(sp => new PluginInstaller(
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<IPluginStateStore>()));
        services.AddSingleton<PluginUpdateService>();
        services.AddTransient<PluginStoreViewModel>();
        services.AddSingleton<Func<PluginStoreViewModel>>(sp => sp.GetRequiredService<PluginStoreViewModel>);

        // Connections: metadata in a JSON file, secrets in the OS-native keychain.
        // Migrate pre-v10 configs (legacy "Kind" enum) to the manifest "ProviderId" once at startup.
        var connectionStore = new JsonConnectionStore();
        connectionStore.MigrateLegacyProviderIds();
        services.AddSingleton<IConnectionStore>(connectionStore);
        services.AddSingleton<ISecretStore>(SecretStores.CreateForCurrentOs());

        // UI preferences (window geometry, sidebar width) alongside connections.json.
        services.AddSingleton<IAppSettingsStore>(new JsonAppSettingsStore());

        // Plugin-declared settings (keyed by plugin id) in their own file, so they never race the
        // app-settings save above.
        services.AddSingleton<IPluginSettingsStore>(new JsonPluginSettingsStore());

        // User keyboard-shortcut overrides (keymap.json). The service is the live keymap consulted by
        // the main window's key bindings and the editor's comment shortcut. Plugin-contributed shortcuts
        // (IShortcutContributor on any provider/tool) are merged in here so they share rebinding,
        // conflict detection and persistence with the built-ins.
        services.AddSingleton<IKeymapStore>(new JsonKeymapStore());
        var pluginShortcuts = CollectPluginShortcuts(registrations, tools);
        services.AddSingleton(sp => new KeymapService(sp.GetRequiredService<IKeymapStore>(), pluginShortcuts));

        // Query history (searchable, re-runnable) beside connections.json.
        services.AddSingleton<IQueryHistoryStore>(new JsonQueryHistoryStore());
        services.AddSingleton<IOpenTabsStore>(new JsonOpenTabsStore());
        services.AddSingleton<ConnectionService>();

        // Per-connection schema snapshot (tables/views/columns), built by walking the lazy tree at
        // connect. Powers object-search and schema-aware completion.
        services.AddSingleton<ISchemaCache, SchemaCache>();

        // The connection form VM — now embedded as the Connection Manager's detail panel (the standalone
        // modal it used to back was retired), so it's resolved per-connection via this factory.
        services.AddTransient<ConnectionDialogViewModel>();
        services.AddSingleton<Func<ConnectionDialogViewModel>>(sp => sp.GetRequiredService<ConnectionDialogViewModel>);

        // Connection Manager window (master-detail): opened from the sidebar/menu, same factory-delegate
        // pattern as the dialogs. Reuses the connection-form factory above for its detail panel.
        services.AddTransient<ConnectionManagerViewModel>();
        services.AddSingleton<Func<ConnectionManagerViewModel>>(sp => sp.GetRequiredService<ConnectionManagerViewModel>);

        // DDL Create dialog ("New Database…"/"New Schema…"/"New Table…"): reconfigured per open via
        // Configure(...), same factory-delegate pattern as ConnectionDialogViewModel.
        services.AddTransient<CreateObjectDialogViewModel>();
        services.AddSingleton<Func<CreateObjectDialogViewModel>>(sp => sp.GetRequiredService<CreateObjectDialogViewModel>);

        // DROP/ALTER confirmation dialog (host-only SQL, see Core/Ddl/AlterStatementBuilder) — same
        // factory-delegate pattern as the other two dialogs.
        services.AddTransient<AlterObjectDialogViewModel>();
        services.AddSingleton<Func<AlterObjectDialogViewModel>>(sp => sp.GetRequiredService<AlterObjectDialogViewModel>);

        // CSV-import column-mapping dialog — same factory-delegate pattern as the other dialogs.
        services.AddTransient<ImportCsvDialogViewModel>();
        services.AddSingleton<Func<ImportCsvDialogViewModel>>(sp => sp.GetRequiredService<ImportCsvDialogViewModel>);

        // Preferences window — same factory-delegate pattern as the other dialogs.
        services.AddTransient<SettingsViewModel>();
        services.AddSingleton<Func<SettingsViewModel>>(sp => sp.GetRequiredService<SettingsViewModel>);

        // Generic tool dialog — reconfigured per tool run, same factory-delegate pattern.
        services.AddTransient<ToolDialogViewModel>();
        services.AddSingleton<Func<ToolDialogViewModel>>(sp => sp.GetRequiredService<ToolDialogViewModel>);

        services.AddTransient<RoutineParametersDialogViewModel>();
        services.AddSingleton<Func<RoutineParametersDialogViewModel>>(sp => sp.GetRequiredService<RoutineParametersDialogViewModel>);

        services.AddTransient<MainViewModel>();

        var provider = services.BuildServiceProvider();

        // Editor tabs are created outside DI (as DataTemplate content), so expose the keymap statically
        // for the editor's comment shortcut to resolve against.
        KeymapService.Current = provider.GetRequiredService<KeymapService>();

        return provider;
    }

    // Flatten IShortcutContributor across providers and tools into namespaced host shortcuts. The id is
    // prefixed with the plugin id so two plugins can use the same local id without clashing.
    private static List<PluginShortcut> CollectPluginShortcuts(
        IReadOnlyList<ProviderRegistration> providers,
        IReadOnlyList<IToolPlugin> tools)
    {
        var result = new List<PluginShortcut>();

        void Add(string pluginId, string pluginTitle, object plugin)
        {
            if (plugin is not IShortcutContributor contributor)
            {
                return;
            }

            foreach (var shortcut in contributor.Shortcuts)
            {
                result.Add(new PluginShortcut(
                    $"{pluginId}:{shortcut.Id}",
                    pluginId,
                    pluginTitle,
                    shortcut.Title,
                    shortcut.DefaultGesture,
                    shortcut.ExecuteAsync));
            }
        }

        foreach (var registration in providers)
        {
            Add(registration.Id, registration.Provider.DisplayName, registration.Provider);
        }

        foreach (var tool in tools)
        {
            Add(tool.Id, tool.Title, tool);
        }

        return result;
    }
}
