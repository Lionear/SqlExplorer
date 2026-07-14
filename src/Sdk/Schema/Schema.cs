namespace Lionear.SqlExplorer.Sdk.Schema;

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

    /// <summary>Grouping node under a table that holds its foreign keys.</summary>
    ForeignKeyFolder,

    /// <summary>A single foreign key on a table; <see cref="DbTreeNode.Detail"/> describes the relation.</summary>
    ForeignKey,

    /// <summary>
    /// A provider-defined leaf object the host doesn't model specially — a user, role, login,
    /// agent job, certificate, … The provider owns the label; the host just shows a generic icon.
    /// </summary>
    Object,

    /// <summary>A provider-defined cosmetic grouping folder (e.g. SQL Server "Security"/"Administration").</summary>
    Group,

    /// <summary>A grouping node that holds a server's databases (SQL Server "Databases" folder). Distinct
    /// from a plain <see cref="Group"/> so "New Database…" can be offered on it.</summary>
    DatabaseFolder,

    /// <summary>A grouping node under a table that holds its columns (SSMS/DBeaver-style "Columns" folder),
    /// so a table's columns don't sit alongside its Indexes/Foreign Keys folders.</summary>
    ColumnFolder,

    /// <summary>Grouping node under a schema/database that holds its stored procedures.</summary>
    ProcedureFolder,

    /// <summary>A single stored procedure; runnable via the routine "Execute…" flow.</summary>
    Procedure,

    /// <summary>Grouping node under a schema/database that holds its functions.</summary>
    FunctionFolder,

    /// <summary>A single function; runnable via the routine "Execute…" flow.</summary>
    Function,

    /// <summary>Grouping node under a table that holds its triggers.</summary>
    TriggerFolder,

    /// <summary>A single trigger on a table (or, for SQLite, a view). Definition-only — not runnable.</summary>
    Trigger
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

    /// <summary>Optional right-aligned badge, e.g. an object's on-disk size ("1.8G"). See <see cref="ByteSize"/>.</summary>
    public string? Badge { get; init; }

    /// <summary>Optional hover text, e.g. a table's estimated row count.</summary>
    public string? Tooltip { get; init; }

    /// <summary>True for an engine-managed system object (e.g. SQL Server's master/msdb databases). The
    /// host hides these from the tree unless the user opts in, and never indexes them for completion.</summary>
    public bool IsSystem { get; init; }

    public bool HasChildren { get; init; }
}
