using SqlExplorer.Sdk;

namespace SqlExplorer.Tools.UniversalBackup;

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

    // ── Non-table objects (v3): views / procedures / functions / triggers, schema-only ──────────────────

    /// <summary>A non-table object found in the tree walk, with the path needed to resolve its definition
    /// later. Collecting refs is cheap (no per-object definition query), so the selection tree can list
    /// every object — including ones whose DDL turns out to be unavailable (resolved and skipped at backup).</summary>
    public sealed record BackupObjectRef(LbakObjectKind Kind, string Schema, string Name, string ParentTable, IReadOnlyList<DbNodeRef> Path);

    /// <summary>Walk the tree for non-table objects (views/procedures/functions/triggers) — names/paths only,
    /// no definition fetch. Indexes are deliberately not collected — reconstructing CREATE INDEX needs
    /// introspection the providers don't expose yet.</summary>
    public static async Task<IReadOnlyList<BackupObjectRef>> CollectObjectRefsAsync(
        IDbProvider provider, ConnectionProfile profile, DbNodeRef? start, CancellationToken ct)
    {
        var found = new List<BackupObjectRef>();
        var ancestors = start is { } node ? new List<DbNodeRef> { node } : [];
        await WalkObjectsAsync(provider, profile, ancestors, found, ct);
        return found;
    }

    /// <summary>Resolve one object's CREATE text via <see cref="IDbProvider.GetObjectDefinitionAsync"/>.
    /// Null = the provider can't supply a definition (e.g. an encrypted or CLR routine); the caller logs it
    /// as skipped rather than silently dropping the object from the selection tree.</summary>
    public static Task<string?> ResolveDefinitionAsync(
        IDbProvider provider, ConnectionProfile profile, BackupObjectRef obj, CancellationToken ct) =>
        provider.GetObjectDefinitionAsync(profile, obj.Path, ct);

    private static async Task WalkObjectsAsync(
        IDbProvider provider, ConnectionProfile profile, List<DbNodeRef> ancestors,
        List<BackupObjectRef> found, CancellationToken ct)
    {
        var children = await provider.GetChildNodesAsync(profile, ancestors, ct);
        foreach (var child in children)
        {
            ct.ThrowIfCancellationRequested();
            var path = new List<DbNodeRef>(ancestors) { new(child.Kind, child.Name) };

            if (child.Kind is DbNodeKind.View or DbNodeKind.Procedure or DbNodeKind.Function or DbNodeKind.Trigger)
            {
                var schema = path.FirstOrDefault(p => p.Kind == DbNodeKind.Schema)?.Name ?? string.Empty;
                // A trigger hangs under its owning table/view — record that as the parent.
                var parent = child.Kind == DbNodeKind.Trigger
                    ? path.LastOrDefault(p => p.Kind is DbNodeKind.Table or DbNodeKind.View)?.Name ?? string.Empty
                    : string.Empty;
                found.Add(new BackupObjectRef(ObjectKind(child.Kind), schema, child.Name, parent, path));

                // A view can itself carry triggers (e.g. SQLite INSTEAD OF) — descend into it too.
                if (child.Kind == DbNodeKind.View)
                {
                    await WalkObjectsAsync(provider, profile, path, found, ct);
                }
            }
            else if (IsObjectContainer(child.Kind))
            {
                await WalkObjectsAsync(provider, profile, path, found, ct);
            }
        }
    }

    // Descend the folders that lead to non-table objects — including into Tables, whose TriggerFolder holds
    // the table's triggers. (The table itself isn't collected here; its data goes through CollectTablesAsync.)
    private static bool IsObjectContainer(DbNodeKind kind) =>
        kind is DbNodeKind.Database or DbNodeKind.SchemaFolder or DbNodeKind.Schema or DbNodeKind.Group
            or DbNodeKind.TableFolder or DbNodeKind.ViewFolder or DbNodeKind.ProcedureFolder
            or DbNodeKind.FunctionFolder or DbNodeKind.TriggerFolder or DbNodeKind.Table;

    private static LbakObjectKind ObjectKind(DbNodeKind kind) => kind switch
    {
        DbNodeKind.View => LbakObjectKind.View,
        DbNodeKind.Procedure => LbakObjectKind.Procedure,
        DbNodeKind.Function => LbakObjectKind.Function,
        _ => LbakObjectKind.Trigger
    };

    public static async Task<IReadOnlyList<BackupColumn>> ReadColumnsAsync(
        IDbProvider provider, ConnectionProfile profile, IReadOnlyList<DbNodeRef> tablePath, CancellationToken ct)
    {
        var children = await provider.GetChildNodesAsync(profile, tablePath, ct);

        // Columns live under a "Columns" folder (next to Indexes/Foreign Keys); descend into it when present.
        if (children.FirstOrDefault(c => c.Kind == DbNodeKind.ColumnFolder) is { } folder)
        {
            var folderPath = new List<DbNodeRef>(tablePath) { new(folder.Kind, folder.Name) };
            children = await provider.GetChildNodesAsync(profile, folderPath, ct);
        }

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
