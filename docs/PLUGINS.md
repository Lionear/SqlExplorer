# Writing Plugins for Lionear SQL Explorer

This document explains the plugin system: what a plugin is, how it is loaded,
and how to build one.

## Overview

Lionear SQL Explorer ships **no database drivers in the host binaries**. Every
database engine (PostgreSQL, MySQL, SQL Server, SQLite, ...) is a separate
plugin, discovered at startup and loaded in its own isolated
`AssemblyLoadContext`. This keeps the host provider-agnostic and lets each
plugin carry its own driver version.

There are two plugin types today, distinguished by the manifest `type` field:

- **`provider`** — a database engine integration (`IDbProvider`). Covered first,
  below.
- **`tool`** — an action that operates on a connection/node (`IToolPlugin`), e.g.
  the Universal Backup & Restore tool. See [Plugin type: `tool`](#plugin-type-tool).

On top of that, **any** plugin (provider or tool) may opt into cross-cutting
*capabilities* by implementing extra optional interfaces the host discovers with
an `is`-check at load — no manifest change needed:

- **Settings UI** — a persistent settings pane in Settings ▸ Plugins, either a
  host-rendered form (`IPluginSettings`, Route A) or the plugin's own Avalonia
  view (`ICustomPluginSettingsUi`, Route B).
- **Keyboard shortcuts** — global shortcuts the user can rebind
  (`IShortcutContributor`).

Both are documented under
[Optional capabilities](#optional-capabilities-settings-ui--keyboard-shortcuts).
All plugin contracts live in the single public SDK assembly
`Lionear.SqlExplorer.Sdk` (`src/Sdk`, namespace `Lionear.SqlExplorer.Sdk.*`) —
the only assembly a plugin references from this repository.

## Plugin type: `provider`

A provider plugin teaches the host how to talk to one database engine. It
implements a single interface, `IDbProvider`, from the public SDK project
`src/Sdk` (namespace `Lionear.SqlExplorer.Sdk`). `Sdk` is
MIT-licensed specifically so third parties can build and ship their own
providers freely — it is the *only* assembly a provider plugin references
from this repository; no reference to `Core`, `App`, or any driver-specific
host code is needed or allowed.

### The contract: `IDbProvider`

```csharp
public interface IDbProvider
{
    string DisplayName { get; }
    ProviderIcon? Icon { get; }
    ISqlDialect Dialect { get; }
    IReadOnlyList<ConnectionField> ConnectionFields { get; }

    string BuildConnectionString(IReadOnlyDictionary<string, string?> values);

    Task<bool> TestConnectionAsync(ConnectionProfile profile, CancellationToken ct);

    Task<IReadOnlyList<DbTreeNode>> GetChildNodesAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct);

    Task<QueryResult> ExecuteQueryAsync(ConnectionProfile profile, string sql, CancellationToken ct);

    Task<int> ExecuteBatchAsync(
        ConnectionProfile profile,
        IReadOnlyList<SqlStatement> statements,
        CancellationToken ct);
}
```

| Member | Purpose |
|---|---|
| `DisplayName` | Human-readable name shown in the UI (e.g. `"PostgreSQL"`). |
| `Icon` | Optional glyph/image for connection nodes. Use `ProviderIconLoader.Load(typeof(YourProvider), "🔧")` — it embeds an `icon.png` next to the project if present, otherwise falls back to the given emoji glyph. |
| `Dialect` | The provider's `ISqlDialect` implementation (see below). |
| `ConnectionFields` | Declares the fields of the connection dialog. The host renders a generic form from this — no provider-specific UI code is ever needed. |
| `BuildConnectionString` | Composes a driver connection string from the submitted field values (keyed by `ConnectionField.Key`), including any secret just retrieved from the OS keychain. |
| `TestConnectionAsync` | Opens and validates a connection; used by the "Test connection" button. |
| `GetChildNodesAsync` | Lazily lists the children of one schema-tree node (DBeaver-style on-demand loading, so large servers are never introspected all at once). `ancestors` is the path from the connection root to the node being expanded — empty for the top-level nodes. Each provider decides its own hierarchy shape (server → database → schema → tables/views → columns, or something flatter, as SQLite does). |
| `ExecuteQueryAsync` | Runs a free-form SQL string and returns a `QueryResult`. |
| `ExecuteBatchAsync` | Runs a set of parameterised `SqlStatement`s inside a single transaction, rolling back on any failure. This is the commit step of the editable-grid save flow: the host generates dialect-quoted INSERT/UPDATE/DELETE statements, the provider only owns parameter binding and transaction handling. |

### The dialect: `ISqlDialect`

```csharp
public interface ISqlDialect
{
    IReadOnlySet<string> Keywords { get; }
    string QuoteIdentifier(string identifier);
    string QualifyName(string? database, string? schema, string table);
    string Paginate(string sql, int limit, int offset, string? orderBy = null);
}
```

| Member | Purpose |
|---|---|
| `Keywords` | SQL keyword set used for syntax highlighting. |
| `QuoteIdentifier` | Quotes/escapes a single identifier (table, column, ...) in the engine's own syntax. |
| `QualifyName` | Builds a fully qualified, quoted object name from optional database/schema and a table name. |
| `Paginate` | Wraps a query with the engine's pagination syntax (`LIMIT/OFFSET`, `OFFSET/FETCH`, ...), optionally applying an `ORDER BY`. Used by the Browse tab's paging and sorting. |

### Supporting DTOs (all in `Sdk`)

- **`ConnectionField(Key, Label, Type, Required, Default, Placeholder)`** — one
  field of the connection dialog. `Type` is `Text | Password | Number | File |
  Bool`. Fields of type `Password` are automatically routed to the OS
  keychain (`IsSecret == true`) and never written to the connection config
  file.
- **`ConnectionProfile(Name, ConnectionString, Database)`** — what a provider
  method receives at execute time. `Database` is the optional catalog/database
  context selected in the UI.
- **`DbNodeKind`** — enum of schema-tree node kinds: `Database, SchemaFolder,
  Schema, TableFolder, ViewFolder, IndexFolder, SequenceFolder, Table, View,
  Column, Index, Sequence, Object, Group`.
- **`DbNodeRef(Kind, Name)`** / **`DbTreeNode { Kind, Name, Detail,
  HasChildren }`** — a path segment / a node returned by `GetChildNodesAsync`.
- **`QueryResult { Columns, Rows, RecordsAffected, Elapsed }`** with
  **`ResultColumn(Name, ClrType)`** carrying edit metadata (`BaseSchema,
  BaseTable, BaseColumn, IsKey, IsReadOnly, AllowDbNull`) — this metadata is
  what lets the host decide whether a result grid is safely editable (traces
  back to a single table with a primary key).
- **`SqlStatement(Text, Parameters)`** / **`SqlParam(Name, Value)`** —
  parameterised statement with named placeholders (`@p0, @p1, ...`).

### Host API versioning

`ProviderHostApi.Version` (currently `15`) is the contract version. Every
plugin declares the version it was built against in its manifest
(`hostApiVersion`); the loader rejects a plugin whose version does not match,
rather than risk loading against a contract it doesn't fully implement.
Check `src/Sdk/ProviderHostApi.cs` for the current value and its
changelog comments before starting a new provider.

## Building a provider plugin, step by step

### 1. Create the project

Add a new project under `src/`, e.g. `src/Providers.MyEngine/`, referencing
**only** `Sdk`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Lionear.SqlExplorer.Providers.MyEngine</RootNamespace>
    <!-- Required: emit the full private dependency closure (driver + its own
         dependencies) so the plugin loads correctly in its own ALC. -->
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
  </PropertyGroup>

  <ItemGroup>
    <!-- Private=false keeps Sdk.dll OUT of the plugin's own output
         folder, so the host's copy is used across the ALC boundary and
         IDbProvider keeps a single type identity. -->
    <ProjectReference Include="..\Sdk\Lionear.SqlExplorer.Sdk.csproj" Private="false" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="MyEngine.Driver" Version="x.y.z" />
  </ItemGroup>

  <ItemGroup>
    <Content Include="plugin.json" CopyToOutputDirectory="PreserveNewest" />
    <!-- Optional: drop a square PNG here as icon.png for branding. -->
    <EmbeddedResource Include="icon.png" LogicalName="icon.png" Condition="Exists('icon.png')" />
  </ItemGroup>

</Project>
```

### 2. Implement `IDbProvider` and `ISqlDialect`

Use `src/Providers.Sqlite/SqliteProvider.cs` and `SqliteDialect.cs` as the
simplest reference implementation (no server/database/schema layers — SQLite
exposes Tables/Views/Sequences directly under the connection root). For an
engine with server → database → schema layering, see
`src/Providers.Postgres` or `src/Providers.MsSql`.

Minimal skeleton:

```csharp
using Lionear.SqlExplorer.Sdk;

namespace Lionear.SqlExplorer.Providers.MyEngine;

public sealed class MyEngineProvider : IDbProvider
{
    public string DisplayName => "MyEngine";

    public ProviderIcon? Icon { get; } = ProviderIconLoader.Load(typeof(MyEngineProvider), "🔧");

    public ISqlDialect Dialect { get; } = new MyEngineDialect();

    public IReadOnlyList<ConnectionField> ConnectionFields { get; } =
    [
        new("host", "Host", ConnectionFieldType.Text, Required: true),
        new("port", "Port", ConnectionFieldType.Number, Default: "5432"),
        new("database", "Database", ConnectionFieldType.Text, Required: true),
        new("username", "Username", ConnectionFieldType.Text, Required: true),
        new("password", "Password", ConnectionFieldType.Password)
    ];

    public string BuildConnectionString(IReadOnlyDictionary<string, string?> values) =>
        /* compose the driver's connection string from `values` */;

    public Task<bool> TestConnectionAsync(ConnectionProfile profile, CancellationToken ct) => /* ... */;

    public Task<IReadOnlyList<DbTreeNode>> GetChildNodesAsync(
        ConnectionProfile profile, IReadOnlyList<DbNodeRef> ancestors, CancellationToken ct) => /* ... */;

    public Task<QueryResult> ExecuteQueryAsync(ConnectionProfile profile, string sql, CancellationToken ct) => /* ... */;

    public Task<int> ExecuteBatchAsync(
        ConnectionProfile profile, IReadOnlyList<SqlStatement> statements, CancellationToken ct) => /* ... */;
}
```

### 3. Write the manifest (`plugin.json`)

Every plugin folder needs a `plugin.json` describing it:

```json
{
  "schemaVersion": 1,
  "id": "myengine",
  "type": "provider",
  "name": "MyEngine",
  "version": "1.0.0",
  "hostApiVersion": 15,
  "entryAssembly": "Lionear.SqlExplorer.Providers.MyEngine.dll"
}
```

| Field | Meaning |
|---|---|
| `schemaVersion` | Manifest format version (currently `1`). |
| `id` | The engine's permanent identity. There is no host-side enum of engines — `id` is what makes the set of engines open; pick something short, lowercase, and stable, since saved connections reference it. |
| `type` | Plugin kind discriminator. Must be `"provider"` — the only value the loader currently accepts. |
| `name` | Display name (informational; `IDbProvider.DisplayName` is what the UI actually shows). |
| `version` | Your plugin's own version string. |
| `hostApiVersion` | Must equal `ProviderHostApi.Version` at build time (currently 15). A mismatch causes the loader to skip the plugin rather than risk a broken contract. |
| `entryAssembly` | Path (relative to the plugin's own folder) to the compiled plugin DLL. |

### 4. Ship it

A plugin is a folder next to the host executable:

```
plugins/
  myengine/
    plugin.json
    Lionear.SqlExplorer.Providers.MyEngine.dll
    Lionear.SqlExplorer.Providers.MyEngine.deps.json
    MyEngine.Driver.dll
    ... (rest of the build output)
```

For the first-party providers this copy is automated by an MSBuild target,
`StageProviderPlugins`, in `src/Desktop/Lionear.SqlExplorer.Desktop.csproj`,
which runs after build and copies each `Providers.*` project's full output
into `<TargetDir>/plugins/<id>/`. A genuinely third-party/out-of-tree plugin
ships the same way manually — just place the built output (including the
`.deps.json`) plus `plugin.json` in `plugins/<id>/` next to the host
executable.

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

## Plugin type: `tool`

A **tool** contributes an action rather than a database engine: it shows up as a
menu item on the schema tree, collects some inputs in a dialog, and runs against
the selected connection/node. The Universal Backup & Restore feature is itself a
tool plugin. Tools reference the same `Lionear.SqlExplorer.Sdk` assembly as
providers and are staged into `plugins/` the same way.

### The contract: `IToolPlugin`

```csharp
public interface IToolPlugin
{
    string Id { get; }                       // stable; one assembly may ship several tools
    string Title { get; }                    // menu-item / dialog title, e.g. "Backup…"
    ProviderIcon? Icon => null;
    ToolTarget Target { get; }               // where in the tree the tool is offered
    IReadOnlyList<ToolField> Fields { get; } // Route A: the inputs the host renders

    bool IsDestructive => false;             // true → host shows a confirmation first (e.g. restore)

    Task<string?> PreviewAsync(string filePath, CancellationToken ct) => Task.FromResult<string?>(null);

    Task ExecuteAsync(
        ToolExecutionContext context,
        IReadOnlyDictionary<string, string?> inputs,
        IProgress<ToolProgress> progress,
        CancellationToken ct);
}
```

| Member | Purpose |
|---|---|
| `Id` | Stable id. One tool assembly may contain several `IToolPlugin` classes — all are loaded — so this need not match the manifest `id`. |
| `Title` | Menu-item and dialog title. |
| `Target` | A `ToolTarget` that decides which tree nodes offer the tool (see below). |
| `Fields` | The Route A input declarations; the host renders a generic dialog from them, exactly like the connection form. Empty when the tool uses a Route B custom view. |
| `IsDestructive` | When true the host shows a destructive-action confirmation before running. |
| `PreviewAsync` | Optional: when a `File` field changes, return a short summary of the chosen file (e.g. read a backup header) shown under that field before Execute runs. |
| `ExecuteAsync` | Runs the tool. `inputs` holds the collected field values keyed by `ToolField.Key`; report progress lines through `progress`. |

### Where the tool is offered: `ToolTarget`

```csharp
public sealed record ToolTarget(
    IReadOnlyList<string>? ProviderIds = null,   // null = every provider (the "universal" case)
    IReadOnlyList<DbNodeKind>? NodeKinds = null,  // null = any node kind
    bool IncludeConnectionRoot = false);          // the connection root has no node kind
```

The host shows the tool on a node only when the node's provider is in
`ProviderIds` **and** its kind is in `NodeKinds`. Because the connection root has
no node kind, a whole-connection tool sets `IncludeConnectionRoot = true` rather
than trying to express the root via `NodeKinds`.

### What a tool receives at run time: `ToolExecutionContext`

```csharp
public sealed record ToolExecutionContext(
    ConnectionProfile Profile,   // includes the resolved ConnectionString (secrets already fetched)
    DbNodeRef? Node,             // the node the tool launched on; null at the connection root
    IDbProvider Provider,        // walk schema / run queries through the same interface the host uses
    string ProviderId,
    IToolHost Host);             // host-only services: file pickers + GetPluginSetting(key)
```

The `Provider` handed over is the live provider for that connection, so a generic
("universal") tool can introspect the schema, run queries and recreate objects
through the same `IDbProvider` the host uses — no driver dependency of its own.

### The `ToolField` form (Route A)

```csharp
public sealed record ToolField(
    string Key, string Label,
    ToolFieldType Type = ToolFieldType.Text,   // Text | Password | Choice | File | Bool
    bool Required = false,
    string? Default = null,
    string? Placeholder = null,
    IReadOnlyList<string>? Choices = null,      // for Choice
    IReadOnlyList<string>? FileExtensions = null, // for File (picker filter)
    bool SaveFile = false);                     // File: true = save picker, false = open picker
```

A `Password` field is routed to the OS keychain and never written to disk; a
`File` field gets a Browse button wired to the host's save/open picker.

### Custom tool UI (Route B) — `ICustomToolUi`

When the inputs are interdependent (a choice that shows/hides other fields, a
custom layout), a tool can supply its own Avalonia view instead of the generated
form:

```csharp
public interface ICustomToolUi
{
    Control CreateView(IToolUiContext context);   // read/write values by ToolField.Key
}
```

The plugin implements `IToolPlugin` **and** `ICustomToolUi`; the host hosts the
returned control in the tool dialog and still collects values through
`IToolUiContext.GetValue/SetValue`, so `ExecuteAsync` is unchanged. Because the
returned `Control` is an Avalonia type shared across the ALC boundary, add an
Avalonia reference to the plugin `.csproj` with `ExcludeAssets="runtime"` (share
the host's copy) — see the capability note below.

### Tool manifest

Identical to a provider's, but `type` is `"tool"` and `hostApiVersion` tracks the
**tool** contract (`ToolHostApi.Version`, currently `1`), which versions
separately from the provider contract:

```json
{
  "schemaVersion": 1,
  "id": "universal-backup",
  "type": "tool",
  "name": "Universal Backup & Restore",
  "version": "1.0.0",
  "hostApiVersion": 1,
  "entryAssembly": "Lionear.SqlExplorer.Tools.UniversalBackup.dll"
}
```

## Optional capabilities (settings UI & keyboard shortcuts)

These are **optional interfaces any plugin may add** — provider or tool. The host
detects each with an `is`-check at load (the same pattern for all of them), so a
plugin opts in simply by implementing the interface; nothing changes in the
manifest. A plugin can implement several at once — `TemplateProvider`
(`plugins/Providers.Template`) implements all three as the reference example.

### Persistent settings — Route A (`IPluginSettings`)

Plugin-wide values the user sets once (a path to an external binary, a default
folder) that apply to every use of the plugin. Declare fields; the host renders a
generic form in Settings ▸ Plugins and persists the values to
`plugin-settings.json` keyed by plugin id.

```csharp
public sealed class MyTool : IToolPlugin, IPluginSettings
{
    public IReadOnlyList<PluginSettingField> SettingsFields { get; } =
    [
        new("binaryPath", "Executable path", PluginSettingFieldType.File, Group: "Paths"),
        new("outputDir",  "Default output folder", PluginSettingFieldType.Folder, Group: "Paths"),
        new("logLevel",   "Log level", PluginSettingFieldType.Choice,
            Default: "info", Choices: ["debug", "info", "warn", "error"], Group: "Behaviour"),
        new("verbose",    "Verbose output", PluginSettingFieldType.Bool, Group: "Behaviour"),
    ];
    // ... IToolPlugin members ...
}
```

`PluginSettingFieldType` is `Text | Bool | Choice | File | Folder`. `Group`
sections a single pane under headers. At run time a tool reads a saved value with
`context.Host.GetPluginSetting("binaryPath")`.

### Persistent settings — Route B (`ICustomPluginSettingsUi`)

When settings are interdependent, supply your own Avalonia view for the pane
instead of the generated form:

```csharp
public interface ICustomPluginSettingsUi
{
    Control CreateSettingsView(IPluginSettingsContext context); // read/write by key
}
```

Values still flow through `IPluginSettingsContext.GetValue/SetValue`, so the host
persists them the same way regardless of route. A plugin may implement Route A,
Route B, or both (the host prefers the custom view when present).

### Keyboard shortcuts (`IShortcutContributor`)

Register global shortcuts that appear in Settings ▸ Keyboard under the plugin's
own section, where the user can rebind or clear them. They share the host's live
conflict detection, persistence (`keymap.json`) and rebinding with the built-in
shortcuts.

```csharp
public sealed class MyTool : IToolPlugin, IShortcutContributor
{
    public IReadOnlyList<ShortcutContribution> Shortcuts { get; } =
    [
        new("run", "Run my tool", "Mod+Shift+B", ct => RunAsync(ct)),
        new("secondary", "Secondary action", DefaultGesture: null, ct => DoOtherAsync(ct)),
    ];
}

public sealed record ShortcutContribution(
    string Id,                // unique within the plugin; the host namespaces it as pluginId:Id
    string Title,             // label in the shortcut list
    string? DefaultGesture,   // Avalonia gesture syntax; null = ships unbound
    Func<CancellationToken, Task> ExecuteAsync);
```

Key points:

- **`Mod` token** in a default gesture maps to the platform primary modifier —
  **Cmd on macOS, Ctrl on Windows/Linux** — so `"Mod+Shift+B"` ships as ⌘⇧B on a
  Mac and Ctrl+Shift+B elsewhere. Use plain modifiers (`Ctrl`, `Shift`, `Alt`)
  when you deliberately want the same key on every platform.
- **`DefaultGesture: null`** ships the command unbound; the user assigns a key.
- **Ids are namespaced** by the host (`pluginId:localId`), so two plugins can use
  the same local id without clashing. Keep your local id stable — it is persisted.
- The callback is **self-contained** and runs on the UI thread; capture whatever
  state you need when you build the contribution, and offload heavy work yourself.
  (Shortcuts are window-scoped; a plugin cannot bind an editor-only key.)

### Referencing Avalonia for a Route B view

Route B capabilities (`ICustomToolUi`, `ICustomPluginSettingsUi`) return an
Avalonia `Control`. Add Avalonia to the plugin `.csproj` so it compiles, but keep
the host's copy authoritative across the ALC boundary:

```xml
<PackageReference Include="Avalonia" Version="12.0.5" ExcludeAssets="runtime" />
```

A plugin that only uses declarative Route A (no custom view) needs no Avalonia
reference at all.

## Reference implementations

| Plugin | Type | Notable for |
|---|---|---|
| `src/Providers.Sqlite` | provider | Simplest complete example — no server/database/schema layers, good starting template. |
| `src/Providers.Postgres` | provider | Full server → database → schema → table hierarchy; also the proof-of-concept that a provider builds independently of the host. |
| `src/Providers.MySql` | provider | MySQL/MariaDB dialect quirks. |
| `src/Providers.MsSql` | provider | SQL Server dialect and schema layering. |
| `plugins/Tools.UniversalBackup` | tool | Full `IToolPlugin` example (streaming backup/restore); also implements `IPluginSettings` (default backup folder). |
| `plugins/Providers.Template` | provider | Debug-only reference example implementing **every** capability: `IPluginSettings` (Route A, all field types), `IShortcutContributor` (two shortcuts). The place to copy from. |

## Future plugin types

The manifest's `type` field is a discriminator, so more plugin kinds can be added
without a breaking format change. A per-dialect SQL formatter (currently a single
host-owned `ISqlFormatter` baseline, see `src/Core/Formatting/`) is noted as a
roadmap candidate.
