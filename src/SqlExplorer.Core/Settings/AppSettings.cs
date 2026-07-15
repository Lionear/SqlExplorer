namespace SqlExplorer.Core.Settings;

/// <summary>Preferred colour scheme; <see cref="System"/> follows the OS setting live.</summary>
public enum AppTheme
{
    System,
    Light,
    Dark
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

    /// <summary>Two-letter culture code (e.g. "nl", "en"); null = follow the OS/thread default.</summary>
    public string? Language { get; set; }

    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>SQL editor font size in points; null = AvaloniaEdit's design-time default.</summary>
    public double? EditorFontSize { get; set; }

    public bool EditorWordWrap { get; set; }

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
}
