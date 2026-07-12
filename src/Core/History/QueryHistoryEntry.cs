namespace Lionear.SqlExplorer.Core.History;

/// <summary>What produced a history entry — a typed query run or an editable-grid save.</summary>
public enum QueryHistoryKind
{
    Query,
    Save
}

/// <summary>
/// One executed statement kept in the query history so it can be searched and re-run. Holds only the
/// SQL and outcome metadata — never result data or secrets. Browse paging is deliberately not logged.
/// </summary>
public sealed record QueryHistoryEntry
{
    public required string Id { get; init; }
    public required DateTime TimestampUtc { get; init; }
    public required string ConnectionId { get; init; }
    public required string ConnectionName { get; init; }
    public required QueryHistoryKind Kind { get; init; }
    public required string Sql { get; init; }
    public long DurationMs { get; init; }
    public int RowCount { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
}
