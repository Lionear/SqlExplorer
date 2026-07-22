namespace SqlExplorer.Plugins.Schema;

/// <summary>
/// Reads a <see cref="SchemaSnapshot"/> from a live connection through the host's <see cref="IDbProvider"/>,
/// picking the catalogue an engine actually has: ANSI <c>information_schema</c> for Postgres, MySQL and
/// SQL Server (<see cref="InformationSchemaReader"/>), or <c>sqlite_master</c> + PRAGMA for SQLite
/// (<see cref="SqliteSchemaReader"/>). Both produce the same model: base tables, their columns
/// (type, nullability, default), primary key, unique constraints, secondary indexes and foreign keys.
///
/// <para><b>Scope:</b> same-provider only — the two sides are read and rendered with one dialect. A
/// cross-engine diff needs type mapping between dialects and is deliberately not attempted (SE-186 §3);
/// <see cref="Supports"/> reports whether an engine can be read at all. Views, routines, triggers and
/// sequences are out of the model by design.</para>
/// </summary>
public sealed class SchemaReader(IDbProvider provider)
{
    public static bool Supports(string providerId) =>
        providerId is "postgres" or "mysql" or "sqlserver" or "sqlite";

    /// <param name="onlyTable">Narrows the read to a single base table by name, for the caller that wants one
    /// table's shape rather than the whole database (Copy Table). Null reads everything.</param>
    public Task<SchemaSnapshot> ReadAsync(
        ConnectionProfile profile, string providerId, CancellationToken ct, string? onlyTable = null) =>
        providerId == "sqlite"
            ? new SqliteSchemaReader(provider, onlyTable).ReadAsync(profile, ct)
            : new InformationSchemaReader(provider, providerId, profile.Database, onlyTable).ReadAsync(profile, ct);
}
