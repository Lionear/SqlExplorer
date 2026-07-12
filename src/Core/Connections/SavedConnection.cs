using Lionear.SqlExplorer.Sdk;

namespace Lionear.SqlExplorer.Core.Connections;

/// <summary>
/// A stored connection as it lives in the config file: identity + provider + the
/// <b>non-secret</b> field values only. Secrets (passwords) live in the OS keychain,
/// keyed by this connection's <see cref="Id"/>, and are never written here.
/// </summary>
public sealed record SavedConnection
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required DatabaseKind Kind { get; init; }
    public required IReadOnlyDictionary<string, string?> Values { get; init; }
}
