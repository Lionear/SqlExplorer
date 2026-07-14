namespace Lionear.SqlExplorer.Core.Settings;

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
}
