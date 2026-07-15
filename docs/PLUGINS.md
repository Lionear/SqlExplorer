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

- **`provider`** — a database engine integration (`IDbProvider`).
  See [Plugin type: `provider`](plugins/provider.md).
- **`tool`** — an action that operates on a connection/node (`IToolPlugin`), e.g.
  the Universal Backup & Restore tool. See [Plugin type: `tool`](plugins/tool.md).

Both plugin types are discovered and loaded the same way — see
[How discovery and loading work](plugins/discovery-and-loading.md).

On top of that, **any** plugin (provider or tool) may opt into cross-cutting
*capabilities* by implementing extra optional interfaces the host discovers with
an `is`-check at load — no manifest change needed:

- **Settings UI** — a persistent settings pane in Settings ▸ Plugins, either a
  host-rendered form (`IPluginSettings`, Route A) or the plugin's own Avalonia
  view (`ICustomPluginSettingsUi`, Route B).
- **Keyboard shortcuts** — global shortcuts the user can rebind
  (`IShortcutContributor`).
- **Route B theming** — how a plugin-supplied Avalonia view (XAML or code)
  picks up the host's dark/light theme.

All of these are documented in
[Optional capabilities](plugins/capabilities.md).

All plugin contracts live in the single public SDK assembly
`SqlExplorer.Sdk` (`src/Sdk`, namespace `SqlExplorer.Sdk.*`) —
the only assembly a plugin references from this repository.

## Guides

| Doc | Covers |
|---|---|
| [`plugins/provider.md`](plugins/provider.md) | `IDbProvider`/`ISqlDialect` contracts, supporting DTOs, host API versioning, building and shipping a provider plugin step by step. |
| [`plugins/tool.md`](plugins/tool.md) | `IToolPlugin` contract, `ToolTarget`, `ToolExecutionContext`, the Route A `ToolField` form, Route B `ICustomToolUi`, the tool manifest. |
| [`plugins/discovery-and-loading.md`](plugins/discovery-and-loading.md) | How the host finds, validates and loads plugins into isolated `AssemblyLoadContext`s at startup, for both plugin types. |
| [`plugins/capabilities.md`](plugins/capabilities.md) | Optional cross-cutting capabilities: settings UI (Route A/B), keyboard shortcuts, referencing Avalonia, authoring a Route B view in XAML, and the theming contract for matching host chrome. |

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
