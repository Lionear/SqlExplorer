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

    /// <summary>Which provider this connection uses — the plugin manifest's <c>id</c> (e.g. "postgres").</summary>
    public required string ProviderId { get; init; }

    /// <summary>Optional hex accent (e.g. <c>#E5484D</c>) to flag this connection in the tree (prod = red).
    /// Null/absent = no colour. Purely cosmetic; back-compat with configs that predate it.</summary>
    public string? Color { get; init; }

    /// <summary>Safe mode: when true the editable-grid save-flow is blocked to prevent accidental writes
    /// (e.g. on a production connection). Absent = false.</summary>
    public bool ReadOnly { get; init; }

    public required IReadOnlyDictionary<string, string?> Values { get; init; }
}
