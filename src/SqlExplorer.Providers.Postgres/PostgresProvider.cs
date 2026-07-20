using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using SqlExplorer.Sdk;
using SqlExplorer.Sdk.Provisioning;
using Npgsql;

namespace SqlExplorer.Providers.Postgres;

public sealed class PostgresProvider : IDbProvider
{
    public string DisplayName => "PostgreSQL";

    // Uses the embedded brand PNG (icon.png) when present; falls back to a glyph otherwise.
    public ProviderIcon? Icon { get; } = ProviderIconLoader.Load(typeof(PostgresProvider), "🐘");

    public ISqlDialect Dialect { get; } = new PostgresDialect();

    // How to spin up an empty local Postgres container matching a connection (SE-166). Declared here rather
    // than hardcoded in the Docker plugin so the recipe travels with the provider; the Docker plugin reads it
    // via IProviderCatalog and prefers it over its own built-in fallback. The three POSTGRES_* env vars are the
    // official image's init contract — user/password/first database.
    public ContainerRecipe? ContainerRecipe { get; } = new(
        Image: "postgres",
        DefaultTag: "16",
        ContainerPort: 5432,
        DataPath: "/var/lib/postgresql/data",
        DefaultUser: "postgres",
        DefaultPassword: "changeme",
        Environment: e =>
        [
            new("POSTGRES_DB", e.Database ?? "postgres"),
            new("POSTGRES_USER", e.User),
            new("POSTGRES_PASSWORD", e.Password)
        ]);

    public IReadOnlyList<ConnectionField> ConnectionFields { get; } =
    [
        new("host", "Host", ConnectionFieldType.Text, Required: true, Default: "localhost"),
        new("port", "Port", ConnectionFieldType.Number, Default: "5432"),
        new("database", "Database", ConnectionFieldType.Text, Required: true, Default: "postgres"),
        new("username", "Username", ConnectionFieldType.Text, Required: true, Default: "postgres"),
        new("password", "Password", ConnectionFieldType.Password),

        // Advanced — SSL. Mirrors Npgsql's SslMode enum; Prefer keeps a plain local server working.
        new("sslMode", "SSL mode", ConnectionFieldType.Choice, Default: "Prefer",
            Group: "Security", Advanced: true,
            Choices: ["Disable", "Allow", "Prefer", "Require", "VerifyCA", "VerifyFull"])
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

        if (Value(values, "sslMode") is { } sslMode && Enum.TryParse<SslMode>(sslMode, out var mode))
        {
            builder.SslMode = mode;
        }

        return builder.ConnectionString;
    }

    private static string? Value(IReadOnlyDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    // Inverse of BuildConnectionString. Unlike SqlClient/MySqlConnector, NpgsqlConnectionStringBuilder
    // reports ContainsKey == true for every known keyword (all defaulted), so it can't tell "set" from
    // "default". Guard on a non-empty value instead so a partial paste never blanks host/db/credentials.
    public IReadOnlyDictionary<string, string?>? ParseConnectionString(string connectionString)
    {
        var b = new NpgsqlConnectionStringBuilder(connectionString);
        var result = new Dictionary<string, string?>();

        void Put(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                result[key] = value;
            }
        }

        Put("host", b.Host);
        Put("port", b.Port.ToString());
        Put("database", b.Database);
        Put("username", b.Username);
        Put("password", b.Password);
        Put("sslMode", b.SslMode.ToString());

        return result;
    }

    public async Task<bool> TestConnectionAsync(ConnectionProfile profile, CancellationToken ct)
    {
        await using var connection = await OpenAsync(profile, ct);
        return connection.State == ConnectionState.Open;
    }

    // Npgsql exposes the PostgreSQL server version (e.g. "16.2") on the open connection, so no round-trip.
    public async Task<string?> GetServerVersionAsync(ConnectionProfile profile, CancellationToken ct)
    {
        await using var connection = await OpenAsync(profile, ct);
        return string.IsNullOrWhiteSpace(connection.ServerVersion) ? null : connection.ServerVersion;
    }

    public async Task<QueryResult> ExecuteQueryAsync(ConnectionProfile profile, string sql, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        await using var connection = await OpenAsync(profile, ct);

        await using var command = new NpgsqlCommand(sql, connection);
        // KeyInfo makes Npgsql resolve base table/column names and primary-key flags via the
        // catalog; without it GetColumnSchema returns bare names and marks every column read-only,
        // so the result would never be editable (Notes §8).
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.KeyInfo, ct);

        return await ReadResultAsync(reader, stopwatch, ct);
    }

    public async Task<IReadOnlyList<QueryResult>> ExecuteScriptAsync(ConnectionProfile profile, string sql, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        await using var connection = await OpenAsync(profile, ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.KeyInfo, ct);

        var results = new List<QueryResult>();
        do
        {
            results.Add(await ReadResultAsync(reader, stopwatch, ct));
        } while (await reader.NextResultAsync(ct));

        return results;
    }

    private static async Task<QueryResult> ReadResultAsync(NpgsqlDataReader reader, Stopwatch stopwatch, CancellationToken ct)
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
        await using var connection = await OpenAsync(profile, ct);
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
            // Root: the databases plus a cluster-wide Security group (roles/users aren't per-database).
            null => [.. await LoadDatabasesAsync(profile, ct), new DbTreeNode { Kind = DbNodeKind.Group, Name = "Security", HasChildren = true }],
            DbNodeKind.Group => [new DbTreeNode { Kind = DbNodeKind.UserFolder, Name = "Users", HasChildren = true }],
            DbNodeKind.UserFolder => await LoadUsersAsync(profile, ct),
            DbNodeKind.Database => [SchemaFolder()],
            DbNodeKind.SchemaFolder => await LoadSchemasAsync(profile, Name(ancestors, DbNodeKind.Database), ct),
            DbNodeKind.Schema => await FoldersAsync(profile, ancestors, ct),
            DbNodeKind.TableFolder => await LoadRelationsAsync(profile, ancestors, isView: false, ct),
            DbNodeKind.ViewFolder => await LoadRelationsAsync(profile, ancestors, isView: true, ct),
            DbNodeKind.SequenceFolder => await LoadSequencesAsync(profile, ancestors, ct),
            DbNodeKind.ProcedureFolder => await LoadRoutinesAsync(profile, ancestors, kind: 'p', ct),
            DbNodeKind.FunctionFolder => await LoadRoutinesAsync(profile, ancestors, kind: 'f', ct),
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

    // A database groups its schemas under a "Schemas" node (DataGrip-style).
    private static DbTreeNode SchemaFolder() =>
        new() { Kind = DbNodeKind.SchemaFolder, Name = "Schemas", HasChildren = true };

    private static DbTreeNode ColumnFolder() =>
        new() { Kind = DbNodeKind.ColumnFolder, Name = "Columns", HasChildren = true };

    private static DbTreeNode IndexFolder() =>
        new() { Kind = DbNodeKind.IndexFolder, Name = "Indexes", HasChildren = true };

    private static DbTreeNode ForeignKeyFolder() =>
        new() { Kind = DbNodeKind.ForeignKeyFolder, Name = "Foreign Keys", HasChildren = true };

    private static DbTreeNode TriggerFolder() =>
        new() { Kind = DbNodeKind.TriggerFolder, Name = "Triggers", HasChildren = true };

    // The Programmability/relation folders under a schema, each carrying its child count so the tree shows
    // "Tables (22)" without expanding. Counts mirror exactly what each Load*Async returns (information_schema
    // tables/sequences; DISTINCT proname for routines, matching the load's overload-collapse).
    private async Task<IReadOnlyList<DbTreeNode>> FoldersAsync(
        ConnectionProfile profile, IReadOnlyList<DbNodeRef> ancestors, CancellationToken ct)
    {
        var schema = Name(ancestors, DbNodeKind.Schema);
        await using var connection = new NpgsqlConnection(ConnectionStringFor(profile, Name(ancestors, DbNodeKind.Database)));
        await connection.OpenAsync(ct);

        var tables = 0;
        var views = 0;
        await using (var cmd = new NpgsqlCommand(
            "SELECT table_type, count(*) FROM information_schema.tables WHERE table_schema = @schema GROUP BY table_type", connection))
        {
            cmd.Parameters.AddWithValue("schema", schema);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (r.GetString(0) == "VIEW") views = (int)r.GetInt64(1); else tables += (int)r.GetInt64(1);
            }
        }

        var procedures = 0;
        var functions = 0;
        await using (var cmd = new NpgsqlCommand("""
            SELECT p.prokind::text, count(DISTINCT p.proname)
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = @schema AND p.prokind IN ('p', 'f') GROUP BY p.prokind
            """, connection))
        {
            cmd.Parameters.AddWithValue("schema", schema);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (r.GetString(0) == "p") procedures = (int)r.GetInt64(1); else functions = (int)r.GetInt64(1);
            }
        }

        var sequences = 0;
        await using (var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM information_schema.sequences WHERE sequence_schema = @schema", connection))
        {
            cmd.Parameters.AddWithValue("schema", schema);
            sequences = (int)(long)(await cmd.ExecuteScalarAsync(ct))!;
        }

        return
        [
            Folder(DbNodeKind.TableFolder, "Tables", tables),
            Folder(DbNodeKind.ViewFolder, "Views", views),
            Folder(DbNodeKind.ProcedureFolder, "Procedures", procedures),
            Folder(DbNodeKind.FunctionFolder, "Functions", functions),
            Folder(DbNodeKind.SequenceFolder, "Sequences", sequences)
        ];
    }

    // A counted grouping folder: an empty one (count 0) drops its expander and reads as a leaf.
    private static DbTreeNode Folder(DbNodeKind kind, string name, int count) =>
        new() { Kind = kind, Name = name, Count = count, HasChildren = count > 0 };

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

    // Per-database on-disk size via pg_database_size. Best-effort — omitted on error.
    private async Task<IReadOnlyDictionary<string, long>> LoadDatabaseSizesAsync(ConnectionProfile profile, CancellationToken ct)
    {
        const string sql = "SELECT datname, pg_database_size(oid) FROM pg_database WHERE datallowconn = true";

        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        try
        {
            await using var connection = new NpgsqlConnection(profile.ConnectionString);
            await connection.OpenAsync(ct);
            await using var command = new NpgsqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                sizes[reader.GetString(0)] = reader.GetInt64(1);
            }
        }
        catch
        {
            // No access → no badges.
        }

        return sizes;
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
        var schema = Name(ancestors, DbNodeKind.Schema);
        var nodes = new List<DbTreeNode>();
        await using var connection = new NpgsqlConnection(ConnectionStringFor(profile, Name(ancestors, DbNodeKind.Database)));
        await connection.OpenAsync(ct);

        // Tables carry a size badge (table + indexes + toast) + row-count tooltip; views have neither.
        var stats = isView ? null : await LoadTableStatsAsync(connection, schema, ct);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("type", isView ? "VIEW" : "BASE TABLE");
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            var stat = stats is not null && stats.TryGetValue(name, out var s) ? s : default;
            nodes.Add(new DbTreeNode
            {
                Kind = kind,
                Name = name,
                HasChildren = true,
                Badge = ByteSize.Format(stat.Size),
                Tooltip = TableStats.RowTooltip(stat.Rows)
            });
        }

        return nodes;
    }

    // Total on-disk size (heap + indexes + toast) + row estimate per table. reltuples is -1 until the
    // table is first analysed; RowTooltip drops non-positive counts. Best-effort.
    private static async Task<IReadOnlyDictionary<string, TableStats>> LoadTableStatsAsync(
        NpgsqlConnection connection,
        string schema,
        CancellationToken ct)
    {
        const string sql = """
            SELECT c.relname, pg_total_relation_size(c.oid), c.reltuples::bigint
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND c.relkind = 'r'
            """;

        var stats = new Dictionary<string, TableStats>(StringComparer.Ordinal);
        try
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("schema", schema);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                stats[reader.GetString(0)] = new TableStats(reader.GetInt64(1), reader.GetInt64(2));
            }
        }
        catch
        {
            // No access → no badges/tooltips.
        }

        return stats;
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
            SELECT column_name, data_type, is_nullable,
                   character_maximum_length, numeric_precision, numeric_scale
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
                var pk = primaryKeys.Contains(name) ? " (PK)" : string.Empty;
                // Full type incl. length/precision — data_type alone is "character varying"/"numeric" without
                // the size, so a DDL round-trip (e.g. backup/restore) would lose it.
                var fullType = FormatColumnType(
                    colReader.GetString(1),
                    Nullable(colReader, 3),
                    Nullable(colReader, 4),
                    Nullable(colReader, 5));
                nodes.Add(new DbTreeNode { Kind = DbNodeKind.Column, Name = name, Detail = $"{fullType}{pk}" });
            }
        }

        return nodes;
    }

    private static int? Nullable(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal));

    // Rebuild the full Postgres type string from information_schema parts: (length) for the
    // character/bit types, (precision, scale) for numeric, bare name otherwise.
    private static string FormatColumnType(string dataType, int? maxLength, int? precision, int? scale)
    {
        switch (dataType)
        {
            case "character varying" or "character" or "bit" or "bit varying" when maxLength is { } length:
                return $"{dataType}({length})";
            case "numeric" when precision is { } p:
                return scale is { } s and > 0 ? $"{dataType}({p},{s})" : $"{dataType}({p})";
            default:
                return dataType;
        }
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

    private async Task<IReadOnlyList<DbTreeNode>> LoadForeignKeysAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct)
    {
        const string sql = """
            SELECT con.conname, a.attname AS column_name, ft.relname AS ref_table, fa.attname AS ref_column
            FROM pg_constraint con
            JOIN pg_class t ON t.oid = con.conrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            JOIN unnest(con.conkey) WITH ORDINALITY AS ck(attnum, ord) ON true
            JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = ck.attnum
            JOIN unnest(con.confkey) WITH ORDINALITY AS fk(attnum, ord) ON fk.ord = ck.ord
            JOIN pg_class ft ON ft.oid = con.confrelid
            JOIN pg_attribute fa ON fa.attrelid = ft.oid AND fa.attnum = fk.attnum
            WHERE con.contype = 'f' AND n.nspname = @schema AND t.relname = @table
            ORDER BY con.conname, ck.ord
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
            var column = reader.GetString(1);
            var refTable = reader.GetString(2);
            var refColumn = reader.GetString(3);
            nodes.Add(new DbTreeNode { Kind = DbNodeKind.ForeignKey, Name = name, Detail = $"{column} → {refTable}.{refColumn}" });
        }

        return nodes;
    }

    // Procedures (prokind 'p', PG11+) and functions (prokind 'f') are schema-scoped. DISTINCT collapses
    // overloads to a single node (v1: the definition/params flow then picks the first overload).
    private async Task<IReadOnlyList<DbTreeNode>> LoadRoutinesAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        char kind,
        CancellationToken ct)
    {
        const string sql = """
            SELECT DISTINCT p.proname
            FROM pg_proc p
            JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = @schema AND p.prokind = @kind
            ORDER BY p.proname
            """;

        var nodeKind = kind == 'p' ? DbNodeKind.Procedure : DbNodeKind.Function;
        var nodes = new List<DbTreeNode>();
        await using var connection = new NpgsqlConnection(ConnectionStringFor(profile, Name(ancestors, DbNodeKind.Database)));
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schema", Name(ancestors, DbNodeKind.Schema));
        command.Parameters.AddWithValue("kind", kind);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            nodes.Add(new DbTreeNode { Kind = nodeKind, Name = reader.GetString(0) });
        }

        return nodes;
    }

    private async Task<IReadOnlyList<DbTreeNode>> LoadTriggersAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct)
    {
        // NOT tgisinternal drops the hidden triggers Postgres creates to enforce foreign keys.
        const string sql = """
            SELECT t.tgname
            FROM pg_trigger t
            JOIN pg_class c ON c.oid = t.tgrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND c.relname = @table AND NOT t.tgisinternal
            ORDER BY t.tgname
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
            nodes.Add(new DbTreeNode { Kind = DbNodeKind.Trigger, Name = reader.GetString(0) });
        }

        return nodes;
    }

    // View Definition / CREATE script: pg_get_functiondef reconstructs a procedure/function's CREATE text;
    // pg_get_triggerdef a trigger's; pg_get_viewdef a view's body (wrapped into a CREATE VIEW). Tables have
    // no native reconstruction function, so a best-effort script is built from information_schema (columns +
    // primary key — no foreign keys/indexes).
    public async Task<string?> GetObjectDefinitionAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct)
    {
        var schema = Name(ancestors, DbNodeKind.Schema);
        var name = ancestors[^1].Name;
        await using var connection = new NpgsqlConnection(ConnectionStringFor(profile, Name(ancestors, DbNodeKind.Database)));
        await connection.OpenAsync(ct);

        if (ancestors[^1].Kind == DbNodeKind.Table)
        {
            return await ScriptTableAsync(connection, schema, name, ct);
        }

        NpgsqlCommand command;
        if (ancestors[^1].Kind == DbNodeKind.Trigger)
        {
            command = new NpgsqlCommand("""
                SELECT pg_get_triggerdef(t.oid)
                FROM pg_trigger t
                JOIN pg_class c ON c.oid = t.tgrelid
                JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = @schema AND c.relname = @table AND t.tgname = @name
                """, connection);
            command.Parameters.AddWithValue("table", Name(ancestors, DbNodeKind.Table));
        }
        else if (ancestors[^1].Kind == DbNodeKind.View)
        {
            command = new NpgsqlCommand(
                "SELECT pg_get_viewdef(format('%I.%I', @schema, @name)::regclass, true)", connection);
        }
        else
        {
            command = new NpgsqlCommand("""
                SELECT pg_get_functiondef(p.oid)
                FROM pg_proc p
                JOIN pg_namespace n ON n.oid = p.pronamespace
                WHERE n.nspname = @schema AND p.proname = @name
                LIMIT 1
                """, connection);
        }

        await using (command)
        {
            command.Parameters.AddWithValue("schema", schema);
            command.Parameters.AddWithValue("name", name);
            var result = await command.ExecuteScalarAsync(ct) as string;

            // pg_get_viewdef returns only the SELECT body; wrap it into a runnable CREATE VIEW.
            return ancestors[^1].Kind == DbNodeKind.View && result is not null
                ? $"CREATE OR REPLACE VIEW {Dialect.QuoteIdentifier(schema)}.{Dialect.QuoteIdentifier(name)} AS\n{result}"
                : result;
        }
    }

    private async Task<string?> ScriptTableAsync(NpgsqlConnection connection, string schema, string name, CancellationToken ct)
    {
        var columns = new List<string>();
        await using (var command = new NpgsqlCommand("""
            SELECT column_name, data_type, is_nullable, character_maximum_length,
                   numeric_precision, numeric_scale, column_default
            FROM information_schema.columns
            WHERE table_schema = @schema AND table_name = @name
            ORDER BY ordinal_position
            """, connection))
        {
            command.Parameters.AddWithValue("schema", schema);
            command.Parameters.AddWithValue("name", name);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var type = FormatColumnType(reader.GetString(1),
                    reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : reader.GetInt32(5));
                var nullable = reader.GetString(2) == "NO" ? " NOT NULL" : "";
                var def = reader.IsDBNull(6) ? "" : $" DEFAULT {reader.GetString(6)}";
                columns.Add($"    {Dialect.QuoteIdentifier(reader.GetString(0))} {type}{def}{nullable}");
            }
        }

        if (columns.Count == 0)
        {
            return null;
        }

        var pk = new List<string>();
        await using (var command = new NpgsqlCommand("""
            SELECT kcu.column_name
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
                ON kcu.constraint_name = tc.constraint_name AND kcu.table_schema = tc.table_schema
            WHERE tc.table_schema = @schema AND tc.table_name = @name AND tc.constraint_type = 'PRIMARY KEY'
            ORDER BY kcu.ordinal_position
            """, connection))
        {
            command.Parameters.AddWithValue("schema", schema);
            command.Parameters.AddWithValue("name", name);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                pk.Add(Dialect.QuoteIdentifier(reader.GetString(0)));
            }
        }

        var lines = new List<string>(columns);
        if (pk.Count > 0)
        {
            lines.Add($"    PRIMARY KEY ({string.Join(", ", pk)})");
        }

        return $"CREATE TABLE {Dialect.QuoteIdentifier(schema)}.{Dialect.QuoteIdentifier(name)} (\n{string.Join(",\n", lines)}\n);";
    }

    public async Task<IReadOnlyList<RoutineParameter>> GetRoutineParametersAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct)
    {
        // information_schema.parameters keyed via the routine's specific_name (overloads share proname,
        // so pick the first match). parameter_mode is IN/OUT/INOUT/VARIADIC.
        const string sql = """
            SELECT p.parameter_name, p.data_type, p.parameter_mode
            FROM information_schema.parameters p
            WHERE p.specific_schema = @schema AND p.specific_name = (
                SELECT r.specific_name FROM information_schema.routines r
                WHERE r.routine_schema = @schema AND r.routine_name = @name
                LIMIT 1)
            ORDER BY p.ordinal_position
            """;

        var result = new List<RoutineParameter>();
        await using var connection = new NpgsqlConnection(ConnectionStringFor(profile, Name(ancestors, DbNodeKind.Database)));
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schema", Name(ancestors, DbNodeKind.Schema));
        command.Parameters.AddWithValue("name", ancestors[^1].Name);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var index = 0;
        while (await reader.ReadAsync(ct))
        {
            index++;
            var name = reader.IsDBNull(0) ? $"${index}" : reader.GetString(0);
            var type = reader.GetString(1);
            // Treat OUT as pure output; INOUT/VARIADIC still need a user value, so mark them input.
            var isOutput = reader.GetString(2).Equals("OUT", StringComparison.OrdinalIgnoreCase);
            result.Add(new RoutineParameter(name, type, isOutput, Default: null));
        }

        return result;
    }

    public SqlStatement BuildCallStatement(
        IReadOnlyList<DbNodeRef> ancestors,
        IReadOnlyList<RoutineParameter> parameters,
        IReadOnlyDictionary<string, string?> inputValues)
    {
        var qualified = $"{Dialect.QuoteIdentifier(Name(ancestors, DbNodeKind.Schema))}.{Dialect.QuoteIdentifier(ancestors[^1].Name)}";

        if (ancestors[^1].Kind == DbNodeKind.Function)
        {
            // A function's OUT parameters come back as result columns, so only IN args are passed.
            var args = string.Join(", ", parameters.Where(p => !p.IsOutput).Select(p => FormatValue(p, inputValues)));
            return new SqlStatement($"SELECT * FROM {qualified}({args});", []);
        }

        // Procedure (PG11+): CALL passes every parameter positionally — OUT placeholders are NULL and
        // CALL returns them (PG14+).
        var callArgs = string.Join(", ", parameters.Select(p => p.IsOutput ? "NULL" : FormatValue(p, inputValues)));
        return new SqlStatement($"CALL {qualified}({callArgs});", []);
    }

    private static string FormatValue(RoutineParameter parameter, IReadOnlyDictionary<string, string?> values)
    {
        var raw = values.TryGetValue(parameter.Name, out var v) ? v : null;
        if (string.IsNullOrEmpty(raw))
        {
            return "NULL";
        }

        return NeedsQuote(parameter.Type) ? "'" + raw.Replace("'", "''") + "'" : raw;
    }

    // Numeric/boolean types pass through unquoted; everything else (text/date/uuid/json/…) is quoted.
    private static bool NeedsQuote(string type) => type.ToLowerInvariant() switch
    {
        "smallint" or "integer" or "bigint" or "numeric" or "decimal" or "real"
            or "double precision" or "boolean" or "money" or "oid" => false,
        _ => true
    };

    // Re-point the connection at a sibling database on the same server; secrets/host stay intact.
    private static string ConnectionStringFor(ConnectionProfile profile, string database) =>
        new NpgsqlConnectionStringBuilder(profile.ConnectionString) { Database = database }.ConnectionString;

    // Open against the tree's database when the host set ConnectionProfile.Database (browsing/querying
    // under a specific catalog); otherwise the connection's own default. Mirrors MsSql's OpenAsync —
    // execute previously always ignored ConnectionProfile.Database here (v11 only fixed MSSQL).
    private static async Task<NpgsqlConnection> OpenAsync(ConnectionProfile profile, CancellationToken ct)
    {
        var connectionString = string.IsNullOrWhiteSpace(profile.Database)
            ? profile.ConnectionString
            : ConnectionStringFor(profile, profile.Database);

        var connection = new NpgsqlConnection(connectionString);
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
        ["integer", "bigint", "text", "varchar(255)", "boolean", "numeric", "timestamp", "timestamptz", "date", "uuid", "jsonb"];

    public SqlStatement BuildCreateStatement(CreateObjectSpec spec)
    {
        var sql = spec.Kind switch
        {
            DbObjectKind.Database => $"CREATE DATABASE {Dialect.QuoteIdentifier(spec.Name)}",
            DbObjectKind.Schema => $"CREATE SCHEMA {Dialect.QuoteIdentifier(spec.Name)}",
            DbObjectKind.Table => BuildCreateTable(spec),
            _ => throw new NotSupportedException($"Postgres cannot create a {spec.Kind}.")
        };

        return new SqlStatement(sql, []);
    }

    private string BuildCreateTable(CreateObjectSpec spec)
    {
        var qualified = spec.Schema is { Length: > 0 }
            ? $"{Dialect.QuoteIdentifier(spec.Schema)}.{Dialect.QuoteIdentifier(spec.Name)}"
            : Dialect.QuoteIdentifier(spec.Name);

        // GENERATED ALWAYS AS IDENTITY is the modern (PG 10+) auto-increment form — works alongside a
        // separate trailing PRIMARY KEY clause, unlike SQLite where autoincrement reshapes the column
        // definition itself.
        var columns = spec.Columns.Select(c =>
            $"{Dialect.QuoteIdentifier(c.Name)} {c.Type}{(c.AutoIncrement ? " GENERATED ALWAYS AS IDENTITY" : "")}{(c.Nullable ? "" : " NOT NULL")}");

        var primaryKey = spec.Columns.Where(c => c.PrimaryKey).Select(c => Dialect.QuoteIdentifier(c.Name)).ToList();
        var clauses = primaryKey.Count > 0
            ? columns.Append($"PRIMARY KEY ({string.Join(", ", primaryKey)})")
            : columns;

        return $"CREATE TABLE {qualified} ({string.Join(", ", clauses)})";
    }

    // CREATE DATABASE must run outside a transaction (Postgres forbids it inside one) — no
    // BeginTransactionAsync here, unlike ExecuteBatchAsync. Autocommit handles schema/table DDL fine too.
    public async Task ExecuteDdlAsync(ConnectionProfile profile, string sql, CancellationToken ct)
    {
        await using var connection = await OpenAsync(profile, ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<QueryResult> ExplainAsync(ConnectionProfile profile, string sql, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        await using var connection = await OpenAsync(profile, ct);
        await using var command = new NpgsqlCommand($"EXPLAIN {sql}", connection);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.KeyInfo, ct);
        return await ReadResultAsync(reader, stopwatch, ct);
    }

    public async Task<IReadOnlyList<string>> GetDatabasesAsync(ConnectionProfile profile, CancellationToken ct)
    {
        const string sql = """
            SELECT datname FROM pg_database
            WHERE datistemplate = false AND datallowconn = true
            ORDER BY datname
            """;

        var names = new List<string>();
        await using var connection = new NpgsqlConnection(profile.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    // Activity Monitor. pg_stat_activity, one row per backend; the connection's own pid comes from
    // pg_backend_pid() so the host can leave it visible-but-disabled. Postgres separates a soft cancel
    // (pg_cancel_backend — abort the running statement) from a hard terminate (pg_terminate_backend).
    public bool SupportsActivityMonitor => true;

    public string SessionIdColumn => "pid";

    public bool SupportsCancelQuery => true;

    public async Task<ActiveSessionSnapshot> GetActiveSessionsAsync(ConnectionProfile profile, CancellationToken ct)
    {
        const string sql = """
            SELECT pid, usename, datname, client_addr, state, wait_event_type, wait_event,
                   query, query_start, backend_start
            FROM pg_stat_activity
            WHERE backend_type = 'client backend'
            ORDER BY pid
            """;

        var stopwatch = Stopwatch.StartNew();
        await using var connection = await OpenAsync(profile, ct);

        string? currentId;
        await using (var pid = new NpgsqlCommand("SELECT pg_backend_pid()", connection))
        {
            currentId = (await pid.ExecuteScalarAsync(ct))?.ToString();
        }

        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = await ReadResultAsync(reader, stopwatch, ct);
        return new ActiveSessionSnapshot(result, currentId);
    }

    // Login-roles (rolcanlogin), the closest Postgres equivalent to a "user". Group roles belong to the
    // later permissions editor (plan decision #3). Roles are cluster-wide, so any database context works.
    private async Task<IReadOnlyList<DbTreeNode>> LoadUsersAsync(ConnectionProfile profile, CancellationToken ct)
    {
        var nodes = new List<DbTreeNode>();
        await using var connection = await OpenAsync(profile, ct);
        await using var command = new NpgsqlCommand(
            "SELECT rolname FROM pg_roles WHERE rolcanlogin = true AND rolname NOT LIKE 'pg\\_%' ORDER BY rolname",
            connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            nodes.Add(new DbTreeNode { Kind = DbNodeKind.User, Name = reader.GetString(0) });
        }

        return nodes;
    }

    public bool CanManageUsers => true;

    public IReadOnlyList<UserField> UserFields { get; } =
    [
        new("password", "Password", UserFieldType.Password, Required: true),
        new("superuser", "Superuser", UserFieldType.Bool, Default: "false"),
        new("createdb", "Create databases", UserFieldType.Bool, Default: "false")
    ];

    public async Task<IReadOnlyList<string>> GetAssignableRolesAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct)
    {
        var roles = new List<string>();
        await using var connection = await OpenAsync(profile, ct);
        // Group roles to grant membership of (non-login, excluding the built-in pg_* roles).
        await using var command = new NpgsqlCommand(
            "SELECT rolname FROM pg_roles WHERE rolcanlogin = false AND rolname NOT LIKE 'pg\\_%' ORDER BY rolname",
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
        var name = Dialect.QuoteIdentifier(values["name"] ?? string.Empty);
        var password = (values.GetValueOrDefault("password") ?? string.Empty).Replace("'", "''");

        var options = new StringBuilder("LOGIN");
        if (values.GetValueOrDefault("superuser") == "true") options.Append(" SUPERUSER");
        if (values.GetValueOrDefault("createdb") == "true") options.Append(" CREATEDB");

        var script = new StringBuilder();
        script.Append($"CREATE ROLE {name} WITH {options} PASSWORD '{password}';");
        foreach (var role in roles)
        {
            script.Append($"\nGRANT {Dialect.QuoteIdentifier(role)} TO {name};");
        }

        return new SqlStatement(script.ToString(), []);
    }

    public SqlStatement BuildDropUserStatement(DbNodeRef userNode, IReadOnlyList<DbNodeRef> ancestors) =>
        new($"DROP ROLE {Dialect.QuoteIdentifier(userNode.Name)};", []);

    public Task KillSessionAsync(ConnectionProfile profile, string sessionId, CancellationToken ct) =>
        RunBackendActionAsync(profile, "pg_terminate_backend", sessionId, ct);

    public Task CancelQueryAsync(ConnectionProfile profile, string sessionId, CancellationToken ct) =>
        RunBackendActionAsync(profile, "pg_cancel_backend", sessionId, ct);

    // pid is a parameter (not string-interpolated) so the value can only ever be an integer bind, and
    // the same call shape serves both the hard terminate and the soft cancel.
    private static async Task RunBackendActionAsync(ConnectionProfile profile, string function, string sessionId, CancellationToken ct)
    {
        var pid = int.Parse(sessionId, CultureInfo.InvariantCulture);
        await using var connection = await OpenAsync(profile, ct);
        await using var command = new NpgsqlCommand($"SELECT {function}($1)", connection)
        {
            Parameters = { new NpgsqlParameter { Value = pid } }
        };
        await command.ExecuteNonQueryAsync(ct);
    }
}
