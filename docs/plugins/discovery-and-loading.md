← [Plugins overview](../PLUGINS.md)

## How discovery and loading work

At startup (`src/App/DependencyInjection/AppServices.cs`), the host:

1. Resolves `plugins/` next to the executable (`AppContext.BaseDirectory`).
2. Runs `ProviderPluginLoader.Load(pluginsDir)`
   (`src/Core/Plugins/ProviderPluginLoader.cs`), which for each subfolder:
   - Skips folders without a `plugin.json`.
   - Parses the manifest; skips it if `type != "provider"` or
     `hostApiVersion` doesn't match `ProviderHostApi.Version`.
   - Loads `entryAssembly` into a fresh, isolated `ProviderLoadContext`
     (`src/Core/Plugins/ProviderLoadContext.cs`, an `AssemblyLoadContext`
     subclass using `AssemblyDependencyResolver` against the plugin's own
     `.deps.json`) — so each plugin can carry its own driver version
     independent of every other plugin.
   - Reflects for a non-abstract class implementing `IDbProvider` and
     activates it.
   - Never throws back to the caller: failures are captured per plugin as an
     `Error` on the `ProviderLoadResult`, and logged, so one broken plugin
     doesn't take down the app.
3. Registers every successfully loaded provider into `DbProviderRegistry`
   (keyed by manifest `id`) as the DI singleton `IDbProviderRegistry`.

`type: "tool"` plugins load in parallel through `ToolPluginLoader`
(`src/Core/Plugins/ToolPluginLoader.cs`), which mirrors the above but reflects for
`IToolPlugin` (and instantiates every implementation in the assembly, since one
tool assembly may ship several). Right after loading, the host scans every loaded
plugin for the optional capabilities: `IPluginSettings`/`ICustomPluginSettingsUi`
populate Settings ▸ Plugins, and `IShortcutContributor` shortcuts are merged into
the keymap (`AppServices.CollectPluginShortcuts`, keyed as `pluginId:localId`).

One important detail if you're debugging an ALC loading issue:
`ProviderLoadContext` deliberately returns `null` (falls back to the default
load context) when asked to resolve `Sdk` itself, so the host's copy
of `Sdk.dll` is reused across the ALC boundary and `IDbProvider`
keeps a single type identity. This is exactly why every provider `.csproj`
sets `Private="false"` on the `Sdk` project reference — it must
*not* be copied into the plugin's own output folder.
