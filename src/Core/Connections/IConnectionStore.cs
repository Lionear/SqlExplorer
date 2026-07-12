namespace Lionear.SqlExplorer.Core.Connections;

/// <summary>Persists the non-secret part of saved connections (see <see cref="SavedConnection"/>).</summary>
public interface IConnectionStore
{
    IReadOnlyList<SavedConnection> GetAll();

    void Save(SavedConnection connection);

    void Delete(string id);
}
