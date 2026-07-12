namespace Lionear.SqlExplorer.Core.History;

/// <summary>Persists executed queries so they can be listed, searched and re-run across runs.</summary>
public interface IQueryHistoryStore
{
    /// <summary>Raised after the history changes (append/clear) so an open panel can refresh.</summary>
    event Action? Changed;

    void Append(QueryHistoryEntry entry);

    /// <summary>Most-recent-first, capped at <paramref name="limit"/>.</summary>
    IReadOnlyList<QueryHistoryEntry> GetRecent(int limit);

    /// <summary>Most-recent-first entries whose SQL or connection name contains <paramref name="text"/>
    /// (case-insensitive); blank text returns everything.</summary>
    IReadOnlyList<QueryHistoryEntry> Search(string text);

    void Clear();
}
