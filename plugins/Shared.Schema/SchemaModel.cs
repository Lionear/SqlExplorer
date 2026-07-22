namespace SqlExplorer.Plugins.Schema;

/// <summary>
/// A provider-agnostic snapshot of a database's structure, read from <c>information_schema</c> plus a
/// per-dialect index query (<see cref="SchemaReader"/>). It is deliberately just enough to diff two
/// same-provider schemas and emit an ALTER script — tables, their columns, and the key/index/FK objects
/// on them — not a full catalogue (no views, routines, triggers, sequences).
/// </summary>
public sealed record SchemaSnapshot(IReadOnlyList<TableDef> Tables);

/// <summary>One base table. <see cref="Schema"/> is the owning schema/namespace (e.g. <c>public</c>,
/// <c>dbo</c>); for engines without schemas (SQLite) it is empty.</summary>
public sealed record TableDef(
    string Schema,
    string Name,
    IReadOnlyList<ColumnDef> Columns,
    PrimaryKeyDef? PrimaryKey,
    IReadOnlyList<IndexDef> Indexes,
    IReadOnlyList<ForeignKeyDef> ForeignKeys,
    IReadOnlyList<UniqueDef> Uniques)
{
    /// <summary>Schema-qualified identity used to match a table across the two snapshots (case-insensitive).</summary>
    public string Key => string.IsNullOrEmpty(Schema) ? Name : $"{Schema}.{Name}";
}

/// <summary><see cref="DataType"/> is the fully-rendered type as the engine reports it (e.g.
/// <c>character varying(255)</c>, <c>numeric(10,2)</c>) so a length/precision change shows up as a type
/// change. <see cref="Default"/> is the raw default expression, or null for none.
///
/// <para><see cref="IsIdentity"/> marks a column the engine numbers itself — Postgres <c>serial</c> /
/// <c>GENERATED AS IDENTITY</c>, MySQL <c>AUTO_INCREMENT</c>, SQL Server <c>IDENTITY</c>, SQLite
/// <c>INTEGER PRIMARY KEY</c>. It is not in <c>information_schema.columns</c> in any portable form, so each
/// reader digs it out its own way; without it a recreated table silently loses its auto-numbering and the
/// next insert fails or writes a null key.</para></summary>
public sealed record ColumnDef(
    string Name, string DataType, bool Nullable, string? Default, int Ordinal, bool IsIdentity = false);

public sealed record PrimaryKeyDef(string Name, IReadOnlyList<string> Columns);

public sealed record IndexDef(string Name, bool Unique, IReadOnlyList<string> Columns);

public sealed record UniqueDef(string Name, IReadOnlyList<string> Columns);

public sealed record ForeignKeyDef(
    string Name,
    IReadOnlyList<string> Columns,
    string RefSchema,
    string RefTable,
    IReadOnlyList<string> RefColumns);
