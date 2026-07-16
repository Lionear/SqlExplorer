# DragonflyDB provider

A database provider that plugs [DragonflyDB](https://www.dragonflydb.io/) into SQL Explorer. It is
**not shipped by default**: it lives under the repo-root `plugins/` folder (not `src/`) and is staged
only in **Debug** builds, so it is directly usable while developing but never part of a Release/MVP.

DragonflyDB is **RESP2/RESP3-protocol compatible with Redis** — a drop-in replacement with the same
command set, the same `MULTI`/`EXEC`, the same `SCAN` cursor model, and the same `StackExchange.Redis`
client. This provider is therefore a **near-verbatim copy of the Redis provider** (see
`plugins/Providers.Redis`); everything in that provider's README about the schema tree, query tab,
context menu, and editable grid applies here unchanged. This document only covers what differs.

## What differs from Redis

The RESP contract is identical, so the shared code paths behave the same. The genuine Dragonfly
differences and how this provider handles them:

- **Database count.** The number of logical databases is server-config, never hardcoded. Redis exposes
  it as `CONFIG GET databases`; Dragonfly's is the `--dbnum` flag, read as `CONFIG GET dbnum`. The
  provider tries `databases` first, then `dbnum`, then falls back to the shared default of 16
  (`GetDatabaseCountAsync`). All probes are best-effort — `CONFIG GET` is disabled on some managed
  deployments.
- **`OBJECT ENCODING` is unsupported.** `Explain` reports a key's `TYPE`/`OBJECT ENCODING` on Redis;
  on Dragonfly the `OBJECT ENCODING` call fails and is caught, so `Explain` shows the `TYPE` alone.
  No other code path uses `OBJECT`.
- **`FUNCTION`/`FCALL` are unsupported**, `SCRIPT FLUSH` lacks the `ASYNC`/`SYNC` modifiers, and
  keyspace notifications (`CONFIG SET notify-keyspace-events`) fail. None of these are used by the
  provider. If a user types one into the console, Dragonfly returns its own distinguishable error
  string, which flows straight to the grid/status — no crash, no silent no-op.
- **`MULTI`/`EXEC`** runs on Dragonfly's multi-threaded shard architecture rather than Redis'
  single-thread loop, but the client-visible guarantee is the same all-or-nothing transaction for the
  single key touched per grid row — so the Hash writeback path needs no change.

Everything else — connection fields (default port `6379`), the `:`-prefix tree grouping, the typed
command console, the Hash-only editable grid — is identical to the Redis provider.

## Development notes

- **Naming:** `plugins/Providers.DragonflyDb`, namespace `SqlExplorer.Providers.DragonflyDb`, manifest
  `id` = `dragonflydb`.
- **Manifest** (`plugin.json`): `id` = `dragonflydb`, `type` = `provider`, `hostApiVersion` = 23.
- **Driver:** `StackExchange.Redis` (same package/version as Providers.Redis); `CopyLocalLockFileAssemblies`
  emits its closure into the plugin folder for isolated (ALC) loading. A `ConnectionMultiplexer` is
  opened per call, mirroring the Redis provider — no shared/cached connection state.
- **Debug wiring:** a Debug-only `ProjectReference` in `src/SqlExplorer.App` forces the build, and a
  Debug-only `ProviderPluginFile` (`PluginId` = `dragonflydb`) in `src/SqlExplorer.Desktop` stages it
  into `plugins/dragonflydb/` beside the executable.
- **Icon:** drop a square `icon.png` in this folder — it is embedded automatically and shown on the
  provider's connection nodes (falls back to a 🐉 glyph when absent).

## Verifying against a live server

```
docker run --rm -p 6379:6379 docker.dragonflydb.io/dragonflydb/dragonfly
```

Then connect (host `localhost`, port `6379`), seed with `SET`/`HSET`/`RPUSH`/`SADD`/`ZADD`, and check:
the tree shows DBs with key type/TTL detail; the console runs typed commands; `FCALL`/`FUNCTION LIST`
return a clean Dragonfly error (not a crash); a Hash key's grid edits/deletes a field via `MULTI`/`EXEC`;
a read-only connection blocks Save. Cross-verify the same sequence against `redis:latest` to confirm no
Dragonfly- or Redis-specific assumption leaked in.
