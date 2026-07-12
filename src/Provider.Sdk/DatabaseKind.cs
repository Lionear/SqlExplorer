namespace Lionear.SqlExplorer.Sdk;

/// <summary>
/// Supported database engines. Each kind maps to exactly one <see cref="IDbProvider"/>,
/// so adding an engine is a new provider — never a UI change.
/// </summary>
/// <remarks>
/// For externally installed providers this closed enum becomes a limitation: a
/// third party cannot add a value. When the plugin loaders land (Notes.md §4.1),
/// provider identity moves to a string id from the provider manifest; this enum
/// stays only as a fast path for the built-in relational engines.
/// </remarks>
public enum DatabaseKind
{
    PostgreSql,
    MySql,
    Sqlite,
    SqlServer,
    Oracle
}
