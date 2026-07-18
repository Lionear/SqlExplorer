using System.Text.Json;
using SqlExplorer.Core.Session;

namespace SqlExplorer.Core.Tests.Session;

public class OpenTabStateTests
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    // SE-154 added an optional FilePath to OpenTabState. A session file written before that change has no
    // such property; it must still load (FilePath defaulting to null) rather than throw, or an upgrade
    // would wipe the user's restored tabs.
    [Fact]
    public void Deserialize_LegacyJsonWithoutFilePath_DefaultsToNull()
    {
        const string legacy = """{ "ConnectionId": "c1", "Database": "db", "Sql": "select 1" }""";

        var state = JsonSerializer.Deserialize<OpenTabState>(legacy, Options);

        Assert.NotNull(state);
        Assert.Equal("c1", state!.ConnectionId);
        Assert.Equal("db", state.Database);
        Assert.Equal("select 1", state.Sql);
        Assert.Null(state.FilePath);
    }

    [Fact]
    public void RoundTrip_WithFilePath_PreservesAssociation()
    {
        var original = new OpenTabState("c1", "db", "select 1", "/home/rick/queries/report.sql");

        var restored = JsonSerializer.Deserialize<OpenTabState>(
            JsonSerializer.Serialize(original, Options), Options);

        Assert.Equal(original, restored);
        Assert.Equal("/home/rick/queries/report.sql", restored!.FilePath);
    }
}
