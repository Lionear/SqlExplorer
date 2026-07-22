namespace SqlExplorer.Tools.SchemaDiff;

/// <summary>
/// Reads a <see cref="SchemaSnapshot"/> from an engine that exposes ANSI <c>information_schema</c> —
/// Postgres, MySQL and SQL Server — in four queries: columns, key constraints, the referenced side of each
/// foreign key, and secondary indexes.
///
/// <para>The first three are portable; <b>indexes are not</b> (no ANSI view describes them), so each engine
/// has its own catalogue query: <c>pg_index</c>, <c>information_schema.statistics</c> and <c>sys.indexes</c>.
/// Each of those deliberately excludes the indexes an engine creates <i>behind</i> a primary key or unique
/// constraint — those are already read as constraints, and emitting them again would make the diff
/// double-drop them.</para>
///
/// <para>Every query takes its schema filter as an explicit column expression
/// (<see cref="_filter"/>), rather than one filter string rewritten per alias — the aliases differ per query
/// and rewriting was fragile.</para>
/// </summary>
public sealed class InformationSchemaReader(IDbProvider provider, string providerId, string? database)
{
    // Given the column that holds the schema name in one query, the predicate that keeps only user schemas.
    private readonly Func<string, string> _filter = providerId switch
    {
        // MySQL's information_schema spans every database; pin it to the connected one.
        "mysql" => column => $"{column} = '{(database ?? string.Empty).Replace("'", "''")}'",
        "sqlserver" => column => $"{column} NOT IN ('sys', 'INFORMATION_SCHEMA')",
        _ => column => $"{column} NOT IN ('pg_catalog', 'information_schema')"
    };

    // On MySQL a "schema" is the database itself, not a namespace inside it. Keeping it in the model would
    // make `probe_left.orders` and `probe_right.orders` different tables, so a diff of two databases reads
    // as "drop everything, create everything". Postgres and SQL Server do have real schemas, so theirs stay.
    private readonly bool _schemaIsDatabase = providerId == "mysql";

    public async Task<SchemaSnapshot> ReadAsync(ConnectionProfile profile, CancellationToken ct)
    {
        var columns = await Query(profile, ColumnsSql(), ct);
        var constraints = await Query(profile, ConstraintsSql(), ct);
        var references = await Query(profile, ReferencesSql(), ct);
        var indexes = await Query(profile, IndexesSql(), ct);

        return new SchemaSnapshot(BuildTables(columns, constraints, references, indexes, _schemaIsDatabase));
    }

    /// <summary>The pure half: catalogue rows in, tables out. Unit-tested without a database.</summary>
    public static IReadOnlyList<TableDef> BuildTables(
        SqlRows columns, SqlRows constraints, SqlRows references, SqlRows indexes, bool schemaIsDatabase = false)
    {
        // Referenced (schema, table, ordered columns) per foreign-key constraint, keyed by its name.
        var refTargets = references.All
            .GroupBy(r => (Schema: r["table_schema"], Table: r["table_name"], Constraint: r["constraint_name"]))
            .ToDictionary(
                g => g.Key,
                g => (RefSchema: g.First()["ref_schema"], RefTable: g.First()["ref_table"],
                      RefColumns: (IReadOnlyList<string>)g.OrderBy(Ordinal).Select(r => r["ref_column"]).ToList()));

        var tables = new List<TableDef>();

        foreach (var tableGroup in columns.All.GroupBy(r => (Schema: r["table_schema"], Table: r["table_name"])))
        {
            var (schema, table) = tableGroup.Key;

            var cols = tableGroup
                .OrderBy(Ordinal)
                .Select(r => new ColumnDef(
                    r["column_name"],
                    BuildType(r),
                    string.Equals(r["is_nullable"], "YES", StringComparison.OrdinalIgnoreCase),
                    r.NullIfBlank("column_default"),
                    r.Int("ordinal_position")))
                .ToList();

            var tableConstraints = constraints.All
                .Where(r => r["table_schema"] == schema && r["table_name"] == table)
                .ToList();

            PrimaryKeyDef? primaryKey = null;
            var uniques = new List<UniqueDef>();
            var foreignKeys = new List<ForeignKeyDef>();

            foreach (var constraintGroup in tableConstraints.GroupBy(r => r["constraint_name"]))
            {
                var type = constraintGroup.First()["constraint_type"];
                var name = constraintGroup.Key;
                var constraintColumns = constraintGroup.OrderBy(Ordinal).Select(r => r["column_name"]).ToList();

                switch (type.ToUpperInvariant())
                {
                    case "PRIMARY KEY":
                        primaryKey = new PrimaryKeyDef(name, constraintColumns);
                        break;
                    case "UNIQUE":
                        uniques.Add(new UniqueDef(name, constraintColumns));
                        break;
                    case "FOREIGN KEY" when refTargets.TryGetValue((schema, table, name), out var target):
                        foreignKeys.Add(new ForeignKeyDef(
                            name, constraintColumns,
                            schemaIsDatabase ? string.Empty : target.RefSchema,
                            target.RefTable, target.RefColumns));
                        break;
                }
            }

            var tableIndexes = indexes.All
                .Where(r => r["table_schema"] == schema && r["table_name"] == table)
                .GroupBy(r => r["index_name"])
                .Select(g => new IndexDef(
                    g.Key,
                    g.First().Flag("is_unique"),
                    g.OrderBy(Ordinal).Select(r => r["column_name"]).ToList()))
                .ToList();

            tables.Add(new TableDef(
                schemaIsDatabase ? string.Empty : schema, table, cols, primaryKey, tableIndexes, foreignKeys, uniques));
        }

        return tables;
    }

    // --- SQL ---

    private string ColumnsSql() => $"""
        SELECT c.table_schema, c.table_name, c.column_name, c.ordinal_position, c.is_nullable,
               c.column_default, c.data_type, c.character_maximum_length, c.numeric_precision, c.numeric_scale
        FROM information_schema.columns c
        JOIN information_schema.tables t
          ON t.table_schema = c.table_schema AND t.table_name = c.table_name
        WHERE t.table_type = 'BASE TABLE' AND {_filter("c.table_schema")}
        ORDER BY c.table_schema, c.table_name, c.ordinal_position
        """;

    private string ConstraintsSql() => $"""
        SELECT tc.table_schema, tc.table_name, tc.constraint_name, tc.constraint_type,
               kcu.column_name, kcu.ordinal_position
        FROM information_schema.table_constraints tc
        JOIN information_schema.key_column_usage kcu
          ON kcu.constraint_schema = tc.constraint_schema AND kcu.constraint_name = tc.constraint_name
         AND kcu.table_schema = tc.table_schema AND kcu.table_name = tc.table_name
        WHERE tc.constraint_type IN ('PRIMARY KEY', 'UNIQUE', 'FOREIGN KEY') AND {_filter("tc.table_schema")}
        ORDER BY tc.table_schema, tc.table_name, tc.constraint_name, kcu.ordinal_position
        """;

    // Referenced side of each foreign key. MySQL gets its own query: it names every primary key "PRIMARY",
    // so joining the referenced side by constraint name matches *every* table's PK — a cartesian product
    // that produced REFERENCES customers (id, id, id). MySQL carries the referenced table and column on the
    // foreign key's own row, which is both exact and cheaper. Postgres and SQL Server name constraints
    // uniquely per schema, so the portable join is correct there.
    private string ReferencesSql() => providerId == "mysql" ? $"""
        SELECT kcu.constraint_schema AS table_schema, kcu.table_name AS table_name,
               kcu.constraint_name AS constraint_name,
               kcu.referenced_table_schema AS ref_schema, kcu.referenced_table_name AS ref_table,
               kcu.referenced_column_name AS ref_column, kcu.ordinal_position
        FROM information_schema.key_column_usage kcu
        WHERE kcu.referenced_table_name IS NOT NULL AND {_filter("kcu.constraint_schema")}
        ORDER BY kcu.constraint_schema, kcu.table_name, kcu.constraint_name, kcu.ordinal_position
        """ : $"""
        SELECT rc.constraint_schema AS table_schema, kcu.table_name AS table_name,
               rc.constraint_name AS constraint_name,
               ccu.table_schema AS ref_schema, ccu.table_name AS ref_table,
               ccu.column_name AS ref_column, ccu.ordinal_position
        FROM information_schema.referential_constraints rc
        JOIN information_schema.key_column_usage kcu
          ON kcu.constraint_schema = rc.constraint_schema AND kcu.constraint_name = rc.constraint_name
        JOIN information_schema.key_column_usage ccu
          ON ccu.constraint_schema = rc.unique_constraint_schema
         AND ccu.constraint_name = rc.unique_constraint_name
         AND ccu.ordinal_position = kcu.ordinal_position
        WHERE {_filter("rc.constraint_schema")}
        ORDER BY table_schema, table_name, constraint_name, ccu.ordinal_position
        """;

    // Secondary indexes — one query per engine, since information_schema has no index view. Each excludes
    // the index an engine materialises for a primary key or unique constraint: those are read as
    // constraints already, and a duplicate would be dropped twice by the diff.
    private string IndexesSql() => providerId switch
    {
        "mysql" => $"""
            SELECT s.table_schema, s.table_name, s.index_name,
                   CASE WHEN s.non_unique = 0 THEN 1 ELSE 0 END AS is_unique,
                   s.column_name, s.seq_in_index AS ordinal_position
            FROM information_schema.statistics s
            WHERE s.index_name <> 'PRIMARY'
              AND NOT EXISTS (
                    SELECT 1 FROM information_schema.table_constraints tc
                    WHERE tc.table_schema = s.table_schema AND tc.table_name = s.table_name
                      AND tc.constraint_name = s.index_name
                      AND tc.constraint_type IN ('UNIQUE', 'FOREIGN KEY'))
              AND {_filter("s.table_schema")}
            ORDER BY s.table_schema, s.table_name, s.index_name, s.seq_in_index
            """,

        "sqlserver" => $"""
            SELECT sch.name AS table_schema, t.name AS table_name, i.name AS index_name,
                   CASE WHEN i.is_unique = 1 THEN 1 ELSE 0 END AS is_unique,
                   c.name AS column_name, ic.key_ordinal AS ordinal_position
            FROM sys.indexes i
            JOIN sys.tables t ON t.object_id = i.object_id
            JOIN sys.schemas sch ON sch.schema_id = t.schema_id
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.is_primary_key = 0 AND i.is_unique_constraint = 0
              AND i.type <> 0 AND i.is_hypothetical = 0 AND ic.is_included_column = 0
              AND {_filter("sch.name")}
            ORDER BY sch.name, t.name, i.name, ic.key_ordinal
            """,

        // Postgres: indkey is the ordered attribute list; unnest WITH ORDINALITY restores that order.
        // conindid ties an index to the constraint it backs, so constraint-backed indexes drop out.
        _ => $"""
            SELECT n.nspname AS table_schema, t.relname AS table_name, i.relname AS index_name,
                   CASE WHEN ix.indisunique THEN 1 ELSE 0 END AS is_unique,
                   a.attname AS column_name, k.ord AS ordinal_position
            FROM pg_index ix
            JOIN pg_class i ON i.oid = ix.indexrelid
            JOIN pg_class t ON t.oid = ix.indrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            JOIN LATERAL unnest(ix.indkey) WITH ORDINALITY AS k(attnum, ord) ON true
            JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum
            WHERE t.relkind = 'r' AND NOT ix.indisprimary
              AND NOT EXISTS (SELECT 1 FROM pg_constraint con WHERE con.conindid = i.oid)
              AND {_filter("n.nspname")}
            ORDER BY n.nspname, t.relname, i.relname, k.ord
            """
    };

    // --- helpers ---

    private static string BuildType(SqlRow r)
    {
        var type = r["data_type"];
        var charLen = r.NullableInt("character_maximum_length");
        var precision = r.NullableInt("numeric_precision");
        var scale = r.NullableInt("numeric_scale");

        if (charLen is > 0)
        {
            return $"{type}({charLen})";
        }

        // Only decimal/numeric carry a meaningful (precision, scale); integers report a precision we ignore.
        var isDecimal = type.Contains("numeric", StringComparison.OrdinalIgnoreCase)
            || type.Contains("decimal", StringComparison.OrdinalIgnoreCase);
        if (isDecimal && precision is { } p)
        {
            return scale is { } s and > 0 ? $"{type}({p},{s})" : $"{type}({p})";
        }

        return type;
    }

    private async Task<SqlRows> Query(ConnectionProfile profile, string sql, CancellationToken ct) =>
        SqlRows.From(await provider.ExecuteQueryAsync(profile, sql, ct));

    private static int Ordinal(SqlRow r) => r.Int("ordinal_position");
}
