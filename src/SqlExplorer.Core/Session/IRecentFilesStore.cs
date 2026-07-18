namespace SqlExplorer.Core.Session;

/// <summary>Persists the most-recently opened/saved <c>.sql</c> files (SE-154) so the "Recent" menu can
/// offer them across runs. Newest first, capped, and de-duplicated by path.</summary>
public interface IRecentFilesStore
{
    /// <summary>Raised after the list changes (add/clear) so an open menu can refresh.</summary>
    event Action? Changed;

    /// <summary>Most-recently-used first, capped at the store's capacity.</summary>
    IReadOnlyList<string> GetRecent();

    /// <summary>Record a file as most-recently-used: moves it to the front if already present, else adds
    /// it, dropping the oldest entry past capacity.</summary>
    void Add(string path);

    void Clear();
}
