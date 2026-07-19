using SqlExplorer.Sdk.Extensibility;

namespace SqlExplorer.Core.Connections;

/// <summary>
/// The host's <see cref="IManagedConnections"/> for one subsystem plugin: delegates to
/// <see cref="ConnectionService"/> (so secrets still land in the OS keychain and the tree updates the same
/// way) and stamps every created connection with the plugin's <paramref name="origin"/>. Scoped to that
/// origin — <see cref="Mine"/>/<see cref="Remove"/> only ever see or touch this plugin's own connections,
/// never the user's or another plugin's.
/// </summary>
public sealed class ManagedConnections(string origin, ConnectionService connections) : IManagedConnections
{
    public string Create(NewConnectionSpec spec)
    {
        var id = Guid.NewGuid().ToString("N");
        connections.Save(id, spec.Name, spec.ProviderId, spec.Values, folder: spec.Folder, origin: origin);
        return id;
    }

    public void Remove(string connectionId)
    {
        // Only remove a connection this plugin actually owns — never the user's or another plugin's.
        if (connections.List().Any(c => c.Id == connectionId && c.Origin == origin))
        {
            connections.Delete(connectionId);
        }
    }

    public IReadOnlyList<string> Mine() =>
        connections.List().Where(c => c.Origin == origin).Select(c => c.Id).ToList();

    // Read-only across all connections. SavedConnection.Values already holds only the non-secret fields
    // (secrets live in the keychain via ConnectionService), so nothing sensitive leaves the host here.
    public IReadOnlyList<ManagedConnectionInfo> All() =>
        connections.List()
            .Select(c => new ManagedConnectionInfo(c.Id, c.Name, c.ProviderId, c.Folder, c.Values))
            .ToList();
}
