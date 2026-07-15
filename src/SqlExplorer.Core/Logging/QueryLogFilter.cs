using SqlExplorer.Core.History;

namespace SqlExplorer.Core.Logging;

/// <summary>Read-side filter for the query log. A null member means "no constraint on that field".</summary>
public sealed record QueryLogFilter
{
    /// <summary>Case-insensitive substring matched against the SQL and the connection name.</summary>
    public string? Text { get; init; }

    /// <summary>Restrict to a single source (application vs AI/MCP), or null for both.</summary>
    public QueryHistorySource? Source { get; init; }

    /// <summary>Restrict to successful (true) or failed (false) runs, or null for both.</summary>
    public bool? Success { get; init; }

    /// <summary>Only entries at or after this UTC instant, or null for all time.</summary>
    public DateTime? SinceUtc { get; init; }

    /// <summary>Cap on the number of newest entries returned.</summary>
    public int Limit { get; init; } = 2000;
}
