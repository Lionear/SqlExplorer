using System.Collections.Generic;

namespace SqlExplorer.Core.Shortcuts;

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

        /// <summary>Open a <c>.sql</c> file into a new query tab (SE-154).</summary>
        public const string OpenQuery = "OpenQuery";

        /// <summary>Save the active query tab's text to its <c>.sql</c> file (SE-154). This is the
        /// SSMS/DataGrip meaning of Ctrl+S; the grid-edit save moved to <see cref="CommitEdits"/>.</summary>
        public const string Save = "Save";

        /// <summary>Write pending grid-row edits back to the database (the former Ctrl+S action, SE-154),
        /// rebound to Ctrl+Shift+S now that Ctrl+S saves the query file.</summary>
        public const string CommitEdits = "CommitEdits";

        public const string Format = "Format";
        public const string ToggleSearch = "ToggleSearch";
        public const string ToggleComment = "ToggleComment";
        public const string ZoomIn = "ZoomIn";
        public const string ZoomOut = "ZoomOut";
        public const string RefreshTree = "RefreshTree";
    }

    private static class Groups
    {
        public const string Tabs = "ShortcutGroupTabs";
        public const string Query = "ShortcutGroupQuery";
        public const string Editor = "ShortcutGroupEditor";
        public const string Search = "ShortcutGroupSearch";
        public const string Tree = "ShortcutGroupTree";
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
        new(Ids.OpenQuery, "OpenQuery", Groups.Query, ShortcutScope.Window, "Mod+O"),
        new(Ids.Save, "SaveQuery", Groups.Query, ShortcutScope.Window, "Mod+S"),
        new(Ids.CommitEdits, "CommitEdits", Groups.Query, ShortcutScope.Window, "Mod+Shift+S"),
        new(Ids.Format, "Format", Groups.Query, ShortcutScope.Window, "Mod+Shift+F"),

        new(Ids.ToggleComment, "ToggleComment", Groups.Editor, ShortcutScope.Editor, "Mod+OemQuestion"),
        new(Ids.ZoomIn, "ZoomIn", Groups.Editor, ShortcutScope.Editor, "Mod+OemPlus"),
        new(Ids.ZoomOut, "ZoomOut", Groups.Editor, ShortcutScope.Editor, "Mod+OemMinus"),

        new(Ids.ToggleSearch, "ToggleSearch", Groups.Search, ShortcutScope.Window, "Mod+K"),

        new(Ids.RefreshTree, "RefreshTree", Groups.Tree, ShortcutScope.Window, "Mod+R"),
    ];
}
