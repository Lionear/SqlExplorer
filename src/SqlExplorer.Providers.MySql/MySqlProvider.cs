using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using SqlExplorer.Sdk;
using MySqlConnector;

namespace SqlExplorer.Providers.MySql;

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
            Password = Value(values, "password"),
            // MySqlConnector otherwise treats every @name in ad-hoc SQL as a parameter placeholder, which
            // breaks session variables (@out1) — needed for the routine "Execute…" OUT-capture script and
            // generally expected in a SQL editor (SET @x := …; SELECT @x;).
            AllowUserVariables = true
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

    // MySqlConnector reports the server version (e.g. "8.0.34" / "11.4.2-MariaDB") on the open connection.
    public async Task<string?> GetServerVersionAsync(ConnectionProfile profile, CancellationToken ct)
    {
        await using var connection = await OpenAsync(profile, ct);
        return string.IsNullOrWhiteSpace(connection.ServerVersion) ? null : connection.ServerVersion;
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
            // Root: the databases plus a server-wide Security group (mysql.user is not per-database).
            null => [.. await LoadDatabasesAsync(profile, ct), new DbTreeNode { Kind = DbNodeKind.Group, Name = "Security", HasChildren = true }],
            DbNodeKind.Group => [new DbTreeNode { Kind = DbNodeKind.UserFolder, Name = "Users", HasChildren = true }],
            DbNodeKind.UserFolder => await LoadUsersAsync(profile, ct),
            DbNodeKind.Database => await FoldersAsync(profile, ancestors, ct),
            DbNodeKind.TableFolder => await LoadRelationsAsync(profile, ancestors, isView: false, ct),
            DbNodeKind.ViewFolder => await LoadRelationsAsync(profile, ancestors, isView: true, ct),
            DbNodeKind.ProcedureFolder => await LoadRoutinesAsync(profile, ancestors, isFunction: false, ct),
            DbNodeKind.FunctionFolder => await LoadRoutinesAsync(profile, ancestors, isFunction: true, ct),
            DbNodeKind.TriggerFolder => await LoadTriggersAsync(profile, ancestors, ct),
            // Tables carry extra "Indexes"/"Foreign Keys"/"Triggers" folders; views have none.
            DbNodeKind.Table => [ColumnFolder(), IndexFolder(), ForeignKeyFolder(), TriggerFolder()],
            DbNodeKind.ColumnFolder => await LoadColumnsAsync(profile, ancestors.Take(ancestors.Count - 1).ToList(), ct),
            DbNodeKind.View => await LoadColumnsAsync(profile, ancestors, ct),
            DbNodeKind.IndexFolder => await LoadIndexesAsync(profile, ancestors, ct),
            DbNodeKind.ForeignKeyFolder => await LoadForeignKeysAsync(profile, ancestors, ct),
            _ => []
        };
    }

    // Database folders with child counts ("Tables (22)"), so the size shows without expanding. Counts come
    // from the same information_schema sources the Load*Async methods read (tables + ROUTINES).
    private async Task<IReadOnlyList<DbTreeNode>> FoldersAsync(
        ConnectionProfile profile, IReadOnlyList<DbNodeRef> ancestors, CancellationToken ct)
    {
        var db = Name(ancestors, DbNodeKind.Database);
        int tables = 0, views = 0, procedures = 0, functions = 0;
        await using var connection = new MySqlConnection(ConnectionStringFor(profile, db));
        await connection.OpenAsync(ct);

        await using (var cmd = new MySqlCommand(
            "SELECT table_type, COUNT(*) FROM information_schema.tables WHERE table_schema = @db GROUP BY table_type", connection))
        {
            cmd.Parameters.AddWithValue("db", db);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (r.GetString(0) == "VIEW") views = (int)r.GetInt64(1); else tables += (int)r.GetInt64(1);
            }
        }

        await using (var cmd = new MySqlCommand(
            "SELECT ROUTINE_TYPE, COUNT(*) FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = @db GROUP BY ROUTINE_TYPE", connection))
        {
            cmd.Parameters.AddWithValue("db", db);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (r.GetString(0) == "PROCEDURE") procedures = (int)r.GetInt64(1); else functions = (int)r.GetInt64(1);
            }
        }

        return
        [
            Folder(DbNodeKind.TableFolder, "Tables", tables),
            Folder(DbNodeKind.ViewFolder, "Views", views),
            Folder(DbNodeKind.ProcedureFolder, "Procedures", procedures),
            Folder(DbNodeKind.FunctionFolder, "Functions", functions)
        ];
    }

    private static DbTreeNode Folder(DbNodeKind kind, string name, int count) =>
        new() { Kind = kind, Name = name, Count = count, HasChildren = count > 0 };

    private static DbTreeNode ColumnFolder() =>
        new() { Kind = DbNodeKind.ColumnFolder, Name = "Columns", HasChildren = true };

    private static DbTreeNode IndexFolder() =>
        new() { Kind = DbNodeKind.IndexFolder, Name = "Indexes", HasChildren = true };

    private static DbTreeNode ForeignKeyFolder() =>
        new() { Kind = DbNodeKind.ForeignKeyFolder, Name = "Foreign Keys", HasChildren = true };

    private static DbTreeNode TriggerFolder() =>
        new() { Kind = DbNodeKind.TriggerFolder, Name = "Triggers", HasChildren = true };

    private static readonly HashSet<string> SystemSchemas =
        new(StringComparer.OrdinalIgnoreCase) { "information_schema", "mysql", "performance_schema", "sys" };

    private async Task<IReadOnlyList<DbTreeNode>> LoadDatabasesAsync(ConnectionProfile profile, CancellationToken ct)
    {
        // All schemas here (system ones flagged) — the host decides whether to show them. The switcher's
        // GetDatabasesAsync stays user-only.
        const string sql = "SELECT schema_name FROM information_schema.schemata ORDER BY schema_name";

        var sizes = await LoadDatabaseSizesAsync(profile, ct);
        var nodes = new List<DbTreeNode>();
        await using var connection = new MySqlConnection(profile.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            nodes.Add(new DbTreeNode
            {
                Kind = DbNodeKind.Database,
                Name = name,
                HasChildren = true,
                IsSystem = SystemSchemas.Contains(name),
                Badge = sizes.TryGetValue(name, out var bytes) ? ByteSize.Format(bytes) : null
            });
        }

        return nodes;
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
        // data_length + index_length gives the table's on-disk size, table_rows an estimate; both are
        // NULL for views (no storage).
        const string sql = """
            SELECT table_name, data_length + index_length, table_rows
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
                Badge = reader.IsDBNull(1) ? null : ByteSize.Format(Convert.ToInt64(reader.GetValue(1))),
                Tooltip = reader.IsDBNull(2) ? null : TableStats.RowTooltip(Convert.ToInt64(reader.GetValue(2)))
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

    // Procedures and functions are database-scoped (MySQL's database == schema). Leaf nodes.
    private static async Task<IReadOnlyList<DbTreeNode>> LoadRoutinesAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        bool isFunction,
        CancellationToken ct)
    {
        const string sql = """
            SELECT ROUTINE_NAME FROM information_schema.ROUTINES
            WHERE ROUTINE_SCHEMA = @db AND ROUTINE_TYPE = @type
            ORDER BY ROUTINE_NAME
            """;

        var kind = isFunction ? DbNodeKind.Function : DbNodeKind.Procedure;
        var nodes = new List<DbTreeNode>();
        await using var connection = new MySqlConnection(profile.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("db", Name(ancestors, DbNodeKind.Database));
        command.Parameters.AddWithValue("type", isFunction ? "FUNCTION" : "PROCEDURE");
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            nodes.Add(new DbTreeNode { Kind = kind, Name = reader.GetString(0) });
        }

        return nodes;
    }

    private static async Task<IReadOnlyList<DbTreeNode>> LoadTriggersAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct)
    {
        const string sql = """
            SELECT TRIGGER_NAME FROM information_schema.TRIGGERS
            WHERE TRIGGER_SCHEMA = @db AND EVENT_OBJECT_TABLE = @table
            ORDER BY TRIGGER_NAME
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
            nodes.Add(new DbTreeNode { Kind = DbNodeKind.Trigger, Name = reader.GetString(0) });
        }

        return nodes;
    }

    // View Definition: SHOW CREATE PROCEDURE/FUNCTION/TRIGGER — roundtrip-safe (includes the CREATE
    // SHOW CREATE {kind}. The create text's result column differs by kind: TABLE/VIEW put it at ordinal 1,
    // while PROCEDURE/FUNCTION/TRIGGER carry a leading sql_mode column so it's at ordinal 2.
    public async Task<string?> GetObjectDefinitionAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct)
    {
        var (what, createColumn) = ancestors[^1].Kind switch
        {
            DbNodeKind.Table => ("TABLE", 1),
            DbNodeKind.View => ("VIEW", 1),
            DbNodeKind.Procedure => ("PROCEDURE", 2),
            DbNodeKind.Function => ("FUNCTION", 2),
            DbNodeKind.Trigger => ("TRIGGER", 2),
            _ => (null, 0)
        };

        if (what is null)
        {
            return null;
        }

        var db = Name(ancestors, DbNodeKind.Database);
        var qualified = $"{Dialect.QuoteIdentifier(db)}.{Dialect.QuoteIdentifier(ancestors[^1].Name)}";
        await using var connection = new MySqlConnection(ConnectionStringFor(profile, db));
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand($"SHOW CREATE {what} {qualified}", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) && !reader.IsDBNull(createColumn) ? reader.GetString(createColumn) : null;
    }

    public async Task<IReadOnlyList<RoutineParameter>> GetRoutineParametersAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct)
    {
        // ORDINAL_POSITION 0 is a function's return value (no name/mode) — excluded. ROUTINE_TYPE
        // disambiguates a procedure and function that share a name in the same database.
        const string sql = """
            SELECT PARAMETER_NAME, DATA_TYPE, PARAMETER_MODE
            FROM information_schema.PARAMETERS
            WHERE SPECIFIC_SCHEMA = @db AND SPECIFIC_NAME = @name
              AND ROUTINE_TYPE = @type AND ORDINAL_POSITION > 0
            ORDER BY ORDINAL_POSITION
            """;

        var isProcedure = ancestors[^1].Kind == DbNodeKind.Procedure;
        var result = new List<RoutineParameter>();
        await using var connection = new MySqlConnection(ConnectionStringFor(profile, Name(ancestors, DbNodeKind.Database)));
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("db", Name(ancestors, DbNodeKind.Database));
        command.Parameters.AddWithValue("name", ancestors[^1].Name);
        command.Parameters.AddWithValue("type", isProcedure ? "PROCEDURE" : "FUNCTION");
        await using var reader = await command.ExecuteReaderAsync(ct);
        var index = 0;
        while (await reader.ReadAsync(ct))
        {
            index++;
            var name = reader.IsDBNull(0) ? $"p{index}" : reader.GetString(0);
            var type = reader.GetString(1);
            var mode = reader.IsDBNull(2) ? "IN" : reader.GetString(2);
            // MySQL requires OUT and INOUT arguments to be session variables (a literal errors), so both
            // are captured as outputs. INOUT loses its input value in v1 (starts NULL) — a known limitation.
            var isOutput = mode.Equals("OUT", StringComparison.OrdinalIgnoreCase)
                || mode.Equals("INOUT", StringComparison.OrdinalIgnoreCase);
            result.Add(new RoutineParameter(name, type, isOutput, Default: null));
        }

        return result;
    }

    public SqlStatement BuildCallStatement(
        IReadOnlyList<DbNodeRef> ancestors,
        IReadOnlyList<RoutineParameter> parameters,
        IReadOnlyDictionary<string, string?> inputValues)
    {
        var db = Name(ancestors, DbNodeKind.Database);
        var qualified = $"{Dialect.QuoteIdentifier(db)}.{Dialect.QuoteIdentifier(ancestors[^1].Name)}";

        if (ancestors[^1].Kind == DbNodeKind.Function)
        {
            // MySQL functions take only IN parameters; the return value is the SELECT's column.
            var args = string.Join(", ", parameters.Select(p => FormatValue(p, inputValues)));
            return new SqlStatement($"SELECT {qualified}({args}) AS {Dialect.QuoteIdentifier("Result")};", []);
        }

        // Procedure: OUT/INOUT arguments must be session variables — SET them to NULL, CALL with the
        // variable, then a trailing SELECT reads them back as a result set.
        var outputs = parameters.Where(p => p.IsOutput).ToList();
        var script = new StringBuilder();
        foreach (var o in outputs)
        {
            script.AppendLine($"SET {SessionVar(o.Name)} = NULL;");
        }

        var callArgs = parameters.Select(p => p.IsOutput ? SessionVar(p.Name) : FormatValue(p, inputValues));
        script.AppendLine($"CALL {qualified}({string.Join(", ", callArgs)});");

        if (outputs.Count > 0)
        {
            var selects = outputs.Select(o => $"{SessionVar(o.Name)} AS {Dialect.QuoteIdentifier(o.Name)}");
            script.AppendLine($"SELECT {string.Join(", ", selects)};");
        }

        return new SqlStatement(script.ToString(), []);
    }

    private static string SessionVar(string paramName) => "@" + paramName;

    private static string FormatValue(RoutineParameter parameter, IReadOnlyDictionary<string, string?> values)
    {
        var raw = values.TryGetValue(parameter.Name, out var v) ? v : null;
        if (string.IsNullOrEmpty(raw))
        {
            return "NULL";
        }

        return NeedsQuote(parameter.Type) ? "'" + raw.Replace("'", "''") + "'" : raw;
    }

    private static bool NeedsQuote(string type) => type.ToLowerInvariant() switch
    {
        "tinyint" or "smallint" or "mediumint" or "int" or "integer" or "bigint"
            or "decimal" or "numeric" or "float" or "double" or "bit" => false,
        _ => true
    };

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

    // Activity Monitor. information_schema.PROCESSLIST (same data as SHOW FULL PROCESSLIST, but a real
    // result set); CONNECTION_ID() gives this connection's own id for the visible-but-disabled guard.
    // MySQL separates KILL (hard, drops the connection) from KILL QUERY (soft, aborts just the statement).
    public bool SupportsActivityMonitor => true;

    public string SessionIdColumn => "Id";

    public bool SupportsCancelQuery => true;

    public async Task<ActiveSessionSnapshot> GetActiveSessionsAsync(ConnectionProfile profile, CancellationToken ct)
    {
        const string sql = """
            SELECT ID AS Id, USER AS User, HOST AS Host, DB AS db, COMMAND AS Command,
                   TIME AS Time, STATE AS State, INFO AS Info
            FROM information_schema.PROCESSLIST
            ORDER BY ID
            """;

        var stopwatch = Stopwatch.StartNew();
        await using var connection = await OpenAsync(profile, ct);

        string? currentId;
        await using (var id = new MySqlCommand("SELECT CONNECTION_ID()", connection))
        {
            currentId = (await id.ExecuteScalarAsync(ct))?.ToString();
        }

        await using var command = new MySqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = await ReadResultAsync(reader, stopwatch, ct);
        return new ActiveSessionSnapshot(result, currentId);
    }

    public Task KillSessionAsync(ConnectionProfile profile, string sessionId, CancellationToken ct) =>
        RunKillAsync(profile, $"KILL {ParseId(sessionId)}", ct);

    public Task CancelQueryAsync(ConnectionProfile profile, string sessionId, CancellationToken ct) =>
        RunKillAsync(profile, $"KILL QUERY {ParseId(sessionId)}", ct);

    // MySQL's KILL takes no parameter marker, so the id is parsed to a long first — it can then only ever
    // be an integer in the statement text.
    private static long ParseId(string sessionId) => long.Parse(sessionId, CultureInfo.InvariantCulture);

    private static async Task RunKillAsync(ConnectionProfile profile, string sql, CancellationToken ct)
    {
        await using var connection = await OpenAsync(profile, ct);
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    // User management. MySQL identity is name@host, so the tree node is named "user@host" (the host is part
    // of the identity, not optional). Users are server-wide (mysql.user), so any database context works.
    private async Task<IReadOnlyList<DbTreeNode>> LoadUsersAsync(ConnectionProfile profile, CancellationToken ct)
    {
        var nodes = new List<DbTreeNode>();
        await using var connection = await OpenAsync(profile, ct);
        await using var command = new MySqlCommand(
            "SELECT User, Host FROM mysql.user WHERE User NOT LIKE 'mysql.%' ORDER BY User, Host", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            nodes.Add(new DbTreeNode { Kind = DbNodeKind.User, Name = $"{reader.GetString(0)}@{reader.GetString(1)}" });
        }

        return nodes;
    }

    public bool CanManageUsers => true;

    public IReadOnlyList<UserField> UserFields { get; } =
    [
        new("password", "Password", UserFieldType.Password, Required: true),
        new("host", "Host", UserFieldType.Text, Default: "%", Hint: "% = any host, localhost = local only")
    ];

    public async Task<IReadOnlyList<string>> GetAssignableRolesAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct)
    {
        var roles = new List<string>();
        await using var connection = await OpenAsync(profile, ct);
        // MySQL 8 roles are created as locked, passwordless accounts — that is how they differ from users.
        await using var command = new MySqlCommand(
            "SELECT DISTINCT User FROM mysql.user WHERE account_locked = 'Y' AND (authentication_string = '' OR authentication_string IS NULL) AND User NOT LIKE 'mysql.%' ORDER BY User",
            connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            roles.Add(reader.GetString(0));
        }

        return roles;
    }

    public SqlStatement BuildCreateUserStatement(IReadOnlyDictionary<string, string?> values, IReadOnlyList<string> roles)
    {
        var host = values.GetValueOrDefault("host") is { Length: > 0 } h ? h : "%";
        var account = UserLiteral(values["name"] ?? string.Empty, host);
        var password = (values.GetValueOrDefault("password") ?? string.Empty).Replace("'", "''");

        var script = new StringBuilder();
        script.Append($"CREATE USER {account} IDENTIFIED BY '{password}';");
        foreach (var role in roles)
        {
            script.Append($"\nGRANT {UserLiteral(role, "%")} TO {account};");
        }

        return new SqlStatement(script.ToString(), []);
    }

    public SqlStatement BuildDropUserStatement(DbNodeRef userNode, IReadOnlyList<DbNodeRef> ancestors)
    {
        // The node is "user@host"; split on the last '@' so a user name containing '@' still parses.
        var at = userNode.Name.LastIndexOf('@');
        var user = at < 0 ? userNode.Name : userNode.Name[..at];
        var host = at < 0 ? "%" : userNode.Name[(at + 1)..];
        return new SqlStatement($"DROP USER {UserLiteral(user, host)};", []);
    }

    // MySQL accounts are 'user'@'host' string literals (not identifiers) — quote/escape both parts.
    private static string UserLiteral(string user, string host) =>
        $"'{user.Replace("'", "''")}'@'{host.Replace("'", "''")}'";
}
