using Avalonia.Media;
using SqlExplorer.Sdk;

namespace SqlExplorer.App.ViewModels;

/// <summary>
/// Monochrome line-icon geometries for the schema tree, drawn as stroked Paths. Unlike emoji
/// glyphs these render crisply everywhere and don't depend on an emoji-capable system font
/// (Linux/Avalonia has no colour-emoji fallback, so emoji show as tofu boxes). Coordinates sit
/// in a 16x16 box.
/// </summary>
public static class NodeIcons
{
    // Two stacked rack units → a server/connection.
    public static readonly Geometry Connection =
        Parse("M2.5,3.5 H13.5 V6.5 H2.5 Z M2.5,9.5 H13.5 V12.5 H2.5 Z M4.3,5 H5.3 M4.3,11 H5.3");

    // Cylinder → a database.
    public static readonly Geometry Database =
        Parse("M8,2 C4.7,2 2.5,2.9 2.5,4 C2.5,5.1 4.7,6 8,6 C11.3,6 13.5,5.1 13.5,4 " +
              "C13.5,2.9 11.3,2 8,2 Z M2.5,4 V12 C2.5,13.1 4.7,14 8,14 C11.3,14 13.5,13.1 13.5,12 V4");

    // Boxed namespace with a header rule → a schema.
    public static readonly Geometry Schema =
        Parse("M3,3 H13 V13 H3 Z M3,6 H13");

    // Folder → the Tables/Views grouping nodes.
    public static readonly Geometry Folder =
        Parse("M2.5,5 H6 L7.2,6.4 H13.5 V12.5 H2.5 Z");

    // Ruled grid → a table.
    public static readonly Geometry Table =
        Parse("M2.5,3.5 H13.5 V12.5 H2.5 Z M2.5,6.5 H13.5 M2.5,9.5 H13.5 M6.2,3.5 V12.5 M9.8,3.5 V12.5");

    // Eye → a view.
    public static readonly Geometry View =
        Parse("M1.7,8 C3.2,4.8 5.8,3.2 8,3.2 C10.2,3.2 12.8,4.8 14.3,8 " +
              "C12.8,11.2 10.2,12.8 8,12.8 C5.8,12.8 3.2,11.2 1.7,8 Z " +
              "M6,8 A2,2 0 1 0 10,8 A2,2 0 1 0 6,8 Z");

    // I-beam → a single column.
    public static readonly Geometry Column =
        Parse("M6.5,3.5 V12.5 M4.5,3.5 H8.5 M4.5,12.5 H8.5");

    // Sorted list (decreasing lines) → an index.
    public static readonly Geometry Index =
        Parse("M3,4 H13 M3,8 H10 M3,12 H7");

    // Ascending staircase → a sequence / auto-increment generator.
    public static readonly Geometry Sequence =
        Parse("M2.5,12.5 H6 V8.5 H9.5 V4.5 H13");

    // Diamond → a generic provider-defined object (user, role, login, job, …).
    public static readonly Geometry Object =
        Parse("M8,3 L13,8 L8,13 L3,8 Z");

    // Box with a play triangle → a stored procedure (a runnable block).
    public static readonly Geometry Procedure =
        Parse("M3.5,4 H12.5 V12 H3.5 Z M6.3,6.5 L9.7,8 L6.3,9.5 Z");

    // Parentheses with a mid-bar → a function ƒ().
    public static readonly Geometry Function =
        Parse("M6.5,3.5 C4.8,5.2 4.8,10.8 6.5,12.5 M9.5,3.5 C11.2,5.2 11.2,10.8 9.5,12.5 M6.8,8 H9.2");

    // Lightning bolt → a trigger (fires on an event).
    public static readonly Geometry Trigger =
        Parse("M9.2,2.5 L4.8,8.5 H7.4 L6.8,13.5 L11.2,7 H8.6 Z");

    // --- Toolbar action glyphs (Connection Manager). Same stroked 16x16 style as the node icons. ---

    // Plus → new connection.
    public static readonly Geometry Plus =
        Parse("M8,3.5 V12.5 M3.5,8 H12.5");

    // --- Document tab-strip glyphs (SE-123). Same stroked 16x16 style as the node icons. ---

    // Document with folded corner + text lines → query tab.
    public static readonly Geometry TabQuery =
        Parse("M4,2.5 H9.5 L13,6 V13.5 H4 Z M9.5,2.5 V6 H13 M6,9 H10 M6,11 H8");

    // Table (rows + one column split) → browse tab.
    public static readonly Geometry TabBrowse =
        Parse("M2,3 H14 V13 H2 Z M2,6.5 H14 M6,6.5 V13");

    // Clock face + hour+minute hand → monitor tab.
    public static readonly Geometry TabMonitor =
        Parse("M2,8 A6,6 0 1 0 14,8 A6,6 0 1 0 2,8 M8,4.5 V8 L10.5,9.5");

    // --- Tool-window glyphs (SE-123). Shown on the stripe / status-bar toggles. ---

    // Panel with a header bar + log lines → the Output tool-window.
    public static readonly Geometry ToolOutput =
        Parse("M2,3 H14 V13 H2 Z M2,6.5 H14 M4.5,9 H8.5 M4.5,11 H10.5");

    // Clock → the History tool-window (same shape as the monitor tab; both are "over time").
    public static readonly Geometry ToolHistory =
        Parse("M2,8 A6,6 0 1 0 14,8 A6,6 0 1 0 2,8 M8,4.5 V8 L10.5,9.5");

    // Folder with a plus → new folder.
    public static readonly Geometry FolderPlus =
        Parse("M2.5,5 H6 L7.2,6.4 H13.5 V12.5 H2.5 Z M8,8.4 V11 M6.7,9.7 H9.3");

    // Two overlapping sheets → duplicate.
    public static readonly Geometry Duplicate =
        Parse("M5.5,5.5 H12.5 V12.5 H5.5 Z M3.5,10.5 V3.5 H10.5");

    // Bin with a lid → delete.
    public static readonly Geometry Trash =
        Parse("M3.5,4.7 H12.5 M6.2,4.7 V3.3 H9.8 V4.7 M4.8,4.7 L5.4,12.8 H10.6 L11.2,4.7 " +
              "M6.9,6.7 V10.8 M9.1,6.7 V10.8");

    // Triangle with an exclamation → a warning. Stroked, so the exclamation dot is a tiny round-capped
    // segment. Replaces the ⚠️ emoji in warning banners (Linux/Avalonia has no colour-emoji fallback).
    public static readonly Geometry Warning =
        Parse("M8,2.5 L14.5,13.5 H1.5 Z M8,6 V9.7 M8,11.6 V11.8");

    // --- Settings category-rail glyphs. Stroked 16x16, same style as the rest. ---

    // Two rails with knobs → general/sliders.
    public static readonly Geometry SettingsGeneral =
        Parse("M2.5,5.5 H13.5 M2.5,10.5 H13.5 M6,4 V7 M10,9 V12");

    // Half-shaded circle → appearance/theme (light vs dark).
    public static readonly Geometry SettingsAppearance =
        Parse("M8,2.5 A5.5,5.5 0 1 0 8,13.5 A5.5,5.5 0 1 0 8,2.5 M8,2.5 V13.5");

    // Pencil → editor.
    public static readonly Geometry SettingsEditor =
        Parse("M3.2,12.8 L2.7,13.3 L3.4,10.6 L10.4,3.6 L12.4,5.6 L5.4,12.6 Z M9.4,4.6 L11.4,6.6");

    // Play triangle → query.
    public static readonly Geometry SettingsQuery =
        Parse("M5.5,3.5 L12.5,8 L5.5,12.5 Z");

    // Keyboard outline with a few keys → keyboard shortcuts.
    public static readonly Geometry SettingsKeyboard =
        Parse("M2,4.5 H14 V11.5 H2 Z M4,6.5 H4.01 M6,6.5 H6.01 M8,6.5 H8.01 M10,6.5 H10.01 M12,6.5 H12.01 M4,9.5 H4.01 M12,9.5 H12.01 M6,9.5 H10");

    // Four blocks → plugins/extensions.
    public static readonly Geometry SettingsPlugins =
        Parse("M3,3 H6.5 V6.5 H3 Z M9.5,3 H13 V6.5 H9.5 Z M3,9.5 H6.5 V13 H3 Z M9.5,9.5 H13 V13 H9.5 Z");

    // Head + shoulders → a database/server user.
    public static readonly Geometry User =
        Parse("M8,3 A2.4,2.4 0 1 1 8,7.8 A2.4,2.4 0 1 1 8,3 Z M3.2,13.2 A4.8,4.8 0 0 1 12.8,13.2 Z");

    public static Geometry For(DbNodeKind kind) => kind switch
    {
        DbNodeKind.Database => Database,
        DbNodeKind.Schema => Schema,
        DbNodeKind.SchemaFolder => Folder,
        DbNodeKind.TableFolder => Folder,
        DbNodeKind.ViewFolder => Folder,
        DbNodeKind.IndexFolder => Folder,
        DbNodeKind.SequenceFolder => Folder,
        DbNodeKind.ProcedureFolder => Folder,
        DbNodeKind.FunctionFolder => Folder,
        DbNodeKind.TriggerFolder => Folder,
        DbNodeKind.Group => Folder,
        DbNodeKind.DatabaseFolder => Folder,
        DbNodeKind.ColumnFolder => Folder,
        DbNodeKind.UserFolder => Folder,
        DbNodeKind.User => User,
        DbNodeKind.LoginFolder => Folder,
        DbNodeKind.Login => User,
        DbNodeKind.Table => Table,
        DbNodeKind.View => View,
        DbNodeKind.Column => Column,
        DbNodeKind.Index => Index,
        DbNodeKind.Sequence => Sequence,
        DbNodeKind.Procedure => Procedure,
        DbNodeKind.Function => Function,
        DbNodeKind.Trigger => Trigger,
        DbNodeKind.Object => Object,
        _ => Connection
    };

    private static Geometry Parse(string data) => StreamGeometry.Parse(data);
}
