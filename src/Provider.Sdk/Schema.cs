namespace Lionear.SqlExplorer.Sdk;

/// <summary>
/// The kind of object a schema-tree node represents. A provider chooses which levels it
/// exposes: relational engines walk <see cref="Database"/>→<see cref="Schema"/>→… while
/// SQLite skips straight to the table/view folders (it has no server/database layer).
/// </summary>
public enum DbNodeKind
{
    Database,

    /// <summary>A grouping node that holds the schemas of a database (DataGrip-style "schemas" folder).</summary>
    SchemaFolder,

    Schema,
    TableFolder,
    ViewFolder,

    /// <summary>Grouping node under a table that holds its indexes.</summary>
    IndexFolder,

    /// <summary>Grouping node under a schema that holds its sequences.</summary>
    SequenceFolder,

    Table,
    View,
    Column,

    /// <summary>A single index on a table.</summary>
    Index,

    /// <summary>A single sequence / auto-increment generator in a schema.</summary>
    Sequence,

    /// <summary>
    /// A provider-defined leaf object the host doesn't model specially — a user, role, login,
    /// agent job, certificate, … The provider owns the label; the host just shows a generic icon.
    /// </summary>
    Object,

    /// <summary>A provider-defined cosmetic grouping folder (e.g. SQL Server "Security"/"Administration").</summary>
    Group
}

/// <summary>
/// One step on the path from a connection root to a node: its kind and name. The provider
/// reads this ancestry to know what to introspect when a node is lazily expanded.
/// </summary>
public sealed record DbNodeRef(DbNodeKind Kind, string Name);

/// <summary>
/// A lazily-produced schema-tree node. <see cref="HasChildren"/> tells the UI whether to
/// show an expander and call back for children when the node is opened.
/// </summary>
public sealed record DbTreeNode
{
    public required DbNodeKind Kind { get; init; }

    public required string Name { get; init; }

    /// <summary>Optional secondary label, e.g. a column's data type or "(PK)".</summary>
    public string? Detail { get; init; }

    public bool HasChildren { get; init; }
}
