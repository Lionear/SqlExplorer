using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Lionear.SqlExplorer.Sdk;
using Npgsql;

namespace Lionear.SqlExplorer.Providers.Postgres;

public sealed class PostgresProvider : IDbProvider
{
    public DatabaseKind Kind => DatabaseKind.PostgreSql;

    public string DisplayName => "PostgreSQL";

    // Uses the embedded brand PNG (icon.png) when present; falls back to a glyph otherwise.
    public ProviderIcon? Icon { get; } = ProviderIconLoader.Load(typeof(PostgresProvider), "🐘");

    public ISqlDialect Dialect { get; } = new PostgresDialect();

    public IReadOnlyList<ConnectionField> ConnectionFields { get; } =
    [
        new("host", "Host", ConnectionFieldType.Text, Required: true, Default: "localhost"),
        new("port", "Port", ConnectionFieldType.Number, Default: "5432"),
        new("database", "Database", ConnectionFieldType.Text, Required: true, Default: "postgres"),
        new("username", "Username", ConnectionFieldType.Text, Required: true, Default: "postgres"),
        new("password", "Password", ConnectionFieldType.Password)
    ];

    public string BuildConnectionString(IReadOnlyDictionary<string, string?> values)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = Value(values, "host"),
            Database = Value(values, "database"),
            Username = Value(values, "username"),
            Password = Value(values, "password")
        };

        if (int.TryParse(Value(values, "port"), out var port))
        {
            builder.Port = port;
        }

        return builder.ConnectionString;
    }

    private static string? Value(IReadOnlyDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    public async Task<bool> TestConnectionAsync(ConnectionProfile profile, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(profile.ConnectionString);
        await connection.OpenAsync(ct);
        return connection.State == ConnectionState.Open;
    }

    public async Task<QueryResult> ExecuteQueryAsync(ConnectionProfile profile, string sql, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        await using var connection = new NpgsqlConnection(profile.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new NpgsqlCommand(sql, connection);
        // KeyInfo makes Npgsql resolve base table/column names and primary-key flags via the
        // catalog; without it GetColumnSchema returns bare names and marks every column read-only,
        // so the result would never be editable (Notes §8).
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.KeyInfo, ct);

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

    // Map the driver's column schema onto our ResultColumn metadata. BaseTable/IsKey come from
    // Npgsql's catalog lookup and let the host decide whether the result set is editable (Notes §8).
    private static List<ResultColumn> BuildColumns(NpgsqlDataReader reader)
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
        await using var connection = new NpgsqlConnection(profile.ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var affected = 0;
        foreach (var statement in statements)
        {
            await using var command = new NpgsqlCommand(statement.Text, connection, transaction);
            foreach (var parameter in statement.Parameters)
            {
                command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            }

            affected += await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return affected;
    }

    // A Postgres server exposes Database → Schema → (Tables|Views folder) → Table|View → Column.
    // Each level is a separate lazy query so a big server is never introspected all at once.
    public async Task<IReadOnlyList<DbTreeNode>> GetChildNodesAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct)
    {
        var parent = ancestors.Count == 0 ? (DbNodeKind?)null : ancestors[^1].Kind;

        return parent switch
        {
            null => await LoadDatabasesAsync(profile, ct),
            DbNodeKind.Database => [SchemaFolder()],
            DbNodeKind.SchemaFolder => await LoadSchemasAsync(profile, Name(ancestors, DbNodeKind.Database), ct),
            DbNodeKind.Schema => Folders(),
            DbNodeKind.TableFolder => await LoadRelationsAsync(profile, ancestors, isView: false, ct),
            DbNodeKind.ViewFolder => await LoadRelationsAsync(profile, ancestors, isView: true, ct),
            DbNodeKind.SequenceFolder => await LoadSequencesAsync(profile, ancestors, ct),
            // Tables carry an extra "Indexes" folder; views have no indexes.
            DbNodeKind.Table => [.. await LoadColumnsAsync(profile, ancestors, ct), IndexFolder()],
            DbNodeKind.View => await LoadColumnsAsync(profile, ancestors, ct),
            DbNodeKind.IndexFolder => await LoadIndexesAsync(profile, ancestors, ct),
            _ => []
        };
    }

    // A database groups its schemas under a "Schemas" node (DataGrip-style).
    private static DbTreeNode SchemaFolder() =>
        new() { Kind = DbNodeKind.SchemaFolder, Name = "Schemas", HasChildren = true };

    private static DbTreeNode IndexFolder() =>
        new() { Kind = DbNodeKind.IndexFolder, Name = "Indexes", HasChildren = true };

    private static IReadOnlyList<DbTreeNode> Folders() =>
    [
        new() { Kind = DbNodeKind.TableFolder, Name = "Tables", HasChildren = true },
        new() { Kind = DbNodeKind.ViewFolder, Name = "Views", HasChildren = true },
        new() { Kind = DbNodeKind.SequenceFolder, Name = "Sequences", HasChildren = true }
    ];

    private async Task<IReadOnlyList<DbTreeNode>> LoadDatabasesAsync(ConnectionProfile profile, CancellationToken ct)
    {
        const string sql = """
            SELECT datname FROM pg_database
            WHERE datistemplate = false AND datallowconn = true
            ORDER BY datname
            """;

        var nodes = new List<DbTreeNode>();
        await using var connection = new NpgsqlConnection(profile.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            nodes.Add(new DbTreeNode { Kind = DbNodeKind.Database, Name = reader.GetString(0), HasChildren = true });
        }

        return nodes;
    }

    private async Task<IReadOnlyList<DbTreeNode>> LoadSchemasAsync(
        ConnectionProfile profile,
        string database,
        CancellationToken ct)
    {
        const string sql = """
            SELECT schema_name FROM information_schema.schemata
            WHERE schema_name NOT LIKE 'pg_%' AND schema_name <> 'information_schema'
            ORDER BY schema_name
            """;

        var nodes = new List<DbTreeNode>();
        await using var connection = new NpgsqlConnection(ConnectionStringFor(profile, database));
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            nodes.Add(new DbTreeNode { Kind = DbNodeKind.Schema, Name = reader.GetString(0), HasChildren = true });
        }

        return nodes;
    }

    private async Task<IReadOnlyList<DbTreeNode>> LoadRelationsAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        bool isView,
        CancellationToken ct)
    {
        const string sql = """
            SELECT table_name FROM information_schema.tables
            WHERE table_schema = @schema AND table_type = @type
            ORDER BY table_name
            """;

        var kind = isView ? DbNodeKind.View : DbNodeKind.Table;
        var nodes = new List<DbTreeNode>();
        await using var connection = new NpgsqlConnection(ConnectionStringFor(profile, Name(ancestors, DbNodeKind.Database)));
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schema", Name(ancestors, DbNodeKind.Schema));
        command.Parameters.AddWithValue("type", isView ? "VIEW" : "BASE TABLE");
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            nodes.Add(new DbTreeNode { Kind = kind, Name = reader.GetString(0), HasChildren = true });
        }

        return nodes;
    }

    private async Task<IReadOnlyList<DbTreeNode>> LoadColumnsAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct)
    {
        var schema = Name(ancestors, DbNodeKind.Schema);
        var table = ancestors[^1].Name;

        const string pkSql = """
            SELECT kcu.column_name
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
              ON tc.constraint_name = kcu.constraint_name AND tc.table_schema = kcu.table_schema
            WHERE tc.constraint_type = 'PRIMARY KEY' AND tc.table_schema = @schema AND tc.table_name = @table
            """;

        const string colSql = """
            SELECT column_name, data_type, is_nullable
            FROM information_schema.columns
            WHERE table_schema = @schema AND table_name = @table
            ORDER BY ordinal_position
            """;

        await using var connection = new NpgsqlConnection(ConnectionStringFor(profile, Name(ancestors, DbNodeKind.Database)));
        await connection.OpenAsync(ct);

        var primaryKeys = new HashSet<string>();
        await using (var pkCommand = new NpgsqlCommand(pkSql, connection))
        {
            pkCommand.Parameters.AddWithValue("schema", schema);
            pkCommand.Parameters.AddWithValue("table", table);
            await using var pkReader = await pkCommand.ExecuteReaderAsync(ct);
            while (await pkReader.ReadAsync(ct))
            {
                primaryKeys.Add(pkReader.GetString(0));
            }
        }

        var nodes = new List<DbTreeNode>();
        await using (var colCommand = new NpgsqlCommand(colSql, connection))
        {
            colCommand.Parameters.AddWithValue("schema", schema);
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

    private async Task<IReadOnlyList<DbTreeNode>> LoadSequencesAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct)
    {
        const string sql = """
            SELECT sequence_name FROM information_schema.sequences
            WHERE sequence_schema = @schema
            ORDER BY sequence_name
            """;

        var nodes = new List<DbTreeNode>();
        await using var connection = new NpgsqlConnection(ConnectionStringFor(profile, Name(ancestors, DbNodeKind.Database)));
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schema", Name(ancestors, DbNodeKind.Schema));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            nodes.Add(new DbTreeNode { Kind = DbNodeKind.Sequence, Name = reader.GetString(0) });
        }

        return nodes;
    }

    private async Task<IReadOnlyList<DbTreeNode>> LoadIndexesAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct)
    {
        const string sql = """
            SELECT c.relname AS index_name, i.indisunique, i.indisprimary
            FROM pg_index i
            JOIN pg_class c ON c.oid = i.indexrelid
            JOIN pg_class t ON t.oid = i.indrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            WHERE n.nspname = @schema AND t.relname = @table
            ORDER BY c.relname
            """;

        var nodes = new List<DbTreeNode>();
        await using var connection = new NpgsqlConnection(ConnectionStringFor(profile, Name(ancestors, DbNodeKind.Database)));
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schema", Name(ancestors, DbNodeKind.Schema));
        command.Parameters.AddWithValue("table", Name(ancestors, DbNodeKind.Table));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            var unique = reader.GetBoolean(1);
            var primary = reader.GetBoolean(2);
            var detail = primary ? "PK" : unique ? "unique" : null;
            nodes.Add(new DbTreeNode { Kind = DbNodeKind.Index, Name = name, Detail = detail });
        }

        return nodes;
    }

    // Re-point the connection at a sibling database on the same server; secrets/host stay intact.
    private static string ConnectionStringFor(ConnectionProfile profile, string database) =>
        new NpgsqlConnectionStringBuilder(profile.ConnectionString) { Database = database }.ConnectionString;

    private static string Name(IReadOnlyList<DbNodeRef> ancestors, DbNodeKind kind) =>
        ancestors.First(a => a.Kind == kind).Name;
}
