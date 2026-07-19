using System.Linq;
using System.Text.Json;
using System.Threading;
using SqlExplorer.Core.Connections;
using SqlExplorer.Core.Mcp;
using SqlExplorer.Mcp.Server;
using SqlExplorer.Sdk.Mcp;

namespace SqlExplorer.Core.Tests.Mcp;

// Drives the SE-155 tools through CoreToolProvider's actual handlers — real JSON arguments, real host — so
// the arg-parsing layer (ReadStringMap number→string coercion, ReadBool, RequireString) is covered end-to-end,
// not just the McpHost methods the other tests call directly.
public class CoreToolProviderConnectionTests
{
    private static McpToolDefinition Tool(string name) =>
        new CoreToolProvider().GetTools().Single(t => t.Name == name);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static (McpHost Host, ConnectionService Connections) Host()
    {
        var b = McpTestHost.Build(new McpConnectionPolicy(true, [], "MCP"));
        return (b.Host, b.Connections);
    }

    [Fact]
    public async Task create_connection_parses_json_args_and_creates_a_transient_sandbox_connection()
    {
        var (host, connections) = Host();

        // Note: port arrives as a JSON number, exercising ReadStringMap's number→string coercion.
        var args = Json("""
        {"providerId":"fake","name":"Temp DB","values":{"host":"127.0.0.1","port":5432,"password":"s3cret"},"persistent":false,"access":"sandbox"}
        """);

        var result = await Tool("create_connection").Handler(args, host, CancellationToken.None);

        var created = Assert.IsType<McpCreateConnectionResult>(result);
        Assert.Equal("Sandbox", created.Access);
        Assert.False(created.Persistent);
        Assert.Single(connections.ListTransient());
        Assert.Empty(connections.List());
    }

    [Fact]
    public async Task create_connection_defaults_persistent_to_false_when_omitted()
    {
        var (host, connections) = Host();
        var args = Json("""{"providerId":"fake","name":"T","values":{"host":"127.0.0.1"}}""");

        await Tool("create_connection").Handler(args, host, CancellationToken.None);

        Assert.Single(connections.ListTransient()); // ReadBool absent → default false → transient
    }

    [Fact]
    public async Task create_connection_surfaces_a_refusal_as_McpAccessException()
    {
        var (host, _) = Host();
        var args = Json("""{"providerId":"fake","name":"T","values":{"port":5432}}"""); // no host

        await Assert.ThrowsAsync<McpAccessException>(() =>
            Tool("create_connection").Handler(args, host, CancellationToken.None));
    }

    [Fact]
    public async Task delete_connection_removes_a_connection_created_through_the_tool()
    {
        var (host, connections) = Host();
        var create = await Tool("create_connection").Handler(
            Json("""{"providerId":"fake","name":"T","values":{"host":"127.0.0.1"},"persistent":true}"""),
            host, CancellationToken.None);
        var id = Assert.IsType<McpCreateConnectionResult>(create).ConnectionId;

        await Tool("delete_connection").Handler(
            Json($$"""{"connectionId":"{{id}}"}"""), host, CancellationToken.None);

        Assert.DoesNotContain(connections.List(), c => c.Id == id);
    }

    [Fact]
    public async Task list_providers_returns_the_registered_providers()
    {
        var (host, _) = Host();

        var result = await Tool("list_providers").Handler(Json("{}"), host, CancellationToken.None);

        var providers = Assert.IsAssignableFrom<System.Collections.Generic.IReadOnlyList<McpProviderInfo>>(result);
        Assert.Contains(providers, p => p.Id == "fake");
    }
}
