namespace Lionear.SqlExplorer.Core.Settings;

/// <summary>
/// User-scoped UI preferences that survive across runs (window geometry, layout).
/// Null members mean "never set" → the view falls back to its design-time default.
/// Deliberately no secrets and no connection data — that lives in their own stores.
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
}
