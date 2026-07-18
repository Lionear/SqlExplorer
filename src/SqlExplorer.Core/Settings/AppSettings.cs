using SqlExplorer.Core.Update;
using SqlExplorer.Sdk.Formatting;

namespace SqlExplorer.Core.Settings;

/// <summary>Preferred colour scheme; <see cref="System"/> follows the OS setting live.</summary>
public enum AppTheme
{
    System,
    Light,
    Dark
}

/// <summary>What the proactive plugin-update check does when compatible updates are found (SE-138).
/// <see cref="Off"/> = no background check; <see cref="Notify"/> = badge + toast; <see cref="Auto"/> =
/// stage compatible, non-pinned updates for the next restart (phase 3).</summary>
public enum PluginUpdatePolicy
{
    Off,
    Notify,
    Auto
}

/// <summary>
/// User-scoped UI preferences that survive across runs (window geometry, layout, theme,
/// language, editor/query preferences). Null members mean "never set" → the view falls back to
/// its design-time default. Deliberately no secrets and no connection data — that lives in their
/// own stores.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Restored window width in device-independent pixels.</summary>
    public double? WindowWidth { get; set; }

    /// <summary>Restored window height in device-independent pixels.</summary>
    public double? WindowHeight { get; set; }

    /// <summary>Restored window left position; paired with <see cref="WindowY"/>.</summary>
    public double? WindowX { get; set; }

    /// <summary>Restored window top position; paired with <see cref="WindowX"/>.</summary>
    public double? WindowY { get; set; }

    /// <summary>Whether the window was maximized on last close.</summary>
    public bool WindowMaximized { get; set; }

    /// <summary>Restored width of the connection sidebar column, in pixels.</summary>
    public double? SidebarWidth { get; set; }

    /// <summary>Restored height of the Output tool-window (Edge.Bottom), in pixels. Null = default.</summary>
    public double? OutputHeight { get; set; }

    /// <summary>Restored width of the History tool-window (Edge.Right), in pixels. Null = default.</summary>
    public double? HistoryWidth { get; set; }

    /// <summary>Two-letter culture code (e.g. "nl", "en"); null = follow the OS/thread default.</summary>
    public string? Language { get; set; }

    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>SQL editor font size in points; null = AvaloniaEdit's design-time default.</summary>
    public double? EditorFontSize { get; set; }

    public bool EditorWordWrap { get; set; }

    /// <summary>Keyword casing the SQL formatter applies (SE-148). Default UPPERCASE.</summary>
    public KeywordCasing FormatKeywordCasing { get; set; } = KeywordCasing.Upper;

    /// <summary>Indent width (spaces) the SQL formatter uses (SE-148). Default 4.</summary>
    public int FormatIndentSize { get; set; } = 4;

    /// <summary>Whether the save-flow shows the generated SQL for review before running it.</summary>
    public bool ConfirmBeforeSave { get; set; } = true;

    /// <summary>Whether the query tabs from the previous session are reopened on startup.</summary>
    public bool RestoreTabsOnStartup { get; set; } = true;

    /// <summary>Whether engine-managed system databases (SQL Server's master/msdb, MySQL's mysql/sys, …)
    /// are shown in the schema tree.</summary>
    public bool ShowSystemDatabases { get; set; }

    /// <summary>Whether closing the app asks for confirmation first. Cleared when the user ticks
    /// "always close without asking" in the exit dialog.</summary>
    public bool ConfirmOnExit { get; set; } = true;

    /// <summary>When on, clicking the window's close button hides the app to the system tray instead of
    /// quitting, so it keeps running in the background (notably the MCP server). A real quit is still
    /// available from the tray menu and File &gt; Exit. Off by default.</summary>
    public bool CloseToTray { get; set; }

    /// <summary>Global query timeout in seconds for app-run queries; 0 = no limit. Applied by cancelling the
    /// run's token after the interval (same mechanism as the Stop button). MCP has its own timeout.</summary>
    public int QueryTimeoutSeconds { get; set; }

    /// <summary>Rows fetched per page when browsing a table/collection/index (the "Browse table" grid).
    /// Applied to newly opened browse tabs. Default 200.</summary>
    public int BrowsePageSize { get; set; } = 200;

    // ── Master password (optional app-level encryption of connection secrets) ────────────────────────
    // All three below are NON-secret: they enable the feature and let the app verify a typed password.
    // The derived AES key itself is never stored — only held in memory while the session is unlocked.

    /// <summary>Whether an app master password guards the connection secrets (extra layer over the OS vault).</summary>
    public bool MasterPasswordEnabled { get; set; }

    /// <summary>Base64 PBKDF2 salt (16 bytes) for the master-password key derivation.</summary>
    public string? MasterPasswordSalt { get; set; }

    /// <summary>Base64 AES-GCM ciphertext of a fixed constant, used to verify a typed password without
    /// storing the key or password.</summary>
    public string? MasterPasswordVerifier { get; set; }

    /// <summary>Auto-lock the master key after this many minutes of inactivity; 0 = never (only re-lock on
    /// restart). Any secret access (connection resolve) resets the timer.</summary>
    public int MasterPasswordLockMinutes { get; set; }

    // ── Query log (opt-in audit log, separate from the always-on re-run history) ─────────────────────

    /// <summary>Master switch for the query log. Off by default — nothing is written until enabled.</summary>
    public bool QueryLogEnabled { get; set; }

    /// <summary>Log queries executed from the application (user-issued). Combined with
    /// <see cref="QueryLogMcp"/> under <see cref="QueryLogEnabled"/> this lets the user record only the
    /// app, only MCP, or both.</summary>
    public bool QueryLogApp { get; set; } = true;

    /// <summary>Log queries executed by AI clients over the MCP server.</summary>
    public bool QueryLogMcp { get; set; } = true;

    /// <summary>Size at which the JSONL log rotates to a single <c>.1</c> backup (megabytes).</summary>
    public int QueryLogMaxSizeMb { get; set; } = 10;

    // ── MCP server (top-level; the host owns the server, plugins only contribute tools) ──────────────

    /// <summary>Master switch for the MCP server. Off by default — no listener until the user turns it on.</summary>
    public bool McpEnabled { get; set; }

    /// <summary>Loopback port the MCP server binds (127.0.0.1 only, never configurable to another host).</summary>
    public int McpPort { get; set; } = 5488;

    /// <summary>Require a bearer token on the MCP listener. Default on (recommended); turning it off lets any
    /// local process reach the AI-accessible connections, so the UI must warn on disable (plan §6 / CRIT-3).</summary>
    public bool McpRequireAuth { get; set; } = true;

    /// <summary>The generated bearer token (≥256-bit, base64). Created on first enable when auth is on;
    /// regenerating invalidates the old one.</summary>
    public string? McpToken { get; set; }

    /// <summary>Server-side hard row cap for MCP queries — the AI can only ever request fewer (HIGH-1).</summary>
    public int McpMaxRows { get; set; } = 200;

    /// <summary>Server-side query timeout (seconds) for MCP queries.</summary>
    public int McpTimeoutSeconds { get; set; } = 30;

    /// <summary>Redact suspected secrets out of MCP query results before they reach the AI (SE-145). Default on:
    /// any MCP-reachable connection is AI-facing, so live tokens/keys in result cells would otherwise leak into
    /// the AI context. Turning it off restores verbatim values — the UI warns on disable.</summary>
    public bool McpScrubSecrets { get; set; } = true;

    // ── App updates (SE-137) ─────────────────────────────────────────────────────────────────────────

    /// <summary>Release channel the in-app updater follows. Null = never chosen → follow the channel of the
    /// running build (a nightly build tracks Nightly, a release tracks Stable), until the user picks one.</summary>
    public UpdateChannel? UpdateChannel { get; set; }

    /// <summary>Whether to check the chosen channel for a newer build once on startup. On by default.</summary>
    public bool CheckForUpdatesOnStartup { get; set; } = true;

    /// <summary>Proactive plugin-update behaviour (SE-138). Default <see cref="PluginUpdatePolicy.Notify"/>.
    /// Reuses <see cref="UpdateCheckIntervalMinutes"/> for the background re-check cadence.</summary>
    public PluginUpdatePolicy PluginUpdatePolicy { get; set; } = PluginUpdatePolicy.Notify;

    /// <summary>Plugins auto-staged by the Auto policy (SE-138 phase 3), each as "Name x.y.z", pending the
    /// next restart. Read once on the next startup to show an "updated" summary, then cleared.</summary>
    public List<string>? PendingAutoUpdateNotice { get; set; }

    /// <summary>Background re-check interval in minutes. 0 = only on startup (no periodic loop). Default 240
    /// (4 hours), matching the interval that was hardcoded before SE-152.</summary>
    public int UpdateCheckIntervalMinutes { get; set; } = 240;

    /// <summary>The version the user dismissed with "Later"; the startup banner stays hidden for it until a
    /// newer build appears. Null = nothing dismissed.</summary>
    public string? DismissedUpdateVersion { get; set; }
}
