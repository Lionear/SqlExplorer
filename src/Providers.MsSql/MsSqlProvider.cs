using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Lionear.SqlExplorer.Sdk;
using Microsoft.Data.SqlClient;

namespace Lionear.SqlExplorer.Providers.MsSql;

public sealed class MsSqlProvider : IDbProvider
{
    public DatabaseKind Kind => DatabaseKind.SqlServer;

    public string DisplayName => "Microsoft SQL Server";

    // Uses the embedded brand PNG (icon.png) when present; falls back to a glyph otherwise.
    public ProviderIcon? Icon { get; } = ProviderIconLoader.Load(typeof(MsSqlProvider), "🗄");

    public ISqlDialect Dialect { get; } = new MsSqlDialect();

    public IReadOnlyList<ConnectionField> ConnectionFields { get; } =
    [
        new("host", "Host", ConnectionFieldType.Text, Required: true, Default: "localhost"),
        new("port", "Port", ConnectionFieldType.Number, Default: "1433"),
        new("database", "Database", ConnectionFieldType.Text, Required: true, Default: "master"),
        new("username", "Username", ConnectionFieldType.Text, Required: true, Default: "sa"),
        new("password", "Password", ConnectionFieldType.Password)
    ];

    public string BuildConnectionString(IReadOnlyDictionary<string, string?> values)
    {
        var host = Value(values, "host") ?? "localhost";
        var dataSource = Value(values, "port") is { } port ? $"{host},{port}" : host;

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = dataSource,
            InitialCatalog = Value(values, "database") ?? "master",
            UserID = Value(values, "username") ?? string.Empty,
            Password = Value(values, "password") ?? string.Empty,
            // Dev-friendly: SQL Server images use a self-signed cert; trust it rather than fail the handshake.
            TrustServerCertificate = true
        };

        return builder.ConnectionString;
    }

    private static string? Value(IReadOnlyDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    public async Task<bool> TestConnectionAsync(ConnectionProfile profile, CancellationToken ct)
    {
        await using var connection = new SqlConnection(profile.ConnectionString);
        await connection.OpenAsync(ct);
        return connection.State == ConnectionState.Open;
    }

    public async Task<QueryResult> ExecuteQueryAsync(ConnectionProfile profile, string sql, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        await using var connection = new SqlConnection(profile.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new SqlCommand(sql, connection);
        // KeyInfo makes SqlClient resolve base table/column names and primary-key flags, so the result
        // can map back to a table for the editable-grid save-flow (Notes §8); without it every column
        // comes back read-only with no base table.
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

    private static List<ResultColumn> BuildColumns(SqlDataReader reader)
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
        await using var connection = new SqlConnection(profile.ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);

        var affected = 0;
        foreach (var statement in statements)
        {
            await using var command = new SqlCommand(statement.Text, connection, transaction);
            foreach (var parameter in statement.Parameters)
            {
                command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            }

            affected += await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return affected;
    }

    // SQL Server exposes Database → Schema → (Tables|Views folder) → Table|View → Column, like Postgres.
    // information_schema is per-database, so introspecting a database means connecting to it.
    public async Task<IReadOnlyList<DbTreeNode>> GetChildNodesAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct)
    {
        var parent = ancestors.Count == 0 ? (DbNodeKind?)null : ancestors[^1].Kind;

        return parent switch
        {
            // The connection root is a server: Databases sits alongside SSMS-style server folders.
            null => RootFolders(),
            // Every cosmetic folder shares the Group kind, so route Group nodes by their name.
            DbNodeKind.Group => await LoadGroupAsync(profile, ancestors, ct),
            DbNodeKind.Database => DatabaseChildren(),
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

    // Group-folder names — shared between the nodes that produce them and the routing that reads them.
    private const string Databases = "Databases";
    private const string Security = "Security";
    private const string Administration = "Administration";
    private const string Logins = "Logins";
    private const string ServerRoles = "Server Roles";
    private const string AgentJobs = "Agent Jobs";

    private static IReadOnlyList<DbTreeNode> RootFolders() =>
    [
        new() { Kind = DbNodeKind.Group, Name = Databases, HasChildren = true },
        new() { Kind = DbNodeKind.Group, Name = Security, HasChildren = true },
        new() { Kind = DbNodeKind.Group, Name = Administration, HasChildren = true }
    ];

    // Cosmetic Group folders all share one kind; dispatch on the folder's name.
    private static async Task<IReadOnlyList<DbTreeNode>> LoadGroupAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct) =>
        ancestors[^1].Name switch
        {
            Databases => await LoadDatabasesAsync(profile, ct),
            Security =>
            [
                new() { Kind = DbNodeKind.Group, Name = Logins, HasChildren = true },
                new() { Kind = DbNodeKind.Group, Name = ServerRoles, HasChildren = true }
            ],
            Administration =>
            [
                new() { Kind = DbNodeKind.Group, Name = AgentJobs, HasChildren = true }
            ],
            Logins => await LoadPrincipalsAsync(profile,
                "SELECT name FROM sys.server_principals WHERE type IN ('S','U','G','C','K') " +
                "AND name NOT LIKE '##%' ORDER BY name", ct),
            ServerRoles => await LoadPrincipalsAsync(profile,
                "SELECT name FROM sys.server_principals WHERE type = 'R' AND name NOT LIKE '##%' ORDER BY name", ct),
            AgentJobs => await LoadAgentJobsAsync(profile, ct),
            _ => []
        };

    // Each database currently just groups its schemas (server-level Security/Administration live at the root).
    private static IReadOnlyList<DbTreeNode> DatabaseChildren() =>
    [
        new() { Kind = DbNodeKind.SchemaFolder, Name = "Schemas", HasChildren = true }
    ];

    // Run a single-column name query and map each row to a generic Object leaf.
    private static async Task<IReadOnlyList<DbTreeNode>> LoadPrincipalsAsync(
        ConnectionProfile profile,
        string sql,
        CancellationToken ct)
    {
        var nodes = new List<DbTreeNode>();
        await using var connection = new SqlConnection(profile.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            nodes.Add(new DbTreeNode { Kind = DbNodeKind.Object, Name = reader.GetString(0) });
        }

        return nodes;
    }

    private static async Task<IReadOnlyList<DbTreeNode>> LoadAgentJobsAsync(ConnectionProfile profile, CancellationToken ct)
    {
        var nodes = new List<DbTreeNode>();
        await using var connection = new SqlConnection(profile.ConnectionString);
        await connection.OpenAsync(ct);

        // SQL Server Agent lives in msdb; on SQL Server for Linux it is often absent/disabled.
        await using (var probe = new SqlCommand("SELECT OBJECT_ID('msdb.dbo.sysjobs')", connection))
        {
            if (await probe.ExecuteScalarAsync(ct) is null or DBNull)
            {
                return nodes;
            }
        }

        await using var command = new SqlCommand("SELECT name FROM msdb.dbo.sysjobs ORDER BY name", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            nodes.Add(new DbTreeNode { Kind = DbNodeKind.Object, Name = reader.GetString(0) });
        }

        return nodes;
    }

    private static DbTreeNode IndexFolder() =>
        new() { Kind = DbNodeKind.IndexFolder, Name = "Indexes", HasChildren = true };

    private static IReadOnlyList<DbTreeNode> Folders() =>
    [
        new() { Kind = DbNodeKind.TableFolder, Name = "Tables", HasChildren = true },
        new() { Kind = DbNodeKind.ViewFolder, Name = "Views", HasChildren = true },
        new() { Kind = DbNodeKind.SequenceFolder, Name = "Sequences", HasChildren = true }
    ];

    private static async Task<IReadOnlyList<DbTreeNode>> LoadDatabasesAsync(ConnectionProfile profile, CancellationToken ct)
    {
        // database_id 1..4 are the system databases (master/tempdb/model/msdb).
        const string sql = "SELECT name FROM sys.databases WHERE database_id > 4 ORDER BY name";

        var nodes = new List<DbTreeNode>();
        await using var connection = new SqlConnection(profile.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            nodes.Add(new DbTreeNode { Kind = DbNodeKind.Database, Name = reader.GetString(0), HasChildren = true });
        }

        return nodes;
    }

    private static async Task<IReadOnlyList<DbTreeNode>> LoadSchemasAsync(
        ConnectionProfile profile,
        string database,
        CancellationToken ct)
    {
        // Exclude the fixed system + database-role schemas; keep dbo and user schemas.
        const string sql = """
            SELECT s.name FROM sys.schemas s
            WHERE s.name NOT IN ('sys', 'guest', 'INFORMATION_SCHEMA', 'db_owner', 'db_accessadmin',
                'db_securityadmin', 'db_ddladmin', 'db_backupoperator', 'db_datareader', 'db_datawriter',
                'db_denydatareader', 'db_denydatawriter')
            ORDER BY s.name
            """;

        var nodes = new List<DbTreeNode>();
        await using var connection = new SqlConnection(ConnectionStringFor(profile, database));
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            nodes.Add(new DbTreeNode { Kind = DbNodeKind.Schema, Name = reader.GetString(0), HasChildren = true });
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
            WHERE table_schema = @schema AND table_type = @type
            ORDER BY table_name
            """;

        var kind = isView ? DbNodeKind.View : DbNodeKind.Table;
        var nodes = new List<DbTreeNode>();
        await using var connection = new SqlConnection(ConnectionStringFor(profile, Name(ancestors, DbNodeKind.Database)));
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("schema", Name(ancestors, DbNodeKind.Schema));
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

        await using var connection = new SqlConnection(ConnectionStringFor(profile, Name(ancestors, DbNodeKind.Database)));
        await connection.OpenAsync(ct);

        var primaryKeys = new HashSet<string>();
        await using (var pkCommand = new SqlCommand(pkSql, connection))
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
        await using (var colCommand = new SqlCommand(colSql, connection))
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

    private static async Task<IReadOnlyList<DbTreeNode>> LoadSequencesAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct)
    {
        const string sql = """
            SELECT s.name FROM sys.sequences s
            JOIN sys.schemas sc ON sc.schema_id = s.schema_id
            WHERE sc.name = @schema
            ORDER BY s.name
            """;

        var nodes = new List<DbTreeNode>();
        await using var connection = new SqlConnection(ConnectionStringFor(profile, Name(ancestors, DbNodeKind.Database)));
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("schema", Name(ancestors, DbNodeKind.Schema));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            nodes.Add(new DbTreeNode { Kind = DbNodeKind.Sequence, Name = reader.GetString(0) });
        }

        return nodes;
    }

    private static async Task<IReadOnlyList<DbTreeNode>> LoadIndexesAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct)
    {
        // OBJECT_ID resolves the qualified name to the table; heaps have a NULL index name.
        const string sql = """
            SELECT name, is_unique, is_primary_key
            FROM sys.indexes
            WHERE object_id = OBJECT_ID(@qualified) AND name IS NOT NULL
            ORDER BY name
            """;

        var qualified = $"{Name(ancestors, DbNodeKind.Schema)}.{Name(ancestors, DbNodeKind.Table)}";
        var nodes = new List<DbTreeNode>();
        await using var connection = new SqlConnection(ConnectionStringFor(profile, Name(ancestors, DbNodeKind.Database)));
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("qualified", qualified);
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

    // Re-point the connection at another database on the same server; host/credentials stay intact.
    private static string ConnectionStringFor(ConnectionProfile profile, string database) =>
        new SqlConnectionStringBuilder(profile.ConnectionString) { InitialCatalog = database }.ConnectionString;

    private static string Name(IReadOnlyList<DbNodeRef> ancestors, DbNodeKind kind) =>
        ancestors.First(a => a.Kind == kind).Name;
}
