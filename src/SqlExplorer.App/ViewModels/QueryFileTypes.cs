using System.Windows.Input;

namespace SqlExplorer.App.ViewModels;

/// <summary>One entry in the File ▸ Recent menu (SE-154): the full <paramref name="Path"/> to open, a short
/// <paramref name="Display"/> (file name) for the menu header, and the <paramref name="Open"/> command the
/// menu item invokes with <see cref="Path"/> — carried on the item itself so the menu popup (a separate
/// visual tree) needs no ancestor binding back to the window.</summary>
public sealed record RecentFileItem(string Path, string Display, ICommand Open);

/// <summary>The user's answer to the "save this query before closing?" prompt (SE-154). <see cref="Cancel"/>
/// is the zero value so a dialog dismissed via Esc/× (which yields <c>default</c>) aborts the close rather
/// than silently discarding work.</summary>
public enum SaveCloseChoice
{
    Cancel,
    Save,
    DontSave
}
