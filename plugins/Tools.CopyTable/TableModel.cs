namespace SqlExplorer.Tools.CopyTable;

/// <summary>Just enough of a base table to re-create it on another connection: its columns (in order) and
/// its primary key. Read from <c>information_schema</c> by <see cref="TableReader"/>. Indexes and foreign
/// keys are a planned follow-up (SE-188) — v1 copies columns, primary key and data.</summary>
public sealed record TableModel(string Schema, string Name, IReadOnlyList<TableColumn> Columns, TablePrimaryKey? PrimaryKey)
{
    /// <summary>The identity/auto-increment column, if any (a table has at most one in the engines we read).</summary>
    public TableColumn? IdentityColumn => Columns.FirstOrDefault(c => c.IsIdentity);
}

/// <summary><see cref="DataType"/> is the fully-rendered engine type (e.g. <c>character varying(255)</c>,
/// <c>numeric(10,2)</c>). <see cref="Default"/> is the raw default expression, or null.
/// <see cref="IsIdentity"/> marks an identity / serial / auto-increment column: the copy either keeps its
/// values verbatim (rendered as a plain column so an explicit insert is legal on every engine) or lets the
/// target regenerate them (rendered with the engine's identity clause and left out of the insert).</summary>
public sealed record TableColumn(string Name, string DataType, bool Nullable, string? Default, int Ordinal, bool IsIdentity = false);

public sealed record TablePrimaryKey(string? Name, IReadOnlyList<string> Columns);
