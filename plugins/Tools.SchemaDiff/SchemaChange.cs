namespace SqlExplorer.Tools.SchemaDiff;

/// <summary>One structural difference between two schemas, phrased as an operation that transforms the
/// <i>from</i> schema into the <i>to</i> schema. <see cref="SchemaDiffer"/> produces them already ordered
/// so that applying them top-to-bottom is dependency-safe; <see cref="AlterScriptWriter"/> renders each to
/// dialect SQL.</summary>
public abstract record SchemaChange;

public sealed record CreateTable(TableDef Def) : SchemaChange;

public sealed record DropTable(TableDef Def) : SchemaChange;

public sealed record AddColumn(TableDef Table, ColumnDef Column) : SchemaChange;

public sealed record DropColumn(TableDef Table, ColumnDef Column) : SchemaChange;

public sealed record AlterColumn(TableDef Table, ColumnDef From, ColumnDef To) : SchemaChange;

public sealed record AddPrimaryKey(TableDef Table, PrimaryKeyDef Key) : SchemaChange;

public sealed record DropPrimaryKey(TableDef Table, PrimaryKeyDef Key) : SchemaChange;

public sealed record AddUnique(TableDef Table, UniqueDef Unique) : SchemaChange;

public sealed record DropUnique(TableDef Table, UniqueDef Unique) : SchemaChange;

public sealed record AddIndex(TableDef Table, IndexDef Index) : SchemaChange;

public sealed record DropIndex(TableDef Table, IndexDef Index) : SchemaChange;

public sealed record AddForeignKey(TableDef Table, ForeignKeyDef ForeignKey) : SchemaChange;

public sealed record DropForeignKey(TableDef Table, ForeignKeyDef ForeignKey) : SchemaChange;
