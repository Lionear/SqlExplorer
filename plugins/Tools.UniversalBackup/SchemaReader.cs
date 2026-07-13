using Lionear.SqlExplorer.Sdk;

namespace Lionear.SqlExplorer.Tools.UniversalBackup;

/// <summary>A table found in the schema walk: the tree path to it, its owning schema (null if none)
/// and its name.</summary>
public sealed record TableRef(IReadOnlyList<DbNodeRef> Path, string? Schema, string Table);

/// <summary>
/// Reads schema shape through the host's <see cref="IDbProvider"/> alone — a small DFS mirroring
/// <c>Core/Schema/SchemaCache</c> (it can't be reused: it lives in Core, which tool plugins don't
/// reference). Descends container nodes, collects Table nodes, and parses column defs from the tree's
/// <c>Detail</c> text (the same text the tree shows, e.g. "varchar(255)" / "integer (PK)").
/// </summary>
public static class SchemaReader
{
    public static async Task<IReadOnlyList<TableRef>> CollectTablesAsync(
        IDbProvider provider, ConnectionProfile profile, DbNodeRef? start, CancellationToken ct)
    {
        var tables = new List<TableRef>();
        var ancestors = start is { } node ? new List<DbNodeRef> { node } : [];
        await WalkAsync(provider, profile, ancestors, tables, ct);
        return tables;
    }

    private static async Task WalkAsync(
        IDbProvider provider, ConnectionProfile profile, List<DbNodeRef> ancestors, List<TableRef> tables, CancellationToken ct)
    {
        var children = await provider.GetChildNodesAsync(profile, ancestors, ct);
        foreach (var child in children)
        {
            ct.ThrowIfCancellationRequested();
            var path = new List<DbNodeRef>(ancestors) { new(child.Kind, child.Name) };

            if (child.Kind == DbNodeKind.Table)
            {
                var schema = path.FirstOrDefault(p => p.Kind == DbNodeKind.Schema)?.Name;
                tables.Add(new TableRef(path, schema, child.Name));
            }
            else if (IsContainer(child.Kind))
            {
                await WalkAsync(provider, profile, path, tables, ct);
            }
        }
    }

    // Descend structural containers on the way to tables; skip Views/Indexes/etc (no own row data).
    private static bool IsContainer(DbNodeKind kind) =>
        kind is DbNodeKind.Database or DbNodeKind.SchemaFolder or DbNodeKind.Schema or DbNodeKind.TableFolder or DbNodeKind.Group;

    public static async Task<IReadOnlyList<BackupColumn>> ReadColumnsAsync(
        IDbProvider provider, ConnectionProfile profile, IReadOnlyList<DbNodeRef> tablePath, CancellationToken ct)
    {
        var children = await provider.GetChildNodesAsync(profile, tablePath, ct);
        var columns = new List<BackupColumn>();
        foreach (var child in children.Where(c => c.Kind == DbNodeKind.Column))
        {
            var detail = child.Detail ?? string.Empty;
            var primaryKey = detail.Contains("(PK)", StringComparison.Ordinal);
            var declaredType = primaryKey
                ? detail[..detail.LastIndexOf("(PK)", StringComparison.Ordinal)].Trim()
                : detail.Trim();

            // Nullability isn't carried in the tree's Detail text; default to nullable (relaxed on restore).
            columns.Add(new BackupColumn(child.Name, declaredType, Nullable: true, primaryKey));
        }

        return columns;
    }
}
