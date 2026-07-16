using System.Diagnostics;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;
using SqlExplorer.Sdk;
using SqlExplorer.Sdk.Editing;

namespace SqlExplorer.Providers.MongoDb;

/// <summary>
/// A MongoDB provider. Mongo is a document store, not a SQL engine, so this maps its world onto the
/// host's (SQL-shaped) <see cref="IDbProvider"/> contract honestly:
/// <list type="bullet">
/// <item>Schema tree: connection root → databases → collections (schemaless, so a collection is a leaf).</item>
/// <item>Queries: the editor holds mongo-shell-ish text (<c>db.coll.find(...)</c> / <c>.aggregate([...])</c>),
///   parsed by <see cref="MongoQuery"/> and run through the native driver; documents are flattened to a grid.</item>
/// <item>Editable grid: <c>_id</c> is tagged <c>IsKey</c>/<c>BaseTable</c> so the host's usual editable-grid
///   flow lights up, but writeback goes through <see cref="ApplyChangesAsync"/> (SE-114) — no SQL generated.</item>
/// <item>DDL / routines / user management: not modelled (the relevant capability flags stay off).</item>
/// </list>
/// It ships from the repo-root <c>plugins/</c> folder (not <c>src/</c>) and is staged only in Debug builds,
/// so it is directly usable while developing but never part of a Release/MVP.
/// </summary>
public sealed class MongoDbProvider : IDbProvider
{
    private const int DefaultLimit = 1000;

    public string DisplayName => "MongoDB";

    public ProviderIcon? Icon { get; } = ProviderIconLoader.Load(typeof(MongoDbProvider), "🍃");

    public ISqlDialect Dialect { get; } = new MongoDbDialect();

    // Not a SQL engine: the host suppresses its SQL-scaffold "SQL commands" menu and asks this provider
    // for node-action query text (BuildNodeQuery) and DROP/TRUNCATE statements (BuildAlterStatement).
    public bool IsSqlBased => false;

    public IReadOnlyList<ConnectionField> ConnectionFields { get; } =
    [
        new("host", "Host", ConnectionFieldType.Text, Required: true, Default: "localhost"),
        new("port", "Port", ConnectionFieldType.Number, Default: "27017"),
        new("username", "Username", ConnectionFieldType.Text),
        new("password", "Password", ConnectionFieldType.Password),
        new("authSource", "Auth database", ConnectionFieldType.Text, Default: "admin", Advanced: true),
        // Escape hatch for replica sets / SRV / Atlas: a full mongodb:// or mongodb+srv:// URI that,
        // when set, overrides every field above.
        new("uri", "Connection URI (overrides the above)", ConnectionFieldType.Text, Advanced: true,
            Placeholder: "mongodb+srv://user:pass@cluster.example.mongodb.net/")
    ];

    public string BuildConnectionString(IReadOnlyDictionary<string, string?> values)
    {
        var uri = Value(values, "uri");
        if (!string.IsNullOrWhiteSpace(uri))
        {
            return uri;
        }

        var builder = new MongoUrlBuilder
        {
            Server = new MongoServerAddress(
                Value(values, "host") ?? "localhost",
                int.TryParse(Value(values, "port"), out var port) ? port : 27017)
        };

        var username = Value(values, "username");
        if (!string.IsNullOrWhiteSpace(username))
        {
            builder.Username = username;
            builder.Password = Value(values, "password") ?? string.Empty;
            builder.AuthenticationSource = Value(values, "authSource") ?? "admin";
        }

        return builder.ToString();
    }

    // Inverse of BuildConnectionString: unpack a pasted URI back into the individual fields so the
    // connection dialog can prefill itself.
    public IReadOnlyDictionary<string, string?>? ParseConnectionString(string connectionString)
    {
        MongoUrl url;
        try
        {
            url = MongoUrl.Create(connectionString);
        }
        catch
        {
            return null;
        }

        var result = new Dictionary<string, string?>();
        var server = url.Servers.FirstOrDefault();
        if (server is not null)
        {
            result["host"] = server.Host;
            result["port"] = server.Port.ToString();
        }

        if (!string.IsNullOrEmpty(url.Username)) result["username"] = url.Username;
        if (!string.IsNullOrEmpty(url.AuthenticationSource)) result["authSource"] = url.AuthenticationSource;
        return result;
    }

    public async Task<bool> TestConnectionAsync(ConnectionProfile profile, CancellationToken ct)
    {
        var client = CreateClient(profile);
        var command = (Command<BsonDocument>)new BsonDocument("ping", 1);
        var result = await client.GetDatabase("admin").RunCommandAsync(command, cancellationToken: ct);
        return result.GetValue("ok", 0).ToDouble() == 1.0;
    }

    // --- Schema tree: root → databases → collections ------------------------------------------------
    public async Task<IReadOnlyList<DbTreeNode>> GetChildNodesAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct)
    {
        var parent = ancestors.Count == 0 ? (DbNodeKind?)null : ancestors[^1].Kind;
        return parent switch
        {
            null => await LoadDatabasesAsync(profile, ct),
            DbNodeKind.Database => await LoadCollectionsAsync(profile, ancestors[^1].Name, ct),
            _ => []
        };
    }

    private async Task<IReadOnlyList<DbTreeNode>> LoadDatabasesAsync(ConnectionProfile profile, CancellationToken ct)
    {
        var client = CreateClient(profile);
        using var cursor = await client.ListDatabaseNamesAsync(ct);
        var names = await cursor.ToListAsync(ct);
        names.Sort(StringComparer.Ordinal);

        return names.Select(name => new DbTreeNode
        {
            Kind = DbNodeKind.Database,
            Name = name,
            HasChildren = true,
            IsSystem = name is "admin" or "local" or "config"
        }).ToList();
    }

    private async Task<IReadOnlyList<DbTreeNode>> LoadCollectionsAsync(
        ConnectionProfile profile,
        string databaseName,
        CancellationToken ct)
    {
        var database = CreateClient(profile).GetDatabase(databaseName);
        using var cursor = await database.ListCollectionNamesAsync(cancellationToken: ct);
        var names = await cursor.ToListAsync(ct);
        names.Sort(StringComparer.Ordinal);

        var nodes = new List<DbTreeNode>(names.Count);
        foreach (var name in names)
        {
            // EstimatedDocumentCount is a fast metadata read (no collection scan) — a nice inline badge.
            long? count = null;
            try
            {
                count = await database.GetCollection<BsonDocument>(name).EstimatedDocumentCountAsync(cancellationToken: ct);
            }
            catch
            {
                // Views and some special collections don't support the estimate; just omit the badge.
            }

            // A collection is a Table node so the host offers "browse data" (double-click) on it. Mongo is
            // schemaless, so there are no child column nodes to expand.
            nodes.Add(new DbTreeNode
            {
                Kind = DbNodeKind.Table,
                Name = name,
                HasChildren = false,
                Detail = count is { } c ? $"~{c:N0} docs" : null
            });
        }

        return nodes;
    }

    // Databases for the query-tab database switcher (also what sets ConnectionProfile.Database).
    public async Task<IReadOnlyList<string>> GetDatabasesAsync(ConnectionProfile profile, CancellationToken ct)
    {
        var client = CreateClient(profile);
        using var cursor = await client.ListDatabaseNamesAsync(ct);
        return await cursor.ToListAsync(ct);
    }

    // --- Query execution ----------------------------------------------------------------------------
    public async Task<QueryResult> ExecuteQueryAsync(ConnectionProfile profile, string sql, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var query = MongoQuery.Parse(sql);
        var database = ResolveDatabase(profile);
        var collection = database.GetCollection<BsonDocument>(query.Collection);

        List<BsonDocument> documents;
        if (query.Kind == MongoQueryKind.Aggregate)
        {
            PipelineDefinition<BsonDocument, BsonDocument> pipeline =
                query.Pipeline.Select(stage => stage.AsBsonDocument).ToArray();
            using var cursor = await collection.AggregateAsync(pipeline, cancellationToken: ct);
            documents = await cursor.ToListAsync(ct);
        }
        else
        {
            var find = collection.Find(query.Filter);
            if (query.Projection is not null) find = find.Project<BsonDocument>(query.Projection);
            if (query.Sort is not null) find = find.Sort(query.Sort);
            if (query.Skip is { } skip) find = find.Skip(skip);
            find = find.Limit(query.Limit ?? DefaultLimit);
            documents = await find.ToListAsync(ct);
        }

        return Flatten(documents, query.Collection, stopwatch.Elapsed);
    }

    // Mongo has no multi-statement scripts; a "script" is just one query.
    public async Task<IReadOnlyList<QueryResult>> ExecuteScriptAsync(ConnectionProfile profile, string sql, CancellationToken ct) =>
        [await ExecuteQueryAsync(profile, sql, ct)];

    public async Task<QueryResult> ExplainAsync(ConnectionProfile profile, string sql, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var query = MongoQuery.Parse(sql);
        var database = ResolveDatabase(profile);

        var inner = query.Kind == MongoQueryKind.Aggregate
            ? new BsonDocument { { "aggregate", query.Collection }, { "pipeline", query.Pipeline }, { "cursor", new BsonDocument() } }
            : BuildFindCommand(query);

        var explain = new BsonDocument { { "explain", inner }, { "verbosity", "queryPlanner" } };
        var result = await database.RunCommandAsync((Command<BsonDocument>)explain, cancellationToken: ct);

        // The plan is one (deeply nested) document — surface it as a single JSON cell rather than
        // trying to flatten an engine-defined, non-tabular shape.
        return new QueryResult
        {
            Columns = [new ResultColumn("queryPlanner", typeof(string))],
            Rows = [[ToJson(result)]],
            Elapsed = stopwatch.Elapsed
        };
    }

    private static BsonDocument BuildFindCommand(MongoQuery query)
    {
        var command = new BsonDocument { { "find", query.Collection }, { "filter", query.Filter } };
        if (query.Projection is not null) command["projection"] = query.Projection;
        if (query.Sort is not null) command["sort"] = query.Sort;
        if (query.Skip is { } skip) command["skip"] = skip;
        command["limit"] = query.Limit ?? DefaultLimit;
        return command;
    }

    // --- Provider-owned statement generation (non-SQL seam) -----------------------------------------
    // The tree's "Select top 1000" and (were it shown) SQL-commands actions: return mongo-shell text the
    // query tab understands. The database is bound by the tab (ConnectionProfile.Database), so only the
    // collection name — the last node on the path — is needed here.
    public string? BuildNodeQuery(
        NodeQueryKind kind,
        IReadOnlyList<DbNodeRef> nodePath,
        IReadOnlyList<ResultColumn>? columns,
        ConnectionProfile profile)
    {
        if (nodePath.Count == 0)
        {
            return null;
        }

        var collection = nodePath[^1].Name;
        return kind switch
        {
            NodeQueryKind.SelectAll => $"db.{collection}.find({{}})",
            NodeQueryKind.SelectTop => $"db.{collection}.find({{}}).limit(1000)",
            NodeQueryKind.Count => $"db.{collection}.aggregate([ {{ \"$count\": \"count\" }} ])",
            // Column-shaped scaffolds (SELECT columns / INSERT / UPDATE / DELETE) don't map to a schemaless
            // document store — the host hides that submenu for a non-SQL provider anyway.
            _ => null
        };
    }

    // "Drop" and "Truncate" on a collection: the host previews this text and runs it via ExecuteDdlAsync.
    public SqlStatement? BuildAlterStatement(AlterSpec spec) => spec.Action switch
    {
        AlterAction.DropTable => new SqlStatement($"db.{spec.Target}.drop()", []),
        AlterAction.TruncateTable => new SqlStatement($"db.{spec.Target}.deleteMany({{}})", []),
        _ => null
    };

    // --- Capabilities Mongo does not model ----------------------------------------------------------
    public IReadOnlyList<CreateCapability> CreateCapabilities { get; } = [];

    public IReadOnlyList<string> ColumnTypes { get; } = [];

    private static NotSupportedException NotSupported() =>
        new("The MongoDB provider does not support SQL DDL. Use db.<collection> operations instead.");

    public SqlStatement BuildCreateStatement(CreateObjectSpec spec) => throw NotSupported();

    // Runs the small admin commands BuildAlterStatement emits (and that the user may edit in the preview):
    // db.<collection>.drop() and db.<collection>.deleteMany({ filter }).
    private static readonly Regex DropCommand =
        new(@"^db\.(?<c>.+?)\.drop\(\s*\)\s*;?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DeleteManyCommand =
        new(@"^db\.(?<c>.+?)\.deleteMany\(\s*(?<f>[\s\S]*?)\s*\)\s*;?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task ExecuteDdlAsync(ConnectionProfile profile, string sql, CancellationToken ct)
    {
        var text = sql.Trim();
        var database = ResolveDatabase(profile);

        var drop = DropCommand.Match(text);
        if (drop.Success)
        {
            await database.DropCollectionAsync(UnquoteName(drop.Groups["c"].Value), ct);
            return;
        }

        var delete = DeleteManyCommand.Match(text);
        if (delete.Success)
        {
            var collection = database.GetCollection<BsonDocument>(UnquoteName(delete.Groups["c"].Value));
            var raw = delete.Groups["f"].Value.Trim();
            var filter = raw.Length == 0 ? new BsonDocument() : BsonDocument.Parse(raw);
            await collection.DeleteManyAsync(filter, ct);
            return;
        }

        throw new NotSupportedException(
            "Unsupported MongoDB command. Expected db.<collection>.drop() or db.<collection>.deleteMany({ ... }).");
    }

    private static string UnquoteName(string s)
    {
        s = s.Trim();
        return s.Length >= 2 && s[0] == s[^1] && s[0] is '"' or '\'' or '`' ? s[1..^1] : s;
    }

    public Task<int> ExecuteBatchAsync(ConnectionProfile profile, IReadOnlyList<SqlStatement> statements, CancellationToken ct) =>
        throw NotSupported();

    // --- Editable-grid save-flow (SE-114 non-SQL writeback) ------------------------------------------
    // Flatten() marks _id IsKey/BaseTable=<collection>, so EditableResultSet/ChangeSetBuilder treat a
    // Mongo grid exactly like a SQL one — the host never generates SQL, it hands us structured changes
    // keyed by _id and we turn them into native driver calls inside one client-side session (best-effort
    // transaction: a standalone/no-replica-set deployment silently runs without one).
    public async Task<WritebackResult> ApplyChangesAsync(ConnectionProfile profile, ChangeSet changes, CancellationToken ct)
    {
        var collection = ResolveDatabase(profile).GetCollection<BsonDocument>(changes.Table);
        var affected = 0;

        foreach (var row in changes.Rows)
        {
            switch (row.Kind)
            {
                case RowChangeKind.Added:
                    var doc = new BsonDocument();
                    foreach (var cell in row.Cells)
                    {
                        doc[cell.Column] = BsonValue.Create(cell.Value);
                    }

                    await collection.InsertOneAsync(doc, cancellationToken: ct);
                    affected++;
                    break;

                case RowChangeKind.Modified:
                    var update = Builders<BsonDocument>.Update.Combine(
                        row.Cells.Select(c => Builders<BsonDocument>.Update.Set(c.Column, BsonValue.Create(c.Value))));
                    var updateResult = await collection.UpdateOneAsync(IdentityFilter(row.Identity), update, cancellationToken: ct);
                    affected += (int)updateResult.ModifiedCount;
                    break;

                case RowChangeKind.Deleted:
                    var deleteResult = await collection.DeleteOneAsync(IdentityFilter(row.Identity), ct);
                    affected += (int)deleteResult.DeletedCount;
                    break;
            }
        }

        // One collection, sequential ops, no multi-document transaction (requires a replica set Mongo
        // doesn't guarantee here) — a failure partway through leaves earlier ops committed.
        return new WritebackResult(affected, IsAtomic: false, RowErrors: []);
    }

    private static FilterDefinition<BsonDocument> IdentityFilter(IReadOnlyDictionary<string, object?> identity)
    {
        var filters = identity.Select(kv => Builders<BsonDocument>.Filter.Eq(kv.Key, IdentityValue(kv.Key, kv.Value)));
        return Builders<BsonDocument>.Filter.And(filters);
    }

    // Render() stringifies _id (BsonType.ObjectId => .ToString()) so the grid can display/edit it as
    // text; convert it back to a real ObjectId here, or an ObjectId-typed filter would never match an
    // ObjectId-typed _id field.
    private static BsonValue IdentityValue(string column, object? value) =>
        column == "_id" && value is string s && ObjectId.TryParse(s, out var id)
            ? id
            : BsonValue.Create(value);

    // --- Helpers ------------------------------------------------------------------------------------
    private static IMongoClient CreateClient(ConnectionProfile profile) => new MongoClient(profile.ConnectionString);

    // A query runs against the database chosen in the switcher (ConnectionProfile.Database), falling back
    // to the one embedded in the connection URI. Mongo has no implicit default, so require one.
    private static IMongoDatabase ResolveDatabase(ConnectionProfile profile)
    {
        var name = profile.Database;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = MongoUrl.Create(profile.ConnectionString).DatabaseName;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException(
                "No database selected. Pick a database in the toolbar, or include one in the connection URI.");
        }

        return CreateClient(profile).GetDatabase(name);
    }

    private static string? Value(IReadOnlyDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    // Flatten a batch of BSON documents into a rectangular grid: the column set is the union of every
    // document's top-level fields (order of first appearance, _id first). Nested documents/arrays render
    // as relaxed extended JSON so a single cell can still show them. Every column is tagged BaseTable =
    // <collection> and _id is IsKey/IsReadOnly, so EditableResultSet/ChangeSetBuilder recognise this grid
    // as editable via the exact same Base*/IsKey mechanism SQL providers use (SE-114) — no SQL involved.
    private static QueryResult Flatten(IReadOnlyList<BsonDocument> documents, string collection, TimeSpan elapsed)
    {
        var order = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var doc in documents)
        {
            foreach (var element in doc.Elements)
            {
                if (seen.Add(element.Name))
                {
                    order.Add(element.Name);
                }
            }
        }

        // Keep _id first when present — that's the conventional, expected primary column.
        if (order.Remove("_id"))
        {
            order.Insert(0, "_id");
        }

        var clrTypes = new Type?[order.Count];
        var rows = new List<object?[]>(documents.Count);
        foreach (var doc in documents)
        {
            var row = new object?[order.Count];
            for (var i = 0; i < order.Count; i++)
            {
                if (doc.TryGetValue(order[i], out var value))
                {
                    var rendered = Render(value);
                    row[i] = rendered;
                    clrTypes[i] ??= rendered?.GetType();
                }
            }

            rows.Add(row);
        }

        var columns = order
            .Select((name, i) => new ResultColumn(name, clrTypes[i] ?? typeof(string))
            {
                BaseTable = collection,
                BaseColumn = name,
                IsKey = name == "_id",
                IsReadOnly = name == "_id"
            })
            .ToList();

        return new QueryResult { Columns = columns, Rows = rows, Elapsed = elapsed };
    }

    // Render a BSON value as a CLR value the grid can display. Scalars map to their natural CLR type;
    // documents and arrays become JSON text.
    private static object? Render(BsonValue value) => value.BsonType switch
    {
        BsonType.Null or BsonType.Undefined => null,
        BsonType.String => value.AsString,
        BsonType.Int32 => value.AsInt32,
        BsonType.Int64 => value.AsInt64,
        BsonType.Double => value.AsDouble,
        BsonType.Decimal128 => value.AsDecimal,
        BsonType.Boolean => value.AsBoolean,
        BsonType.DateTime => value.ToUniversalTime(),
        BsonType.ObjectId => value.AsObjectId.ToString(),
        BsonType.Document => ToJson(value.AsBsonDocument),
        BsonType.Array => ToJson(value.AsBsonArray),
        _ => value.ToString()
    };

    private static readonly JsonWriterSettings RelaxedJson = new() { OutputMode = JsonOutputMode.RelaxedExtendedJson };

    private static string ToJson(BsonDocument document) => document.ToJson(RelaxedJson);

    // BsonArray/BsonValue has no ToJson(settings) overload of its own; wrap and lift back out.
    private static string ToJson(BsonValue value)
    {
        var json = new BsonDocument("_", value).ToJson(RelaxedJson);
        // Strip the {"_": … } wrapper we added: content between the first ':' and the last '}'.
        var start = json.IndexOf(':') + 1;
        var end = json.LastIndexOf('}');
        return json[start..end].Trim();
    }
}
