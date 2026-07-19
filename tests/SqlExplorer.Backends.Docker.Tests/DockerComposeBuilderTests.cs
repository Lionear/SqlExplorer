using System.Collections.Generic;
using SqlExplorer.Backends.Docker;

namespace SqlExplorer.Backends.Docker.Tests;

public class DockerComposeBuilderTests
{
    private static readonly DockerComposeBuilder Builder = new();

    private static Dictionary<string, string?> Values(params (string Key, string? Value)[] pairs)
    {
        var d = new Dictionary<string, string?>(System.StringComparer.Ordinal);
        foreach (var (k, v) in pairs)
        {
            d[k] = v;
        }

        return d;
    }

    [Fact]
    public void Postgres_compose_matches_the_expected_shape()
    {
        var spec = new ContainerSpec("postgres",
            Values(("port", "5432"), ("username", "postgres"), ("password", "devpassword")),
            Database: "sales", Tag: "16", ContainerName: "sales-pg-local");

        var expected =
            "services:\n" +
            "  db:\n" +
            "    image: postgres:16\n" +
            "    container_name: sales-pg-local\n" +
            "    restart: unless-stopped\n" +
            "    environment:\n" +
            "      POSTGRES_DB: sales\n" +
            "      POSTGRES_USER: postgres\n" +
            "      POSTGRES_PASSWORD: devpassword\n" +
            "    ports:\n" +
            "      - \"5432:5432\"\n" +
            "    volumes:\n" +
            "      - sales-pg-local-data:/var/lib/postgresql/data\n" +
            "volumes:\n" +
            "  sales-pg-local-data:\n";

        Assert.Equal(expected, Builder.Build(spec, SnippetFormat.Compose));
    }

    [Fact]
    public void Postgres_run_matches_the_expected_one_liner()
    {
        var spec = new ContainerSpec("postgres",
            Values(("port", "5432"), ("username", "postgres"), ("password", "devpassword")),
            Database: "sales", Tag: "16", ContainerName: "sales-pg-local");

        var expected =
            "docker run -d \\\n" +
            "  --name sales-pg-local \\\n" +
            "  -e POSTGRES_DB=sales \\\n" +
            "  -e POSTGRES_USER=postgres \\\n" +
            "  -e POSTGRES_PASSWORD=devpassword \\\n" +
            "  -p 5432:5432 \\\n" +
            "  -v sales-pg-local-data:/var/lib/postgresql/data \\\n" +
            "  postgres:16\n";

        Assert.Equal(expected, Builder.Build(spec, SnippetFormat.Run));
    }

    [Fact] // root connection user: only the root password, no MYSQL_USER/MYSQL_PASSWORD.
    public void MySql_root_user_gets_only_the_root_password()
    {
        var spec = new ContainerSpec("mysql",
            Values(("username", "root"), ("password", "pw")), Database: "app", ContainerName: "c");

        var compose = Builder.Build(spec, SnippetFormat.Compose);

        Assert.Contains("MYSQL_ROOT_PASSWORD: pw", compose);
        Assert.Contains("MYSQL_DATABASE: app", compose);
        Assert.DoesNotContain("MYSQL_USER", compose);
    }

    [Fact] // non-root connection user: root password PLUS the user's own MYSQL_USER/MYSQL_PASSWORD.
    public void MySql_non_root_user_gets_a_dedicated_user()
    {
        var spec = new ContainerSpec("mysql",
            Values(("username", "appuser"), ("password", "pw")), Database: "app", ContainerName: "c");

        var compose = Builder.Build(spec, SnippetFormat.Compose);

        Assert.Contains("MYSQL_ROOT_PASSWORD: pw", compose);
        Assert.Contains("MYSQL_USER: appuser", compose);
        Assert.Contains("MYSQL_PASSWORD: pw", compose);
    }

    [Fact] // SQL Server: mandatory ACCEPT_EULA (quoted "Y" — bare Y is a YAML bool), no MSSQL_DATABASE.
    public void SqlServer_requires_quoted_eula_and_has_no_database_env()
    {
        var spec = new ContainerSpec("sqlserver",
            Values(("port", "1433"), ("username", "sa"), ("password", "Str0ng!Passw0rd")),
            Database: "crm", ContainerName: "crm-mssql-local");

        var compose = Builder.Build(spec, SnippetFormat.Compose);

        Assert.Contains("ACCEPT_EULA: \"Y\"", compose);
        Assert.Contains("MSSQL_SA_PASSWORD: \"Str0ng!Passw0rd\"", compose); // '!' → quoted
        Assert.Contains("MSSQL_PID: Developer", compose);
        Assert.Contains("image: mcr.microsoft.com/mssql/server:2022-latest", compose);
        Assert.Contains("- \"1433:1433\"", compose);
        Assert.DoesNotContain("MSSQL_DATABASE", compose);
    }

    [Fact] // Elasticsearch keeps its port inside a single `url` key — the builder parses it.
    public void Elasticsearch_derives_the_host_port_from_the_url()
    {
        var custom = new ContainerSpec("elasticsearch",
            Values(("url", "https://localhost:9243"), ("password", "pw")), ContainerName: "c");
        Assert.Contains("- \"9243:9200\"", Builder.Build(custom, SnippetFormat.Compose));

        // No explicit port in the url → the engine default 9200.
        var noPort = new ContainerSpec("elasticsearch",
            Values(("url", "https://localhost"), ("password", "pw")), ContainerName: "c");
        Assert.Contains("- \"9200:9200\"", Builder.Build(noPort, SnippetFormat.Compose));

        var es = Builder.Build(custom, SnippetFormat.Compose);
        Assert.Contains("discovery.type: single-node", es);
        Assert.Contains("ELASTIC_PASSWORD: pw", es);
    }

    [Fact]
    public void Mongo_enables_auth_only_when_a_username_is_present()
    {
        var withAuth = new ContainerSpec("mongodb",
            Values(("username", "admin"), ("password", "pw")), Database: "shop", ContainerName: "c");
        var authCompose = Builder.Build(withAuth, SnippetFormat.Compose);
        Assert.Contains("MONGO_INITDB_ROOT_USERNAME: admin", authCompose);
        Assert.Contains("MONGO_INITDB_ROOT_PASSWORD: pw", authCompose);
        Assert.Contains("MONGO_INITDB_DATABASE: shop", authCompose);

        var noAuth = new ContainerSpec("mongodb", Values(("port", "27017")), ContainerName: "c");
        Assert.DoesNotContain("MONGO_INITDB_ROOT_USERNAME", Builder.Build(noAuth, SnippetFormat.Compose));
    }

    [Fact] // Redis auth is a server flag (command), present only with a password.
    public void Redis_requirepass_only_with_a_password()
    {
        var withPw = new ContainerSpec("redis", Values(("password", "secret")), ContainerName: "c");
        Assert.Contains("command: [\"redis-server\", \"--requirepass\", \"secret\"]", Builder.Build(withPw, SnippetFormat.Compose));

        var noPw = new ContainerSpec("redis", Values(("port", "6379")), ContainerName: "c");
        Assert.DoesNotContain("command:", Builder.Build(noPw, SnippetFormat.Compose));
    }

    [Fact]
    public void Dragonfly_sets_the_memlock_ulimit()
    {
        var spec = new ContainerSpec("dragonflydb", Values(("password", "pw")), ContainerName: "c");
        var compose = Builder.Build(spec, SnippetFormat.Compose);
        Assert.Contains("image: docker.dragonflydb.io/dragonflydb/dragonfly:latest", compose);
        Assert.Contains("memlock: -1", compose);
        Assert.Contains("command: [\"--requirepass\", \"pw\"]", compose);

        var run = Builder.Build(spec, SnippetFormat.Run);
        Assert.Contains("--ulimit memlock=-1", run);
    }

    [Fact] // Passwords with shell/YAML metacharacters must be quoted in both formats.
    public void Special_characters_in_a_password_are_quoted()
    {
        var spec = new ContainerSpec("postgres",
            Values(("username", "postgres"), ("password", "p@ss w0rd:!")), ContainerName: "c");

        Assert.Contains("POSTGRES_PASSWORD: \"p@ss w0rd:!\"", Builder.Build(spec, SnippetFormat.Compose));
        Assert.Contains("-e 'POSTGRES_PASSWORD=p@ss w0rd:!'", Builder.Build(spec, SnippetFormat.Run));
    }

    [Theory]
    [InlineData("postgres", true)]
    [InlineData("sqlserver", true)]
    [InlineData("elasticsearch", true)]
    [InlineData("sqlite", false)]
    [InlineData("nonsense", false)]
    public void Supports_reflects_containerisable_engines(string providerId, bool supported) =>
        Assert.Equal(supported, Builder.Supports(providerId));

    [Fact]
    public void Unsupported_provider_throws()
    {
        var spec = new ContainerSpec("sqlite", Values(), ContainerName: "c");
        Assert.Throws<NotSupportedException>(() => Builder.Build(spec, SnippetFormat.Compose));
    }
}
