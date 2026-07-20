namespace SqlExplorer.Tools.SchemaDiff;

/// <summary>
/// Compares two schema snapshots and yields the ordered set of <see cref="SchemaChange"/>s that turn
/// <paramref name="from"/> into <paramref name="to"/>. Pure and provider-agnostic — the dialect only
/// enters when <see cref="AlterScriptWriter"/> renders the result — which is why the whole comparison is
/// unit-tested without a database.
///
/// Identity is by name, case-insensitively: tables by schema-qualified <see cref="TableDef.Key"/>, columns
/// by name, and constraints/indexes by their own name. A same-named index/constraint whose definition
/// changed is emitted as a drop followed by an add (engines don't alter these in place portably).
///
/// The output is ordered for a dependency-safe apply: drop foreign keys first (they pin columns and
/// tables), then drop indexes/uniques/PKs, then per-table column work, then dropped tables, then created
/// tables, then re-add keys/indexes, and finally add foreign keys once every table and column they touch
/// exists.
/// </summary>
public static class SchemaDiffer
{
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    public static IReadOnlyList<SchemaChange> Diff(SchemaSnapshot from, SchemaSnapshot to)
    {
        var fromTables = from.Tables.ToDictionary(t => t.Key, Ci);
        var toTables = to.Tables.ToDictionary(t => t.Key, Ci);

        var dropForeignKeys = new List<SchemaChange>();
        var dropObjects = new List<SchemaChange>();     // indexes, uniques, PKs being removed or replaced
        var columnWork = new List<SchemaChange>();       // add/alter/drop columns on surviving tables
        var dropTables = new List<SchemaChange>();
        var createTables = new List<SchemaChange>();
        var addObjects = new List<SchemaChange>();       // PKs, uniques, indexes being added or replaced
        var addForeignKeys = new List<SchemaChange>();

        // Tables only in `to` are created whole (columns + keys + indexes + FKs all come with the CREATE);
        // tables only in `from` are dropped (their FKs drop first, below).
        foreach (var t in to.Tables.Where(t => !fromTables.ContainsKey(t.Key)))
        {
            createTables.Add(new CreateTable(t));
            addForeignKeys.AddRange(t.ForeignKeys.Select(fk => (SchemaChange)new AddForeignKey(t, fk)));
        }

        foreach (var t in from.Tables.Where(t => !toTables.ContainsKey(t.Key)))
        {
            dropForeignKeys.AddRange(t.ForeignKeys.Select(fk => (SchemaChange)new DropForeignKey(t, fk)));
            dropTables.Add(new DropTable(t));
        }

        // Tables in both: diff their columns and objects.
        foreach (var toTable in to.Tables.Where(t => fromTables.ContainsKey(t.Key)))
        {
            var fromTable = fromTables[toTable.Key];
            DiffColumns(fromTable, toTable, columnWork);
            DiffPrimaryKey(fromTable, toTable, dropObjects, addObjects);
            DiffNamed(fromTable, toTable, fromTable.Uniques, toTable.Uniques, u => u.Name, UniqueEquiv,
                u => new DropUnique(toTable, u), u => new AddUnique(toTable, u), dropObjects, addObjects);
            DiffNamed(fromTable, toTable, fromTable.Indexes, toTable.Indexes, i => i.Name, IndexEquiv,
                i => new DropIndex(toTable, i), i => new AddIndex(toTable, i), dropObjects, addObjects);
            DiffNamed(fromTable, toTable, fromTable.ForeignKeys, toTable.ForeignKeys, fk => fk.Name, ForeignKeyEquiv,
                fk => new DropForeignKey(toTable, fk), fk => new AddForeignKey(toTable, fk),
                dropForeignKeys, addForeignKeys);
        }

        return
        [
            .. dropForeignKeys,
            .. dropObjects,
            .. columnWork,
            .. dropTables,
            .. createTables,
            .. addObjects,
            .. addForeignKeys
        ];
    }

    private static void DiffColumns(TableDef from, TableDef to, List<SchemaChange> work)
    {
        var fromCols = from.Columns.ToDictionary(c => c.Name, Ci);
        var toCols = to.Columns.ToDictionary(c => c.Name, Ci);

        foreach (var c in to.Columns.Where(c => !fromCols.ContainsKey(c.Name)))
        {
            work.Add(new AddColumn(to, c));
        }

        foreach (var c in to.Columns.Where(c => fromCols.ContainsKey(c.Name)))
        {
            var before = fromCols[c.Name];
            if (!ColumnEquiv(before, c))
            {
                work.Add(new AlterColumn(to, before, c));
            }
        }

        foreach (var c in from.Columns.Where(c => !toCols.ContainsKey(c.Name)))
        {
            work.Add(new DropColumn(to, c));
        }
    }

    private static void DiffPrimaryKey(TableDef from, TableDef to, List<SchemaChange> drops, List<SchemaChange> adds)
    {
        if (PrimaryKeyEquiv(from.PrimaryKey, to.PrimaryKey))
        {
            return;
        }

        if (from.PrimaryKey is { } removed)
        {
            drops.Add(new DropPrimaryKey(to, removed));
        }

        if (to.PrimaryKey is { } added)
        {
            adds.Add(new AddPrimaryKey(to, added));
        }
    }

    // Generic name-keyed diff for uniques/indexes/FKs: gone → drop, new → add, changed → drop+add.
    private static void DiffNamed<T>(
        TableDef from,
        TableDef to,
        IReadOnlyList<T> fromItems,
        IReadOnlyList<T> toItems,
        Func<T, string> name,
        Func<T, T, bool> equiv,
        Func<T, SchemaChange> drop,
        Func<T, SchemaChange> add,
        List<SchemaChange> drops,
        List<SchemaChange> adds)
    {
        var fromByName = fromItems.ToDictionary(name, Ci);
        var toByName = toItems.ToDictionary(name, Ci);

        foreach (var item in fromItems.Where(i => !toByName.ContainsKey(name(i))))
        {
            drops.Add(drop(item));
        }

        foreach (var item in toItems)
        {
            if (!fromByName.TryGetValue(name(item), out var before))
            {
                adds.Add(add(item));
            }
            else if (!equiv(before, item))
            {
                drops.Add(drop(before));
                adds.Add(add(item));
            }
        }
    }

    private static bool ColumnEquiv(ColumnDef a, ColumnDef b) =>
        Ci.Equals(a.DataType, b.DataType)
        && a.Nullable == b.Nullable
        && Ci.Equals(a.Default ?? string.Empty, b.Default ?? string.Empty);

    private static bool PrimaryKeyEquiv(PrimaryKeyDef? a, PrimaryKeyDef? b) =>
        (a is null && b is null) || (a is not null && b is not null && SameColumns(a.Columns, b.Columns));

    private static bool UniqueEquiv(UniqueDef a, UniqueDef b) => SameColumns(a.Columns, b.Columns);

    private static bool IndexEquiv(IndexDef a, IndexDef b) => a.Unique == b.Unique && SameColumns(a.Columns, b.Columns);

    private static bool ForeignKeyEquiv(ForeignKeyDef a, ForeignKeyDef b) =>
        SameColumns(a.Columns, b.Columns)
        && Ci.Equals(a.RefSchema, b.RefSchema)
        && Ci.Equals(a.RefTable, b.RefTable)
        && SameColumns(a.RefColumns, b.RefColumns);

    // Column order is significant for keys/indexes/FKs, so compare positionally.
    private static bool SameColumns(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Count == b.Count && a.Zip(b, (x, y) => Ci.Equals(x, y)).All(same => same);
}
