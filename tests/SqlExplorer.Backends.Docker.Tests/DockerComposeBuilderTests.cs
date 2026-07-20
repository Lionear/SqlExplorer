using System.Collections.Generic;
using SqlExplorer.Backends.Docker;
using SqlExplorer.Sdk.Provisioning;

namespace SqlExplorer.Backends.Docker.Tests;

// The builder is purely provider-driven (SE-176): it renders whatever recipes the host hands in, and owns no
// recipe table itself. These tests therefore feed it local FIXTURE recipes — representative shapes that exercise
// every render branch (env, command, memlock, host-port override, YAML/shell quoting) — and assert the emitted
// compose/run text. The recipes' own CONTENT (which env a given engine emits) is tested against the real
// providers in SqlExplorer.Core.Tests' ProviderContainerRecipeTests, not here.
public class DockerComposeBuilderTests
{
    // Fed the fixture engines (see RecipeFixtures) — the builder ships no recipes of its own (SE-176).
    private static readonly DockerComposeBuilder Builder = RecipeFixtures.Builder();

    private static Dictionary<string, string?> Values(params (string Key, string? Value)[] pairs)
    {
        var d = new Dictionary<string, string?>(System.StringComparer.Ordinal);
        foreach (var (k, v) in pairs)
        {
            d[k] = v;
        }

        return d;
    }

    // ---- render mechanics --------------------------------------------------------------------------

    [Fact]
    public void Compose_matches_the_expected_shape()
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
            "    labels:\n" +
            "      kontena.managed: \"true\"\n" +
            "      kontena.source: sqlexplorer\n" +
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
    public void Run_matches_the_expected_one_liner()
    {
        var spec = new ContainerSpec("postgres",
            Values(("port", "5432"), ("username", "postgres"), ("password", "devpassword")),
            Database: "sales", Tag: "16", ContainerName: "sales-pg-local");

        var expected =
            "docker run -d \\\n" +
            "  --name sales-pg-local \\\n" +
            "  --label kontena.managed=true \\\n" +
            "  --label kontena.source=sqlexplorer \\\n" +
            "  -e POSTGRES_DB=sales \\\n" +
            "  -e POSTGRES_USER=postgres \\\n" +
            "  -e POSTGRES_PASSWORD=devpassword \\\n" +
            "  -p 5432:5432 \\\n" +
            "  -v sales-pg-local-data:/var/lib/postgresql/data \\\n" +
            "  postgres:16\n";

        Assert.Equal(expected, Builder.Build(spec, SnippetFormat.Run));
    }

    [Fact] // A YAML 1.1 bool keyword ("Y") and a '!' in a password force quoting; DatabaseAfterStart emits no db env.
    public void Yaml_keywords_and_special_characters_are_quoted()
    {
        var spec = new ContainerSpec("sqlserver",
            Values(("port", "1433"), ("username", "sa"), ("password", "Str0ng!Passw0rd")),
            Database: "crm", ContainerName: "crm-mssql-local");

        var compose = Builder.Build(spec, SnippetFormat.Compose);

        Assert.Contains("ACCEPT_EULA: \"Y\"", compose);
        Assert.Contains("MSSQL_SA_PASSWORD: \"Str0ng!Passw0rd\"", compose); // '!' → quoted
        Assert.Contains("MSSQL_PID: Developer", compose);
        Assert.Contains("image: mcr.microsoft.com/mssql/server:2025-latest", compose);
        Assert.Contains("- \"1433:1433\"", compose);
    }

    [Fact]
    public void Special_characters_in_a_password_are_quoted_in_both_formats()
    {
        var spec = new ContainerSpec("postgres",
            Values(("username", "postgres"), ("password", "p@ss w0rd:!")), ContainerName: "c");

        Assert.Contains("POSTGRES_PASSWORD: \"p@ss w0rd:!\"", Builder.Build(spec, SnippetFormat.Compose));
        Assert.Contains("-e 'POSTGRES_PASSWORD=p@ss w0rd:!'", Builder.Build(spec, SnippetFormat.Run));
    }

    [Fact] // A recipe's command args render into both the compose array and the run tail; absent without one.
    public void Command_args_render_in_both_formats()
    {
        var withPw = new ContainerSpec("redis", Values(("password", "secret")), ContainerName: "c");
        Assert.Contains("command: [\"redis-server\", \"--requirepass\", \"secret\"]", Builder.Build(withPw, SnippetFormat.Compose));
        Assert.Contains("redis:7 redis-server --requirepass secret", Builder.Build(withPw, SnippetFormat.Run));

        var noPw = new ContainerSpec("redis", Values(("port", "6379")), ContainerName: "c");
        Assert.DoesNotContain("command:", Builder.Build(noPw, SnippetFormat.Compose));
    }

    [Fact]
    public void Memlock_ulimit_renders_in_both_formats()
    {
        var spec = new ContainerSpec("dragonflydb", Values(("password", "pw")), ContainerName: "c");

        var compose = Builder.Build(spec, SnippetFormat.Compose);
        Assert.Contains("image: docker.dragonflydb.io/dragonflydb/dragonfly:latest", compose);
        Assert.Contains("memlock: -1", compose);
        Assert.Contains("command: [\"--requirepass\", \"pw\"]", compose);

        Assert.Contains("--ulimit memlock=-1", Builder.Build(spec, SnippetFormat.Run));
    }

    [Fact] // A recipe can carry its host port somewhere other than a plain `port` field (Elasticsearch: in the url).
    public void Host_port_override_is_honoured()
    {
        var custom = new ContainerSpec("elasticsearch",
            Values(("url", "https://localhost:9243"), ("password", "pw")), ContainerName: "c");
        Assert.Contains("- \"9243:9200\"", Builder.Build(custom, SnippetFormat.Compose));

        // No explicit port in the url → the recipe's default container port.
        var noPort = new ContainerSpec("elasticsearch",
            Values(("url", "https://localhost"), ("password", "pw")), ContainerName: "c");
        Assert.Contains("- \"9200:9200\"", Builder.Build(noPort, SnippetFormat.Compose));
    }

    [Fact] // SE-184: every generated container carries the Kontena ownership labels, in both formats.
    public void Both_formats_carry_the_kontena_ownership_labels()
    {
        var spec = new ContainerSpec("postgres", Values(("password", "pw")), ContainerName: "c");

        Assert.Contains("    labels:\n      kontena.managed: \"true\"\n      kontena.source: sqlexplorer\n",
            Builder.Build(spec, SnippetFormat.Compose));

        var run = Builder.Build(spec, SnippetFormat.Run);
        Assert.Contains("--label kontena.managed=true", run);
        Assert.Contains("--label kontena.source=sqlexplorer", run);
    }

    // ---- provider-driven catalog -------------------------------------------------------------------

    [Fact] // A third-party engine that ships a recipe becomes containerisable with no change to the builder.
    public void A_declared_recipe_makes_a_new_engine_containerisable()
    {
        var recipe = new ContainerRecipe("cooldb", "1", 9999, "/var/cool", "root", "changeme",
            e => [new("COOL_PASS", e.Password)]);
        var builder = new DockerComposeBuilder([new ProviderRecipe("cooldb", "CoolDB", recipe)]);

        Assert.True(builder.Supports("cooldb"));
        Assert.Contains("cooldb", builder.SupportedProviderIds);

        var spec = new ContainerSpec("cooldb",
            Values(("port", "9999"), ("password", "secret")), Tag: "1", ContainerName: "cool-local");

        var expected =
            "services:\n" +
            "  db:\n" +
            "    image: cooldb:1\n" +
            "    container_name: cool-local\n" +
            "    restart: unless-stopped\n" +
            "    labels:\n" +
            "      kontena.managed: \"true\"\n" +
            "      kontena.source: sqlexplorer\n" +
            "    environment:\n" +
            "      COOL_PASS: secret\n" +
            "    ports:\n" +
            "      - \"9999:9999\"\n" +
            "    volumes:\n" +
            "      - cool-local-data:/var/cool\n" +
            "volumes:\n" +
            "  cool-local-data:\n";

        Assert.Equal(expected, builder.Build(spec, SnippetFormat.Compose));
    }

    [Theory]
    [InlineData("postgres", true)]
    [InlineData("sqlserver", true)]
    [InlineData("elasticsearch", true)]
    [InlineData("sqlite", false)]
    [InlineData("nonsense", false)]
    public void Supports_reflects_the_declared_recipes(string providerId, bool supported) =>
        Assert.Equal(supported, Builder.Supports(providerId));

    [Fact] // No recipes declared (e.g. the "providers" capability wasn't granted) → nothing is containerisable.
    public void An_empty_builder_supports_nothing()
    {
        var empty = new DockerComposeBuilder();

        Assert.Empty(empty.SupportedProviderIds);
        Assert.False(empty.Supports("postgres"));
        Assert.Null(empty.ImageName("postgres"));

        var spec = new ContainerSpec("postgres", Values(("password", "x")), ContainerName: "c");
        Assert.Throws<NotSupportedException>(() => empty.Build(spec, SnippetFormat.Compose));
    }

    [Fact]
    public void Unsupported_provider_throws()
    {
        var spec = new ContainerSpec("sqlite", Values(), ContainerName: "c");
        Assert.Throws<NotSupportedException>(() => Builder.Build(spec, SnippetFormat.Compose));
    }
}
