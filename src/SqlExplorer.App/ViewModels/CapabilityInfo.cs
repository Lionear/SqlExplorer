namespace SqlExplorer.App.ViewModels;

/// <summary>A capability a plugin declares, in human terms — the title + one-line explanation shown on the
/// install consent overlay (SE-164), instead of the bare key.</summary>
public sealed record CapabilityInfo(string Key, string Title, string Description);

/// <summary>Maps a plugin capability key to a friendly title + description for the consent overlay. Unknown
/// keys fall back to the raw key so a future capability still shows something.</summary>
public static class CapabilityCatalog
{
    public static CapabilityInfo Describe(string key) => key switch
    {
        "storage" => new(key, "Private storage", "Keeps its own data and settings in a private file."),
        "connections" => new(key, "Connections", "Reads your connections and can add its own managed ones — never your passwords."),
        "panel" => new(key, "Docked panel", "Adds a panel to the workspace, next to Output and History."),
        "menu" => new(key, "Menu items", "Adds items to the Tools menu and to a connection's right-click menu."),
        "background" => new(key, "Background task", "Runs a background task while the app is open."),
        "process" => new(key, "External processes", "Can start external programs on your machine (e.g. docker)."),
        _ => new(key, key, string.Empty)
    };
}
