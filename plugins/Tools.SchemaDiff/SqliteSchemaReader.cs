namespace SqlExplorer.Tools.SchemaDiff;

/// <summary>
/// Reads a <see cref="SchemaSnapshot"/> from SQLite, which has no <c>information_schema</c>. Everything
/// comes from <c>sqlite_master</c> joined to the PRAGMA table-valued functions
/// (<c>pragma_table_info</c> / <c>pragma_foreign_key_list</c> / <c>pragma_index_list</c> +
/// <c>pragma_index_info</c>), so it is still three queries rather than a PRAGMA round-trip per table.
///
/// <para>Two things SQLite doesn't give us, and how they're handled:</para>
/// <list type="bullet">
/// <item>Primary keys and foreign keys are <b>unnamed</b>. Names are synthesised (<c>pk_&lt;table&gt;</c>,
/// <c>fk_&lt;table&gt;_&lt;n&gt;</c>) so the model stays uniform. The differ matches primary keys by their
/// columns, so that one is safe; foreign keys match by name, so reordering a table's foreign keys can read
/// as a drop plus an add rather than as no change.</item>
/// <item>A foreign key may omit the referenced column (<c>REFERENCES other(…)</c> implied), which PRAGMA
/// reports as NULL. Those resolve against the referenced table's own primary key.</item>
/// </list>
///
/// <para>Expression indexes (<c>CREATE INDEX … ON t(lower(name))</c>) have no column name in PRAGMA and are
/// skipped: they can't be diffed portably, and inventing a column would emit a wrong migration.</para>
/// </summary>
public sealed class SqliteSchemaReader(IDbProvider provider)
{
    public async Task<SchemaSnapshot> ReadAsync(ConnectionProfile profile, CancellationToken ct)
    {
        var columns = await Query(profile, ColumnsSql, ct);
        var foreignKeys = await Query(profile, ForeignKeysSql, ct);
        var indexes = await Query(profile, IndexesSql, ct);

        return new SchemaSnapshot(BuildTables(columns, foreignKeys, indexes));
    }

    /// <summary>The pure half: PRAGMA rows in, tables out. Unit-tested without a database.</summary>
    public static IReadOnlyList<TableDef> BuildTables(SqlRows columns, SqlRows foreignKeys, SqlRows indexes)
    {
        var tables = new List<TableDef>();

        // Primary keys are needed twice: on their own table, and to resolve a foreign key that leaves its
        // referenced columns implicit — so collect them before building anything.
        var primaryKeys = columns.All
            .Where(r => r.Int("pk") > 0)
            .GroupBy(r => r["table_name"])
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.OrderBy(r => r.Int("pk")).Select(r => r["column_name"]).ToList(),
                StringComparer.OrdinalIgnoreCase);

        foreach (var tableGroup in columns.All.GroupBy(r => r["table_name"]))
        {
            var table = tableGroup.Key;

            var cols = tableGroup
                .OrderBy(r => r.Int("ordinal_position"))
                .Select(r => new ColumnDef(
                    r["column_name"],
                    // SQLite reports the declared type verbatim, lengths included — no rebuild needed.
                    r["data_type"],
                    !r.Flag("notnull"),
                    r.NullIfBlank("column_default"),
                    r.Int("ordinal_position")))
                .ToList();

            var primaryKey = primaryKeys.TryGetValue(table, out var pkColumns)
                ? new PrimaryKeyDef($"pk_{table}", pkColumns)
                : null;

            var tableForeignKeys = foreignKeys.All
                .Where(r => string.Equals(r["table_name"], table, StringComparison.OrdinalIgnoreCase))
                .GroupBy(r => r["fk_id"])
                .Select(g => BuildForeignKey(table, g.Key, [.. g.OrderBy(r => r.Int("ordinal_position"))], primaryKeys))
                .ToList();

            // origin: 'pk' is the primary-key index (already read from table_info), 'u' a UNIQUE constraint,
            // 'c' an explicit CREATE INDEX. Only the last two belong in the model, and as different objects.
            var tableIndexRows = indexes.All
                .Where(r => string.Equals(r["table_name"], table, StringComparison.OrdinalIgnoreCase)
                            && r["origin"] != "pk"
                            && !string.IsNullOrEmpty(r["column_name"]))
                .GroupBy(r => r["index_name"])
                .ToList();

            var uniques = tableIndexRows
                .Where(g => g.First()["origin"] == "u")
                .Select(g => new UniqueDef(UniqueName(g.Key, table, Columns(g)), Columns(g)))
                .ToList();

            var tableIndexes = tableIndexRows
                .Where(g => g.First()["origin"] == "c")
                .Select(g => new IndexDef(g.Key, g.First().Flag("is_unique"), Columns(g)))
                .ToList();

            tables.Add(new TableDef(string.Empty, table, cols, primaryKey, tableIndexes, tableForeignKeys, uniques));
        }

        return tables;
    }

    private static ForeignKeyDef BuildForeignKey(
        string table, string id, IReadOnlyList<SqlRow> rows, Dictionary<string, IReadOnlyList<string>> primaryKeys)
    {
        var refTable = rows[0]["ref_table"];
        var columns = rows.Select(r => r["column_name"]).ToList();

        // A NULL "to" means the reference targets the other table's primary key implicitly.
        var refColumns = rows.Select(r => r["ref_column"]).ToList();
        if (refColumns.Any(string.IsNullOrEmpty) && primaryKeys.TryGetValue(refTable, out var pk))
        {
            refColumns = [.. pk.Take(refColumns.Count)];
        }

        return new ForeignKeyDef($"fk_{table}_{id}", columns, string.Empty, refTable, refColumns);
    }

    // A UNIQUE constraint's backing index is auto-named `sqlite_autoindex_<table>_<n>`, where n is just
    // the declaration order — so the same constraint gets different names in two databases, and the diff
    // would read that as a drop plus an add. Worse, SQLite refuses to create any object whose name starts
    // with `sqlite_`, so such a name in a generated CREATE TABLE is a script that cannot run. Both go away
    // with a name derived from the columns, which is what actually identifies the constraint.
    private static string UniqueName(string indexName, string table, IReadOnlyList<string> columns) =>
        indexName.StartsWith("sqlite_autoindex", StringComparison.OrdinalIgnoreCase)
            ? $"uq_{table}_{string.Join("_", columns)}"
            : indexName;

    private static IReadOnlyList<string> Columns(IEnumerable<SqlRow> rows) =>
        rows.OrderBy(r => r.Int("ordinal_position")).Select(r => r["column_name"]).ToList();

    // --- SQL (sqlite_master + PRAGMA table-valued functions; SQLite 3.16+) ---

    // "notnull" and "unique" are keywords, and "table"/"from"/"to" are PRAGMA column names that clash with
    // SQL keywords — all quoted.
    private const string ColumnsSql = """
        SELECT m.name AS table_name, p.cid AS ordinal_position, p.name AS column_name,
               p.type AS data_type, p."notnull" AS "notnull", p.dflt_value AS column_default, p.pk AS pk
        FROM sqlite_master m
        JOIN pragma_table_info(m.name) p
        WHERE m.type = 'table' AND m.name NOT LIKE 'sqlite_%'
        ORDER BY m.name, p.cid
        """;

    private const string ForeignKeysSql = """
        SELECT m.name AS table_name, f.id AS fk_id, f.seq AS ordinal_position,
               f."table" AS ref_table, f."from" AS column_name, f."to" AS ref_column
        FROM sqlite_master m
        JOIN pragma_foreign_key_list(m.name) f
        WHERE m.type = 'table' AND m.name NOT LIKE 'sqlite_%'
        ORDER BY m.name, f.id, f.seq
        """;

    private const string IndexesSql = """
        SELECT m.name AS table_name, il.name AS index_name, il."unique" AS is_unique,
               il.origin AS origin, ii.seqno AS ordinal_position, ii.name AS column_name
        FROM sqlite_master m
        JOIN pragma_index_list(m.name) il
        JOIN pragma_index_info(il.name) ii
        WHERE m.type = 'table' AND m.name NOT LIKE 'sqlite_%'
        ORDER BY m.name, il.name, ii.seqno
        """;

    private async Task<SqlRows> Query(ConnectionProfile profile, string sql, CancellationToken ct) =>
        SqlRows.From(await provider.ExecuteQueryAsync(profile, sql, ct));
}
