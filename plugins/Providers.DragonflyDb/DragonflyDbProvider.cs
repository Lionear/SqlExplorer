using System.Diagnostics;
using SqlExplorer.Sdk;
using StackExchange.Redis;

namespace SqlExplorer.Providers.DragonflyDb;

/// <summary>
/// A DragonflyDB provider. DragonflyDB is RESP2/RESP3-protocol compatible with Redis (a drop-in
/// replacement: same command set for strings/hashes/lists/sets/zsets, same <c>MULTI</c>/<c>EXEC</c>, same
/// <c>SCAN</c> cursor model, same <c>StackExchange.Redis</c> client), so this provider is a near-verbatim
/// copy of the Redis provider — it maps a typed key-value store onto the host's (SQL-shaped)
/// <see cref="IDbProvider"/> contract in exactly the same way (SE-4, mirroring SE-20/SE-114):
/// <list type="bullet">
/// <item>Schema tree: connection root → DB indices → keys, grouped one level deep by a <c>:</c>-prefix.</item>
/// <item>Queries: typed command lines (<c>GET key</c> / <c>HGETALL key</c> / …), parsed by
///   <see cref="DragonflyDbQuery"/> and run through the native driver.</item>
/// <item>Editable grid: Hash keys only (edit/delete an existing field's value). String/List/Set/ZSet stay
///   read-only in the grid; mutate them via console commands.</item>
/// <item>DDL / routines / user management: not modelled.</item>
/// </list>
/// Where Dragonfly genuinely differs from Redis (unsupported <c>OBJECT ENCODING</c>/<c>FUNCTION</c>/
/// <c>FCALL</c>, a different DB-count config key, no keyspace notifications), the shared code paths already
/// degrade gracefully — the concrete deviation is the DB-count lookup below, which consults Dragonfly's
/// <c>dbnum</c> config key. See the plugin's README for the full compatibility notes.
///
/// It ships from the repo-root <c>plugins/</c> folder (not <c>src/</c>) and is staged only in Debug builds,
/// so it is directly usable while developing but never part of a Release/MVP.
/// </summary>
public sealed class DragonflyDbProvider : IDbProvider
{
    private const int DefaultLimit = 1000;
    private const int MaxKeysPerNode = 2000;

    public string DisplayName => "DragonflyDB";

    public ProviderIcon? Icon { get; } = ProviderIconLoader.Load(typeof(DragonflyDbProvider), "🐉");

    public ISqlDialect Dialect { get; } = new DragonflyDbDialect();

    public bool IsSqlBased => false;

    public IReadOnlyList<ConnectionField> ConnectionFields { get; } =
    [
        new("host", "Host", ConnectionFieldType.Text, Required: true, Default: "localhost"),
        new("port", "Port", ConnectionFieldType.Number, Default: "6379"),
        new("password", "Password", ConnectionFieldType.Password),
        new("database", "Database index", ConnectionFieldType.Number, Default: "0", Advanced: true),
        new("useTls", "Use TLS", ConnectionFieldType.Bool, Advanced: true)
    ];

    public string BuildConnectionString(IReadOnlyDictionary<string, string?> values)
    {
        var options = new ConfigurationOptions
        {
            EndPoints = { { Value(values, "host") ?? "localhost", int.TryParse(Value(values, "port"), out var port) ? port : 6379 } },
            Password = Value(values, "password"),
            Ssl = Value(values, "useTls") is "true" or "True",
            DefaultDatabase = int.TryParse(Value(values, "database"), out var db) ? db : 0,
            AbortOnConnectFail = false
        };

        return options.ToString();
    }

    public IReadOnlyDictionary<string, string?>? ParseConnectionString(string connectionString)
    {
        ConfigurationOptions options;
        try
        {
            options = ConfigurationOptions.Parse(connectionString);
        }
        catch
        {
            return null;
        }

        var result = new Dictionary<string, string?>();
        var endpoint = options.EndPoints.FirstOrDefault();
        if (endpoint is System.Net.DnsEndPoint dns)
        {
            result["host"] = dns.Host;
            result["port"] = dns.Port.ToString();
        }

        if (!string.IsNullOrEmpty(options.Password)) result["password"] = options.Password;
        if (options.DefaultDatabase is { } d) result["database"] = d.ToString();
        result["useTls"] = options.Ssl ? "true" : "false";
        return result;
    }

    public async Task<bool> TestConnectionAsync(ConnectionProfile profile, CancellationToken ct)
    {
        await using var multiplexer = await ConnectAsync(profile);
        var pong = await GetDatabase(multiplexer, profile).PingAsync();
        return pong >= TimeSpan.Zero;
    }

    // --- Schema tree: root -> DB indices -> keys (grouped one level by ':'-prefix) --------------------
    public async Task<IReadOnlyList<DbTreeNode>> GetChildNodesAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct)
    {
        await using var multiplexer = await ConnectAsync(profile);

        if (ancestors.Count == 0)
        {
            return await LoadDatabasesAsync(multiplexer, ct);
        }

        var dbIndex = int.Parse(ancestors[0].Name);
        var server = multiplexer.GetServer(multiplexer.GetEndPoints()[0]);

        if (ancestors[^1].Kind == DbNodeKind.Group)
        {
            var prefix = ancestors[^1].Name;
            return LoadKeys(server, dbIndex, $"{prefix}:*");
        }

        // Database node: list every key, grouped one level deep by a ':'-prefix.
        var keys = server.Keys(dbIndex, "*", pageSize: 250).Take(MaxKeysPerNode).Select(k => (string)k!).ToList();
        var groups = new List<DbTreeNode>();
        var leaves = new List<DbTreeNode>();
        var seenGroups = new HashSet<string>(StringComparer.Ordinal);

        foreach (var key in keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var colon = key.IndexOf(':');
            if (colon > 0)
            {
                var prefix = key[..colon];
                if (seenGroups.Add(prefix))
                {
                    groups.Add(new DbTreeNode { Kind = DbNodeKind.Group, Name = prefix, HasChildren = true });
                }
            }
            else
            {
                leaves.Add(new DbTreeNode { Kind = DbNodeKind.Table, Name = key, HasChildren = false });
            }
        }

        return [.. groups, .. leaves];
    }

    private static List<DbTreeNode> LoadKeys(IServer server, int dbIndex, string pattern) =>
        server.Keys(dbIndex, pattern, pageSize: 250)
            .Take(MaxKeysPerNode)
            .Select(k => new DbTreeNode { Kind = DbNodeKind.Table, Name = (string)k!, HasChildren = false })
            .OrderBy(n => n.Name, StringComparer.Ordinal)
            .ToList();

    private async Task<IReadOnlyList<DbTreeNode>> LoadDatabasesAsync(IConnectionMultiplexer multiplexer, CancellationToken ct)
    {
        var databaseCount = await GetDatabaseCountAsync(multiplexer);

        var nodes = new List<DbTreeNode>();
        for (var i = 0; i < databaseCount; i++)
        {
            long size;
            try
            {
                size = (long)await multiplexer.GetDatabase(i).ExecuteAsync("DBSIZE");
            }
            catch
            {
                continue;
            }

            // Always show db0 (even empty) so a fresh server isn't a dead-looking tree; skip other
            // empty DBs to avoid 16 mostly-empty nodes on every connection.
            if (size == 0 && i != 0)
            {
                continue;
            }

            nodes.Add(new DbTreeNode { Kind = DbNodeKind.Database, Name = i.ToString(), HasChildren = true, Detail = $"{size:N0} keys" });
        }

        return nodes;
    }

    // The number of logical databases is server-config, overridable per deployment — never hardcode 16.
    // Redis exposes it as `CONFIG GET databases`; DragonflyDB's is the `--dbnum` flag, surfaced as
    // `CONFIG GET dbnum`. Try the Redis key first (a Dragonfly build may alias it), then Dragonfly's own
    // key, then fall back to the shared default of 16. CONFIG GET is disabled on some managed/ACL-restricted
    // deployments, so every probe is best-effort.
    private static async Task<int> GetDatabaseCountAsync(IConnectionMultiplexer multiplexer)
    {
        var server = multiplexer.GetServer(multiplexer.GetEndPoints()[0]);
        foreach (var parameter in new[] { "databases", "dbnum" })
        {
            try
            {
                var config = await server.ConfigGetAsync(parameter);
                if (config.Length > 0 && int.TryParse(config[0].Value, out var configured) && configured > 0)
                {
                    return configured;
                }
            }
            catch
            {
                // This parameter isn't recognised (or CONFIG GET is restricted) — try the next candidate.
            }
        }

        return 16;
    }

    public async Task<IReadOnlyList<string>> GetDatabasesAsync(ConnectionProfile profile, CancellationToken ct)
    {
        await using var multiplexer = await ConnectAsync(profile);
        var nodes = await LoadDatabasesAsync(multiplexer, ct);
        return nodes.Select(n => n.Name).ToList();
    }

    // --- Query execution ------------------------------------------------------------------------------
    public async Task<QueryResult> ExecuteQueryAsync(ConnectionProfile profile, string sql, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var query = DragonflyDbQuery.Parse(sql);
        await using var multiplexer = await ConnectAsync(profile);
        var db = GetDatabase(multiplexer, profile);

        return query.Kind == DragonflyDbQueryKind.Browse
            ? await BrowseKeyAsync(db, query, stopwatch.Elapsed)
            : await RunCommandAsync(db, query.Command, query.Args, stopwatch.Elapsed);
    }

    // Dragonfly has no multi-statement scripts in the SQL sense; a "script" is one command line per line.
    public async Task<IReadOnlyList<QueryResult>> ExecuteScriptAsync(ConnectionProfile profile, string sql, CancellationToken ct)
    {
        var lines = sql.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();

        if (lines.Count == 0)
        {
            return [await ExecuteQueryAsync(profile, sql, ct)];
        }

        var results = new List<QueryResult>(lines.Count);
        foreach (var line in lines)
        {
            results.Add(await ExecuteQueryAsync(profile, line, ct));
        }

        return results;
    }

    // Dragonfly has no query planner (direct key access is O(1)/O(log N) depending on the command) —
    // surface that fact plus the key's TYPE as the closest useful equivalent. OBJECT ENCODING is
    // "Unsupported" on Dragonfly (it is on Redis' side of the compatibility table), so the try/catch below
    // simply omits the encoding there — the TYPE alone is still useful.
    public async Task<QueryResult> ExplainAsync(ConnectionProfile profile, string sql, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var query = DragonflyDbQuery.Parse(sql);
        var key = query.Kind == DragonflyDbQueryKind.Browse ? query.Key : query.Args.FirstOrDefault();

        await using var multiplexer = await ConnectAsync(profile);
        var db = GetDatabase(multiplexer, profile);

        string plan;
        if (key is null)
        {
            plan = "DragonflyDB has no query planner — every command is a direct, O(1)/O(log N) key operation.";
        }
        else
        {
            var type = await db.KeyTypeAsync(key);
            string? encoding = null;
            try
            {
                encoding = (string?)await db.ExecuteAsync("OBJECT", "ENCODING", key);
            }
            catch
            {
                // OBJECT ENCODING is Unsupported on Dragonfly (and restricted on some Redis deployments);
                // the type alone is still useful.
            }

            plan = type == RedisType.None
                ? $"Key \"{key}\" does not exist."
                : $"Key \"{key}\": type={type}{(encoding is null ? "" : $", encoding={encoding}")}. " +
                  "DragonflyDB has no query planner — every command is a direct, O(1)/O(log N) key operation.";
        }

        return new QueryResult
        {
            Columns = [new ResultColumn("plan", typeof(string))],
            Rows = [[plan]],
            Elapsed = stopwatch.Elapsed
        };
    }

    // --- Provider-owned statement generation (non-SQL seam) --------------------------------------------
    // The tree's "Select top 1000" action: return the literal command text for the given key, so the user
    // sees (and can edit) real Redis/Dragonfly syntax in the query tab instead of the browse pseudo-SQL.
    // Needs a live TYPE lookup (StackExchange.Redis exposes true synchronous methods, not just Task-wrapped
    // ones) since a key's shape isn't known until queried — see the design note on IDbProvider.BuildNodeQuery.
    public string? BuildNodeQuery(
        NodeQueryKind kind,
        IReadOnlyList<DbNodeRef> nodePath,
        IReadOnlyList<ResultColumn>? columns,
        ConnectionProfile profile)
    {
        if (nodePath.Count == 0 || nodePath[^1].Kind != DbNodeKind.Table)
        {
            return null;
        }

        var key = nodePath[^1].Name;
        if (kind == NodeQueryKind.Count)
        {
            return $"EXISTS \"{key}\"";
        }

        if (kind is not (NodeQueryKind.SelectAll or NodeQueryKind.SelectTop))
        {
            // Column-shaped scaffolds (SELECT columns / INSERT / UPDATE / DELETE) don't map to a typed
            // key store — the host hides that submenu for a non-SQL provider anyway.
            return null;
        }

        using var multiplexer = ConnectAsync(profile).GetAwaiter().GetResult();
        var db = GetDatabase(multiplexer, profile);
        var type = db.KeyType(key);
        var limit = kind == NodeQueryKind.SelectTop ? DefaultLimit : int.MaxValue;

        return type switch
        {
            RedisType.String => $"GET \"{key}\"",
            RedisType.Hash => $"HGETALL \"{key}\"",
            RedisType.List => limit == int.MaxValue ? $"LRANGE \"{key}\" 0 -1" : $"LRANGE \"{key}\" 0 {limit - 1}",
            RedisType.Set => $"SMEMBERS \"{key}\"",
            RedisType.SortedSet => limit == int.MaxValue
                ? $"ZRANGE \"{key}\" 0 -1 WITHSCORES"
                : $"ZRANGE \"{key}\" 0 {limit - 1} WITHSCORES",
            _ => $"TYPE \"{key}\""
        };
    }

    // "Drop" and "Truncate" on a key: the host previews this text and runs it via ExecuteDdlAsync. Deleting
    // every element of a collection auto-deletes the key, so there is no "clear a key but keep it present"
    // distinct from "the key doesn't exist" — Drop and Truncate are deliberately the same DEL operation here.
    public SqlStatement? BuildAlterStatement(AlterSpec spec) => spec.Action switch
    {
        AlterAction.DropTable or AlterAction.TruncateTable => new SqlStatement($"DEL \"{spec.Target}\"", []),
        _ => null
    };

    // --- Capabilities DragonflyDB does not model --------------------------------------------------
    public IReadOnlyList<CreateCapability> CreateCapabilities { get; } = [];

    public IReadOnlyList<string> ColumnTypes { get; } = [];

    private static NotSupportedException NotSupported() =>
        new("The DragonflyDB provider does not support SQL DDL. Use Redis/Dragonfly commands instead.");

    public SqlStatement BuildCreateStatement(CreateObjectSpec spec) => throw NotSupported();

    // Runs the DEL that BuildAlterStatement emits (and that the user may edit in the preview) — any
    // command line works here too, same escape hatch as ExecuteQueryAsync's command path.
    public async Task ExecuteDdlAsync(ConnectionProfile profile, string sql, CancellationToken ct)
    {
        var query = DragonflyDbQuery.Parse(sql);
        await using var multiplexer = await ConnectAsync(profile);
        var db = GetDatabase(multiplexer, profile);

        if (query.Kind == DragonflyDbQueryKind.Browse)
        {
            await db.KeyDeleteAsync(query.Key);
            return;
        }

        await db.ExecuteAsync(query.Command, query.Args.Cast<object>().ToArray());
    }

    public Task<int> ExecuteBatchAsync(ConnectionProfile profile, IReadOnlyList<SqlStatement> statements, CancellationToken ct) =>
        throw NotSupported();

    // --- Editable-grid save-flow (SE-114 non-SQL writeback), Hash keys only ---------------------------
    // BrowseHashAsync only tags field/value columns IsKey/BaseTable for a Hash (see below) — that's the
    // only shape EditableResultSet/ChangeSetBuilder will ever hand us a non-empty ChangeSet for, so this
    // method can assume Hash semantics without re-deriving the key's type.
    //
    // Scope cut: "field" is IsKey + IsReadOnly (like Mongo's _id), so ChangeSetBuilder never includes it
    // in a row's Cells — there is no per-row "editable only on insert" concept in the current SDK, and
    // letting an existing row's field name be edited in place would silently no-op rather than rename the
    // hash field. Consequence: "Add Row" cannot supply a field name and is therefore unsupported for Hash
    // keys — reported as a row error, not silently dropped. Add a field via the console instead
    // (HSET key field value).
    public async Task<WritebackResult> ApplyChangesAsync(ConnectionProfile profile, ChangeSet changes, CancellationToken ct)
    {
        await using var multiplexer = await ConnectAsync(profile);
        var db = GetDatabase(multiplexer, profile);
        var key = changes.Table;
        var affected = 0;
        var errors = new List<string>();

        // One MULTI/EXEC transaction for the whole grid save. Dragonfly runs MULTI/EXEC on its
        // multi-threaded shard architecture rather than Redis' single-thread loop, but the RESP contract to
        // the client is the same all-or-nothing guarantee for the single key touched per row.
        var transaction = db.CreateTransaction();
        var pending = new List<Task>();

        foreach (var row in changes.Rows)
        {
            switch (row.Kind)
            {
                case RowChangeKind.Added:
                    errors.Add("New hash fields can't be added from the grid (the field name has no cell to " +
                               "type it into) — use HSET \"" + key + "\" <field> <value> in the console instead.");
                    break;

                case RowChangeKind.Modified:
                    var field = row.Identity.GetValueOrDefault("field") as string;
                    var value = row.Cells.FirstOrDefault(c => c.Column == "value")?.Value?.ToString();
                    if (field is not null && value is not null)
                    {
                        pending.Add(transaction.HashSetAsync(key, field, value));
                        affected++;
                    }
                    break;

                case RowChangeKind.Deleted:
                    if (row.Identity.GetValueOrDefault("field") is string deletedField)
                    {
                        pending.Add(transaction.HashDeleteAsync(key, deletedField));
                        affected++;
                    }
                    break;
            }
        }

        if (pending.Count == 0)
        {
            return new WritebackResult(0, IsAtomic: true, errors);
        }

        var committed = await transaction.ExecuteAsync();
        if (committed)
        {
            await Task.WhenAll(pending);
        }
        else
        {
            errors.Add("MULTI/EXEC transaction was aborted.");
            affected = 0;
        }

        return new WritebackResult(affected, IsAtomic: true, errors);
    }

    // --- Helpers ------------------------------------------------------------------------------------
    private static async Task<ConnectionMultiplexer> ConnectAsync(ConnectionProfile profile) =>
        await ConnectionMultiplexer.ConnectAsync(profile.ConnectionString);

    private static IDatabase GetDatabase(IConnectionMultiplexer multiplexer, ConnectionProfile profile)
    {
        // The switcher (ConnectionProfile.Database) takes precedence over the connection string's own
        // DefaultDatabase, same convention as the Redis/Mongo providers.
        var index = int.TryParse(profile.Database, out var i) ? i : -1;
        return index >= 0 ? multiplexer.GetDatabase(index) : multiplexer.GetDatabase();
    }

    private static string? Value(IReadOnlyDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    // --- Browse (pseudo-SQL "SELECT * FROM key LIMIT n OFFSET m") --------------------------------------
    private async Task<QueryResult> BrowseKeyAsync(IDatabase db, DragonflyDbQuery query, TimeSpan elapsed)
    {
        var key = query.Key;
        var limit = query.Limit ?? DefaultLimit;
        var offset = query.Offset ?? 0;
        var type = await db.KeyTypeAsync(key);

        return type switch
        {
            RedisType.String => await BrowseStringAsync(db, key, elapsed),
            RedisType.Hash => await BrowseHashAsync(db, key, elapsed),
            RedisType.List => await BrowseListAsync(db, key, offset, limit, elapsed),
            RedisType.Set => await BrowseSetAsync(db, key, elapsed),
            RedisType.SortedSet => await BrowseSortedSetAsync(db, key, offset, limit, elapsed),
            _ => new QueryResult { Columns = [new ResultColumn("value", typeof(string))], Rows = [], Elapsed = elapsed }
        };
    }

    private static async Task<QueryResult> BrowseStringAsync(IDatabase db, string key, TimeSpan elapsed)
    {
        var value = await db.StringGetAsync(key);
        return new QueryResult
        {
            Columns = [new ResultColumn("value", typeof(string))],
            Rows = value.IsNull ? [] : [[(string?)value]],
            Elapsed = elapsed
        };
    }

    // The only editable shape: field/value rows, keyed by "field" — mirrors Mongo tagging _id IsKey.
    private static async Task<QueryResult> BrowseHashAsync(IDatabase db, string key, TimeSpan elapsed)
    {
        var entries = await db.HashGetAllAsync(key);
        return new QueryResult
        {
            Columns =
            [
                new ResultColumn("field", typeof(string)) { BaseTable = key, BaseColumn = "field", IsKey = true, IsReadOnly = true },
                new ResultColumn("value", typeof(string)) { BaseTable = key, BaseColumn = "value" }
            ],
            Rows = entries.Select(e => new object?[] { (string?)e.Name, (string?)e.Value }).ToList(),
            Elapsed = elapsed
        };
    }

    private static async Task<QueryResult> BrowseListAsync(IDatabase db, string key, int offset, int limit, TimeSpan elapsed)
    {
        var values = await db.ListRangeAsync(key, offset, offset + limit - 1);
        return new QueryResult
        {
            Columns = [new ResultColumn("value", typeof(string))],
            Rows = values.Select(v => new object?[] { (string?)v }).ToList(),
            Elapsed = elapsed
        };
    }

    // Sets are unordered with no native offset/limit paging (SMEMBERS reads the whole set); browse ignores
    // paging here, a known MVP limitation for very large sets (documented in the README).
    private static async Task<QueryResult> BrowseSetAsync(IDatabase db, string key, TimeSpan elapsed)
    {
        var members = await db.SetMembersAsync(key);
        return new QueryResult
        {
            Columns = [new ResultColumn("member", typeof(string))],
            Rows = members.Select(m => new object?[] { (string?)m }).ToList(),
            Elapsed = elapsed
        };
    }

    private static async Task<QueryResult> BrowseSortedSetAsync(IDatabase db, string key, int offset, int limit, TimeSpan elapsed)
    {
        var entries = await db.SortedSetRangeByRankWithScoresAsync(key, offset, offset + limit - 1);
        return new QueryResult
        {
            Columns = [new ResultColumn("member", typeof(string)), new ResultColumn("score", typeof(double))],
            Rows = entries.Select(e => new object?[] { (string?)e.Element, e.Score }).ToList(),
            Elapsed = elapsed
        };
    }

    // --- Literal command execution (console) ------------------------------------------------------------
    private static async Task<QueryResult> RunCommandAsync(IDatabase db, string command, IReadOnlyList<string> args, TimeSpan elapsed)
    {
        var result = await db.ExecuteAsync(command, args.Cast<object>().ToArray());
        return FormatResult(command, args, result, elapsed);
    }

    private static QueryResult FormatResult(string command, IReadOnlyList<string> args, RedisResult result, TimeSpan elapsed)
    {
        if (result.IsNull)
        {
            return new QueryResult { Columns = [new ResultColumn("result", typeof(string))], Rows = [["(nil)"]], Elapsed = elapsed };
        }

        if (result.Resp2Type != ResultType.Array)
        {
            return new QueryResult
            {
                Columns = [new ResultColumn("result", typeof(string))],
                Rows = [[result.ToString()]],
                Elapsed = elapsed
            };
        }

        var items = (RedisResult[])result!;

        if (command == "HGETALL" && items.Length % 2 == 0)
        {
            var rows = new List<object?[]>(items.Length / 2);
            for (var i = 0; i < items.Length; i += 2)
            {
                rows.Add([items[i].ToString(), items[i + 1].ToString()]);
            }

            return new QueryResult
            {
                Columns = [new ResultColumn("field", typeof(string)), new ResultColumn("value", typeof(string))],
                Rows = rows,
                Elapsed = elapsed
            };
        }

        if (command.StartsWith('Z') && args.Any(a => a.Equals("WITHSCORES", StringComparison.OrdinalIgnoreCase)) && items.Length % 2 == 0)
        {
            var rows = new List<object?[]>(items.Length / 2);
            for (var i = 0; i < items.Length; i += 2)
            {
                rows.Add([items[i].ToString(), (double?)items[i + 1]]);
            }

            return new QueryResult
            {
                Columns = [new ResultColumn("member", typeof(string)), new ResultColumn("score", typeof(double))],
                Rows = rows,
                Elapsed = elapsed
            };
        }

        return new QueryResult
        {
            Columns = [new ResultColumn("value", typeof(string))],
            Rows = items.Select(i => new object?[] { i.Resp2Type == ResultType.Array ? string.Join(", ", (RedisResult[])i!) : i.ToString() }).ToList(),
            Elapsed = elapsed
        };
    }
}
