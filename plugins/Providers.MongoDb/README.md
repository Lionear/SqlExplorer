# MongoDB provider

A database provider that plugs MongoDB into SQL Explorer. It is **not shipped by default**: it lives
under the repo-root `plugins/` folder (not `src/`) and is staged only in **Debug** builds, so it is
directly usable while developing but never part of a Release/MVP.

MongoDB is a document store, not a SQL engine, so this provider maps its world onto the host's
(SQL-shaped) contract:

- **Schema tree:** connection root → databases → collections (a collection is a leaf; Mongo is schemaless).
- **Queries:** you write MongoDB filters/operations, not SQL — see below.
- **DDL / routines / user management:** not modelled.

Pick a **database** in the toolbar before running anything — MongoDB has no implicit default database,
so an unset one gives a clear "No database selected" error.

---

## Querying data

### 1. Browse Table — the filter box

When you browse a collection, the **general filter box** takes a **MongoDB filter document** as JSON
(the same document you'd pass to `find`). Examples:

```json
{ "age": { "$gt": 30 } }
```
```json
{ "status": "active" }
```
```json
{ "name": { "$regex": "^A", "$options": "i" } }
```
```json
{ "$or": [ { "qty": 0 }, { "qty": { "$gt": 100 } } ] }
```

Every MongoDB query operator works here: `$gt`, `$gte`, `$lt`, `$lte`, `$ne`, `$in`, `$nin`, `$exists`,
`$regex`, `$and`, `$or`, `$not`, `$elemMatch`, dotted paths (`"address.city"`), etc.

> The filter box expects a JSON **object** (starts with `{`). If it can't be parsed you get an error
> naming the offending text.

### 2. Sorting

Click a column header to sort. It is translated to a MongoDB sort (`ORDER BY "createdAt" DESC` →
`{ createdAt: -1 }`). Paging (the grid's page size / page number) maps to `limit` / `skip`.

### 3. Per-column inline filters

The small filter input under a column becomes a case-insensitive substring match on that field:
typing `foo` under `name` matches `{ "name": { "$regex": "^.*foo.*$", "$options": "i" } }`. Combined with
the general filter box, the conditions are joined under `$and`.

---

## Tree context menu

Because MongoDB declares itself **non-SQL** (`IDbProvider.IsSqlBased => false`), the host adapts the
tree's right-click menu instead of generating SQL:

- **Select top 1000** — opens a query tab with `db.<collection>.find({}).limit(1000)` (bound to the
  collection's database) and runs it. The provider supplies this text via `BuildNodeQuery`.
- The **SQL commands** submenu (SELECT/INSERT/UPDATE/DELETE scaffolds) is **hidden** — those templates
  don't apply to a document store. Use a query tab with shell syntax instead.
- **Drop** / **Truncate** a collection — the provider supplies `db.<collection>.drop()` and
  `db.<collection>.deleteMany({})` (via `BuildAlterStatement`); the host shows the usual editable
  confirm preview, then runs it. You can narrow a truncate by editing the filter, e.g.
  `db.orders.deleteMany({ "paid": false })`.

## Querying in a query tab

A query tab accepts mongo-shell-style text. Two operations are supported:

**find** — filter, optional projection, and chained `.sort()` / `.skip()` / `.limit()`:

```js
db.orders.find({ status: "shipped" })
db.orders.find({ status: "shipped" }, { _id: 0, total: 1 })
db.orders.find({ paid: true }).sort({ createdAt: -1 }).skip(20).limit(50)
```

**aggregate** — a pipeline array:

```js
db.orders.aggregate([
  { $match: { paid: true } },
  { $group: { _id: "$customer", total: { $sum: "$amount" } } },
  { $sort: { total: -1 } }
])
```

Shortcuts:

- A bare collection name (e.g. `orders`) returns everything, capped by a default limit.
- The database is always the one selected in the toolbar — `db` is bound to it, exactly like the mongo
  shell. The collection comes from the query text.

`Explain` runs the operation through MongoDB's `explain` (queryPlanner) and shows the plan document.

---

## Result shape

Returned documents are flattened into a grid: the columns are the **union of every document's top-level
fields** (order of first appearance, `_id` first). Nested documents and arrays render as relaxed
extended JSON inside a single cell. `_id` is tagged key/read-only and every column is tagged with the
collection as its base table, so the grid is editable (SE-114): cell edits, added and deleted rows are
saved via `ApplyChangesAsync` as native `insertOne`/`updateOne`/`deleteOne` calls — no SQL is generated,
and the save is **not** atomic (sequential ops, no multi-document transaction).

Query results are capped at **1000 documents** by default; add an explicit `.limit(n)` (or a browse page
size) to change it.

---

## Connection fields

| Field | Notes |
|---|---|
| Host | default `localhost` |
| Port | default `27017` |
| Username | optional; when set, enables auth |
| Password | stored in the OS keychain |
| Auth database | advanced; default `admin` |
| Connection URI | advanced; a full `mongodb://` / `mongodb+srv://` URI that **overrides** all fields above (use this for replica sets, SRV, and Atlas) |

---

## Development notes

- **Naming** follows the existing providers (`MySql`, `MsSql`, `Sqlite`): acronyms are single-cap
  PascalCase words, so it's `MongoDb`. The `plugins/` folder drops the `SqlExplorer.` prefix
  (`Providers.MongoDb`), while the csproj/namespace keep the full `SqlExplorer.Providers.MongoDb`.
- **Manifest** (`plugin.json`): `id` = `mongodb`, `type` = `provider`, `hostApiVersion` = 23.
- **Driver:** `MongoDB.Driver`; `CopyLocalLockFileAssemblies` emits its full closure into the plugin
  folder for isolated (ALC) loading, independent of any other plugin's driver version.
- **Debug wiring:** a Debug-only `ProjectReference` in `src/SqlExplorer.App` forces the build, and a
  Debug-only `ProviderPluginFile` (`PluginId` = `mongodb`) in `src/SqlExplorer.Desktop` stages it into
  `plugins/mongodb/` beside the executable.
- **Icon:** drop a square `icon.png` in this folder — it is embedded automatically and shown on the
  provider's connection nodes (falls back to a 🍃 glyph when absent).
