using System.Collections.Generic;
using SqlExplorer.Backends.Docker;
using Xunit;

namespace SqlExplorer.Backends.Docker.Tests;

// The host connection a managed container gets must carry the engine's own credentials, not just the
// endpoint — otherwise connecting to the container prompts for a password it should already know (SE-164).
public class ConnectionValuesTests
{
    private static CreateContainerRequest Request(int port = 5432, string? database = null) =>
        new(
            ProviderId: "postgres",
            Values: new Dictionary<string, string?> { ["username"] = "admin", ["password"] = "s3cret" },
            ContainerName: "postgres-local",
            HostPort: port,
            Tag: "16",
            Database: database);

    [Fact]
    public void Carries_credentials_endpoint_and_database()
    {
        var values = DockerSubsystem.BuildConnectionValues(Request(port: 5455, database: "shop"));

        Assert.Equal("localhost", values["host"]);
        Assert.Equal("5455", values["port"]);
        Assert.Equal("admin", values["username"]);
        Assert.Equal("s3cret", values["password"]);
        Assert.Equal("shop", values["database"]);
    }

    [Fact]
    public void Omits_database_when_not_set()
    {
        var values = DockerSubsystem.BuildConnectionValues(Request(database: null));

        Assert.False(values.ContainsKey("database"));
        Assert.Equal("s3cret", values["password"]);
    }
}
