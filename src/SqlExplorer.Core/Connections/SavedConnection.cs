namespace SqlExplorer.Core.Connections;

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

    /// <summary>Optional sidebar folder to group this connection under (e.g. "Production", per client).
    /// Null/blank = ungrouped (shown at the tree root). Purely organisational.</summary>
    public string? Folder { get; init; }

    /// <summary>The plugin/subsystem that created and manages this connection (its plugin id, e.g.
    /// <c>"local-containers"</c>), or null for a user-created one. Drives a "managed by X" tree badge and
    /// lets a subsystem plugin list/remove only its own connections (SE-164 connections seam; the same
    /// origin concept SE-155 uses for MCP-created connections). Absent = null, back-compat.</summary>
    public string? Origin { get; init; }

    /// <summary>How much MCP (AI) access this connection grants. Absent/default = <see cref="AiAccessMode.None"/>
    /// (fail-closed): the connection is invisible to the MCP server until explicitly opted in. See the
    /// MCP-Server plan §4 / CRIT-1.</summary>
    public AiAccessMode AiAccess { get; init; } = AiAccessMode.None;

    /// <summary>Independent hard override that blocks this connection from MCP entirely, regardless of
    /// <see cref="AiAccess"/> — defense-in-depth against accidentally exposing production data. A connection
    /// is MCP-reachable only when <c>AiAccess != None &amp;&amp; !ExcludeFromMcp</c>. Absent = false; never
    /// auto-set (always a manual choice, plan §4 / decision #6).</summary>
    public bool ExcludeFromMcp { get; init; }

    /// <summary>True when this connection may be surfaced to / used by the MCP server: opted in AND not
    /// hard-excluded. The single place both gates are combined, so every MCP code path checks the same rule.</summary>
    public bool IsMcpReachable => AiAccess != AiAccessMode.None && !ExcludeFromMcp;

    /// <summary>True for an in-memory, session-only connection (SE-155): its values and secrets live only in
    /// <see cref="ConnectionService"/>'s transient overlay — never written to the config file or keychain —
    /// and are wiped on shutdown. Drives a "temporary" tree badge. Never serialised (the config DTO omits it),
    /// so a persisted connection can never be transient.</summary>
    public bool IsTransient { get; init; }

    public required IReadOnlyDictionary<string, string?> Values { get; init; }

    /// <summary>Manual sort index within this connection's folder scope; 0 for legacy/unsorted (falls back
    /// to alphabetical by <see cref="Name"/>). Assigned by the Connection Manager's drag-to-reorder flow.</summary>
    public int SortOrder { get; init; }
}
