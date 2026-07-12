using Lionear.SqlExplorer.Core.Connections;
using Lionear.SqlExplorer.Core.Providers;
using Lionear.SqlExplorer.Sdk;

namespace Lionear.SqlExplorer.Core.Schema;

/// <summary>Per-connection cache of the schema snapshot that drives search and completion.</summary>
public interface ISchemaCache
{
    /// <summary>The cached snapshot for a connection, or null when it has not been built yet.</summary>
    SchemaSnapshot? Get(string connectionId);

    /// <summary>
    /// Walk the provider's lazy tree and cache the snapshot for this connection. Costs N round-trips
    /// (one per container node) but runs once at connect, off the UI thread.
    /// </summary>
    Task BuildAsync(SavedConnection connection, CancellationToken ct = default);

    /// <summary>Forget a connection's snapshot (on disconnect / refresh / delete).</summary>
    void Invalidate(string connectionId);

    /// <summary>Raised after the cache changes (a snapshot was built or invalidated).</summary>
    event Action? Changed;
}

/// <summary>
/// Builds each snapshot by walking the same lazy <see cref="IDbProvider.GetChildNodesAsync"/> the
/// sidebar uses — so it needs no SDK/host-API change and works for every provider shape. It descends
/// only through container nodes and stops at each table/view, reading that relation's Column children.
/// </summary>
public sealed class SchemaCache(IDbProviderRegistry providers, ConnectionService connections) : ISchemaCache
{
    // Safety valve: cap total relations so a pathologically large server can't make the walk run away.
    private const int MaxObjects = 5000;

    private readonly Dictionary<string, SchemaSnapshot> _byConnection = [];
    private readonly Lock _gate = new();

    public event Action? Changed;

    public SchemaSnapshot? Get(string connectionId)
    {
        lock (_gate)
        {
            return _byConnection.TryGetValue(connectionId, out var snapshot) ? snapshot : null;
        }
    }

    public async Task BuildAsync(SavedConnection connection, CancellationToken ct = default)
    {
        var provider = providers.Get(connection.ProviderId);
        var profile = connections.Resolve(connection);

        var objects = new List<SchemaObject>();
        await WalkAsync(provider, profile, [], objects, ct);

        lock (_gate)
        {
            _byConnection[connection.Id] = new SchemaSnapshot(objects);
        }

        Changed?.Invoke();
    }

    public void Invalidate(string connectionId)
    {
        bool removed;
        lock (_gate)
        {
            removed = _byConnection.Remove(connectionId);
        }

        if (removed)
        {
            Changed?.Invoke();
        }
    }

    // Depth-first walk mirroring how the sidebar expands nodes: descend through container kinds,
    // record each table/view together with its columns. The plain profile + ancestors is exactly
    // what the tree passes (providers read the target catalog from the ancestry themselves).
    private async Task WalkAsync(
        IDbProvider provider,
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        List<SchemaObject> sink,
        CancellationToken ct)
    {
        if (sink.Count >= MaxObjects)
        {
            return;
        }

        IReadOnlyList<DbTreeNode> children;
        try
        {
            children = await provider.GetChildNodesAsync(profile, ancestors, ct);
        }
        catch
        {
            // A partial snapshot beats none: skip a branch the provider refuses (permissions, offline).
            return;
        }

        foreach (var child in children)
        {
            ct.ThrowIfCancellationRequested();
            var path = new List<DbNodeRef>(ancestors) { new(child.Kind, child.Name) };

            if (child.Kind is DbNodeKind.Table or DbNodeKind.View)
            {
                sink.Add(new SchemaObject
                {
                    Kind = child.Kind,
                    Database = DbNameOf(ancestors),
                    Schema = SchemaNameOf(ancestors),
                    Name = child.Name,
                    Columns = child.HasChildren ? await ColumnsAsync(provider, profile, path, ct) : []
                });

                if (sink.Count >= MaxObjects)
                {
                    return;
                }
            }
            else if (IsContainer(child.Kind) && child.HasChildren)
            {
                await WalkAsync(provider, profile, path, sink, ct);
            }
        }
    }

    private static async Task<IReadOnlyList<SchemaColumn>> ColumnsAsync(
        IDbProvider provider, ConnectionProfile profile, IReadOnlyList<DbNodeRef> tablePath, CancellationToken ct)
    {
        IReadOnlyList<DbTreeNode> children;
        try
        {
            children = await provider.GetChildNodesAsync(profile, tablePath, ct);
        }
        catch
        {
            return [];
        }

        // A table's children may also hold an index folder; keep only the columns.
        return children
            .Where(node => node.Kind == DbNodeKind.Column)
            .Select(node => new SchemaColumn(node.Name, node.Detail))
            .ToList();
    }

    private static string? DbNameOf(IReadOnlyList<DbNodeRef> path) =>
        path.FirstOrDefault(r => r.Kind == DbNodeKind.Database)?.Name;

    private static string? SchemaNameOf(IReadOnlyList<DbNodeRef> path) =>
        path.FirstOrDefault(r => r.Kind == DbNodeKind.Schema)?.Name;

    private static bool IsContainer(DbNodeKind kind) => kind is
        DbNodeKind.Database or DbNodeKind.SchemaFolder or DbNodeKind.Schema or
        DbNodeKind.TableFolder or DbNodeKind.ViewFolder or DbNodeKind.Group;
}
