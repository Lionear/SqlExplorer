namespace Lionear.SqlExplorer.Sdk;

/// <summary>The kind of object DDL Create can build — a narrower, purpose-built set than
/// <see cref="DbNodeKind"/> (which describes tree shape, not what's creatable).</summary>
public enum DbObjectKind
{
    Database,
    Schema,
    Table
}

/// <summary>
/// Declares that a provider can create <see cref="Kind"/> objects, and under which tree-node kind the
/// "New …" action should appear (e.g. Table creation shows up on a <see cref="DbNodeKind.TableFolder"/>
/// node). <see cref="ParentNode"/> is null for the connection root itself (e.g. "New Database" on a
/// Postgres/MsSql connection) — the same null-means-root convention the tree already uses for
/// <c>TreeNodeViewModel.NodeKind</c>. A provider with no capabilities for a kind simply omits it — the
/// host hides the menu item.
/// </summary>
public sealed record CreateCapability(DbObjectKind Kind, DbNodeKind? ParentNode);

/// <summary>
/// One column in a new table, as entered by the user in the DDL Create dialog. <see cref="AutoIncrement"/>
/// is genuinely provider-specific — Postgres renders <c>GENERATED ALWAYS AS IDENTITY</c>, MySQL appends
/// <c>AUTO_INCREMENT</c>, SQL Server appends <c>IDENTITY(1,1)</c>, and SQLite folds it into the column
/// definition itself (<c>INTEGER PRIMARY KEY AUTOINCREMENT</c>, which also changes how the primary key
/// is declared) — so each provider decides how (or whether) to honour it in <c>BuildCreateStatement</c>.
/// </summary>
public sealed record NewColumnSpec(string Name, string Type, bool Nullable, bool PrimaryKey, bool AutoIncrement);

/// <summary>
/// Declarative input for DDL Create, collected by the host and handed to
/// <see cref="IDbProvider.BuildCreateStatement"/>. <see cref="Schema"/> is the parent schema for a
/// <see cref="DbObjectKind.Table"/>; <see cref="Columns"/> is populated only for tables.
/// </summary>
public sealed record CreateObjectSpec(
    DbObjectKind Kind,
    string Name,
    string? Schema,
    IReadOnlyList<NewColumnSpec> Columns);
