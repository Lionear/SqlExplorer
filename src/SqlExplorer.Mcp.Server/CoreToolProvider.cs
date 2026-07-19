using System.Text.Json;

namespace SqlExplorer.Mcp.Server;

/// <summary>
/// The first-party, bundled MCP tool provider — the four core tools every AI client gets: list connections,
/// browse schema, run a query, explain a query. Deliberately thin: each handler only parses its arguments
/// and calls the host's <see cref="IMcpHost"/>, which owns every authorization decision (reachability,
/// read/write/DDL classification, row/timeout caps, audit). These tools never touch a driver or a
/// connection string directly, so they cannot exceed what the host permits.
/// </summary>
public sealed class CoreToolProvider : IMcpToolProvider
{
    public IReadOnlyList<McpToolDefinition> GetTools() =>
    [
        new McpToolDefinition(
            "list_connections",
            "List the database connections the AI is allowed to use (id, name, engine, access mode). Never returns secrets.",
            """{"type":"object","properties":{},"additionalProperties":false}""",
            (_, host, _) => Task.FromResult<object?>(host.ListConnections())),

        new McpToolDefinition(
            "get_schema",
            "List the schema entries (databases/schemas/tables/columns) under an optional path in a connection's tree.",
            """
            {
              "type": "object",
              "properties": {
                "connectionId": { "type": "string", "description": "Connection id from list_connections." },
                "path": { "type": "array", "items": { "type": "string" }, "description": "Ancestor node names from the tree root; omit for the top level." }
              },
              "required": ["connectionId"],
              "additionalProperties": false
            }
            """,
            async (args, host, ct) =>
            {
                var connectionId = RequireString(args, "connectionId");
                var path = ReadStringArray(args, "path");
                return await host.GetSchemaAsync(connectionId, path, ct);
            }),

        new McpToolDefinition(
            "run_query",
            "Run a single SQL statement on a connection and return the (row-capped) result. Writes need a ReadWrite connection; DDL and multi-statement payloads are always refused.",
            """
            {
              "type": "object",
              "properties": {
                "connectionId": { "type": "string", "description": "Connection id from list_connections." },
                "sql": { "type": "string", "description": "A single SQL statement." },
                "maxRows": { "type": "integer", "description": "Optional row cap (only lowers the server cap)." }
              },
              "required": ["connectionId", "sql"],
              "additionalProperties": false
            }
            """,
            async (args, host, ct) =>
            {
                var connectionId = RequireString(args, "connectionId");
                var sql = RequireString(args, "sql");
                var maxRows = ReadInt(args, "maxRows");
                return await host.RunQueryAsync(connectionId, sql, maxRows, ct);
            }),

        new McpToolDefinition(
            "explain_query",
            "Return the query plan (EXPLAIN) for a SQL statement without executing it.",
            """
            {
              "type": "object",
              "properties": {
                "connectionId": { "type": "string", "description": "Connection id from list_connections." },
                "sql": { "type": "string", "description": "The SQL statement to explain." }
              },
              "required": ["connectionId", "sql"],
              "additionalProperties": false
            }
            """,
            async (args, host, ct) =>
            {
                var connectionId = RequireString(args, "connectionId");
                var sql = RequireString(args, "sql");
                return await host.ExplainAsync(connectionId, sql, ct);
            }),

        new McpToolDefinition(
            "get_query_log",
            "List recently executed queries from the on-disk query log (SQL + timing/outcome), newest first. Only entries for connections the AI may use are returned; empty when query logging is disabled. Never returns result data or secrets.",
            """
            {
              "type": "object",
              "properties": {
                "limit": { "type": "integer", "description": "Max entries to return (1-1000, default 100)." },
                "source": { "type": "string", "enum": ["app", "mcp"], "description": "Filter by who ran the query: 'app' (user) or 'mcp' (AI). Omit for both." }
              },
              "additionalProperties": false
            }
            """,
            (args, host, _) => Task.FromResult<object?>(host.GetQueryLog(ReadInt(args, "limit"), ReadString(args, "source")))),

        new McpToolDefinition(
            "list_providers",
            "List the database providers (drivers) available for create_connection, each with its connection fields (which are required, optional, secret, or have a fixed set of choices). Call this first to learn what a create_connection request needs. Never returns any secret or field value.",
            """{"type":"object","properties":{},"additionalProperties":false}""",
            (_, host, _) => Task.FromResult<object?>(host.ListProviders())),

        new McpToolDefinition(
            "create_connection",
            "Create a database connection (SE-155). Off unless the user enabled it in settings. 'persistent' false makes an in-memory, session-only connection wiped when SQL Explorer closes; true saves it. 'access' requests an AI-access level (readonly/readwrite/sandbox); the host may lower it — persistent connections cap at readwrite, and 'sandbox' (which also allows DDL) is only granted to a transient loopback connection. Call list_providers first for the provider id and its fields.",
            """
            {
              "type": "object",
              "properties": {
                "providerId": { "type": "string", "description": "Provider (driver) id from list_providers." },
                "name": { "type": "string", "description": "Display name for the connection." },
                "values": { "type": "object", "description": "Field key → value map (from list_providers' fields); include every required field.", "additionalProperties": { "type": ["string", "null"] } },
                "persistent": { "type": "boolean", "description": "Save it (true) or keep it in-memory/session-only (false). Default false." },
                "access": { "type": "string", "enum": ["readonly", "readwrite", "sandbox"], "description": "Requested AI-access level; the host may lower it. Omit for the host default." }
              },
              "required": ["providerId", "name", "values"],
              "additionalProperties": false
            }
            """,
            async (args, host, ct) =>
            {
                var providerId = RequireString(args, "providerId");
                var name = RequireString(args, "name");
                var values = ReadStringMap(args, "values");
                var persistent = ReadBool(args, "persistent") ?? false;
                var access = ReadString(args, "access");
                return await host.CreateConnectionAsync(
                    new McpCreateConnectionRequest(providerId, name, values, persistent, access), ct);
            }),

        new McpToolDefinition(
            "delete_connection",
            "Delete a connection the AI created earlier — a transient one, or a persisted one created over MCP. Never deletes the user's own or another plugin's connections.",
            """
            {
              "type": "object",
              "properties": {
                "connectionId": { "type": "string", "description": "Connection id from create_connection or list_connections." }
              },
              "required": ["connectionId"],
              "additionalProperties": false
            }
            """,
            (args, host, _) =>
            {
                host.DeleteConnection(RequireString(args, "connectionId"));
                return Task.FromResult<object?>(new { deleted = true });
            })
    ];

    private static string? ReadString(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string RequireString(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new McpAccessException($"Missing required string argument '{name}'.");

    private static int? ReadInt(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var i)
            ? i
            : null;

    private static bool? ReadBool(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    // Read an object argument as a string→string? map (create_connection's field values). Non-string scalar
    // values are stringified so a JSON number/bool for, say, a port still comes through; null stays null.
    private static IReadOnlyDictionary<string, string?> ReadStringMap(JsonElement args, string name)
    {
        var map = new Dictionary<string, string?>();
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty(name, out var obj)
            || obj.ValueKind != JsonValueKind.Object)
        {
            return map;
        }

        foreach (var prop in obj.EnumerateObject())
        {
            map[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Null => null,
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => prop.Value.GetRawText(),
                _ => null
            };
        }

        return map;
    }

    private static IReadOnlyList<string>? ReadStringArray(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                list.Add(item.GetString()!);
            }
        }

        return list;
    }
}
