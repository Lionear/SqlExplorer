# Shared.Schema

Reading a table's shape out of a live database is the same job for every tool that has to recreate one
somewhere else, and it is engine-specific in the same four ways each time: which catalogue holds the
columns, how a type's length and precision are spelled, where an engine hides its auto-numbering flag, and
how identifiers are quoted. Schema Diff and Copy Table both needed all of it, and briefly had two
half-answers — Copy Table could read identity columns but not SQLite; Schema Diff could read SQLite and
secondary indexes but lost `AUTO_INCREMENT`. This folder is the one answer.

It is **not a project**. The files are source-linked into each plugin that needs them:

```xml
<Compile Include="..\Shared.Schema\*.cs" />
```

so each plugin compiles its own copy into its own assembly. That is deliberate: plugins load into isolated
`AssemblyLoadContext`s and only `SqlExplorer.Sdk` and `Avalonia*` are shared with the host, so a separate
`Shared.Schema.dll` would either have to be duplicated per plugin folder anyway or be promoted into the
SDK — and the SDK is a public contract we would then owe compatibility to. These types never cross the ALC
boundary, so two identical copies are harmless.

## What's here

| File | |
| --- | --- |
| `SchemaModel.cs` | The provider-agnostic snapshot: tables, columns, primary key, uniques, indexes, foreign keys. |
| `SchemaReader.cs` | Picks the catalogue an engine actually has, and reports which engines can be read. |
| `InformationSchemaReader.cs` | Postgres / MySQL / SQL Server, via ANSI `information_schema` plus per-engine index and identity queries. |
| `SqliteSchemaReader.cs` | SQLite, via `sqlite_master` + the PRAGMA table-valued functions. |
| `SqlDialect.cs` | Per-engine quoting, column rendering (including identity) and the DDL whose shape genuinely diverges. |
| `SqlRows.cs` | Case-insensitive view over a `QueryResult`, so readers address catalogue cells by name. |
| `SchemaChange.cs` | The change vocabulary a consumer renders: create/drop table, add/alter/drop column, keys, indexes, foreign keys. |
| `AlterScriptWriter.cs` | Renders those changes into runnable DDL for one dialect. Schema Diff feeds it a diff; Generate Scripts feeds it a whole schema as creates. |

The mapping half of each reader is pure (`BuildTables`) and unit-tested without a database, in
`tests/SqlExplorer.Tools.SchemaDiff.Tests`.

## Scope

Same-engine only. Both consumers read and render one side with one dialect; a cross-engine read needs type
mapping between dialects and is deliberately not attempted (SE-186 §3 / SE-192 §3). Views, routines,
triggers and sequences are out of the model by design.
