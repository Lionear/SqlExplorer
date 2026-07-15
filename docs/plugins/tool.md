← [Plugins overview](../PLUGINS.md)

## Plugin type: `tool`

A **tool** contributes an action rather than a database engine: it shows up as a
menu item on the schema tree, collects some inputs in a dialog, and runs against
the selected connection/node. The Universal Backup & Restore feature is itself a
tool plugin. Tools reference the same `SqlExplorer.Sdk` assembly as
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
the host's copy) — see [Referencing Avalonia for a Route B view](capabilities.md#referencing-avalonia-for-a-route-b-view).

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
  "entryAssembly": "SqlExplorer.Tools.UniversalBackup.dll"
}
```

See also: [How discovery and loading work](discovery-and-loading.md).
