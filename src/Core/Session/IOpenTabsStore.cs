namespace Lionear.SqlExplorer.Core.Session;

/// <summary>Persists the open query tabs so they can be reopened on the next launch.</summary>
public interface IOpenTabsStore
{
    IReadOnlyList<OpenTabState> Load();

    void Save(IReadOnlyList<OpenTabState> tabs);
}
