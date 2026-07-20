namespace SqlExplorer.Tools.SchemaDiff;

/// <summary>
/// Reads a <see cref="SchemaSnapshot"/> from a live connection through the host's <see cref="IDbProvider"/>,
/// using ANSI <c>information_schema</c> views — so one set of queries covers Postgres, MySQL and SQL Server.
/// It reads base tables, their columns (type, nullability, default), and the primary-key / unique /
/// foreign-key constraints on them.
///
/// <para><b>Scope (v1):</b> secondary (non-unique) indexes and engines without <c>information_schema</c>
/// (SQLite) are not read yet — the model and differ already support indexes, so that is an additive follow-up.
/// <see cref="Supports"/> reports whether an engine can be read at all.</para>
/// </summary>
public sealed class SchemaReader(IDbProvider provider)
{
    public static bool Supports(string providerId) => providerId is "postgres" or "mysql" or "sqlserver";

    public async Task<SchemaSnapshot> ReadAsync(ConnectionProfile profile, string providerId, CancellationToken ct)
    {
        var filter = SchemaFilter(providerId, profile.Database);

        var columns = await Query(profile, ColumnsSql(filter), ct);
        var constraints = await Query(profile, ConstraintsSql(filter), ct);
        var references = await Query(profile, ReferencesSql(filter), ct);

        var tables = BuildTables(columns, constraints, references);
        return new SchemaSnapshot(tables);
    }

    private static IReadOnlyList<TableDef> BuildTables(Rows columns, Rows constraints, Rows references)
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
                    NullIfBlank(r["column_default"]),
                    ParseInt(r["ordinal_position"])))
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
                            name, constraintColumns, target.RefSchema, target.RefTable, target.RefColumns));
                        break;
                }
            }

            tables.Add(new TableDef(schema, table, cols, primaryKey, [], foreignKeys, uniques));
        }

        return tables;
    }

    // --- SQL (ANSI information_schema; portable across Postgres/MySQL/SQL Server) ---

    private static string ColumnsSql(string filter) => $"""
        SELECT c.table_schema, c.table_name, c.column_name, c.ordinal_position, c.is_nullable,
               c.column_default, c.data_type, c.character_maximum_length, c.numeric_precision, c.numeric_scale
        FROM information_schema.columns c
        JOIN information_schema.tables t
          ON t.table_schema = c.table_schema AND t.table_name = c.table_name
        WHERE t.table_type = 'BASE TABLE' AND {filter}
        ORDER BY c.table_schema, c.table_name, c.ordinal_position
        """;

    private static string ConstraintsSql(string filter) => $"""
        SELECT tc.table_schema, tc.table_name, tc.constraint_name, tc.constraint_type,
               kcu.column_name, kcu.ordinal_position
        FROM information_schema.table_constraints tc
        JOIN information_schema.key_column_usage kcu
          ON kcu.constraint_schema = tc.constraint_schema AND kcu.constraint_name = tc.constraint_name
         AND kcu.table_schema = tc.table_schema AND kcu.table_name = tc.table_name
        WHERE tc.constraint_type IN ('PRIMARY KEY', 'UNIQUE', 'FOREIGN KEY') AND {Rewrite(filter)}
        ORDER BY tc.table_schema, tc.table_name, tc.constraint_name, kcu.ordinal_position
        """;

    // Referenced side of each foreign key: join referential_constraints to the referenced unique/PK
    // constraint's columns. Portable across Postgres/MySQL/SQL Server.
    private static string ReferencesSql(string filter) => $"""
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
        WHERE {ReferencesFilter(filter)}
        ORDER BY table_schema, table_name, constraint_name, ccu.ordinal_position
        """;

    // The columns filter is written against alias `c`/`t`; the other queries alias differently.
    private static string Rewrite(string filter) => filter.Replace("c.table_schema", "tc.table_schema");

    private static string ReferencesFilter(string filter) => filter.Replace("c.table_schema", "rc.constraint_schema");

    private static string SchemaFilter(string providerId, string? database) => providerId switch
    {
        // MySQL's information_schema spans every database; pin it to the connected one.
        "mysql" => $"c.table_schema = '{(database ?? string.Empty).Replace("'", "''")}'",
        "sqlserver" => "c.table_schema NOT IN ('sys', 'INFORMATION_SCHEMA')",
        _ => "c.table_schema NOT IN ('pg_catalog', 'information_schema')"
    };

    // --- helpers ---

    private static string BuildType(Row r)
    {
        var type = r["data_type"];
        var charLen = ParseNullableInt(r["character_maximum_length"]);
        var precision = ParseNullableInt(r["numeric_precision"]);
        var scale = ParseNullableInt(r["numeric_scale"]);

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

    private async Task<Rows> Query(ConnectionProfile profile, string sql, CancellationToken ct)
    {
        var result = await provider.ExecuteQueryAsync(profile, sql, ct);
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < result.Columns.Count; i++)
        {
            index[result.Columns[i].Name] = i;
        }

        var rows = result.Rows.Select(cells => new Row(cells, index)).ToList();
        return new Rows(rows);
    }

    private static int Ordinal(Row r) => ParseInt(r["ordinal_position"]);

    private static int ParseInt(string? s) => int.TryParse(s, out var n) ? n : 0;

    private static int? ParseNullableInt(string? s) => int.TryParse(s, out var n) ? n : null;

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    // Thin case-insensitive row/rowset over a QueryResult, so the reader addresses cells by column name
    // regardless of how an engine cases its information_schema column headers.
    private sealed record Rows(IReadOnlyList<Row> All);

    private sealed class Row(object?[] cells, Dictionary<string, int> index)
    {
        public string this[string column] =>
            index.TryGetValue(column, out var i) && cells[i] is { } v ? v.ToString() ?? string.Empty : string.Empty;
    }
}
