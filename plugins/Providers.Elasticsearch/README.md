# Elasticsearch provider

A database provider that plugs [Elasticsearch](https://www.elastic.co/elasticsearch) into SQL Explorer.
It is **not shipped by default**: it lives under the repo-root `plugins/` folder (not `src/`) and is
staged only in **Debug** builds, so it is directly usable while developing but never part of a
Release/MVP.

Elasticsearch is a document search engine, not a SQL engine, so this provider maps its world onto the
host's (SQL-shaped) contract, following the MongoDB provider's non-SQL pattern (SE-3 / SE-114):

- **Schema tree:** connection root → indices. An index is a leaf (`Table`) node so double-click browses
  it; there is no database layer (an index is globally addressable in the REST path). System indices
  (`.`-prefixed) are flagged. Listed via `GET _cat/indices` with a docs-count/size badge.
- **Queries:** a Kibana Dev-Tools-style console — not SQL. See below.
- **DDL / routines / user management:** not modelled.

There is **no database switcher** — Elasticsearch has no database concept, so the toolbar's database
dropdown stays empty; the index always comes from the request path or the browse target.

---

## Querying in a query tab

A query tab accepts **Kibana Dev-Tools-style** requests: `METHOD path` on the first line, an optional
JSON body after it.

```
GET products/_search
{ "query": { "match": { "name": "widget" } }, "size": 50 }

POST products/_doc
{ "name": "New", "price": 5 }

GET _cat/indices?format=json
DELETE /products
```

- A **search response** (`hits.hits`) is projected into a hybrid grid (see below).
- A **top-level JSON array** (e.g. `_cat/indices?format=json`) becomes a flat, read-only grid.
- Anything else (cluster info, `_count`, `_bulk` results, errors) is shown as one formatted JSON cell.
- A bare index name (e.g. `products`) browses that index. `Run script` runs a single request.

`Explain` maps to `_validate/query?explain=true` for a browse/search (there is no SQL query planner);
a raw console request is just run as-is.

### Tree context menu

Because the provider declares itself **non-SQL** (`IDbProvider.IsSqlBased => false`):

- **Select top 1000** opens a query tab with `GET <index>/_search { "query": { "match_all": {} }, "size": 1000 }`.
- The **SQL commands** submenu is hidden.
- **Drop** an index runs `DELETE /<index>`; **Truncate** runs
  `POST /<index>/_delete_by_query { "query": { "match_all": {} } }` (keeps the mapped index, removes all
  documents) — the same `drop()` vs `deleteMany({})` distinction MongoDB draws.

---

## Browse queries: projection, filters & aggregations

Besides the raw console, the provider understands a **SQL-ish browse** — the shape "Select top 1000"
generates, extended so you can project, filter, and aggregate without hand-writing a `_search` body.

```
SELECT "@timestamp", level FROM logs WHERE {"term":{"level":"Error"}} ORDER BY "@timestamp" DESC LIMIT 100
```

**Projection (`SELECT`).** A field list becomes an ES `_source` filter — only those fields come back
(plus `_id`, always the key column). `SELECT *` (the default) returns everything. Only plain field names
and dot-paths are allowed (`fields.ApplicationContext`, `level.keyword`) — no expressions. This keeps a
wide log index from returning 100s of KB per row.

**`WHERE` takes an Elasticsearch query-DSL object, not a SQL expression.** Paste a raw query object:

```
SELECT * FROM logs
WHERE {"bool":{"filter":[{"term":{"level":"Error"}},{"range":{"@timestamp":{"gte":"now-1d"}}}]}}
```

As a convenience, **simple equality** is translated for you: `level = 'Error'` → `{"term":{"level":"Error"}}`,
and `status != 404` → a negated term. Other SQL operators (`>`, `<`, `IN`, `BETWEEN`, `OR`) are **not**
supported — use a query object for those. An unrecognised `WHERE` returns an error with a worked example.

**Aggregations (`GROUP BY` / `COUNT`).** Server-side aggregation is translated to a `size:0` + `aggs`
search — the result is exact, independent of the row cap:

```
SELECT status, COUNT(*) FROM logs GROUP BY status                 -- terms bucket: status, doc_count
SELECT COUNT(*) FROM logs                                          -- total hit count (never a dump)
SELECT COUNT(*) FROM logs GROUP BY date_histogram("@timestamp", '1m')   -- per-minute counts
SELECT status, AVG(price) FROM orders GROUP BY status             -- terms + avg metric column
```

Supported: a single `GROUP BY` key (a field → `terms`, or `date_histogram("field", 'interval')`), with
`COUNT(*)` (the bucket `doc_count`) and `COUNT`/`SUM`/`AVG`/`MIN`/`MAX(field)` metrics. `LIMIT` sets the
`terms` size. Anything richer (multiple keys, `HAVING`) still needs a raw console `aggs` request — which
the grid now also projects (buckets → rows) instead of showing an empty result. A `COUNT`/`GROUP BY`
shape the provider can't translate returns a clear error rather than silently dumping all documents.

**Time zones (date ranges).** A bare `range` on a date field is read by ES in **UTC**, which silently
shifts a window for a user in another offset (often to 0 hits). The provider injects a `time_zone` into
date range and `date_histogram` clauses that don't already have one — by default the app's local offset,
or the **Time zone** connection field (an offset like `+02:00`, an IANA id, or `UTC` to disable). Numeric
ranges are left untouched.

## Schema: index fields from the mapping

Expanding an index in the tree (and MCP `get_schema`) lists its fields from `GET <index>/_mapping`: the
field name, its ES type (`keyword`/`text`/`date`/`long`/…), and a `.keyword` marker where a keyword
sub-field exists — so you don't have to `SELECT * LIMIT 1` to discover fields or guess `.keyword`. Nested
objects appear as dot-paths. A fully dynamic index with no mapping falls back to `_id` + `_source`.

---

## Result shape / editable grid

Browsing (double-click, or "Select top 1000") projects each hit's `_source` into a grid shaped like
MongoDB's:

| Field kind | Rendering | Editable? |
|---|---|---|
| `_id` | one column, first | No (key, read-only) |
| Scalar `_source` field (string/number/bool) | own column | **Yes** |
| Nested object / array | JSON text in one cell | **Yes** (edited as JSON text) |

The column set is the **union** of every hit's top-level `_source` fields, so heterogeneous indices show
a wide grid with blanks where a document lacks a field. `_id` is tagged key/read-only (like Mongo's
`_id`), so editing a scalar or nested cell, adding a row, or deleting a row flows through
`ApplyChangesAsync`.

### Writeback is **not** transactional

Saving the grid issues **one `_bulk` request** (`update`/`index`/`delete` actions) with
`refresh=wait_for` so the reload observes the change (Elasticsearch is near-real-time). Unlike a SQL or
Mongo save, `_bulk` is **best-effort per item** — there is no rollback. Failed items come back in the
save result's row errors, and the host shows them alongside "N rows saved" rather than implying the
whole batch was reverted. Nested cells are parsed back from JSON text; a modified document is a partial
update (`{ "doc": { … } }`), so untouched fields are preserved.

### Paging beyond the 10 000-hit window

Elasticsearch caps `from + size` at `index.max_result_window` (10 000 by default), so plain offset paging
can't reach a large index's tail. This provider declares `SupportsCursorPaging`, so the host pages it with
an **opaque forward cursor** instead of an offset: each page opens/reuses a **point-in-time (PIT)** snapshot
and continues with **`search_after`** from the previous page's last sort values (a `_shard_doc` tiebreaker
gives a total order so pages never overlap or skip). The cursor is a stateless `{ pit, after }` token; the
PIT lives server-side and auto-expires via `keep_alive`. The trade-off is forward/back navigation only (no
jump-to-arbitrary-page) — the host remembers the cursor that produced each visited page so Prev still works.

For **exporting everything**, `StreamQueryAsync` walks the same PIT + `search_after` loop and streams every
document row-by-row (schema `_id` + full `_source` JSON) with no 10 000 ceiling.

Remaining limit: sorting a browse on an analyzed `text` field can error server-side (use a `keyword`
field) — it surfaces as a normal ES error, not a crash.

---

## Connection fields

| Field | Notes |
|---|---|
| URL | e.g. `https://localhost:9200` |
| Username / Password | HTTP basic auth; password stored in the OS keychain |
| API key | advanced; base64 `Authorization: ApiKey` value — takes precedence over user/pass |
| Verify TLS certificate | advanced; uncheck for a self-signed dev cluster |
| Max field length (chars) | advanced; truncate long text/JSON cells in browse results to N chars (default 2000, `0` = off). Display/MCP only — never the stored data |
| Time zone for date ranges | advanced; injected into bare date `range`/`date_histogram` clauses. Empty = the app's local offset; set an offset (`+02:00`), an IANA id, or `UTC` to disable |

Connection details are stored as an ADO-style `Key=Value;…` string (robust quoting via
`DbConnectionStringBuilder`) and unpacked into `ElasticsearchClientSettings` at connect time.

---

## Development notes

- **Naming:** `plugins/Providers.Elasticsearch`, namespace `SqlExplorer.Providers.Elasticsearch`,
  manifest `id` = `elasticsearch`, `hostApiVersion` = 25.
- **Driver:** `Elastic.Clients.Elasticsearch` (official v8+; NEST is legacy). The provider uses the
  low-level `client.Transport` for raw request passthrough — the console, browse (`_search`), and
  writeback (`_bulk`) all go through it as raw JSON, so the full REST surface is reachable and
  version-tolerant. A client is created per call (no shared state), mirroring Mongo/Redis.
- **Debug wiring:** a Debug-only `ProjectReference` in `src/SqlExplorer.App` forces the build, and a
  Debug-only `ProviderPluginFile` (`PluginId` = `elasticsearch`) in `src/SqlExplorer.Desktop` stages it
  into `plugins/elasticsearch/` beside the executable.
- **Icon:** drop a square `icon.png` in this folder — it is embedded automatically (falls back to a 🔍
  glyph when absent).

## Verifying against a live server

```
docker run --rm -p 9200:9200 -e discovery.type=single-node -e xpack.security.enabled=false \
  docker.elastic.co/elasticsearch/elasticsearch:8.17.0
```

Then connect (URL `http://localhost:9200`), index a few heterogeneous documents, and check: the tree
lists the index with a docs badge; the console runs `_search`/`_cat`/`_count`; double-click browses into
a hybrid grid; editing a scalar and a nested JSON cell, adding, and deleting a row saves via `_bulk` and
the reload reflects it; forcing one bad item (e.g. updating a missing `_id`) surfaces a per-row error
without a crash; Drop and Truncate behave as described.
