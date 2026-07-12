using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Lionear.SqlExplorer.Sdk;
using MySqlConnector;

namespace Lionear.SqlExplorer.Providers.MySql;

public sealed class MySqlProvider : IDbProvider
{
    public DatabaseKind Kind => DatabaseKind.MySql;

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

    public async Task<bool> TestConnectionAsync(ConnectionProfile profile, CancellationToken ct)
    {
        await using var connection = new MySqlConnection(profile.ConnectionString);
        await connection.OpenAsync(ct);
        return connection.State == ConnectionState.Open;
    }

    public async Task<QueryResult> ExecuteQueryAsync(ConnectionProfile profile, string sql, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        await using var connection = new MySqlConnection(profile.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new MySqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);

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

        stopwatch.Stop();

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
        await using var connection = new MySqlConnection(profile.ConnectionString);
        await connection.OpenAsync(ct);
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
            // Tables carry an extra "Indexes" folder; views have no indexes.
            DbNodeKind.Table => [.. await LoadColumnsAsync(profile, ancestors, ct), IndexFolder()],
            DbNodeKind.View => await LoadColumnsAsync(profile, ancestors, ct),
            DbNodeKind.IndexFolder => await LoadIndexesAsync(profile, ancestors, ct),
            _ => []
        };
    }

    private static IReadOnlyList<DbTreeNode> Folders() =>
    [
        new() { Kind = DbNodeKind.TableFolder, Name = "Tables", HasChildren = true },
        new() { Kind = DbNodeKind.ViewFolder, Name = "Views", HasChildren = true }
    ];

    private static DbTreeNode IndexFolder() =>
        new() { Kind = DbNodeKind.IndexFolder, Name = "Indexes", HasChildren = true };

    private static async Task<IReadOnlyList<DbTreeNode>> LoadDatabasesAsync(ConnectionProfile profile, CancellationToken ct)
    {
        const string sql = """
            SELECT schema_name FROM information_schema.schemata
            WHERE schema_name NOT IN ('information_schema', 'mysql', 'performance_schema', 'sys')
            ORDER BY schema_name
            """;

        var nodes = new List<DbTreeNode>();
        await using var connection = new MySqlConnection(profile.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            nodes.Add(new DbTreeNode { Kind = DbNodeKind.Database, Name = reader.GetString(0), HasChildren = true });
        }

        return nodes;
    }

    private static async Task<IReadOnlyList<DbTreeNode>> LoadRelationsAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        bool isView,
        CancellationToken ct)
    {
        const string sql = """
            SELECT table_name FROM information_schema.tables
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
            nodes.Add(new DbTreeNode { Kind = kind, Name = reader.GetString(0), HasChildren = true });
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

    private static string Name(IReadOnlyList<DbNodeRef> ancestors, DbNodeKind kind) =>
        ancestors.First(a => a.Kind == kind).Name;
}
