using Avalonia.Media;
using Lionear.SqlExplorer.Sdk;

namespace Lionear.SqlExplorer.App.ViewModels;

/// <summary>
/// Monochrome line-icon geometries for the schema tree, drawn as stroked Paths. Unlike emoji
/// glyphs these render crisply everywhere and don't depend on an emoji-capable system font
/// (Linux/Avalonia has no colour-emoji fallback, so emoji show as tofu boxes). Coordinates sit
/// in a 16x16 box.
/// </summary>
internal static class NodeIcons
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

    public static Geometry For(DbNodeKind kind) => kind switch
    {
        DbNodeKind.Database => Database,
        DbNodeKind.Schema => Schema,
        DbNodeKind.SchemaFolder => Folder,
        DbNodeKind.TableFolder => Folder,
        DbNodeKind.ViewFolder => Folder,
        DbNodeKind.IndexFolder => Folder,
        DbNodeKind.SequenceFolder => Folder,
        DbNodeKind.Group => Folder,
        DbNodeKind.Table => Table,
        DbNodeKind.View => View,
        DbNodeKind.Column => Column,
        DbNodeKind.Index => Index,
        DbNodeKind.Sequence => Sequence,
        DbNodeKind.Object => Object,
        _ => Connection
    };

    private static Geometry Parse(string data) => StreamGeometry.Parse(data);
}
