using SqlExplorer.Core.Localization;

namespace SqlExplorer.App.ViewModels;

/// <summary>A capability a plugin declares, in human terms — the title + one-line explanation shown on the
/// install consent overlay (SE-164), instead of the bare key.</summary>
public sealed record CapabilityInfo(string Key, string Title, string Description);

/// <summary>Maps a plugin capability key to a friendly, localised title + description for the consent overlay.
/// Only the known keys are looked up in resources (<c>Cap_&lt;key&gt;_Title</c> / <c>_Desc</c>); an unknown
/// future capability falls back to its raw key so it still shows something.</summary>
public static class CapabilityCatalog
{
    private static readonly HashSet<string> Known = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "storage", "connections", "panel", "menu", "background", "process", "services", "providers"
    };

    public static CapabilityInfo Describe(string key, ILocalizer loc) =>
        Known.Contains(key)
            ? new CapabilityInfo(key, loc[$"Cap_{key}_Title"], loc[$"Cap_{key}_Desc"])
            : new CapabilityInfo(key, key, string.Empty);
}
