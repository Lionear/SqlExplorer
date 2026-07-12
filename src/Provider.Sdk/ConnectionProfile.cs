namespace Lionear.SqlExplorer.Sdk;

/// <summary>
/// A saved connection handed to a provider. In the skeleton the connection string
/// carries credentials directly; the shipping app stores secrets in the platform
/// keychain/keystore and injects them at connect time (see Notes.md §11).
/// </summary>
public sealed class ConnectionProfile
{
    public required string Name { get; init; }
    public required DatabaseKind Kind { get; init; }
    public required string ConnectionString { get; init; }
}
