using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Avalonia.Controls;
using Lionear.SqlExplorer.Sdk;
using Lionear.SqlExplorer.Sdk.Ui;
using Microsoft.Data.SqlClient;

namespace Lionear.SqlExplorer.Providers.MsSql;

public sealed class MsSqlProvider : IDbProvider, ICustomConnectionUi
{
    // Route B (Notes §4.4): render the Advanced section with a provider-owned view instead of the
    // host-generated form. The declared Advanced ConnectionFields still define the data — the view
    // just reads/writes them through the context.
    public Control CreateAdvancedView(IConnectionUiContext context) => new MsSqlAdvancedView(context);

    public string DisplayName => "Microsoft SQL Server";

    // Uses the embedded brand PNG (icon.png) when present; falls back to a glyph otherwise.
    public ProviderIcon? Icon { get; } = ProviderIconLoader.Load(typeof(MsSqlProvider), "🗄");

    public ISqlDialect Dialect { get; } = new MsSqlDialect();

    public IReadOnlyList<ConnectionField> ConnectionFields { get; } =
    [
        new("host", "Host", ConnectionFieldType.Text, Required: true, Default: "localhost"),
        new("port", "Port", ConnectionFieldType.Number, Default: "1433"),
        // Optional: leave blank to connect to the server (master) and browse every database from the
        // tree — per-database queries repoint the catalog at execute time (see OpenAsync).
        new("database", "Database", ConnectionFieldType.Text, Default: "master"),
        new("username", "Username", ConnectionFieldType.Text, Required: true, Default: "sa"),
        new("password", "Password", ConnectionFieldType.Password),

        // Advanced — SSL/TLS. Encrypt defaults to Optional and TrustServerCertificate to true so a
        // fresh local SQL Server (self-signed cert) still connects; both are visible and overridable
        // now instead of the old hardcoded TrustServerCertificate=true (FR-3).
        new("encrypt", "Encrypt", ConnectionFieldType.Choice, Default: "Optional",
            Group: "Security", Advanced: true, Choices: ["Optional", "Mandatory", "Strict"]),
        new("trustServerCertificate", "Trust server certificate", ConnectionFieldType.Bool,
            Default: "true", Group: "Security", Advanced: true),

        // Advanced — connection tuning.
        new("applicationName", "Application name", ConnectionFieldType.Text,
            Default: "Lionear SQL Explorer", Group: "Connection", Advanced: true),
        new("connectTimeout", "Connect timeout (s)", ConnectionFieldType.Number,
            Placeholder: "15", Group: "Connection", Advanced: true),
        new("multipleActiveResultSets", "Multiple active result sets (MARS)", ConnectionFieldType.Bool,
            Default: "false", Group: "Connection", Advanced: true)
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
            Encrypt = Value(values, "encrypt") switch
            {
                "Mandatory" => SqlConnectionEncryptOption.Mandatory,
                "Strict" => SqlConnectionEncryptOption.Strict,
                _ => SqlConnectionEncryptOption.Optional
            },
            // Default true (missing field) keeps local dev working; now user-controllable.
            TrustServerCertificate = Bool(values, "trustServerCertificate", fallback: true)
        };

        if (Value(values, "applicationName") is { } appName)
        {
            builder.ApplicationName = appName;
        }

        if (Value(values, "connectTimeout") is { } timeout && int.TryParse(timeout, out var seconds))
        {
            builder.ConnectTimeout = seconds;
        }

        if (Bool(values, "multipleActiveResultSets", fallback: false))
        {
            builder.MultipleActiveResultSets = true;
        }

        return builder.ConnectionString;
    }

    private static string? Value(IReadOnlyDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    // Bool fields are stored as "true"/"false"; an absent/blank value falls back to the field's own default.
    private static bool Bool(IReadOnlyDictionary<string, string?> values, string key, bool fallback) =>
        Value(values, key) is { } v ? bool.TryParse(v, out var b) ? b : fallback : fallback;

    // Inverse of BuildConnectionString: only map keys the pasted string actually set (ContainsKey),
    // so we never overwrite a field with a builder default. DataSource splits back into host[,port].
    public IReadOnlyDictionary<string, string?>? ParseConnectionString(string connectionString)
    {
        var b = new SqlConnectionStringBuilder(connectionString);
        var result = new Dictionary<string, string?>();

        if (b.ContainsKey("Data Source"))
        {
            var ds = b.DataSource;
            if (ds.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
            {
                ds = ds[4..];
            }

            var comma = ds.LastIndexOf(',');
            if (comma >= 0)
            {
                result["host"] = ds[..comma];
                result["port"] = ds[(comma + 1)..];
            }
            else
            {
                result["host"] = ds;
            }
        }

        if (b.ContainsKey("Initial Catalog")) result["database"] = b.InitialCatalog;
        if (b.ContainsKey("User ID")) result["username"] = b.UserID;
        if (b.ContainsKey("Password")) result["password"] = b.Password;
        if (b.ContainsKey("Encrypt"))
        {
            // ToString round-trips to legacy "True"/"False" on older builders; normalise both forms.
            var enc = b.Encrypt.ToString();
            result["encrypt"] = enc.Equals("Strict", StringComparison.OrdinalIgnoreCase) ? "Strict"
                : enc.Equals("Mandatory", StringComparison.OrdinalIgnoreCase) || enc.Equals("True", StringComparison.OrdinalIgnoreCase) ? "Mandatory"
                : "Optional";
        }

        if (b.ContainsKey("Trust Server Certificate")) result["trustServerCertificate"] = b.TrustServerCertificate ? "true" : "false";
        if (b.ContainsKey("Application Name")) result["applicationName"] = b.ApplicationName;
        if (b.ContainsKey("Connect Timeout")) result["connectTimeout"] = b.ConnectTimeout.ToString();
        if (b.ContainsKey("Multiple Active Result Sets")) result["multipleActiveResultSets"] = b.MultipleActiveResultSets ? "true" : "false";

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

        await using var command = new SqlCommand(sql, connection);
        // KeyInfo makes SqlClient resolve base table/column names and primary-key flags, so the result
        // can map back to a table for the editable-grid save-flow (Notes §8); without it every column
        // comes back read-only with no base table.
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.KeyInfo, ct);

        return await ReadResultAsync(reader, stopwatch, ct);
    }

    public async Task<IReadOnlyList<QueryResult>> ExecuteScriptAsync(ConnectionProfile profile, string sql, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        await using var connection = await OpenAsync(profile, ct);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.KeyInfo, ct);

        var results = new List<QueryResult>();
        do
        {
            results.Add(await ReadResultAsync(reader, stopwatch, ct));
        } while (await reader.NextResultAsync(ct));

        return results;
    }

    private static async Task<QueryResult> ReadResultAsync(SqlDataReader reader, Stopwatch stopwatch, CancellationToken ct)
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
        await using var connection = await OpenAsync(profile, ct);
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
            // Tables carry extra "Indexes"/"Foreign Keys" folders; views have neither.
            DbNodeKind.Table => [.. await LoadColumnsAsync(profile, ancestors, ct), IndexFolder(), ForeignKeyFolder()],
            DbNodeKind.View => await LoadColumnsAsync(profile, ancestors, ct),
            DbNodeKind.IndexFolder => await LoadIndexesAsync(profile, ancestors, ct),
            DbNodeKind.ForeignKeyFolder => await LoadForeignKeysAsync(profile, ancestors, ct),
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
    private async Task<IReadOnlyList<DbTreeNode>> LoadGroupAsync(
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
        await using var connection = await OpenAsync(profile, ct);
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
        await using var connection = await OpenAsync(profile, ct);

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

    private static DbTreeNode ForeignKeyFolder() =>
        new() { Kind = DbNodeKind.ForeignKeyFolder, Name = "Foreign Keys", HasChildren = true };

    private static IReadOnlyList<DbTreeNode> Folders() =>
    [
        new() { Kind = DbNodeKind.TableFolder, Name = "Tables", HasChildren = true },
        new() { Kind = DbNodeKind.ViewFolder, Name = "Views", HasChildren = true },
        new() { Kind = DbNodeKind.SequenceFolder, Name = "Sequences", HasChildren = true }
    ];

    private async Task<IReadOnlyList<DbTreeNode>> LoadDatabasesAsync(ConnectionProfile profile, CancellationToken ct) =>
        (await GetDatabasesAsync(profile, ct))
            .Select(name => new DbTreeNode { Kind = DbNodeKind.Database, Name = name, HasChildren = true })
            .ToList();

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

    private static async Task<IReadOnlyList<DbTreeNode>> LoadForeignKeysAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct)
    {
        const string sql = """
            SELECT fk.name, c.name AS column_name, rt.name AS ref_table, rc.name AS ref_column
            FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.columns c ON c.object_id = fkc.parent_object_id AND c.column_id = fkc.parent_column_id
            JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
            JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            WHERE fk.parent_object_id = OBJECT_ID(@qualified)
            ORDER BY fk.name, fkc.constraint_column_id
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
            var column = reader.GetString(1);
            var refTable = reader.GetString(2);
            var refColumn = reader.GetString(3);
            nodes.Add(new DbTreeNode { Kind = DbNodeKind.ForeignKey, Name = name, Detail = $"{column} → {refTable}.{refColumn}" });
        }

        return nodes;
    }

    // Re-point the connection at another database on the same server; host/credentials stay intact.
    private static string ConnectionStringFor(ConnectionProfile profile, string database) =>
        new SqlConnectionStringBuilder(profile.ConnectionString) { InitialCatalog = database }.ConnectionString;

    // Open against the tree's database when the host set ConnectionProfile.Database (browsing a table
    // under a specific catalog); otherwise the connection's own InitialCatalog. This is the fix for
    // queries silently running against 'master' instead of the selected database.
    private static async Task<SqlConnection> OpenAsync(ConnectionProfile profile, CancellationToken ct)
    {
        var connectionString = string.IsNullOrWhiteSpace(profile.Database)
            ? profile.ConnectionString
            : ConnectionStringFor(profile, profile.Database);

        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static string Name(IReadOnlyList<DbNodeRef> ancestors, DbNodeKind kind) =>
        ancestors.First(a => a.Kind == kind).Name;

    // Database creation has no schema/table parent in the tree — CreateCapability.ParentNode is null
    // for "the connection root" (same convention TreeNodeViewModel.NodeKind already uses).
    public IReadOnlyList<CreateCapability> CreateCapabilities { get; } =
    [
        new(DbObjectKind.Database, null),
        new(DbObjectKind.Schema, DbNodeKind.SchemaFolder),
        new(DbObjectKind.Table, DbNodeKind.TableFolder)
    ];

    public IReadOnlyList<string> ColumnTypes { get; } =
        ["int", "bigint", "nvarchar(255)", "varchar(255)", "bit", "decimal(18,2)", "datetime2", "date", "uniqueidentifier", "text"];

    public SqlStatement BuildCreateStatement(CreateObjectSpec spec)
    {
        var sql = spec.Kind switch
        {
            DbObjectKind.Database => $"CREATE DATABASE {Dialect.QuoteIdentifier(spec.Name)}",
            // Must be the only statement in its batch — ExecuteDdlAsync runs one statement, no "GO".
            DbObjectKind.Schema => $"CREATE SCHEMA {Dialect.QuoteIdentifier(spec.Name)}",
            DbObjectKind.Table => BuildCreateTable(spec),
            _ => throw new NotSupportedException($"SQL Server cannot create a {spec.Kind}.")
        };

        return new SqlStatement(sql, []);
    }

    private string BuildCreateTable(CreateObjectSpec spec)
    {
        var qualified = spec.Schema is { Length: > 0 }
            ? $"{Dialect.QuoteIdentifier(spec.Schema)}.{Dialect.QuoteIdentifier(spec.Name)}"
            : Dialect.QuoteIdentifier(spec.Name);

        var columns = spec.Columns.Select(c =>
            $"{Dialect.QuoteIdentifier(c.Name)} {c.Type}{(c.AutoIncrement ? " IDENTITY(1,1)" : "")}{(c.Nullable ? "" : " NOT NULL")}");

        var primaryKey = spec.Columns.Where(c => c.PrimaryKey).Select(c => Dialect.QuoteIdentifier(c.Name)).ToList();
        var clauses = primaryKey.Count > 0
            ? columns.Append($"PRIMARY KEY ({string.Join(", ", primaryKey)})")
            : columns;

        return $"CREATE TABLE {qualified} ({string.Join(", ", clauses)})";
    }

    // CREATE DATABASE runs on master (this connection's own InitialCatalog when ConnectionProfile.Database
    // isn't set, per the "master" ConnectionField default) — no explicit transaction, single statement.
    public async Task ExecuteDdlAsync(ConnectionProfile profile, string sql, CancellationToken ct)
    {
        await using var connection = await OpenAsync(profile, ct);
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    // SHOWPLAN_ALL must be the only thing "on" in its own batch — turned on, the plan-producing query
    // itself is never executed (rows come back describing the plan, not the query's own result), then
    // turned off. Three separate SqlCommands on one connection/session, not a single-statement prefix
    // like Postgres/MySQL EXPLAIN.
    public async Task<QueryResult> ExplainAsync(ConnectionProfile profile, string sql, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        await using var connection = await OpenAsync(profile, ct);

        await using (var on = new SqlCommand("SET SHOWPLAN_ALL ON", connection))
        {
            await on.ExecuteNonQueryAsync(ct);
        }

        QueryResult result;
        try
        {
            await using var command = new SqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.KeyInfo, ct);
            result = await ReadResultAsync(reader, stopwatch, ct);
        }
        finally
        {
            await using var off = new SqlCommand("SET SHOWPLAN_ALL OFF", connection);
            await off.ExecuteNonQueryAsync(ct);
        }

        return result;
    }

    public async Task<IReadOnlyList<string>> GetDatabasesAsync(ConnectionProfile profile, CancellationToken ct)
    {
        // database_id 1..4 are the system databases (master/tempdb/model/msdb).
        const string sql = "SELECT name FROM sys.databases WHERE database_id > 4 ORDER BY name";

        var names = new List<string>();
        await using var connection = await OpenAsync(profile, ct);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
