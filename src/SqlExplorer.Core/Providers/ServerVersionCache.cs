namespace SqlExplorer.Core.Providers;

/// <summary>
/// Per-connection cache of the engine's server version string (e.g. "16.2"), fetched once from
/// <see cref="Sdk.IDbProvider.GetServerVersionAsync"/> at connect. The value does not change during a
/// session, so a single call at connect is enough — no per-query round-trip. A cached <c>null</c> means
/// "unknown" (the provider returned null, or predates host-API v25), and callers fall back to the provider
/// <see cref="Sdk.IDbProvider.DisplayName"/>. Mirrors <see cref="Schema.ISchemaCache"/>'s per-connection shape.
/// </summary>
public interface IServerVersionCache
{
    /// <summary>The cached version for a connection, or null when unknown / not yet fetched.</summary>
    string? Get(string connectionId);

    /// <summary>Record the version fetched for a connection at connect (null = unknown).</summary>
    void Set(string connectionId, string? version);

    /// <summary>Forget a connection's version (on disconnect / refresh / delete).</summary>
    void Invalidate(string connectionId);
}

public sealed class ServerVersionCache : IServerVersionCache
{
    private readonly Dictionary<string, string?> _byConnection = [];
    private readonly Lock _gate = new();

    public string? Get(string connectionId)
    {
        lock (_gate)
        {
            return _byConnection.TryGetValue(connectionId, out var version) ? version : null;
        }
    }

    public void Set(string connectionId, string? version)
    {
        lock (_gate)
        {
            _byConnection[connectionId] = version;
        }
    }

    public void Invalidate(string connectionId)
    {
        lock (_gate)
        {
            _byConnection.Remove(connectionId);
        }
    }
}
