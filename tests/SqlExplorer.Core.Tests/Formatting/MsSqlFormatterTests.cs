using SqlExplorer.Providers.MsSql;
using SqlExplorer.Sdk.Formatting;

namespace SqlExplorer.Core.Tests.Formatting;

public class MsSqlFormatterTests
{
    private static readonly MsSqlFormatter Formatter = new();
    private static readonly FakeDialect Dialect = new(); // ScriptDom ignores the dialect; any instance works

    private static string Format(string sql, SqlFormatOptions? options = null) =>
        Formatter.Format(sql, Dialect, options ?? SqlFormatOptions.Default);

    [Fact]
    public void Formats_a_select_statement()
    {
        var result = Format("select id,name from dbo.users where id=1");

        Assert.Contains("SELECT", result);
        Assert.Contains("FROM", result);
        Assert.Contains("WHERE", result);
    }

    [Fact]
    public void Lowercases_keywords_when_requested()
    {
        var result = Format("SELECT id FROM dbo.t", SqlFormatOptions.Default with { KeywordCasing = KeywordCasing.Lower });

        Assert.Contains("select", result);
        Assert.DoesNotContain("SELECT", result);
    }

    [Fact]
    public void Is_idempotent()
    {
        const string sql = "select u.id, u.name from dbo.users u inner join dbo.orders o " +
                           "on o.user_id = u.id where u.active = 1";

        var once = Format(sql);
        var twice = Format(once);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Returns_input_unchanged_when_unparseable()
    {
        const string sql = "this is not <<< valid t-sql @@@";

        Assert.Equal(sql, Format(sql));
    }
}
