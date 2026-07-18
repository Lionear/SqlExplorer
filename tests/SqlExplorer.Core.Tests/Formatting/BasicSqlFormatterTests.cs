using SqlExplorer.Core.Formatting;
using SqlExplorer.Sdk.Formatting;

namespace SqlExplorer.Core.Tests.Formatting;

public class BasicSqlFormatterTests
{
    private static readonly BasicSqlFormatter Formatter = new();
    private static readonly FakeDialect Dialect = new();

    private static string Format(string sql, SqlFormatOptions? options = null) =>
        Formatter.Format(sql, Dialect, options ?? SqlFormatOptions.Default);

    [Fact]
    public void Uppercases_keywords_by_default()
    {
        var result = Format("select id from users");

        Assert.Contains("SELECT", result);
        Assert.Contains("FROM", result);
        Assert.DoesNotContain("select", result);
    }

    [Fact]
    public void Lowercases_keywords_when_requested()
    {
        var result = Format("SELECT id FROM users", SqlFormatOptions.Default with { KeywordCasing = KeywordCasing.Lower });

        Assert.Contains("select", result);
        Assert.Contains("from", result);
        Assert.DoesNotContain("SELECT", result);
    }

    [Fact]
    public void Preserves_keyword_casing_when_requested()
    {
        var result = Format("Select id From users", SqlFormatOptions.Default with { KeywordCasing = KeywordCasing.Preserve });

        Assert.Contains("Select", result);
        Assert.Contains("From", result);
    }

    [Fact]
    public void Does_not_recase_a_quoted_identifier()
    {
        // "select" here is a quoted identifier, not the keyword — it must survive verbatim.
        var result = Format("SELECT \"select\" FROM t");

        Assert.Contains("\"select\"", result);
    }

    [Fact]
    public void Preserves_string_literal_contents()
    {
        var result = Format("SELECT id FROM t WHERE name = 'From SELECT'");

        Assert.Contains("'From SELECT'", result);
    }

    [Fact]
    public void Breaks_major_clauses_onto_their_own_lines()
    {
        var lines = Format("select id from users where id = 1").Split('\n');

        Assert.Contains(lines, l => l.TrimStart().StartsWith("FROM"));
        Assert.Contains(lines, l => l.TrimStart().StartsWith("WHERE"));
    }

    [Fact]
    public void Is_idempotent()
    {
        const string sql = "select u.id, u.name from users u left join orders o on o.user_id = u.id " +
                           "where u.active = 1 order by u.name";

        var once = Format(sql);
        var twice = Format(once);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Lays_select_columns_one_per_line_indented()
    {
        var result = Format("select a, b from t");

        Assert.Equal("SELECT\n    a,\n    b\nFROM t", result);
    }

    [Fact]
    public void Breaks_where_conjunctions_indented()
    {
        var result = Format("select a from t where x = 1 and y = 2");

        Assert.Equal("SELECT\n    a\nFROM t\nWHERE x = 1\n    AND y = 2", result);
    }

    [Fact]
    public void Respects_indent_size()
    {
        var result = Format("select a, b from t", SqlFormatOptions.Default with { IndentSize = 2 });

        Assert.Equal("SELECT\n  a,\n  b\nFROM t", result);
    }

    [Fact]
    public void Puts_join_on_its_own_indented_line()
    {
        var lines = Format("select u.id from users u left join orders o on o.id = u.id").Split('\n');

        Assert.Contains(lines, l => l == "    LEFT JOIN orders o ON o.id = u.id");
    }

    [Fact]
    public void Indents_a_subquery_in_from()
    {
        var result = Format("select a from (select id from t) x");

        Assert.Equal("SELECT\n    a\nFROM (\n    SELECT\n        id\n    FROM t\n) x", result);
    }
}
