namespace SqlExplorer.Tools.CopyTable;

/// <summary>
/// Reads one base table's shape — columns (type, nullability, default, identity) and primary key — from a
/// live connection through the host's <see cref="IDbProvider"/>, using ANSI <c>information_schema</c> so one
/// pair of queries covers Postgres, MySQL and SQL Server. The tool only knows the table's <em>name</em>
/// (the clicked node), not its schema, so it resolves the schema from the read; when the name is ambiguous
/// across schemas it takes the first and the tool notes it. Indexes and foreign keys are a follow-up.
/// </summary>
public sealed class TableReader(IDbProvider provider)
{
    public static bool Supports(string providerId) => providerId is "postgres" or "mysql" or "sqlserver";

    /// <summary>Read the named table, or null when no base table by that name exists on the connection.</summary>
    public async Task<TableModel?> ReadAsync(ConnectionProfile profile, string providerId, string tableName, CancellationToken ct)
    {
        var name = tableName.Replace("'", "''");
        var filter = SchemaFilter(providerId, profile.Database);

        var columns = await Query(profile, ColumnsSql(name, filter), ct);
        if (columns.Count == 0)
        {
            return null;
        }

        // Resolve the schema from the read; first wins when the name repeats across schemas.
        var schema = columns[0]["table_schema"];
        var identity = await IdentityColumnsAsync(profile, providerId, name, schema, ct);

        var cols = columns
            .Where(r => r["table_schema"] == schema)
            .OrderBy(r => ParseInt(r["ordinal_position"]))
            .Select(r => new TableColumn(
                r["column_name"],
                BuildType(r),
                string.Equals(r["is_nullable"], "YES", StringComparison.OrdinalIgnoreCase),
                NullIfBlank(r["column_default"]),
                ParseInt(r["ordinal_position"]),
                identity.Contains(r["column_name"])))
            .ToList();

        var pkRows = await Query(profile, PrimaryKeySql(name, schema, filter), ct);
        TablePrimaryKey? pk = pkRows.Count == 0
            ? null
            : new TablePrimaryKey(
                NullIfBlank(pkRows[0]["constraint_name"]),
                pkRows.OrderBy(r => ParseInt(r["ordinal_position"])).Select(r => r["column_name"]).ToList());

        return new TableModel(schema, tableName, cols, pk);
    }

    /// <summary>True when more than one schema on the connection has a base table by this name — the tool
    /// warns and scripts the first.</summary>
    public async Task<bool> IsAmbiguousAsync(ConnectionProfile profile, string providerId, string tableName, CancellationToken ct)
    {
        var name = tableName.Replace("'", "''");
        var rows = await Query(profile, ColumnsSql(name, SchemaFilter(providerId, profile.Database)), ct);
        return rows.Select(r => r["table_schema"]).Distinct(StringComparer.Ordinal).Count() > 1;
    }

    // The identity/auto-increment columns of the table — engine-specific, because information_schema exposes
    // it three different ways (Postgres is_identity / serial default, MySQL extra, SQL Server sys.identity_columns).
    private async Task<HashSet<string>> IdentityColumnsAsync(
        ConnectionProfile profile, string providerId, string tableName, string schema, CancellationToken ct)
    {
        var sch = schema.Replace("'", "''");
        var sql = providerId switch
        {
            "mysql" => $"""
                SELECT column_name FROM information_schema.columns
                WHERE table_name = '{tableName}' AND table_schema = '{sch}' AND extra LIKE '%auto_increment%'
                """,
            "sqlserver" => $"""
                SELECT c.name AS column_name
                FROM sys.identity_columns c
                JOIN sys.tables t ON t.object_id = c.object_id
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE t.name = '{tableName}' AND s.name = '{sch}'
                """,
            _ => $"""
                SELECT column_name FROM information_schema.columns
                WHERE table_name = '{tableName}' AND table_schema = '{sch}'
                  AND (is_identity = 'YES' OR column_default LIKE 'nextval(%')
                """
        };

        var rows = await Query(profile, sql, ct);
        return rows.Select(r => r["column_name"]).ToHashSet(StringComparer.Ordinal);
    }

    private static string ColumnsSql(string tableName, string filter) => $"""
        SELECT c.table_schema, c.column_name, c.ordinal_position, c.is_nullable, c.column_default,
               c.data_type, c.character_maximum_length, c.numeric_precision, c.numeric_scale
        FROM information_schema.columns c
        JOIN information_schema.tables t
          ON t.table_schema = c.table_schema AND t.table_name = c.table_name
        WHERE t.table_type = 'BASE TABLE' AND c.table_name = '{tableName}' AND {filter}
        ORDER BY c.table_schema, c.ordinal_position
        """;

    private static string PrimaryKeySql(string tableName, string schema, string filter) => $"""
        SELECT tc.constraint_name, kcu.column_name, kcu.ordinal_position
        FROM information_schema.table_constraints tc
        JOIN information_schema.key_column_usage kcu
          ON kcu.constraint_schema = tc.constraint_schema AND kcu.constraint_name = tc.constraint_name
         AND kcu.table_schema = tc.table_schema AND kcu.table_name = tc.table_name
        WHERE tc.constraint_type = 'PRIMARY KEY' AND tc.table_name = '{tableName}'
          AND tc.table_schema = '{schema.Replace("'", "''")}' AND {Rewrite(filter)}
        ORDER BY kcu.ordinal_position
        """;

    private static string Rewrite(string filter) => filter.Replace("c.table_schema", "tc.table_schema");

    private static string SchemaFilter(string providerId, string? database) => providerId switch
    {
        "mysql" => $"c.table_schema = '{(database ?? string.Empty).Replace("'", "''")}'",
        "sqlserver" => "c.table_schema NOT IN ('sys', 'INFORMATION_SCHEMA')",
        _ => "c.table_schema NOT IN ('pg_catalog', 'information_schema')"
    };

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

        var isDecimal = type.Contains("numeric", StringComparison.OrdinalIgnoreCase)
            || type.Contains("decimal", StringComparison.OrdinalIgnoreCase);
        if (isDecimal && precision is { } p)
        {
            return scale is { } s and > 0 ? $"{type}({p},{s})" : $"{type}({p})";
        }

        return type;
    }

    private async Task<IReadOnlyList<Row>> Query(ConnectionProfile profile, string sql, CancellationToken ct)
    {
        var result = await provider.ExecuteQueryAsync(profile, sql, ct);
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < result.Columns.Count; i++)
        {
            index[result.Columns[i].Name] = i;
        }

        return result.Rows.Select(cells => new Row(cells, index)).ToList();
    }

    private static int ParseInt(string? s) => int.TryParse(s, out var n) ? n : 0;
    private static int? ParseNullableInt(string? s) => int.TryParse(s, out var n) ? n : null;
    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    // Case-insensitive cell access by column name, so the reader survives however an engine cases its
    // information_schema headers.
    private sealed class Row(object?[] cells, Dictionary<string, int> index)
    {
        public string this[string column] =>
            index.TryGetValue(column, out var i) && cells[i] is { } v ? v.ToString() ?? string.Empty : string.Empty;
    }
}
