← [Plugins overview](../PLUGINS.md)

## Plugin type: `provider`

A provider plugin teaches the host how to talk to one database engine. It
implements a single interface, `IDbProvider`, from the public SDK project
`src/Sdk` (namespace `SqlExplorer.Sdk`). `Sdk` is
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

### Optional capabilities (default to "not supported")

Beyond the required members above, `IDbProvider` and its DTOs carry a set of
**optional** capabilities, each a default-interface member that returns the
"nothing extra" value so a minimal provider ignores them entirely. The
convention is consistent: a `bool` flag defaults to `false`, a nullable return
defaults to `null`, a collection defaults to empty, and the paired builders
`throw NotSupportedException` until the flag turns them on. Examples already in
the SDK: `SupportsActivityMonitor`, `CanManageUsers`, `ParseConnectionString`
(null), `CreateCapabilities` (empty), `GetObjectDefinitionAsync` (null).

#### Non-SQL providers

By default the host assumes a SQL engine: it generates `SELECT`/`DROP`/`TRUNCATE`
text itself (using `ISqlDialect` for quoting/paging) for the tree's convenience
actions. A non-SQL engine — a document store like MongoDB — opts out and owns
that generation instead:

```csharp
// Declare the engine non-SQL. The host then hides its SQL-scaffold "SQL commands"
// submenu and routes node-action generation to the two builders below.
bool IsSqlBased => true;   // return false for a non-SQL engine

// "Select top 1000" / "SQL commands": return your own query text, or null to let
// the host generate SQL. nodePath is the connection-root→node path (as in
// GetChildNodesAsync); columns is the node's column metadata when known, else null.
string? BuildNodeQuery(
    NodeQueryKind kind,
    IReadOnlyList<DbNodeRef> nodePath,
    IReadOnlyList<ResultColumn>? columns) => null;

// DROP/TRUNCATE/ALTER from the tree: return your own statement (previewed, then run
// via ExecuteDdlAsync), or null to fall back to the host's SQL builder.
SqlStatement? BuildAlterStatement(AlterSpec spec) => null;
```

- **`NodeQueryKind`** — `SelectAll, SelectTop, Count, SelectColumns, Insert,
  Update, Delete`. For a non-SQL provider the host only ever asks for `SelectTop`
  (the "Select top 1000" action stays visible); the column-shaped kinds are the
  hidden SQL-commands submenu, so returning `null` for them is fine.
- **`AlterSpec(Action, Database, Schema, Target, IsView, Column, NewName, NewType,
  Nullable)`** with **`AlterAction`** = `DropDatabase, DropSchema, DropTable,
  TruncateTable, AddColumn, DropColumn, RenameColumn`. Return `null` for actions
  you don't handle; the host hides the menu items a non-SQL provider can't service
  (columns, schemas) automatically.
- Both builders are also available to *SQL* providers as a plain override hook
  (return non-null to replace the host's default text for any one action).

MongoDB (`plugins/Providers.MongoDb`) is the reference implementation:
`IsSqlBased => false`, `BuildNodeQuery` returns `db.coll.find({}).limit(1000)`,
and `BuildAlterStatement` returns `db.coll.drop()` / `db.coll.deleteMany({})`,
which its `ExecuteDdlAsync` then runs. Because such text is not database-qualified
(the mongo shell binds `db` to the current database), the host binds the generated
query tab to the node's database via `ConnectionProfile.Database`.

#### Server version (host API v25)

Report the engine's user-facing version and the host shows it next to your
`DisplayName` — `PostgreSQL 16.2` in the status bar and the connect message.
Return `null` (the default) and the host shows the name alone, exactly as before.

```csharp
// Fetched once per connection at connect and cached by the host (the value can't
// change mid-session), so a single cheap call is enough — no per-query round-trip.
Task<string?> GetServerVersionAsync(ConnectionProfile profile, CancellationToken ct)
    => Task.FromResult<string?>(null);
```

The four ADO.NET providers read `DbConnection.ServerVersion` off the already-open
connection (no extra round-trip). Non-SQL providers use their own version command:
MongoDB's `buildInfo`, Redis/DragonflyDB's `INFO server`, Elasticsearch's `GET /`.

### Host API versioning

`ProviderHostApi.Version` (currently `25`) is the contract version. Every
plugin declares the version it was built against in its manifest
(`hostApiVersion`); the loader accepts any version in `[MinimumSupported,
Version]` — additive bumps (new default-interface members, enum values, DTOs)
stay binary-compatible, so an older plugin keeps loading. A breaking change
raises `MinimumSupported`. Check `src/Sdk/ProviderHostApi.cs` for the current
values and its changelog comments before starting a new provider.

## Building a provider plugin, step by step

### 1. Create the project

Add a new project under `src/`, e.g. `src/Providers.MyEngine/`, referencing
**only** `Sdk`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>SqlExplorer.Providers.MyEngine</RootNamespace>
    <!-- Required: emit the full private dependency closure (driver + its own
         dependencies) so the plugin loads correctly in its own ALC. -->
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
  </PropertyGroup>

  <ItemGroup>
    <!-- Private=false keeps Sdk.dll OUT of the plugin's own output
         folder, so the host's copy is used across the ALC boundary and
         IDbProvider keeps a single type identity. -->
    <ProjectReference Include="..\Sdk\SqlExplorer.Sdk.csproj" Private="false" />
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
using SqlExplorer.Sdk;

namespace SqlExplorer.Providers.MyEngine;

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
  "hostApiVersion": 25,
  "entryAssembly": "SqlExplorer.Providers.MyEngine.dll"
}
```

| Field | Meaning |
|---|---|
| `schemaVersion` | Manifest format version (currently `1`). |
| `id` | The engine's permanent identity. There is no host-side enum of engines — `id` is what makes the set of engines open; pick something short, lowercase, and stable, since saved connections reference it. |
| `type` | Plugin kind discriminator. Must be `"provider"` — the only value the loader currently accepts. |
| `name` | Display name (informational; `IDbProvider.DisplayName` is what the UI actually shows). |
| `version` | Your plugin's own version string. |
| `hostApiVersion` | The `ProviderHostApi.Version` you built against (currently 25). The host loads any version in `[MinimumSupported, Version]`, so an additive bump doesn't force a rebuild — but declare the newest whose members you use. |
| `entryAssembly` | Path (relative to the plugin's own folder) to the compiled plugin DLL. |

### 4. Ship it

A plugin is a folder next to the host executable:

```
plugins/
  myengine/
    plugin.json
    SqlExplorer.Providers.MyEngine.dll
    SqlExplorer.Providers.MyEngine.deps.json
    MyEngine.Driver.dll
    ... (rest of the build output)
```

For the first-party providers this copy is automated by an MSBuild target,
`StageProviderPlugins`, in `src/Desktop/SqlExplorer.Desktop.csproj`,
which runs after build and copies each `Providers.*` project's full output
into `<TargetDir>/plugins/<id>/`. A genuinely third-party/out-of-tree plugin
ships the same way manually — just place the built output (including the
`.deps.json`) plus `plugin.json` in `plugins/<id>/` next to the host
executable.

See also: [How discovery and loading work](discovery-and-loading.md).
