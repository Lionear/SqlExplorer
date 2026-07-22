# Changelog

All notable changes to SQL Explorer are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/), and this project adheres to
[Semantic Versioning](https://semver.org/). Add finished work under `## [Unreleased]`; releasing a
`v<semver>` tag rolls that section into a dated version heading — see
[CONTRIBUTING.md](CONTRIBUTING.md#changelog).

## [Unreleased]

### Added

- **Schema Diff now reads secondary indexes and supports SQLite.** A migration includes the `CREATE INDEX` /
  `DROP INDEX` work it used to silently skip, and SQLite databases can be compared at all (read through
  `sqlite_master` and PRAGMA rather than `information_schema`). Indexes an engine creates behind a primary
  key or unique constraint are left out, so they aren't dropped twice.
- **Copy Table — right-click a table and copy it to another connection and database.** A store-only tool
  plugin. Choose structure + data, structure only or data only, all rows or the first N, whether to keep the
  source's identity/sequence values, and whether to bring the table's indexes and foreign keys along. Either
  *run the copy* — creating and filling the table on the target, with a live checklist that shows which step
  failed if one does — or *open it as a script* on the target to review the SQL first; the tool remembers
  which you used last. Rows are copied in batches, so a large table shows real progress instead of one long
  wait, and indexes and foreign keys are created once the rows are in: a foreign key pointing at a table the
  copy didn't bring along is reported as skipped rather than failing a copy that otherwise landed. Postgres,
  MySQL, SQL Server and SQLite, with source and target on the same engine — copying between different engines
  needs type mapping between dialects and is not attempted.
- **Tools can own their whole dialog** — a tool plugin's own view may now render the run's progress and result
  itself (stepped checklist with per-step detail and progress, and its own footer buttons) instead of the
  generic checklist and action bar. Copy Table is the first tool to use it; every other tool is unchanged.

### Fixed

- **`varchar(max)` columns are no longer copied as `varchar(1)`.** SQL Server reports the MAX variants as a
  length of -1, which was read as "no length" — and a bare `varchar` in a `CREATE TABLE` means one character
  on SQL Server. The copied or recreated column held a single character and every insert failed with "String
  or binary data would be truncated". `varchar(max)`, `nvarchar(max)` and `varbinary(max)` now come across
  intact, and types whose name already fixes their length (`text`, `longtext`, `mediumblob`, …) no longer get
  an invalid length appended.
- **A migration no longer drops a table's auto-numbering.** Recreating a table on the target lost its
  MySQL `AUTO_INCREMENT` or SQL Server `IDENTITY` — the script ran, but the table was subtly wrong and the
  next insert failed or wrote an empty key. Auto-numbered columns are now read and recreated on every
  engine, and a column that gained or lost its auto-numbering is called out in the migration, since no
  engine can switch that in place.
- **Schema Diff no longer reports constraints the engine named itself as changes.** Two SQL Server databases
  with the same schema carry different invented names for the same unique constraint or foreign key
  (`UQ__customer__AB6E6164DF5AECAE`), so every one of them was dropped and recreated — correct, but it
  buried the real changes. Constraints left unmatched by name are now paired up by what they actually
  describe, which also reads a deliberately renamed constraint as no structural change.
- **A script no longer dumps every row of every table — and every result tab gets its own Previous/Next.**
  `SELECT * FROM a; SELECT * FROM b;` returned both tables in full, because paging only ever applied to a
  single SELECT. When a script is nothing but SELECTs, each result tab now pages independently: the tab shows
  which rows you're looking at ("rows 201–400"), Previous/Next move just that tab, and switching tabs moves
  the page bar to where that tab is. A script that mixes SELECTs with other statements can't map tabs to
  statements safely, so it has no page bar — but its SELECTs are still bounded to one page each on the
  server, and the Output panel says so. Statements with their own `TOP`/`LIMIT` and non-SELECTs run exactly
  as written, and the whole thing follows the existing "Page query results" setting.
- **A query that ends in a semicolon can be paged again.** `SELECT * FROM Donations;` failed with "Incorrect
  syntax near the keyword 'ORDER'" (and the equivalent on every other engine), because paging appends its
  `ORDER BY … OFFSET … FETCH` / `LIMIT` *after* the statement — semicolon and all. The terminator is now
  dropped before the page is built, and a stray extra semicolon no longer costs you the page bar either.
- **Schema Diff against MySQL compared the wrong things.** Two MySQL databases diffed as "drop everything,
  recreate everything", because MySQL's schema *is* the database, and foreign keys came out referencing the
  same column several times. Both are corrected, and a MySQL migration now applies cleanly.
- **Schema Diff produced scripts that couldn't run.** A Postgres `serial` column was recreated with a
  `DEFAULT nextval(…)` pointing at a sequence that doesn't exist on the target; `DROP INDEX` was emitted in
  Postgres form for every engine, though MySQL and SQL Server need the table named. Generated migrations for
  Postgres, MySQL, SQL Server and SQLite are now verified end-to-end against live engines.

## [0.4.0] - 2026-07-21

### Fixed

- **Editing a connection no longer wipes its fields** — changing a connection's AI access, read-only or
  other settings could reset host / port / database / username to the provider defaults and drop the saved
  password. Editing now preserves the stored values, and setting AI access from the tree no longer
  round-trips (and so can't clear) the password.
- **Startup restores the tab you left on** — with "Restore tabs on startup", the previously *selected* tab is
  now reselected instead of always landing on the last one in the row.
- Small UI polish: the results **Export** action now reads as a button (not a text link); and the **AI activity**
  panel's toggle only appears while the MCP server is running (it live-appears/disappears as you start/stop the
  server).
- **Plugin Store "Update All" now clears its badges** — updating every plugin at once staged the updates
  correctly but the rows kept showing "update available" as if nothing happened; they now show as staged
  (restart required), matching the per-plugin Update button.
- **Staged plugin updates apply more reliably on restart** — a blocked rollback-backup folder could leave the
  old plugin version in place across every restart. The swap now falls back to replacing the current copy so
  the update still applies, and a swap that genuinely can't complete is logged instead of failing silently.
- **"What's new" notes no longer overflow the window** — long release notes in the app- and plugin-update
  changelog dialogs wrapped off the right edge and ran past the bottom; the text now wraps to the window width
  and scrolls vertically.
- The plugin-update notification now uses the Lucide icon set (a crisp refresh / download glyph) instead of a
  Unicode symbol that could render as a missing-glyph box on some systems.

### Added

- **Allow multiple instances** (Settings → General) — off by default, launching the app again brings the running
  window to the front (the single-instance behaviour). Turn it on to let each launch open its own independent
  window — handy for keeping two databases, or dev and prod, side by side. Takes effect on the next launch.
- **Script table data as INSERT** — right-click a table → *SQL commands ▸ INSERT (with data)* to generate real
  `INSERT` statements from the table's rows (Top 100, Top 1000, or all rows) into a new query tab, ready to run on
  another connection. Unlike the existing INSERT scaffold (which uses `:name` placeholders), this writes the actual
  values — dialect-correct for booleans, binary and dates — and never auto-runs.
- **Schema Diff tool** — a new first-party tool compares this database against a second one you pick — another
  connection and one of its databases — and generates the migration (an ALTER script that would make this one
  match the other), opening it in a new query tab on this connection/database so you review and run it in the
  normal editor. It diffs tables, columns (type / nullability / default), primary keys, unique constraints and
  foreign keys, and produces dialect-correct DDL for Postgres, MySQL and SQL Server. Reads via
  `information_schema`, so the picker offers same-provider connections only; SQLite and cross-engine diffs are
  not covered yet. Built on new plugin-SDK seams — `ToolFieldType.ConnectionPicker` and `DatabasePicker` plus
  `IToolHost.ListConnections()` / `ListDatabasesAsync()` / `OpenConnection()` / `OpenQueryEditor()` — so any
  tool can take a second connection and database and hand generated SQL to a query tab. Installs from the
  Plugin Store (not bundled with the app).
- **Icons in SQL completion** — each suggestion in the code-completion popup now carries an icon for its kind
  (table, column, function, foreign-key join condition, keyword), reusing the shared Lucide glyphs from the
  schema tree so a table reads the same in both places. The type / signature / join-condition detail alongside
  each item is unchanged.
- **Containers are tagged for Kontena** — containers created by the Local Containers plugin now carry
  `kontena.managed=true` / `kontena.source=sqlexplorer` labels (in both the compose file and the `docker run`
  snippet), so the Kontena desktop app can recognise them as SQL-Explorer-managed and leave them alone.
- **Query Log shows why it's empty** — when logging is off (or only one source is enabled), the Query Log
  window now shows a banner explaining it, instead of just an empty list.
- **Paged query results** — running a single `SELECT` with no `TOP`/`LIMIT` of its own now shows the results one
  page at a time with Previous/Next (DataGrip/DBeaver-style, default 200 rows/page), so a stray
  `SELECT * FROM big_table` doesn't pull the whole table at once; the row-range indicator shows which rows
  you're viewing. Queries with their own `TOP`/`LIMIT`, other statement types and multi-statement scripts run
  unchanged. Toggle and page size live under Settings → Query.
- **Scope-aware SQL completion** — code completion now understands query structure instead of scanning for
  `FROM`/`JOIN` with a regex. It resolves aliases through CTEs (`WITH x AS (…)`) and derived tables
  (`(SELECT …) d`), suggests the columns of the sources actually in scope, offers CTE names alongside real
  tables after `FROM`/`JOIN`, and never suggests from another statement in the editor. Expression positions
  (SELECT list, WHERE, …) now also suggest the engine's **built-in functions** with their signature —
  Postgres, MySQL, SQL Server and SQLite each ship their own catalogue (plugins declare theirs via the new
  `ISqlDialect.Functions`). And right after `JOIN … ON`, it offers the **foreign-key join condition** between
  the tables in scope (e.g. `o.user_id = u.id`) as the top suggestion.
- **Service auto-registration for plugins and the host** (plugin SDK) — classes can opt into dependency
  injection by implementing a lifetime marker (`ISingletonService` / `ITransientService` / `IScopedService`)
  instead of being wired up by hand. Extensions that declare the new `services` capability get their own
  services registered and resolvable via `IPluginRuntimeContext.Services`, scoped so a plugin can add
  services but never replace or read the app's. Plugin host API is now **v4**; extensions built for earlier
  versions keep loading.
- **Panel plugins can supply a toggle icon** (plugin SDK) — `IPanelPlugin.Icon` lets an extension's docked
  panel show its own glyph on the bottom bar instead of the generic default. The Local Containers panel now
  uses a container icon.
- **Provider-declared container recipes** (plugin SDK) — a database provider can declare how to spin up an
  empty local container matching its engine (`IDbProvider.ContainerRecipe`: image, port, data path, and the
  environment/command that carry credentials). The Local Containers plugin reads every installed provider's
  recipe through a new read-only `providers` capability, so a third-party engine becomes containerisable with
  no change to the host. Every first-party engine now ships its own recipe, so the plugin is purely
  provider-driven: the recipe travels with the engine and is the single source of truth.

### Changed

- **Double-click a result cell to open its value in a window** — long text and JSON are shown pretty-printed in
  a standalone, resizable window you can copy from, and several can be open side by side. This replaces the
  always-on strips below the grid (the click-to-view cell value and the selection count/sum/avg summary), which
  are gone.
- The connection tree's **AI access** submenu now marks the active level (None / Read-only / Read-write)
  with a check, so the current setting is visible at a glance instead of having to remember it.
- **Refreshed icon set** — the schema tree, tabs, toolbars and Settings now use a consistent
  [Lucide](https://lucide.dev)-based line-icon set, drawn as crisp vectors that tint with the theme (no
  icon font, no bundled raster assets). The AI-activity panel gets its own icon.
- **New local SQL Server containers use the 2025 image** — the Local Containers "create" flow now defaults
  to `mcr.microsoft.com/mssql/server:2025-latest` (was 2022). Every first-party provider (PostgreSQL, MySQL,
  SQL Server, MongoDB, Redis, DragonflyDB, Elasticsearch) now declares its own container recipe, so the
  recipe travels with the engine instead of being hardcoded in the Local Containers plugin.

## [0.3.0] - 2026-07-19

### Added

- **Open & save queries as `.sql` files** — `Ctrl+O` to open (or drag a `.sql` file onto the window),
  `Ctrl+S` to save, plus Save As and a File ▸ Recent menu. Tabs show a `●` dirty marker and remember
  their file across sessions, and closing a tab or the app offers to save unsaved changes — a
  preference in Settings ▸ Startup turns that prompt off. Saving pending grid-row edits back to the
  database moved from `Ctrl+S` to **`Ctrl+Shift+S`**.
- Configurable **update-check interval** — choose how often the app checks for a new release.
- A shared **"Copied" confirmation** for copy actions, shown bottom-centre.
- **SQL formatting options** in Settings — keyword casing (UPPERCASE / lowercase / preserve) and
  indent width.
- **Proactive plugin-update notifications** — an ambient top-bar badge and a persistent, actionable
  notification when compatible updates are available for your installed plugins, without opening the
  Plugin Store, plus a **per-plugin changelog** (from the notification or any updatable Store row).
  An opt-in **Auto-apply on restart** policy can stage compatible, non-pinned updates silently, and
  updates that need a newer app are shown ("Update app…") instead of hidden. Off / Notify / Auto in
  Settings ▸ Plugins.
- **Plugin Store "Updates" section** — installed plugins with an available update are grouped at the
  top of the Installed tab under "Updates", so you no longer have to hunt for which ones can update
  (they no longer also appear in the list below).
- **`extension` plugin type** — plugins are no longer only one-shot providers and tools: an
  `extension` plugin can run as a long-lived subsystem that contributes its own bottom panel,
  background work, Tools-menu items and managed connections, each behind a per-capability consent
  shown when you install it.
- **AI can create connections over MCP** — with the MCP server on and the new "Let the AI create
  connections" setting enabled (off by default), an AI client can list the available providers and
  create or delete database connections. Fail-closed: creation is refused unless you opt in, only
  loopback hosts are allowed until you add more, persistent connections are capped at read-write, and
  every create/delete is audited. New connections land in an "MCP" folder; temporary ones are
  session-only and cleared when the app closes.
- **AI-activity panel** — a bottom tool panel (toggled from the status bar) showing what an AI does
  over MCP: each call, the connection, and whether it was allowed or denied.
- **AI access on the connection tree** — connections carry "AI" and "Temporary" badges, and a
  right-click **AI access** submenu sets the level (None / Read-only / Read-write) or excludes a
  connection from the AI without opening the Connection Manager.
- **One bottom panel at a time** — a Settings ▸ Appearance toggle (on by default) so opening a bottom
  panel (Output, Containers, AI activity) closes the others instead of stacking them.
- **Search the Settings** — a search box above the category rail filters categories by name or by the
  settings inside them (e.g. "token" surfaces MCP, "theme" surfaces Appearance).

### Changed

- The **Plugin Store type filter** is now a dropdown instead of a row of chips — more compact and it
  scales as new plugin types are added.

- Release notes and the in-app updater now read the curated `CHANGELOG.md` instead of the raw git
  log, so each release describes what changed for you rather than listing commit subjects.
- **App and plugin update checks now log to the Output panel** (channel + result), so you can see when
  a check runs and what it found.
- The **SQL formatter** now indents SELECT column lists, parenthesised subqueries and JOIN/AND/OR
  conditions, instead of only breaking clauses onto their own lines. **SQL Server** gets a dedicated
  T-SQL formatter (Microsoft's official ScriptDom parser); the other engines use the improved generic
  engine.

### Fixed

- The app updater no longer offers a **lower version on another channel as an "update"** — switching
  channels only surfaces a build with an equal-or-higher core version, so a `0.3.0` build is never
  prompted to "update" to `0.2.0-preview`.
- Nightly and preview builds now stamp the version from the branch they are built from, so the About
  dialog no longer shows a mismatched build version.
- The bottom tool panels (Output, Containers, AI activity) can now be **resized** by dragging their
  top edge — previously dragging did nothing, or left an empty band above the status bar.
- **"Restart app"** (and the in-app updater's relaunch) now reliably brings the app back: the new
  instance no longer connects to the still-closing old one, defers to it and exits — which could leave
  no window at all. It also relaunches correctly when the app runs through the dotnet muxer.

## [0.2.0] - 2026-07-18

### Added

- **In-app updater** with release channels (Stable / Preview / Nightly): in-place update with
  rollback, a periodic update check, and an inline update bar that downloads and installs from within
  the app.
- **About / diagnostics dialog**: system information, installed-plugin list, host API contracts, and
  copy-to-clipboard.
- **Third-party notices** generated from the NuGet dependency closure and shipped as
  `THIRD-PARTY-NOTICES.md`.
- **Windows installer** (per-user, no admin) alongside the versioned `.zip`; artifact names now carry
  the version.
- **Elasticsearch query sweep** for exploring an index without hand-writing every query.
- Show the connected engine's **server version** in the UI.

### Changed

- Plugins are matched against a host-API **version range** and tracked by build version; the Plugin
  Store judges MCP plugins against the MCP host-API window.
- Plugin sources moved into Settings, with an HTTPS requirement for sources and downloads.

### Security

- Redact secrets from MCP query results.
- Hardened the build pipeline against command injection and unverified external tools.

## [0.1.0] - 2026-07-17

Initial baseline — the first working SQL Explorer.

### Added

- **Cross-platform desktop app** (Windows / Linux / macOS) built on Avalonia, with a runtime
  NL ⇄ EN language switch.
- **Providers as isolated plugins** loaded from `plugins/` — the host ships no database drivers.
  Bundled engines: PostgreSQL, MySQL / MariaDB, SQL Server and SQLite, plus MongoDB, Redis and
  DragonflyDB through a non-SQL provider seam.
- **Plugin Store**: browse, install, update and version-pin provider and tool plugins from
  configurable sources.
- **Schema tree** (server → database → schema → tables / views / columns), extended with procedures,
  functions and triggers, DDL scripting and per-folder object counts.
- **Query tab**: SQL editor with syntax highlighting, schema-aware completion (Ctrl+Space),
  quick-open object search (Ctrl+K), execute-selection / at-cursor, multiple result sets, EXPLAIN and
  cancellable queries.
- **Browse tab**: page through a table without writing SQL — paging, a WHERE filter and column-header
  sort.
- **Editable result grid with a reviewable save flow**: edit, add or delete rows, preview the
  generated INSERT / UPDATE / DELETE, and run them in a single transaction (enabled only for
  single-table results with a primary key).
- **Import / export**: CSV / JSON / SQL export and CSV import; a cell value viewer with JSON
  pretty-print; selection aggregation (count / sum / avg / min / max).
- **Connection manager** with nested folders, drag-to-reorder, a per-connection colour flag and a
  read-only safe mode; **secure credential storage** in the OS keychain with an optional master
  password.
- **Query history and logging**: persistent, searchable history with re-run, an opt-in query log, and
  an Output panel for feedback and errors.
- **Universal Backup & Restore** tool with per-object schema / data selection and a streaming `.lbak`
  format for large objects.
- **SQL Server admin tools**: login / user management and provider-supplied advanced connection and
  properties UIs.
- **Host-owned MCP server** exposing read query access to AI assistants.
- **Configurable keyboard shortcuts** with a plugin shortcut SDK.
- **Multi-platform build pipeline** (Windows installer + zip, Linux AppImage, macOS DMG) publishing
  rolling nightly and preview releases.

[Unreleased]: https://github.com/Lionear/SqlExplorer/compare/v0.4.0...HEAD
[0.4.0]: https://github.com/Lionear/SqlExplorer/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/Lionear/SqlExplorer/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/Lionear/SqlExplorer/releases/tag/v0.2.0
