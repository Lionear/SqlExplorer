using System.Data.Common;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using SqlExplorer.Sdk;
using SqlExplorer.Sdk.Editing;
using EsHttpMethod = Elastic.Transport.HttpMethod;

namespace SqlExplorer.Providers.Elasticsearch;

/// <summary>
/// An Elasticsearch provider. Elasticsearch is a document search engine, not a SQL engine, so this maps
/// its world onto the host's (SQL-shaped) <see cref="IDbProvider"/> contract honestly, following the
/// MongoDB provider's non-SQL pattern (SE-3 / SE-114):
/// <list type="bullet">
/// <item>Schema tree: connection root → indices (a leaf per index; system <c>.</c>-prefixed indices are
///   flagged). No database layer — an index is globally addressable in the REST path.</item>
/// <item>Queries: a Kibana Dev-Tools-style console (<c>GET myindex/_search { ... }</c>), parsed by
///   <see cref="ElasticQuery"/> and run through the low-level transport; <c>hits.hits._source</c> is
///   flattened to a hybrid grid (scalar fields as columns, nested objects/arrays as JSON cells).</item>
/// <item>Editable grid: <c>_id</c> is tagged <c>IsKey</c>/<c>IsReadOnly</c>/<c>BaseTable=&lt;index&gt;</c>
///   so the host's editable-grid flow lights up; writeback goes through <see cref="ApplyChangesAsync"/>
///   as a single <c>_bulk</c> request. Unlike a SQL/Mongo save this is <b>not transactional</b>: Elastic's
///   bulk API is best-effort per item, so partial failures come back in <see cref="WritebackResult.RowErrors"/>
///   with <see cref="WritebackResult.IsAtomic"/> = false.</item>
/// <item>DDL / routines / user management: not modelled (the relevant capability flags stay off).</item>
/// </list>
/// It ships from the repo-root <c>plugins/</c> folder (not <c>src/</c>) and is staged only in Debug builds,
/// so it is directly usable while developing but never part of a Release/MVP.
/// </summary>
public sealed class ElasticsearchProvider : IDbProvider
{
    private const int DefaultSize = 1000;

    public string DisplayName => "Elasticsearch";

    public ProviderIcon? Icon { get; } = ProviderIconLoader.Load(typeof(ElasticsearchProvider), "🔍");

    public ISqlDialect Dialect { get; } = new ElasticsearchDialect();

    public bool IsSqlBased => false;

    public IReadOnlyList<ConnectionField> ConnectionFields { get; } =
    [
        new("url", "URL", ConnectionFieldType.Text, Required: true, Default: "https://localhost:9200"),
        new("username", "Username", ConnectionFieldType.Text),
        new("password", "Password", ConnectionFieldType.Password),
        // Alternative to user/pass: a base64-encoded API key (the value used in an "Authorization: ApiKey"
        // header). When set, it takes precedence over username/password.
        new("apiKey", "API key", ConnectionFieldType.Password, Advanced: true),
        new("verifyTls", "Verify TLS certificate", ConnectionFieldType.Bool, Default: "true", Advanced: true)
    ];

    // Connection details are carried as an ADO-style key=value string (robust quoting/escaping via
    // DbConnectionStringBuilder), unpacked again in CreateClient — Elastic has no single canonical
    // "connection string" the way Mongo's mongodb:// URI is.
    public string BuildConnectionString(IReadOnlyDictionary<string, string?> values)
    {
        var builder = new DbConnectionStringBuilder { ["Url"] = Value(values, "url") ?? "https://localhost:9200" };
        if (Value(values, "username") is { } u) builder["Username"] = u;
        if (Value(values, "password") is { } p) builder["Password"] = p;
        if (Value(values, "apiKey") is { } k) builder["ApiKey"] = k;
        builder["VerifyTls"] = Value(values, "verifyTls") is "false" or "False" ? "false" : "true";
        return builder.ConnectionString;
    }

    public IReadOnlyDictionary<string, string?>? ParseConnectionString(string connectionString)
    {
        DbConnectionStringBuilder builder;
        try
        {
            builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        }
        catch
        {
            return null;
        }

        var result = new Dictionary<string, string?>();
        if (builder.TryGetValue("Url", out var url)) result["url"] = url?.ToString();
        if (builder.TryGetValue("Username", out var user)) result["username"] = user?.ToString();
        if (builder.TryGetValue("ApiKey", out _)) result["apiKey"] = "";       // present but never echoed back
        if (builder.TryGetValue("VerifyTls", out var tls)) result["verifyTls"] = tls?.ToString();
        return result;
    }

    public async Task<bool> TestConnectionAsync(ConnectionProfile profile, CancellationToken ct)
    {
        await SendAsync(profile, EsHttpMethod.GET, "/", null, ct);
        return true;
    }

    // The Elasticsearch root endpoint (GET /) returns version.number, e.g. "8.13.4".
    public async Task<string?> GetServerVersionAsync(ConnectionProfile profile, CancellationToken ct)
    {
        var body = await SendAsync(profile, EsHttpMethod.GET, "/", null, ct);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("version", out var version)
            && version.TryGetProperty("number", out var number)
            ? number.GetString()
            : null;
    }

    // --- Schema tree: root → indices ----------------------------------------------------------------
    public async Task<IReadOnlyList<DbTreeNode>> GetChildNodesAsync(
        ConnectionProfile profile,
        IReadOnlyList<DbNodeRef> ancestors,
        CancellationToken ct)
    {
        // Indices are leaves (no child column nodes — an index is schemaless-ish and browsed directly).
        if (ancestors.Count > 0)
        {
            return [];
        }

        var body = await SendAsync(profile, EsHttpMethod.GET,
            "_cat/indices?format=json&h=index,docs.count,store.size", null, ct);

        using var doc = JsonDocument.Parse(body);
        var nodes = new List<DbTreeNode>();
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            var name = entry.TryGetProperty("index", out var idx) ? idx.GetString() : null;
            if (name is null) continue;

            var docs = entry.TryGetProperty("docs.count", out var dc) ? dc.GetString() : null;
            var size = entry.TryGetProperty("store.size", out var ss) ? ss.GetString() : null;
            var detail = docs is null ? size : size is null ? $"{docs} docs" : $"{docs} docs · {size}";

            nodes.Add(new DbTreeNode
            {
                Kind = DbNodeKind.Table,
                Name = name,
                HasChildren = false,
                IsSystem = name.StartsWith('.'),
                Detail = detail
            });
        }

        nodes.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return nodes;
    }

    // Elasticsearch has no database layer — the index lives in the REST path, so the toolbar's database
    // switcher stays empty.
    public Task<IReadOnlyList<string>> GetDatabasesAsync(ConnectionProfile profile, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    // --- Query execution ----------------------------------------------------------------------------
    public async Task<QueryResult> ExecuteQueryAsync(ConnectionProfile profile, string sql, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var query = ElasticQuery.Parse(sql);

        string body;
        if (query.Kind == ElasticQueryKind.Browse)
        {
            var search = new JsonObject
            {
                ["query"] = query.Query?.DeepClone() ?? new JsonObject { ["match_all"] = new JsonObject() },
                ["size"] = query.Size ?? DefaultSize,
                ["from"] = query.From ?? 0
            };
            if (query.Sort is { } sort) search["sort"] = sort.DeepClone();

            body = await SendAsync(profile, EsHttpMethod.POST, $"{Encode(query.Index)}/_search", search.ToJsonString(), ct);
        }
        else
        {
            body = await SendAsync(profile, Method(query.Method), query.Path,
                query.Body.Length == 0 ? null : query.Body, ct);
        }

        return Project(body, stopwatch.Elapsed);
    }

    // Elasticsearch Dev-Tools runs one request at a time; a "script" is just one request here.
    public async Task<IReadOnlyList<QueryResult>> ExecuteScriptAsync(ConnectionProfile profile, string sql, CancellationToken ct) =>
        [await ExecuteQueryAsync(profile, sql, ct)];

    // No SQL query planner; the closest equivalent is _validate/query?explain, which reports how the query
    // is interpreted. Only meaningful for a browse/search — a raw console request is just run as-is.
    public async Task<QueryResult> ExplainAsync(ConnectionProfile profile, string sql, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var query = ElasticQuery.Parse(sql);
        if (query.Kind == ElasticQueryKind.Console)
        {
            return await ExecuteQueryAsync(profile, sql, ct);
        }

        var validate = new JsonObject
        {
            ["query"] = query.Query?.DeepClone() ?? new JsonObject { ["match_all"] = new JsonObject() }
        };
        var body = await SendAsync(profile, EsHttpMethod.POST,
            $"{Encode(query.Index)}/_validate/query?explain=true", validate.ToJsonString(), ct);

        return new QueryResult
        {
            Columns = [new ResultColumn("explanation", typeof(string))],
            Rows = [[Prettify(body)]],
            Elapsed = stopwatch.Elapsed
        };
    }

    // --- Cursor paging (search_after + point-in-time) — beyond the 10k from+size window ---------------
    // Elasticsearch caps from+size at index.max_result_window (10 000 by default), so offset paging can't
    // reach a large index's tail. The host drives this provider by cursor instead (SupportsCursorPaging):
    // each page opens/reuses a point-in-time (PIT) snapshot and continues with search_after from the last
    // hit's sort values. A stable tiebreaker (_shard_doc, valid only within a PIT) makes the ordering total
    // so pages neither overlap nor skip. The cursor token is a base64 { pit, after } blob — fully stateless
    // on the host side; the PIT lives server-side and auto-expires via keep_alive.
    private const string PitKeepAlive = "2m";
    private const int StreamPageSize = 1000;

    public bool SupportsCursorPaging => true;

    public async Task<QueryResult> ExecuteCursorPageAsync(
        ConnectionProfile profile, string sql, int pageSize, string? cursor, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var query = ElasticQuery.Parse(sql);
        if (query.Kind != ElasticQueryKind.Browse)
        {
            // The host only cursor-pages a browse; anything else just runs normally (no cursor).
            return await ExecuteQueryAsync(profile, sql, ct);
        }

        string pitId;
        JsonArray? after = null;
        if (cursor is null)
        {
            pitId = await OpenPitAsync(profile, query.Index, ct);
        }
        else
        {
            (pitId, after) = DecodeCursor(cursor);
        }

        var body = new JsonObject
        {
            ["query"] = query.Query?.DeepClone() ?? new JsonObject { ["match_all"] = new JsonObject() },
            ["size"] = pageSize,
            ["pit"] = new JsonObject { ["id"] = pitId, ["keep_alive"] = PitKeepAlive },
            ["track_total_hits"] = false,
            ["sort"] = BuildSort(query.Sort)
        };
        if (after is not null) body["search_after"] = after;

        // A PIT search addresses the snapshot, not an index, so there is no index in the path.
        var responseBody = await SendAsync(profile, EsHttpMethod.POST, "_search", body.ToJsonString(), ct);
        return ProjectCursorPage(responseBody, pageSize, stopwatch.Elapsed);
    }

    // Stream every matching document (no 10k ceiling) for export/backup: same PIT + search_after walk, but
    // pushed row-by-row to the visitor so a huge index never materialises. The schema is deliberately stable
    // — _id plus the full _source as JSON — because a heterogeneous index has no fixed column set to promise
    // a streaming consumer up front (the grid's per-page flatten is a different, page-local concern).
    public async Task StreamQueryAsync(ConnectionProfile profile, string sql, IQueryRowVisitor visitor, CancellationToken ct)
    {
        var query = ElasticQuery.Parse(sql);
        if (query.Kind != ElasticQueryKind.Browse)
        {
            // Console requests don't paginate — replay the materialised result, like the SDK default.
            var single = await ExecuteQueryAsync(profile, sql, ct);
            await visitor.OnColumnsAsync(single.Columns, ct);
            foreach (var row in single.Rows)
            {
                ct.ThrowIfCancellationRequested();
                await visitor.OnRowAsync(new MaterializedStreamedRow(row), ct);
            }

            return;
        }

        await visitor.OnColumnsAsync(
            [new ResultColumn("_id", typeof(string)), new ResultColumn("_source", typeof(string))], ct);

        var pitId = await OpenPitAsync(profile, query.Index, ct);
        try
        {
            JsonArray? after = null;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var body = new JsonObject
                {
                    ["query"] = query.Query?.DeepClone() ?? new JsonObject { ["match_all"] = new JsonObject() },
                    ["size"] = StreamPageSize,
                    ["pit"] = new JsonObject { ["id"] = pitId, ["keep_alive"] = PitKeepAlive },
                    ["track_total_hits"] = false,
                    ["sort"] = BuildSort(query.Sort)
                };
                if (after is not null) body["search_after"] = after;

                var responseBody = await SendAsync(profile, EsHttpMethod.POST, "_search", body.ToJsonString(), ct);

                using var doc = JsonDocument.Parse(responseBody);
                if (doc.RootElement.TryGetProperty("pit_id", out var pit) && pit.GetString() is { } rotated)
                {
                    pitId = rotated;
                }

                var hits = doc.RootElement.GetProperty("hits").GetProperty("hits");
                var count = hits.GetArrayLength();
                if (count == 0) break;

                foreach (var hit in hits.EnumerateArray())
                {
                    var id = hit.TryGetProperty("_id", out var idEl) ? idEl.GetString() : null;
                    var src = hit.TryGetProperty("_source", out var s) ? s.GetRawText() : "{}";
                    await visitor.OnRowAsync(new MaterializedStreamedRow([id, src]), ct);
                }

                if (count < StreamPageSize) break;
                after = (JsonArray)JsonNode.Parse(hits[count - 1].GetProperty("sort").GetRawText())!;
            }
        }
        finally
        {
            await ClosePitAsync(profile, pitId, ct);
        }
    }

    private async Task<string> OpenPitAsync(ConnectionProfile profile, string index, CancellationToken ct)
    {
        var body = await SendAsync(profile, EsHttpMethod.POST, $"{Encode(index)}/_pit?keep_alive={PitKeepAlive}", null, ct);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Elasticsearch did not return a point-in-time id.");
    }

    private async Task ClosePitAsync(ConnectionProfile profile, string pitId, CancellationToken ct)
    {
        try
        {
            await SendAsync(profile, EsHttpMethod.DELETE, "_pit", new JsonObject { ["id"] = pitId }.ToJsonString(), ct);
        }
        catch
        {
            // Best-effort: a PIT auto-expires via keep_alive, so a failed close is not worth surfacing.
        }
    }

    // sort = the user's ORDER BY (if any) + a _shard_doc tiebreaker for a total order under the PIT.
    private static JsonArray BuildSort(JsonArray? userSort)
    {
        var sort = userSort is null ? [] : (JsonArray)userSort.DeepClone();
        sort.Add(new JsonObject { ["_shard_doc"] = "asc" });
        return sort;
    }

    private static QueryResult ProjectCursorPage(string body, int pageSize, TimeSpan elapsed)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var pitId = root.TryGetProperty("pit_id", out var p) ? p.GetString() : null;
        var hits = root.GetProperty("hits").GetProperty("hits");
        var page = FlattenHits(hits, elapsed);

        // A full page implies there may be more; carry the last hit's sort values as the next cursor. A
        // short page is the end of the scan — no cursor (the PIT will expire on its own).
        string? nextCursor = null;
        var count = hits.GetArrayLength();
        if (count == pageSize && pitId is not null && hits[count - 1].TryGetProperty("sort", out var sortEl))
        {
            var token = new JsonObject { ["pit"] = pitId, ["after"] = JsonNode.Parse(sortEl.GetRawText()) };
            nextCursor = Convert.ToBase64String(Encoding.UTF8.GetBytes(token.ToJsonString()));
        }

        return new QueryResult { Columns = page.Columns, Rows = page.Rows, Elapsed = elapsed, NextCursor = nextCursor };
    }

    private static (string Pit, JsonArray After) DecodeCursor(string cursor)
    {
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
        var node = JsonNode.Parse(json)!.AsObject();
        var pit = node["pit"]!.GetValue<string>();
        var after = (JsonArray)node["after"]!.DeepClone();
        return (pit, after);
    }

    // --- Provider-owned statement generation (non-SQL seam) -----------------------------------------
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

        var index = nodePath[^1].Name;
        return kind switch
        {
            NodeQueryKind.SelectAll => $"GET {index}/_search\n{{ \"query\": {{ \"match_all\": {{}} }} }}",
            NodeQueryKind.SelectTop => $"GET {index}/_search\n{{ \"query\": {{ \"match_all\": {{}} }}, \"size\": 1000 }}",
            NodeQueryKind.Count => $"GET {index}/_count\n{{ \"query\": {{ \"match_all\": {{}} }} }}",
            // Column-shaped scaffolds (SELECT columns / INSERT / UPDATE / DELETE) don't map to a document
            // index — the host hides that submenu for a non-SQL provider anyway.
            _ => null
        };
    }

    // "Drop" and "Truncate" on an index: the host previews this text and runs it via ExecuteDdlAsync.
    // Drop deletes the index outright; Truncate keeps the (mapped) index but removes every document.
    public SqlStatement? BuildAlterStatement(AlterSpec spec) => spec.Action switch
    {
        AlterAction.DropTable => new SqlStatement($"DELETE /{spec.Target}", []),
        AlterAction.TruncateTable =>
            new SqlStatement($"POST /{spec.Target}/_delete_by_query?refresh=true\n{{ \"query\": {{ \"match_all\": {{}} }} }}", []),
        _ => null
    };

    // --- Capabilities Elasticsearch does not model --------------------------------------------------
    public IReadOnlyList<CreateCapability> CreateCapabilities { get; } = [];

    public IReadOnlyList<string> ColumnTypes { get; } = [];

    private static NotSupportedException NotSupported() =>
        new("The Elasticsearch provider does not support SQL DDL. Use Dev-Tools-style requests instead.");

    public SqlStatement BuildCreateStatement(CreateObjectSpec spec) => throw NotSupported();

    // Runs the raw request BuildAlterStatement emits (and that the user may edit in the preview): a
    // DELETE /<index> or a _delete_by_query. Any console request works here too.
    public async Task ExecuteDdlAsync(ConnectionProfile profile, string sql, CancellationToken ct)
    {
        var query = ElasticQuery.Parse(sql);
        if (query.Kind != ElasticQueryKind.Console)
        {
            throw new NotSupportedException("Expected a Dev-Tools request, e.g. DELETE /myindex.");
        }

        await SendAsync(profile, Method(query.Method), query.Path, query.Body.Length == 0 ? null : query.Body, ct);
    }

    public Task<int> ExecuteBatchAsync(ConnectionProfile profile, IReadOnlyList<SqlStatement> statements, CancellationToken ct) =>
        throw NotSupported();

    // --- Editable-grid save-flow (SE-114 non-SQL writeback) — one _bulk request ----------------------
    // Flatten() tags _id IsKey/IsReadOnly and every column BaseTable=<index>, so EditableResultSet/
    // ChangeSetBuilder treat an Elastic grid exactly like a SQL one. Unlike Mongo/SQL this is deliberately
    // NOT atomic: _bulk applies each action independently, so a partial failure leaves the succeeding
    // items committed. We surface per-item failures in RowErrors and set IsAtomic=false so the host never
    // implies a rollback happened. refresh=wait_for makes the subsequent grid reload observe the change
    // (Elasticsearch is near-real-time; without it the reload would miss a just-saved edit).
    public async Task<WritebackResult> ApplyChangesAsync(ConnectionProfile profile, ChangeSet changes, CancellationToken ct)
    {
        var index = changes.Table;
        var ndjson = new StringBuilder();
        var rowIds = new List<string>();       // a human label per bulk action, for error reporting

        foreach (var row in changes.Rows)
        {
            switch (row.Kind)
            {
                case RowChangeKind.Added:
                    var source = new JsonObject();
                    foreach (var cell in row.Cells)
                    {
                        source[cell.Column] = CellToJson(cell.Value);
                    }

                    ndjson.Append(new JsonObject { ["index"] = new JsonObject { ["_index"] = index } }.ToJsonString()).Append('\n');
                    ndjson.Append(source.ToJsonString()).Append('\n');
                    rowIds.Add("(new)");
                    break;

                case RowChangeKind.Modified:
                    var id = IdOf(row.Identity);
                    var doc = new JsonObject();
                    foreach (var cell in row.Cells)
                    {
                        doc[cell.Column] = CellToJson(cell.Value);
                    }

                    ndjson.Append(new JsonObject { ["update"] = new JsonObject { ["_index"] = index, ["_id"] = id } }.ToJsonString()).Append('\n');
                    ndjson.Append(new JsonObject { ["doc"] = doc }.ToJsonString()).Append('\n');
                    rowIds.Add(id);
                    break;

                case RowChangeKind.Deleted:
                    var delId = IdOf(row.Identity);
                    ndjson.Append(new JsonObject { ["delete"] = new JsonObject { ["_index"] = index, ["_id"] = delId } }.ToJsonString()).Append('\n');
                    rowIds.Add(delId);
                    break;
            }
        }

        if (rowIds.Count == 0)
        {
            return new WritebackResult(0, IsAtomic: false, []);
        }

        var body = await SendAsync(profile, EsHttpMethod.POST, "_bulk?refresh=wait_for", ndjson.ToString(), ct);

        // Parse the per-item results: items[i] is a single-key object (index/update/delete) whose value
        // carries a status and, on failure, an error.
        var affected = 0;
        var errors = new List<string>();
        using var doc2 = JsonDocument.Parse(body);
        if (doc2.RootElement.TryGetProperty("items", out var items))
        {
            var i = 0;
            foreach (var item in items.EnumerateArray())
            {
                var action = item.EnumerateObject().First().Value;
                var status = action.TryGetProperty("status", out var st) ? st.GetInt32() : 0;
                if (status is >= 200 and < 300)
                {
                    affected++;
                }
                else
                {
                    var reason = action.TryGetProperty("error", out var err) && err.TryGetProperty("reason", out var r)
                        ? r.GetString()
                        : $"HTTP {status}";
                    var label = i < rowIds.Count ? rowIds[i] : "?";
                    errors.Add($"{label}: {reason}");
                }

                i++;
            }
        }

        return new WritebackResult(affected, IsAtomic: false, errors);
    }

    private static string IdOf(IReadOnlyDictionary<string, object?> identity) =>
        identity.TryGetValue("_id", out var v) && v is not null
            ? v.ToString()!
            : throw new InvalidOperationException("Row has no _id to update/delete.");

    // A grid cell comes back as a CLR scalar or, for a nested column, JSON text. Parse the latter back
    // into structured JSON so the bulk doc nests correctly; keep scalars as their natural JSON value.
    private static JsonNode? CellToJson(object? value)
    {
        if (value is null) return null;
        if (value is string s)
        {
            var trimmed = s.TrimStart();
            if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            {
                try { return JsonNode.Parse(s); }
                catch { /* not valid JSON — fall through and store as a plain string */ }
            }

            return JsonValue.Create(s);
        }

        return JsonValue.Create(value);
    }

    // --- Response projection ------------------------------------------------------------------------
    // Three shapes: a search response (hits.hits → hybrid, editable grid), a top-level JSON array
    // (_cat/*?format=json → flat read-only grid), or anything else (one JSON cell).
    private static QueryResult Project(string body, TimeSpan elapsed)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new QueryResult { Columns = [new ResultColumn("result", typeof(string))], Rows = [["(empty)"]], Elapsed = elapsed };
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("hits", out var hits) &&
            hits.TryGetProperty("hits", out var hitArray) &&
            hitArray.ValueKind == JsonValueKind.Array)
        {
            return FlattenHits(hitArray, elapsed);
        }

        if (root.ValueKind == JsonValueKind.Array)
        {
            return FlattenArray(root, elapsed);
        }

        return new QueryResult
        {
            Columns = [new ResultColumn("result", typeof(string))],
            Rows = [[Prettify(body)]],
            Elapsed = elapsed
        };
    }

    // hits.hits → hybrid grid: _id first (key), then the union of top-level _source fields. Every column
    // is tagged BaseTable=<index> and _id IsKey/IsReadOnly, so the grid is editable via the same mechanism
    // SQL providers use — but only when all hits share one index (otherwise there is no single write target,
    // so the grid stays read-only).
    private static QueryResult FlattenHits(JsonElement hitArray, TimeSpan elapsed)
    {
        var order = new List<string> { "_id" };
        var seen = new HashSet<string>(StringComparer.Ordinal) { "_id" };
        string? index = null;
        var singleIndex = true;

        foreach (var hit in hitArray.EnumerateArray())
        {
            if (hit.TryGetProperty("_index", out var idxEl) && idxEl.GetString() is { } idx)
            {
                if (index is null) index = idx;
                else if (index != idx) singleIndex = false;
            }

            if (hit.TryGetProperty("_source", out var src) && src.ValueKind == JsonValueKind.Object)
            {
                foreach (var field in src.EnumerateObject())
                {
                    if (seen.Add(field.Name)) order.Add(field.Name);
                }
            }
        }

        var baseTable = singleIndex ? index : null;
        var clrTypes = new Type?[order.Count];
        var rows = new List<object?[]>();

        foreach (var hit in hitArray.EnumerateArray())
        {
            var row = new object?[order.Count];
            row[0] = hit.TryGetProperty("_id", out var id) ? id.GetString() : null;
            hit.TryGetProperty("_source", out var src);

            for (var i = 1; i < order.Count; i++)
            {
                if (src.ValueKind == JsonValueKind.Object && src.TryGetProperty(order[i], out var value))
                {
                    var rendered = Render(value);
                    row[i] = rendered;
                    clrTypes[i] ??= rendered?.GetType();
                }
            }

            rows.Add(row);
        }

        var columns = order.Select((name, i) => new ResultColumn(name, clrTypes[i] ?? typeof(string))
        {
            BaseTable = baseTable,
            BaseColumn = name,
            IsKey = name == "_id" && baseTable is not null,
            IsReadOnly = name == "_id"
        }).ToList();

        return new QueryResult { Columns = columns, Rows = rows, Elapsed = elapsed };
    }

    // A top-level JSON array (e.g. _cat/indices?format=json) → a flat, read-only grid: the union of object
    // keys as columns, or a single "value" column when the elements aren't objects.
    private static QueryResult FlattenArray(JsonElement array, TimeSpan elapsed)
    {
        var order = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var allObjects = true;

        foreach (var el in array.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) { allObjects = false; break; }
            foreach (var field in el.EnumerateObject())
            {
                if (seen.Add(field.Name)) order.Add(field.Name);
            }
        }

        if (!allObjects || order.Count == 0)
        {
            var valueRows = array.EnumerateArray().Select(el => new object?[] { Render(el) }).ToList();
            return new QueryResult
            {
                Columns = [new ResultColumn("value", typeof(string))],
                Rows = valueRows,
                Elapsed = elapsed
            };
        }

        var rows = new List<object?[]>();
        foreach (var el in array.EnumerateArray())
        {
            var row = new object?[order.Count];
            for (var i = 0; i < order.Count; i++)
            {
                if (el.TryGetProperty(order[i], out var value)) row[i] = Render(value);
            }

            rows.Add(row);
        }

        var columns = order.Select(name => new ResultColumn(name, typeof(string))).ToList();
        return new QueryResult { Columns = columns, Rows = rows, Elapsed = elapsed };
    }

    // Render a JSON value as a CLR value the grid can display. Scalars map to their natural CLR type;
    // objects and arrays become compact JSON text (one cell).
    private static object? Render(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => value.TryGetInt64(out var l) ? l : value.GetDouble(),
        JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
        _ => value.GetRawText()
    };

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static string Prettify(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, Indented);
        }
        catch
        {
            return json;
        }
    }

    // --- Transport helpers --------------------------------------------------------------------------
    private static ElasticsearchClient CreateClient(ConnectionProfile profile)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = profile.ConnectionString };
        var url = builder.TryGetValue("Url", out var u) ? u?.ToString() : null;
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException("No Elasticsearch URL configured.");
        }

        var settings = new ElasticsearchClientSettings(new Uri(url));

        if (builder.TryGetValue("ApiKey", out var apiKey) && apiKey?.ToString() is { Length: > 0 } key)
        {
            settings = settings.Authentication(new ApiKey(key));
        }
        else if (builder.TryGetValue("Username", out var user) && user?.ToString() is { Length: > 0 } username)
        {
            var password = builder.TryGetValue("Password", out var p) ? p?.ToString() ?? "" : "";
            settings = settings.Authentication(new BasicAuthentication(username, password));
        }

        if (builder.TryGetValue("VerifyTls", out var tls) && tls?.ToString() is "false" or "False")
        {
            settings = settings.ServerCertificateValidationCallback((_, _, _, _) => true);
        }

        return new ElasticsearchClient(settings);
    }

    private static async Task<string> SendAsync(
        ConnectionProfile profile, EsHttpMethod method, string path, string? body, CancellationToken ct)
    {
        var client = CreateClient(profile);
        var response = string.IsNullOrEmpty(body)
            ? await client.Transport.RequestAsync<StringResponse>(method, path, ct)
            : await client.Transport.RequestAsync<StringResponse>(method, path, PostData.String(body), ct);

        var call = response.ApiCallDetails;
        if (!call.HasSuccessfulStatusCode)
        {
            var detail = string.IsNullOrWhiteSpace(response.Body) ? call.OriginalException?.Message : response.Body;
            throw new InvalidOperationException($"Elasticsearch returned HTTP {call.HttpStatusCode}: {detail}");
        }

        return response.Body ?? "";
    }

    private static EsHttpMethod Method(string method) => method switch
    {
        "GET" => EsHttpMethod.GET,
        "POST" => EsHttpMethod.POST,
        "PUT" => EsHttpMethod.PUT,
        "DELETE" => EsHttpMethod.DELETE,
        "HEAD" => EsHttpMethod.HEAD,
        _ => EsHttpMethod.GET
    };

    // Index names are already lowercase/URL-safe, but guard against a stray space in a browse target.
    private static string Encode(string index) => Uri.EscapeDataString(index);

    private static string? Value(IReadOnlyDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
}
