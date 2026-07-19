using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SqlExplorer.Core.Connections;
using SqlExplorer.Core.Mcp;
using SqlExplorer.Sdk.Mcp;

namespace SqlExplorer.Core.Tests.Mcp;

// The MCP connection-create/delete surface (SE-155) is security-sensitive: these tests pin the fail-closed
// gates and the access-level capping that keep an AI from creating something more powerful than allowed.
public class McpHostConnectionTests
{
    private static McpConnectionPolicy Allowed(params string[] hosts) => new(true, hosts, "MCP");
    private static McpConnectionPolicy Denied() => new(false, [], "MCP");

    private static Dictionary<string, string?> Values(string host = "127.0.0.1") =>
        new() { ["host"] = host, ["port"] = "5432", ["password"] = "s3cret" };

    private static McpCreateConnectionRequest Req(
        Dictionary<string, string?>? values = null, bool persistent = false, string? access = null) =>
        new("fake", "T", values ?? Values(), persistent, access);

    [Fact]
    public async Task Create_is_refused_when_the_policy_is_off()
    {
        var (host, connections, _) = McpTestHost.Build(Denied());

        await Assert.ThrowsAsync<McpAccessException>(() => host.CreateConnectionAsync(Req(), CancellationToken.None));
        Assert.Empty(connections.List());
        Assert.Empty(connections.ListTransient());
    }

    [Fact]
    public async Task Create_is_refused_for_an_unknown_provider()
    {
        var (host, _, _) = McpTestHost.Build(Allowed());
        var req = new McpCreateConnectionRequest("nope", "T", Values(), false, null);

        await Assert.ThrowsAsync<McpAccessException>(() => host.CreateConnectionAsync(req, CancellationToken.None));
    }

    [Fact]
    public async Task Create_is_refused_when_a_required_field_is_missing()
    {
        var (host, _, _) = McpTestHost.Build(Allowed());
        var req = Req(new Dictionary<string, string?> { ["port"] = "5432" }); // no host

        var ex = await Assert.ThrowsAsync<McpAccessException>(() => host.CreateConnectionAsync(req, CancellationToken.None));
        Assert.Contains("host", ex.Message);
    }

    [Fact]
    public async Task Create_is_refused_for_a_host_outside_the_allowlist()
    {
        var (host, _, _) = McpTestHost.Build(Allowed()); // loopback only
        var req = Req(Values("db.example.com"));

        await Assert.ThrowsAsync<McpAccessException>(() => host.CreateConnectionAsync(req, CancellationToken.None));
    }

    [Fact]
    public async Task Create_allows_a_configured_non_loopback_host_but_caps_it_below_Sandbox()
    {
        var (host, connections, _) = McpTestHost.Build(Allowed("db.example.com"));
        var req = Req(Values("db.example.com"), persistent: false, access: "sandbox");

        var result = await host.CreateConnectionAsync(req, CancellationToken.None);

        // Non-loopback can never be Sandbox — capped to ReadWrite.
        Assert.Equal(AiAccessMode.ReadWrite.ToString(), result.Access);
        Assert.Single(connections.ListTransient());
    }

    [Fact]
    public async Task Transient_loopback_defaults_to_Sandbox_and_never_persists()
    {
        var (host, connections, _) = McpTestHost.Build(Allowed());

        var result = await host.CreateConnectionAsync(Req(persistent: false), CancellationToken.None);

        Assert.Equal(AiAccessMode.Sandbox.ToString(), result.Access);
        Assert.False(result.Persistent);
        Assert.Empty(connections.List());                 // nothing saved
        var transient = Assert.Single(connections.ListTransient());
        Assert.True(transient.IsTransient);
        Assert.Equal(McpHost.CreatedByOrigin, transient.Origin);
    }

    [Fact]
    public async Task Persistent_create_caps_Sandbox_to_ReadWrite_and_saves_with_the_mcp_origin()
    {
        var (host, connections, _) = McpTestHost.Build(Allowed());

        var result = await host.CreateConnectionAsync(Req(persistent: true, access: "sandbox"), CancellationToken.None);

        Assert.Equal(AiAccessMode.ReadWrite.ToString(), result.Access); // persisted can't be Sandbox
        var saved = Assert.Single(connections.List());
        Assert.Equal(McpHost.CreatedByOrigin, saved.Origin);
        Assert.Equal("MCP", saved.Folder);
        Assert.False(saved.IsTransient);
    }

    [Fact]
    public async Task Delete_removes_an_mcp_created_connection_but_refuses_a_user_one()
    {
        var (host, connections, _) = McpTestHost.Build(Allowed());
        var created = await host.CreateConnectionAsync(Req(persistent: true), CancellationToken.None);
        var userConn = connections.Save("user1", "Mine", "fake", Values());

        // Refuses the user's own connection...
        Assert.Throws<McpAccessException>(() => host.DeleteConnection(userConn.Id));
        Assert.NotNull(connections.List().SingleOrDefault(c => c.Id == "user1"));

        // ...but removes the one it created.
        host.DeleteConnection(created.ConnectionId);
        Assert.DoesNotContain(connections.List(), c => c.Id == created.ConnectionId);
    }

    [Fact]
    public async Task ListProviders_reports_fields_with_required_and_secret_flags()
    {
        var (host, _, _) = McpTestHost.Build(Allowed());

        var providers = host.ListProviders();

        var fake = Assert.Single(providers);
        Assert.Equal("fake", fake.Id);
        Assert.True(fake.Fields.Single(f => f.Key == "host").Required);
        Assert.True(fake.Fields.Single(f => f.Key == "password").Secret);
        Assert.False(fake.Fields.Single(f => f.Key == "port").Required);
    }

    [Fact]
    public async Task Every_create_and_delete_is_recorded_in_the_activity_log()
    {
        var (host, _, activity) = McpTestHost.Build(Allowed());

        await host.CreateConnectionAsync(Req(persistent: true), CancellationToken.None);
        await Assert.ThrowsAsync<McpAccessException>(() =>
            host.CreateConnectionAsync(Req(Values("db.example.com")), CancellationToken.None));

        var entries = activity.Snapshot();
        Assert.Contains(entries, e => e.Tool == "create_connection" && e.Allowed);
        Assert.Contains(entries, e => e.Tool == "create_connection" && !e.Allowed);
    }
}
