namespace Lionear.SqlExplorer.Sdk;

/// <summary>
/// A saved connection handed to a provider. In the skeleton the connection string
/// carries credentials directly; the shipping app stores secrets in the platform
/// keychain/keystore and injects them at connect time (see Notes.md §11).
/// </summary>
public sealed class ConnectionProfile
{
    public required string Name { get; init; }
    public required string ConnectionString { get; init; }

    /// <summary>
    /// Which database/catalog to run against, when the host knows it from the schema tree (e.g.
    /// browsing a table under a specific SQL Server database). Null means "use the connection's own
    /// default catalog". Providers where the connection is already per-database (Postgres) or which
    /// have no database layer (SQLite) ignore this.
    /// </summary>
    public string? Database { get; init; }
}
