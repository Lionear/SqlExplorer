using System.Collections.Generic;

namespace Lionear.SqlExplorer.Core.Shortcuts;

/// <summary>
/// The fixed set of user-rebindable commands and their factory defaults. The <see cref="Ids"/> constants
/// are the single source of truth for the persisted keys — referenced by both the keymap store and the
/// command resolver in <c>MainViewModel</c>, so there are no magic strings on either side.
/// </summary>
public static class ShortcutCatalog
{
    public static class Ids
    {
        public const string NewQueryTab = "NewQueryTab";
        public const string CloseTab = "CloseTab";
        public const string ReopenTab = "ReopenTab";
        public const string Run = "Run";
        public const string RunAtCursor = "RunAtCursor";
        public const string Save = "Save";
        public const string Format = "Format";
        public const string ToggleSearch = "ToggleSearch";
        public const string ToggleComment = "ToggleComment";
    }

    private static class Groups
    {
        public const string Tabs = "ShortcutGroupTabs";
        public const string Query = "ShortcutGroupQuery";
        public const string Editor = "ShortcutGroupEditor";
        public const string Search = "ShortcutGroupSearch";
    }

    /// <summary>
    /// Placeholder for the platform's primary command modifier in a default gesture. Expanded by
    /// <see cref="KeymapService"/> to <c>Cmd</c> on macOS and <c>Ctrl</c> on Windows/Linux, so the
    /// shipped defaults follow each platform's convention (⌘T on a Mac, Ctrl+T elsewhere).
    /// </summary>
    public const string PrimaryModifierToken = "Mod";

    /// <summary>
    /// All bindable commands, in display order. The <c>Mod</c> token in a default gesture stands for the
    /// platform primary modifier (see <see cref="PrimaryModifierToken"/>); <c>F5</c> and other bare keys
    /// are identical on every platform.
    /// </summary>
    public static IReadOnlyList<ShortcutCommand> All { get; } =
    [
        new(Ids.NewQueryTab, "NewQueryTab", Groups.Tabs, ShortcutScope.Window, "Mod+T"),
        new(Ids.CloseTab, "CloseTab", Groups.Tabs, ShortcutScope.Window, "Mod+W"),
        new(Ids.ReopenTab, "ReopenTab", Groups.Tabs, ShortcutScope.Window, "Mod+Shift+T"),

        new(Ids.Run, "Run", Groups.Query, ShortcutScope.Window, "F5"),
        new(Ids.RunAtCursor, "RunAtCursor", Groups.Query, ShortcutScope.Window, "Mod+Enter"),
        new(Ids.Save, "Save", Groups.Query, ShortcutScope.Window, "Mod+S"),
        new(Ids.Format, "Format", Groups.Query, ShortcutScope.Window, "Mod+Shift+F"),

        new(Ids.ToggleComment, "ToggleComment", Groups.Editor, ShortcutScope.Editor, "Mod+OemQuestion"),

        new(Ids.ToggleSearch, "ToggleSearch", Groups.Search, ShortcutScope.Window, "Mod+K"),
    ];
}
