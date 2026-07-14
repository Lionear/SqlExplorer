using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Lionear.SqlExplorer.Sdk;
using MySqlConnector;

namespace Lionear.SqlExplorer.Providers.MySql;

public sealed class MySqlProvider : IDbProvider
{
    public string DisplayName => "MySQL / MariaDB";

    // Uses the embedded brand PNG (icon.png) when present; falls back to a glyph otherwise.
    public ProviderIcon? Icon { get; } = ProviderIconLoader.Load(typeof(MySqlProvider), "🐬");

    public ISqlDialect Dialect { get; } = new MySqlDialect();

    public IReadOnlyList<ConnectionField> ConnectionFields { get; } =
    [
        new("host", "Host", ConnectionFieldType.Text, Required: true, Default: "localhost"),
        new("port", "Port", ConnectionFieldType.Number, Default: "3306"),
        new("database", "Database", ConnectionFieldType.Text, Required: true),
        new("username", "Username", ConnectionFieldType.Text, Required: true, Default: "root"),
        new("password", "Password", ConnectionFieldType.Password)
    ];

    public string BuildConnectionString(IReadOnlyDictionary<string, string?> values)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = Value(values, "host") ?? "localhost",
            Database = Value(values, "database"),
            UserID = Value(values, "username"),
            Password = Value(values, "password")
        };

        if (uint.TryParse(Value(values, "port"), out var port))
        {
            builder.Port = port;
        }

        return builder.ConnectionString;
    }

    private static string? Value(IReadOnlyDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    // Inverse of BuildConnectionString: map only the keys the pasted string actually set.
    public IReadOnlyDictionary<string, string?>? ParseConnectionString(string connectionString)
    {
        var b = new MySqlConnectionStringBuilder(connectionString);
        var result = new Dictionary<string, string?>();

        if (b.ContainsKey("Server")) result["host"] = b.Server;
        if (b.ContainsKey("Port")) result["port"] = b.Port.ToString();
        if (b.ContainsKey("Database")) result["database"] = b.Database;
        if (b.ContainsKey("User ID")) result["username"] = b.UserID;
        if (b.ContainsKey("Password")) result["password"] = b.Password;

        return result;
    }

    public async Task<bool> TestConnectionAsync(ConnectionProfile profile, CancellationToken ct)
    {
        await using var connection = await OpenAsync(profile, ct);
        return connection.State == ConnectionState.Open;
    }

    public async Task<QueryResult> ExecuteQueryAsync(ConnectionProfile profile, string sql, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        await using var connection = await OpenAsync(profile, ct);

        await using var command = new MySqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);

        return await ReadResultAsync(reader, stopwatch, ct);
    }

    public async Task<IReadOnlyList<QueryResult>> ExecuteScriptAsync(ConnectionProfile profile, string sql, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        await using var connection = await OpenAsync(profile, ct);
        await using var command = new MySqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var results = new List<QueryResult>();
        do
        {
            results.Add(await ReadResultAsync(reader, stopwatch, ct));
        } while (await reader.NextResultAsync(ct));

        return results;
    }

    private static async Task<QueryResult> ReadResultAsync(MySqlDataReader reader, Stopwatch stopwatch, CancellationToken ct)
    {
        var columns = BuildColumns(reader);

        var rows = new List<object?[]>();
        while (await reader.ReadAsync(ct))
        {
            var row = new object?[reader.FieldCount];
            reader.GetValues(row!);
            for (var i = 0; i < row.Length; i++)
            {
                if (row[i] is DBNull)
                {
                    row[i] = null;
                }
            }

            rows.Add(row);
        }

        return new QueryResult
        {
            Columns = columns,
            Rows = rows,
            RecordsAffected = reader.RecordsAffected,
            Elapsed = stopwatch.Elapsed
        };
    }

    // MySqlConnector fills base table/column names and the PK flag from the protocol's column
    // definitions, so the result maps back to a table for the editable-grid save-flow (Notes §8).
    private static List<ResultColumn> BuildColumns(MySqlDataReader reader)
    {
        var schema = reader.GetColumnSchema();
        var columns = new List<ResultColumn>(reader.FieldCount);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var col = schema[i];
            columns.Add(new ResultColumn(reader.GetName(i), reader.GetFieldType(i))
            {
                BaseSchema = col.BaseSchemaName,
                BaseTable = col.BaseTableName,
                BaseColumn = col.BaseColumnName,
                IsKey = col.IsKey ?? false,
                IsReadOnly = col.IsReadOnly ?? false,
                AllowDbNull = col.AllowDBNull ?? true
            });
        }

        return columns;
    }

    public async Task<int> ExecuteBatchAsync(
        ConnectionProfile profile,
        IReadOnlyList<SqlStatement> statements,
        CancellationToken ct)
    {
        await using var connection = await OpenAsync(profile, ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var affected = 0;
        foreach (var statement in statements)
        {
            await using var command = new MySqlCommand(statement.Text, connection, transaction);
            foreach (var parameter in statement.Parameters)
            {
                command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            }

            affected += await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return affected;
    }

    // MySQL has no separate schema layer: a "database" is the schema. The tree is
    // Database → (Tables|Views folder) → Table|View → Column, all read from information_schema.
    public async Task<IReadOnlyList<DbTreeNode>> GetChildNodesAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct)
    {
        var parent = ancestors.Count == 0 ? (DbNodeKind?)null : ancestors[^1].Kind;

        return parent switch
        {
            null => await LoadDatabasesAsync(profile, ct),
            DbNodeKind.Database => Folders(),
            DbNodeKind.TableFolder => await LoadRelationsAsync(profile, ancestors, isView: false, ct),
            DbNodeKind.ViewFolder => await LoadRelationsAsync(profile, ancestors, isView: true, ct),
            // Tables carry extra "Indexes"/"Foreign Keys" folders; views have neither.
            DbNodeKind.Table => [ColumnFolder(), IndexFolder(), ForeignKeyFolder()],
            DbNodeKind.ColumnFolder => await LoadColumnsAsync(profile, ancestors.Take(ancestors.Count - 1).ToList(), ct),
            DbNodeKind.View => await LoadColumnsAsync(profile, ancestors, ct),
            DbNodeKind.IndexFolder => await LoadIndexesAsync(profile, ancestors, ct),
            DbNodeKind.ForeignKeyFolder => await LoadForeignKeysAsync(profile, ancestors, ct),
            _ => []
        };
    }

    private static IReadOnlyList<DbTreeNode> Folders() =>
    [
        new() { Kind = DbNodeKind.TableFolder, Name = "Tables", HasChildren = true },
        new() { Kind = DbNodeKind.ViewFolder, Name = "Views", HasChildren = true }
    ];

    private static DbTreeNode ColumnFolder() =>
        new() { Kind = DbNodeKind.ColumnFolder, Name = "Columns", HasChildren = true };

    private static DbTreeNode IndexFolder() =>
        new() { Kind = DbNodeKind.IndexFolder, Name = "Indexes", HasChildren = true };

    private static DbTreeNode ForeignKeyFolder() =>
        new() { Kind = DbNodeKind.ForeignKeyFolder, Name = "Foreign Keys", HasChildren = true };

    private async Task<IReadOnlyList<DbTreeNode>> LoadDatabasesAsync(ConnectionProfile profile, CancellationToken ct)
    {
        var sizes = await LoadDatabaseSizesAsync(profile, ct);
        return (await GetDatabasesAsync(profile, ct))
            .Select(name => new DbTreeNode
            {
                Kind = DbNodeKind.Database,
                Name = name,
                HasChildren = true,
                Badge = sizes.TryGetValue(name, out var bytes) ? ByteSize.Format(bytes) : null
            })
            .ToList();
    }

    // Per-schema on-disk size = sum of each table's data + index length. Best-effort.
    private async Task<IReadOnlyDictionary<string, long>> LoadDatabaseSizesAsync(ConnectionProfile profile, CancellationToken ct)
    {
        const string sql = """
            SELECT table_schema, SUM(data_length + index_length)
            FROM information_schema.tables
            GROUP BY table_schema
            """;

        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        try
        {
            await using var connection = new MySqlConnection(profile.ConnectionString);
            await connection.OpenAsync(ct);
            await using var command = new MySqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (!reader.IsDBNull(1))
                {
                    sizes[reader.GetString(0)] = Convert.ToInt64(reader.GetValue(1));
                }
            }
        }
        catch
        {
            // No access → no badges.
        }

        return sizes;
    }

    private static async Task<IReadOnlyList<DbTreeNode>> LoadRelationsAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        bool isView,
        CancellationToken ct)
    {
        // data_length + index_length gives the table's on-disk size; it's NULL for views (no storage).
        const string sql = """
            SELECT table_name, data_length + index_length
            FROM information_schema.tables
            WHERE table_schema = @db AND table_type = @type
            ORDER BY table_name
            """;

        var kind = isView ? DbNodeKind.View : DbNodeKind.Table;
        var nodes = new List<DbTreeNode>();
        await using var connection = new MySqlConnection(profile.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("db", Name(ancestors, DbNodeKind.Database));
        command.Parameters.AddWithValue("type", isView ? "VIEW" : "BASE TABLE");
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            nodes.Add(new DbTreeNode
            {
                Kind = kind,
                Name = reader.GetString(0),
                HasChildren = true,
                Badge = reader.IsDBNull(1) ? null : ByteSize.Format(Convert.ToInt64(reader.GetValue(1)))
            });
        }

        return nodes;
    }

    private static async Task<IReadOnlyList<DbTreeNode>> LoadColumnsAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct)
    {
        var database = Name(ancestors, DbNodeKind.Database);
        var table = ancestors[^1].Name;

        const string pkSql = """
            SELECT column_name FROM information_schema.key_column_usage
            WHERE table_schema = @db AND table_name = @table AND constraint_name = 'PRIMARY'
            """;

        const string colSql = """
            SELECT column_name, column_type, is_nullable
            FROM information_schema.columns
            WHERE table_schema = @db AND table_name = @table
            ORDER BY ordinal_position
            """;

        await using var connection = new MySqlConnection(profile.ConnectionString);
        await connection.OpenAsync(ct);

        var primaryKeys = new HashSet<string>();
        await using (var pkCommand = new MySqlCommand(pkSql, connection))
        {
            pkCommand.Parameters.AddWithValue("db", database);
            pkCommand.Parameters.AddWithValue("table", table);
            await using var pkReader = await pkCommand.ExecuteReaderAsync(ct);
            while (await pkReader.ReadAsync(ct))
            {
                primaryKeys.Add(pkReader.GetString(0));
            }
        }

        var nodes = new List<DbTreeNode>();
        await using (var colCommand = new MySqlCommand(colSql, connection))
        {
            colCommand.Parameters.AddWithValue("db", database);
            colCommand.Parameters.AddWithValue("table", table);
            await using var colReader = await colCommand.ExecuteReaderAsync(ct);
            while (await colReader.ReadAsync(ct))
            {
                var name = colReader.GetString(0);
                var dataType = colReader.GetString(1);
                var pk = primaryKeys.Contains(name) ? " (PK)" : string.Empty;
                nodes.Add(new DbTreeNode { Kind = DbNodeKind.Column, Name = name, Detail = $"{dataType}{pk}" });
            }
        }

        return nodes;
    }

    private static async Task<IReadOnlyList<DbTreeNode>> LoadIndexesAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct)
    {
        // statistics has one row per index column; collapse to one row per index.
        const string sql = """
            SELECT index_name, MIN(non_unique) AS non_unique
            FROM information_schema.statistics
            WHERE table_schema = @db AND table_name = @table
            GROUP BY index_name
            ORDER BY index_name
            """;

        var nodes = new List<DbTreeNode>();
        await using var connection = new MySqlConnection(profile.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("db", Name(ancestors, DbNodeKind.Database));
        command.Parameters.AddWithValue("table", Name(ancestors, DbNodeKind.Table));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            var unique = Convert.ToInt64(reader.GetValue(1)) == 0;
            var detail = name == "PRIMARY" ? "PK" : unique ? "unique" : null;
            nodes.Add(new DbTreeNode { Kind = DbNodeKind.Index, Name = name, Detail = detail });
        }

        return nodes;
    }

    private static async Task<IReadOnlyList<DbTreeNode>> LoadForeignKeysAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct)
    {
        const string sql = """
            SELECT constraint_name, column_name, referenced_table_name, referenced_column_name
            FROM information_schema.key_column_usage
            WHERE table_schema = @db AND table_name = @table AND referenced_table_name IS NOT NULL
            ORDER BY constraint_name, ordinal_position
            """;

        var nodes = new List<DbTreeNode>();
        await using var connection = new MySqlConnection(profile.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("db", Name(ancestors, DbNodeKind.Database));
        command.Parameters.AddWithValue("table", Name(ancestors, DbNodeKind.Table));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            var column = reader.GetString(1);
            var refTable = reader.GetString(2);
            var refColumn = reader.GetString(3);
            nodes.Add(new DbTreeNode { Kind = DbNodeKind.ForeignKey, Name = name, Detail = $"{column} → {refTable}.{refColumn}" });
        }

        return nodes;
    }

    private static string Name(IReadOnlyList<DbNodeRef> ancestors, DbNodeKind kind) =>
        ancestors.First(a => a.Kind == kind).Name;

    // Re-point the connection at a sibling database on the same server; host/credentials stay intact.
    private static string ConnectionStringFor(ConnectionProfile profile, string database) =>
        new MySqlConnectionStringBuilder(profile.ConnectionString) { Database = database }.ConnectionString;

    // Open against the tree's database when the host set ConnectionProfile.Database (query-tab database
    // switcher, DDL on a specific db); otherwise the connection's own default. Previously ignored here
    // entirely (see the removed comment above ExecuteQueryAsync) — now honoured like MsSql/Postgres.
    private static async Task<MySqlConnection> OpenAsync(ConnectionProfile profile, CancellationToken ct)
    {
        var connectionString = string.IsNullOrWhiteSpace(profile.Database)
            ? profile.ConnectionString
            : ConnectionStringFor(profile, profile.Database);

        var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    // MySQL has no separate schema layer — "database" doubles as schema, so only Database and Table are
    // creatable (no CreateCapability for Schema, unlike Postgres/MsSql).
    public IReadOnlyList<CreateCapability> CreateCapabilities { get; } =
    [
        new(DbObjectKind.Database, null),
        new(DbObjectKind.Table, DbNodeKind.TableFolder)
    ];

    public IReadOnlyList<string> ColumnTypes { get; } =
        ["INT", "BIGINT", "VARCHAR(255)", "TEXT", "BOOLEAN", "DECIMAL(18,2)", "DATETIME", "DATE", "JSON"];

    public SqlStatement BuildCreateStatement(CreateObjectSpec spec)
    {
        var sql = spec.Kind switch
        {
            // CREATE DATABASE and CREATE SCHEMA are synonyms in MySQL; database creation is the only
            // "container" this provider declares (see CreateCapabilities).
            DbObjectKind.Database => $"CREATE DATABASE {Dialect.QuoteIdentifier(spec.Name)}",
            // No schema layer to qualify with — the connection is already pointed at the target
            // database via ConnectionProfile.Database when this runs (see ExecuteDdlAsync).
            DbObjectKind.Table => BuildCreateTable(spec),
            _ => throw new NotSupportedException($"MySQL cannot create a {spec.Kind}.")
        };

        return new SqlStatement(sql, []);
    }

    private string BuildCreateTable(CreateObjectSpec spec)
    {
        // MySQL requires an AUTO_INCREMENT column to be a key — satisfied here whenever it's also the
        // primary key (the common case); left to a normal DB error otherwise, same as any other DDL failure.
        var columns = spec.Columns.Select(c =>
            $"{Dialect.QuoteIdentifier(c.Name)} {c.Type}{(c.AutoIncrement ? " AUTO_INCREMENT" : "")}{(c.Nullable ? "" : " NOT NULL")}");

        var primaryKey = spec.Columns.Where(c => c.PrimaryKey).Select(c => Dialect.QuoteIdentifier(c.Name)).ToList();
        var clauses = primaryKey.Count > 0
            ? columns.Append($"PRIMARY KEY ({string.Join(", ", primaryKey)})")
            : columns;

        return $"CREATE TABLE {Dialect.QuoteIdentifier(spec.Name)} ({string.Join(", ", clauses)})";
    }

    // MySQL DDL auto-commits regardless — no explicit transaction needed here either way.
    public async Task ExecuteDdlAsync(ConnectionProfile profile, string sql, CancellationToken ct)
    {
        await using var connection = await OpenAsync(profile, ct);
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<QueryResult> ExplainAsync(ConnectionProfile profile, string sql, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        await using var connection = await OpenAsync(profile, ct);
        await using var command = new MySqlCommand($"EXPLAIN {sql}", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await ReadResultAsync(reader, stopwatch, ct);
    }

    public async Task<IReadOnlyList<string>> GetDatabasesAsync(ConnectionProfile profile, CancellationToken ct)
    {
        const string sql = """
            SELECT schema_name FROM information_schema.schemata
            WHERE schema_name NOT IN ('information_schema', 'mysql', 'performance_schema', 'sys')
            ORDER BY schema_name
            """;

        var names = new List<string>();
        await using var connection = new MySqlConnection(profile.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
