# Redis provider

A database provider that plugs Redis into SQL Explorer. It is **not shipped by default**: it lives
under the repo-root `plugins/` folder (not `src/`) and is staged only in **Debug** builds, so it is
directly usable while developing but never part of a Release/MVP.

Redis is a typed key-value store, not a SQL engine, so this provider maps its world onto the host's
(SQL-shaped) contract, following the MongoDB provider's pattern (SE-114):

- **Schema tree:** connection root → DB indices (Redis' numbered logical databases, `0`–`15` by
  default) → keys, grouped **one level deep** by a `:`-prefix (the common Redis naming convention,
  e.g. `orders:1042` groups under `orders`). Only non-empty DBs are shown (DB `0` always is, even
  empty, so a fresh server isn't a dead-looking tree). Capped at 2000 keys per tree node — a known
  MVP limitation on very large keyspaces (SCAN-based via `IServer.Keys`, never `KEYS *`).
- **Queries:** typed Redis command lines, not SQL — see below.
- **DDL / routines / user management:** not modelled.

Pick a **database index** in the toolbar before browsing — Redis has 16 logical databases per
connection by default (`CONFIG GET databases` is consulted for a non-default count), and the switcher
sets which one a command runs against.

---

## Querying in a query tab

A query tab accepts one Redis command per line:

```
GET mykey
HGETALL myhash
SET mykey "hello world"
LPUSH mylist a b c
LRANGE mylist 0 -1
ZRANGE myzset 0 -1 WITHSCORES
```

Shortcuts:

- A bare key name (e.g. `mykey`) browses that key directly (a type-agnostic peek), same convenience as
  Mongo's bare collection name. A handful of genuinely zero-argument commands (`PING`, `DBSIZE`,
  `FLUSHDB`, `FLUSHALL`) are recognised as commands, not key names, even without arguments.
- Values with spaces need quotes: `SET mykey "hello world"`.
- Multi-line input (`Run script`) executes one command per line and shows one result grid per line.

`Explain` has no real Redis equivalent (there is no query planner — every command is a direct,
O(1)/O(log N) key operation); it instead reports the key's `TYPE`/`OBJECT ENCODING` as the closest
useful information.

### Result shape by command

- Scalar reply (`GET`, `SET`, `INCR`, `TYPE`, …) → a single `result` cell.
- `HGETALL` → a `field`/`value` grid (one row per hash field).
- Any `Z*` command with `WITHSCORES` → a `member`/`score` grid.
- Any other array reply (`LRANGE`, `SMEMBERS`, `KEYS`, …) → a single `value` column, one row per element.

---

## Tree context menu

Because Redis declares itself **non-SQL** (`IDbProvider.IsSqlBased => false`), the host adapts the
tree's right-click menu instead of generating SQL:

- **Select top 1000** — opens a query tab with the command matching the key's live `TYPE`
  (`GET`/`HGETALL`/`LRANGE ... 0 999`/`SMEMBERS`/`ZRANGE ... WITHSCORES`) and runs it. Unlike Mongo (one
  collection is always a document find), a Redis key's browse command genuinely depends on a live
  lookup — see [`BuildNodeQuery` and host API v23](#host-api-v23-buildnodequery-gained-a-connectionprofile)
  below.
- The **SQL commands** submenu (SELECT/INSERT/UPDATE/DELETE scaffolds) is **hidden** — those templates
  don't apply to a typed key store. Use a query tab with command syntax instead.
- **Drop** / **Truncate** a key — both run `DEL "key"`. Redis has no notion of "clear a key but keep it
  present" distinct from "the key doesn't exist" (deleting every element of a collection auto-deletes
  the key), so — unlike Mongo's real `drop()`/`deleteMany({})` distinction — Drop and Truncate are
  deliberately the same operation here. This is a conscious deviation from the original plan (which
  proposed `FLUSHDB` for "truncate"): `FLUSHDB` wipes the *entire selected database*, but Truncate fires
  per-key in the host UI (`TreeNodeViewModel.CanTruncate`), so mapping it to `FLUSHDB` would silently
  wipe far more than the one key the user right-clicked.

---

## Result shape / editable grid

Browsing (double-click, or "Select top 1000") projects a key's value into a grid shaped by its type:

| Type | Columns | Editable? |
|---|---|---|
| String | `value` (one row) | No |
| Hash | `field`, `value` (one row per field) | **Yes** — edit/delete existing fields |
| List | `value` (one row per element) | No |
| Set | `member` (one row per element) | No |
| Sorted set | `member`, `score` | No |

Only **Hash** keys are editable, and only partially: `field` is tagged key/read-only (like Mongo's
`_id`), so an existing field's `value` can be edited or the row deleted via `ApplyChangesAsync`
(`HSET`/`HDEL` inside one `MULTI`/`EXEC` — genuinely atomic, unlike Mongo's sequential ops, since Redis
transactions don't need a replica set). **Adding a new field via "Add Row" is not supported** — there is
no cell to type the new field's name into once `field` is read-only, and the current SDK has no
per-row "editable only on insert" concept to allow it just for new rows without also allowing a
confusing, silently-ignored rename of an existing field. A "New Row" save attempt reports a clear row
error instead of silently doing nothing; add a hash field via the console (`HSET key field value`)
instead. String/List/Set/ZSet stay read-only in the grid for the same underlying reason SE-114 flagged
as out of scope for this pass — mutate them via console commands (`SET`/`LPUSH`/`SADD`/`ZADD`/…).

List/Set/ZSet browsing itself has known MVP limitations: Sets have no native offset/limit paging
(`SMEMBERS` always reads the whole set — fine for small sets, not for huge ones); Lists/ZSets do page
via `LRANGE`/`ZRANGE`'s own start/stop.

---

## Connection fields

| Field | Notes |
|---|---|
| Host | default `localhost` |
| Port | default `6379` |
| Password | stored in the OS keychain |
| Database index | advanced; default `0` — the connection's fallback DB, overridden by the toolbar switcher when set |
| Use TLS | advanced |

---

## Development notes

- **Naming** follows the existing providers: `plugins/Providers.Redis`, namespace
  `SqlExplorer.Providers.Redis`, manifest `id` = `redis`.
- **Manifest** (`plugin.json`): `id` = `redis`, `type` = `provider`, `hostApiVersion` = 23.
- **Driver:** `StackExchange.Redis`; `CopyLocalLockFileAssemblies` emits its closure into the plugin
  folder for isolated (ALC) loading. A `ConnectionMultiplexer` is opened per call (`ConnectAsync`/
  `Dispose`d at the end of each method), mirroring Mongo's per-call `MongoClient` — no shared/cached
  connection state in the provider.
- **Debug wiring:** a Debug-only `ProjectReference` in `src/SqlExplorer.App` forces the build, and a
  Debug-only `ProviderPluginFile` (`PluginId` = `redis`) in `src/SqlExplorer.Desktop` stages it into
  `plugins/redis/` beside the executable.
- **Icon:** drop a square `icon.png` in this folder — it is embedded automatically and shown on the
  provider's connection nodes (falls back to a 🟥 glyph when absent).

### Host API v23: `BuildNodeQuery` gained a `ConnectionProfile`

SE-20's original design note flagged an open question: does `BuildNodeQuery` do its own live `TYPE`
lookup (an async-in-a-sync-method problem, since the interface method isn't `async`), or does the tree
pass the type through from `GetChildNodesAsync`, which already knows it? Neither of `DbNodeRef` (just
`Kind`+`Name`, and `Name` **is** the label the tree displays — it can't be repurposed to smuggle extra
data without corrupting the UI) nor the old `BuildNodeQuery` signature (no `ConnectionProfile` at all)
had anywhere to carry that information.

Resolution: `IDbProvider.BuildNodeQuery` gained a `ConnectionProfile profile` parameter (host API v23).
`StackExchange.Redis`'s `IDatabase` exposes true synchronous methods (not just `Task`-wrapped ones), so
`RedisProvider.BuildNodeQuery` opens a short-lived connection and calls `db.KeyType(key)` synchronously
— a real, if slightly heavier-than-ideal, network round-trip on a user-initiated menu click, not a
hot-path call. This is purely additive to the *host's* call site (it always had a connection profile
available there) and a one-line signature change for the only other override (`MongoDbProvider`, which
ignores the new parameter — its browse text never depended on a live call).

Double-click browse (`BrowseTableCommand` → `LoadPageAsync`) is unaffected by any of this: it never
calls `BuildNodeQuery` at all. It builds a pseudo-SQL `SELECT * FROM "key" LIMIT n OFFSET m` via
`RedisDialect.Paginate` and runs it through `ExecuteQueryAsync`, which is already `async` and does its
own live `TYPE` lookup with no signature change needed — the same convention MongoDB already uses for
its own browse path.
