# Changelog

All notable changes to SQL Explorer are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/), and this project adheres to
[Semantic Versioning](https://semver.org/). Add finished work under `## [Unreleased]`; releasing a
`v<semver>` tag rolls that section into a dated version heading — see
[CONTRIBUTING.md](CONTRIBUTING.md#changelog).

## [Unreleased]

### Added

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

### Changed

- Release notes and the in-app updater now read the curated `CHANGELOG.md` instead of the raw git
  log, so each release describes what changed for you rather than listing commit subjects.
- **App and plugin update checks now log to the Output panel** (channel + result), so you can see when
  a check runs and what it found.
- The **SQL formatter** now indents SELECT column lists, parenthesised subqueries and JOIN/AND/OR
  conditions, instead of only breaking clauses onto their own lines. **SQL Server** gets a dedicated
  T-SQL formatter (Microsoft's official ScriptDom parser); the other engines use the improved generic
  engine.

### Fixed

- Nightly and preview builds now stamp the version from the branch they are built from, so the About
  dialog no longer shows a mismatched build version.

<!--
Add bullets under the section that fits, in this order (omit the empty ones):
### Added      — new features
### Changed    — changes in existing behaviour
### Fixed      — bug fixes
### Security   — vulnerability or hardening work
### Removed    — removed features
-->

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

[Unreleased]: https://github.com/Lionear/SqlExplorer/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/Lionear/SqlExplorer/releases/tag/v0.2.0
