using System.Reflection;
using SqlExplorer.App.Localization;
using SqlExplorer.App.ViewModels;
using SqlExplorer.Core.Connections;
using SqlExplorer.Core.Security;
using SqlExplorer.Core.Formatting;
using SqlExplorer.Sdk.Formatting;
using SqlExplorer.Core.History;
using SqlExplorer.Core.Logging;
using SqlExplorer.Core.Localization;
using SqlExplorer.Core.Mcp;
using SqlExplorer.Core.Plugins;
using SqlExplorer.Core.Providers;
using SqlExplorer.Core.Schema;
using SqlExplorer.Core.Session;
using SqlExplorer.Core.Settings;
using SqlExplorer.Core.Shortcuts;
using SqlExplorer.Core.Store;
using SqlExplorer.Core.Tools;
using SqlExplorer.Core.Update;
using SqlExplorer.Sdk.Shortcuts;
using SqlExplorer.Sdk.Localization;
using SqlExplorer.Sdk.Tools;
using SqlExplorer.Infrastructure.Persistence;
using SqlExplorer.Infrastructure.Secrets;
using SqlExplorer.Infrastructure.Store;
using SqlExplorer.Infrastructure.Update;
using SqlExplorer.Mcp.Hosting;
using SqlExplorer.Sdk;
using SqlExplorer.Sdk.Mcp;
using Microsoft.Extensions.DependencyInjection;

namespace SqlExplorer.App.DependencyInjection;

public static class AppServices
{
    public static IServiceProvider Build()
    {
        var services = new ServiceCollection();

        services.AddSingleton<ISqlFormatter, BasicSqlFormatter>();
        // Instantiated here (not just type-registered) so the plugin loader below can hand the same live
        // localizer to each plugin's PluginLocalizer — its culture is set later (App startup) and reflected
        // on every lookup, so plugin strings follow a language switch just like host strings.
        var localizer = new ResxLocalizer();
        services.AddSingleton<ILocalizer>(localizer);

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
        // The live localizer goes in so each plugin gets a PluginLocalizer over its embedded Lang/*.json.
        var toolResults = new ToolPluginLoader(localizer).Load(enabled);
        var tools = new List<IToolPlugin>();
        var toolLocalizers = new Dictionary<string, IPluginLocalizer>();
        foreach (var result in toolResults)
        {
            if (result.Succeeded)
            {
                tools.AddRange(result.Tools);
                if (result.Localizer is { } toolLocalizer)
                {
                    foreach (var tool in result.Tools)
                    {
                        toolLocalizers[tool.Id] = toolLocalizer;
                    }
                }
            }
            else
            {
                Console.Error.WriteLine($"[plugin] skipped tool '{result.PluginDirectory}': {result.Error}");
            }
        }

        services.AddSingleton<IToolRegistry>(new ToolRegistry(tools, toolLocalizers));

        // MCP plugins (type: "mcp") contribute tools; the host — not any plugin — owns the server. Gather
        // the tools from every enabled MCP plugin so the host server can serve them.
        var mcpToolResults = new McpPluginLoader().Load(enabled);
        var mcpTools = new List<McpToolDefinition>();
        foreach (var result in mcpToolResults)
        {
            if (result.Succeeded)
            {
                mcpTools.AddRange(result.Providers.SelectMany(p => p.GetTools()));
            }
            else
            {
                Console.Error.WriteLine($"[plugin] skipped mcp '{result.PluginDirectory}': {result.Error}");
            }
        }

        // Host-side view of everything installed (loaded or not, enabled or not) for the Plugin Store's
        // Installed tab. Enable/disable/uninstall stage a change here, applied on next startup.
        services.AddSingleton<IPluginStateStore>(stateStore);
        services.AddSingleton(new PluginCatalogService(stateStore, discovered, providerResults, toolResults));

        // Plugin Store: one shared HttpClient feeds the Discovery feed, the catalog merge and the
        // installer. The store window is opened from the menu, same factory-delegate pattern as the dialogs.
        services.AddSingleton(new HttpClient());
        services.AddSingleton<IStoreSourcesStore>(new JsonStoreSourcesStore());
        services.AddSingleton<IPluginPinStore>(new JsonPluginPinStore());
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

        // In-app updater (SE-137): the same shared HttpClient fetches each channel's update.json and the
        // chosen asset. The running version is the informational stamp About also reads.
        services.AddSingleton<IUpdateManifestSource>(sp =>
            new HttpUpdateManifestSource(sp.GetRequiredService<HttpClient>()));
        services.AddSingleton(sp =>
            new AppUpdateService(sp.GetRequiredService<IUpdateManifestSource>(), RunningVersion()));
        services.AddSingleton(sp => new UpdateDownloader(sp.GetRequiredService<HttpClient>()));
        services.AddSingleton<IUpdateApplier>(new UpdateApplier());
        // Singleton: one shared instance behind the main-window banner and the Settings check, so a manual
        // check lights the same banner and offers the same "What's new" action.
        services.AddSingleton<AppUpdateViewModel>();
        // Proactive plugin-update checker (SE-138), sibling of the app-updater VM.
        services.AddSingleton<PluginUpdatesViewModel>();

        // Connections: metadata in a JSON file, secrets in the OS-native keychain.
        // Migrate pre-v10 configs (legacy "Kind" enum) to the manifest "ProviderId" once at startup.
        var connectionStore = new JsonConnectionStore();
        connectionStore.MigrateLegacyProviderIds();
        services.AddSingleton<IConnectionStore>(connectionStore);
        // Optional master-password layer: the OS vault is wrapped by an encrypting decorator keyed by the
        // in-memory master key. With no master password set the decorator is a transparent pass-through.
        services.AddSingleton<IMasterKeyProvider>(new MasterKeyProvider());
        var osSecretStore = SecretStores.CreateForCurrentOs();
        services.AddSingleton<ISecretStore>(sp =>
            new EncryptingSecretStore(osSecretStore, sp.GetRequiredService<IMasterKeyProvider>()));

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
        // Opt-in query log (audit): append-only JSONL, policy applied at startup / on settings save.
        services.AddSingleton<IQueryLog>(new JsonlQueryLogStore());
        services.AddTransient<QueryLogViewModel>();
        services.AddSingleton<Func<QueryLogViewModel>>(sp => sp.GetRequiredService<QueryLogViewModel>);
        services.AddTransient<AboutViewModel>();
        services.AddSingleton<Func<AboutViewModel>>(sp => sp.GetRequiredService<AboutViewModel>);
        services.AddSingleton<IOpenTabsStore>(new JsonOpenTabsStore());
        services.AddSingleton<ConnectionService>();
        services.AddSingleton<MasterPasswordService>();

        // Per-connection schema snapshot (tables/views/columns), built by walking the lazy tree at
        // connect. Powers object-search and schema-aware completion.
        services.AddSingleton<ISchemaCache, SchemaCache>();

        // Per-connection engine version string (e.g. "PostgreSQL 16.2"), fetched once at connect. Shown in
        // the status bar and the connect message; null when the provider reports no version (host-API v25).
        services.AddSingleton<IServerVersionCache, ServerVersionCache>();

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

        // "New User…" dialog — same factory-delegate pattern; reconfigured per open via Configure(...).
        services.AddTransient<NewUserDialogViewModel>();
        services.AddSingleton<Func<NewUserDialogViewModel>>(sp => sp.GetRequiredService<NewUserDialogViewModel>);

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

        // MCP host + server: the host owns the transport + all authorization (McpHost). Registered here so
        // the settings view can restart it on change; started once below after the container is built.
        services.AddSingleton(sp =>
        {
            var settingsStore = sp.GetRequiredService<IAppSettingsStore>();
            string? GetSetting(string key)
            {
                var s = settingsStore.Load();
                return key switch
                {
                    "requireAuth" => s.McpRequireAuth ? "true" : "false",
                    "maxRows" => s.McpMaxRows.ToString(),
                    "timeoutSeconds" => s.McpTimeoutSeconds.ToString(),
                    "scrubSecrets" => s.McpScrubSecrets ? "true" : "false",
                    _ => null
                };
            }

            // Route MCP audit/status to Trace, not Console: under WinExe (SE-147) there is no console on
            // Windows, and Console.Error would write to a dead handle. Trace stays visible in the debugger /
            // dotnet-trace, matching Program.cs's .LogToTrace(). Covers "[MCP] server started" and the
            // "[MCP DENY]" audit lines from McpHost.
            void Audit(string message) => System.Diagnostics.Trace.WriteLine(message);

            var mcpHost = new McpHost(
                sp.GetRequiredService<ConnectionService>(),
                sp.GetRequiredService<IDbProviderRegistry>(),
                sp.GetRequiredService<IQueryHistoryStore>(),
                sp.GetRequiredService<IQueryLog>(),
                sp.GetRequiredService<MasterPasswordService>(),
                GetSetting,
                Audit);

            var server = new McpServer(mcpHost, Audit);
            return new McpService(
                server,
                mcpTools,
                readOptions: () =>
                {
                    var s = settingsStore.Load();
                    return new McpServerOptions(s.McpEnabled, s.McpPort, s.McpRequireAuth, s.McpToken);
                },
                persistToken: token =>
                {
                    var s = settingsStore.Load();
                    s.McpToken = token;
                    settingsStore.Save(s);
                });
        });

        var provider = services.BuildServiceProvider();

        // Start the MCP server now (fire-and-forget). ApplyAsync no-ops when disabled (the default), so no
        // listener opens unless the user turned it on; it also skips starting when there are no tools.
        _ = provider.GetRequiredService<McpService>().ApplyAsync();

        // Editor tabs are created outside DI (as DataTemplate content), so expose the keymap statically
        // for the editor's comment shortcut to resolve against.
        KeymapService.Current = provider.GetRequiredService<KeymapService>();

        return provider;
    }

    // The running build's channel stamp (e.g. 0.2.0-nightly.20260717.42) — the informational version, the
    // only one that survives the stamp (AssemblyVersion is truncated to 0.2.0.0). Same read as About.
    private static string RunningVersion()
    {
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        }

        var plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
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
