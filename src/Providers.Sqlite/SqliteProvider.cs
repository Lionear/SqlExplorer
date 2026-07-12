using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Lionear.SqlExplorer.Sdk;
using Microsoft.Data.Sqlite;

namespace Lionear.SqlExplorer.Providers.Sqlite;

public sealed class SqliteProvider : IDbProvider
{
    public DatabaseKind Kind => DatabaseKind.Sqlite;

    public string DisplayName => "SQLite";

    // Uses the embedded brand PNG (icon.png) when present; falls back to a glyph otherwise.
    public ProviderIcon? Icon { get; } = ProviderIconLoader.Load(typeof(SqliteProvider), "🪶");

    public ISqlDialect Dialect { get; } = new SqliteDialect();

    public IReadOnlyList<ConnectionField> ConnectionFields { get; } =
    [
        new("path", "Database file", ConnectionFieldType.File, Required: true,
            Placeholder: "/path/to/database.db")
    ];

    public string BuildConnectionString(IReadOnlyDictionary<string, string?> values)
    {
        var path = values.TryGetValue("path", out var v) ? v : null;
        return new SqliteConnectionStringBuilder { DataSource = path }.ConnectionString;
    }

    public async Task<bool> TestConnectionAsync(ConnectionProfile profile, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(profile.ConnectionString);
        await connection.OpenAsync(ct);
        return connection.State == ConnectionState.Open;
    }

    public async Task<QueryResult> ExecuteQueryAsync(ConnectionProfile profile, string sql, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        await using var connection = new SqliteConnection(profile.ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
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

    // Map SQLite's column schema onto our ResultColumn metadata. The bundled e_sqlite3 is built
    // with SQLITE_ENABLE_COLUMN_METADATA, so base table/column names and the PK flag are available.
    private static List<ResultColumn> BuildColumns(SqliteDataReader reader)
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
        await using var connection = new SqliteConnection(profile.ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var affected = 0;
        foreach (var statement in statements)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = statement.Text;
            foreach (var parameter in statement.Parameters)
            {
                command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            }

            affected += await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return affected;
    }

    // SQLite has no server/database/schema layer: the connection root shows Tables/Views
    // folders directly, then the objects, then their columns.
    public async Task<IReadOnlyList<DbTreeNode>> GetChildNodesAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct)
    {
        var parent = ancestors.Count == 0 ? (DbNodeKind?)null : ancestors[^1].Kind;

        return parent switch
        {
            null => Folders(),
            DbNodeKind.TableFolder => await LoadObjectsAsync(profile, isView: false, ct),
            DbNodeKind.ViewFolder => await LoadObjectsAsync(profile, isView: true, ct),
            DbNodeKind.SequenceFolder => await LoadSequencesAsync(profile, ct),
            // Tables carry an extra "Indexes" folder; views have no indexes.
            DbNodeKind.Table => [.. await LoadColumnsAsync(profile, ancestors[^1].Name, ct), IndexFolder()],
            DbNodeKind.View => await LoadColumnsAsync(profile, ancestors[^1].Name, ct),
            DbNodeKind.IndexFolder => await LoadIndexesAsync(profile, Name(ancestors, DbNodeKind.Table), ct),
            _ => []
        };
    }

    // No schema layer, so Tables/Views/Sequences sit directly under the connection root.
    private static IReadOnlyList<DbTreeNode> Folders() =>
    [
        new() { Kind = DbNodeKind.TableFolder, Name = "Tables", HasChildren = true },
        new() { Kind = DbNodeKind.ViewFolder, Name = "Views", HasChildren = true },
        new() { Kind = DbNodeKind.SequenceFolder, Name = "Sequences", HasChildren = true }
    ];

    private static DbTreeNode IndexFolder() =>
        new() { Kind = DbNodeKind.IndexFolder, Name = "Indexes", HasChildren = true };

    private static string Name(IReadOnlyList<DbNodeRef> ancestors, DbNodeKind kind) =>
        ancestors.First(a => a.Kind == kind).Name;

    private static async Task<IReadOnlyList<DbTreeNode>> LoadObjectsAsync(
        ConnectionProfile profile,
        bool isView,
        CancellationToken ct)
    {
        const string sql = """
            SELECT name FROM sqlite_schema
            WHERE type = @type AND name NOT LIKE 'sqlite_%'
            ORDER BY name
            """;

        var kind = isView ? DbNodeKind.View : DbNodeKind.Table;
        var nodes = new List<DbTreeNode>();
        await using var connection = new SqliteConnection(profile.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@type", isView ? "view" : "table");
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            nodes.Add(new DbTreeNode { Kind = kind, Name = reader.GetString(0), HasChildren = true });
        }

        return nodes;
    }

    private async Task<IReadOnlyList<DbTreeNode>> LoadColumnsAsync(
        ConnectionProfile profile,
        string tableName,
        CancellationToken ct)
    {
        // PRAGMA takes an identifier, not a bindable parameter; quote it via the dialect.
        var sql = $"PRAGMA table_info({Dialect.QuoteIdentifier(tableName)})";

        var nodes = new List<DbTreeNode>();
        await using var connection = new SqliteConnection(profile.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(ct);

        // table_info columns: 0=cid, 1=name, 2=type, 3=notnull, 4=dflt_value, 5=pk
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(1);
            var dataType = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            var pk = reader.GetInt64(5) != 0 ? " (PK)" : string.Empty;
            var type = string.IsNullOrEmpty(dataType) ? string.Empty : dataType;
            nodes.Add(new DbTreeNode { Kind = DbNodeKind.Column, Name = name, Detail = $"{type}{pk}" });
        }

        return nodes;
    }

    // SQLite has no named sequence objects; AUTOINCREMENT counters live in the internal
    // sqlite_sequence table (one row per AUTOINCREMENT table: name = table, seq = last rowid).
    // That table only exists once an AUTOINCREMENT table has been created, so guard its absence.
    private static async Task<IReadOnlyList<DbTreeNode>> LoadSequencesAsync(
        ConnectionProfile profile,
        CancellationToken ct)
    {
        var nodes = new List<DbTreeNode>();
        await using var connection = new SqliteConnection(profile.ConnectionString);
        await connection.OpenAsync(ct);

        await using (var probe = connection.CreateCommand())
        {
            probe.CommandText = "SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = 'sqlite_sequence'";
            if (await probe.ExecuteScalarAsync(ct) is null)
            {
                return nodes;
            }
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, seq FROM sqlite_sequence ORDER BY name";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            var current = reader.IsDBNull(1) ? null : $"= {reader.GetValue(1)}";
            nodes.Add(new DbTreeNode { Kind = DbNodeKind.Sequence, Name = name, Detail = current });
        }

        return nodes;
    }

    private async Task<IReadOnlyList<DbTreeNode>> LoadIndexesAsync(
        ConnectionProfile profile,
        string tableName,
        CancellationToken ct)
    {
        // PRAGMA takes an identifier, not a bindable parameter; quote it via the dialect.
        var sql = $"PRAGMA index_list({Dialect.QuoteIdentifier(tableName)})";

        var nodes = new List<DbTreeNode>();
        await using var connection = new SqliteConnection(profile.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(ct);

        // index_list columns: 0=seq, 1=name, 2=unique, 3=origin ('c'|'u'|'pk'), 4=partial
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(1);
            var unique = reader.GetInt64(2) != 0;
            var origin = reader.GetString(3);
            var detail = origin == "pk" ? "PK" : unique ? "unique" : null;
            nodes.Add(new DbTreeNode { Kind = DbNodeKind.Index, Name = name, Detail = detail });
        }

        return nodes;
    }
}
