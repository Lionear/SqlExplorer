using System.Linq;
using SqlExplorer.Core.Completion;

namespace SqlExplorer.Core.Tests.Completion;

public class SqlScopeAnalyzerTests
{
    // Analyze at the caret marked by '|' in the query (the marker is stripped before analysis).
    private static SqlScope At(string queryWithCaret)
    {
        var caret = queryWithCaret.IndexOf('|');
        var sql = queryWithCaret.Remove(caret, 1);
        return SqlScopeAnalyzer.Analyze(sql, caret);
    }

    // ---- clause detection --------------------------------------------------------------------------

    [Theory]
    [InlineData("SELECT | FROM users u", SqlClause.Select)]
    [InlineData("SELECT * FROM |", SqlClause.From)]
    [InlineData("SELECT * FROM a JOIN |", SqlClause.From)]
    [InlineData("SELECT * FROM a JOIN b ON |", SqlClause.On)]
    [InlineData("SELECT * FROM users WHERE |", SqlClause.Where)]
    [InlineData("SELECT * FROM users GROUP BY |", SqlClause.GroupBy)]
    [InlineData("SELECT * FROM users ORDER BY |", SqlClause.OrderBy)]
    [InlineData("SELECT * FROM users GROUP BY id HAVING |", SqlClause.Having)]
    public void Detects_the_clause_at_the_caret(string query, SqlClause expected) =>
        Assert.Equal(expected, At(query).Clause);

    // ---- sources -----------------------------------------------------------------------------------

    [Fact]
    public void Resolves_a_simple_aliased_table()
    {
        var scope = At("SELECT | FROM users u");

        var source = Assert.Single(scope.Sources);
        Assert.Equal("u", source.Alias);
        Assert.Equal("users", source.Table);
    }

    [Fact]
    public void An_unaliased_table_is_its_own_alias()
    {
        var source = Assert.Single(At("SELECT | FROM users").Sources);
        Assert.Equal("users", source.Alias);
        Assert.Equal("users", source.Table);
    }

    [Fact]
    public void Resolves_multiple_sources_across_a_join()
    {
        var scope = At("SELECT | FROM users u JOIN orders o ON u.id = o.user_id");

        Assert.Collection(scope.Sources.OrderBy(s => s.Alias),
            o => { Assert.Equal("o", o.Alias); Assert.Equal("orders", o.Table); },
            u => { Assert.Equal("u", u.Alias); Assert.Equal("users", u.Table); });
    }

    [Fact] // The ON condition's columns must not be mistaken for sources.
    public void Ignores_the_on_condition_when_listing_sources()
    {
        var scope = At("SELECT * FROM users u JOIN orders o ON u.id = o.user_id WHERE |");
        Assert.Equal(2, scope.Sources.Count);
        Assert.DoesNotContain(scope.Sources, s => s.Alias is "id" or "user_id");
    }

    [Fact]
    public void Schema_qualified_table_uses_its_last_part()
    {
        var source = Assert.Single(At("SELECT | FROM public.users u").Sources);
        Assert.Equal("users", source.Table);
        Assert.Equal("u", source.Alias);
    }

    // ---- CTEs --------------------------------------------------------------------------------------

    [Fact]
    public void Resolves_a_cte_reference_to_its_inferred_columns()
    {
        var scope = At("WITH cte AS (SELECT a, b FROM t) SELECT | FROM cte c");

        Assert.Contains("cte", scope.CteNames);
        var source = Assert.Single(scope.Sources);
        Assert.Equal("c", source.Alias);
        Assert.Null(source.Table); // resolved through the CTE, not a base table
        Assert.Equal(["a", "b"], source.Columns);
    }

    [Fact]
    public void Honours_an_explicit_cte_column_list()
    {
        var scope = At("WITH cte (x, y) AS (SELECT a, b FROM t) SELECT | FROM cte");
        var source = Assert.Single(scope.Sources);
        Assert.Equal(["x", "y"], source.Columns);
    }

    [Fact] // SELECT * can't be enumerated without the schema → columns unknown (null), so the provider falls back.
    public void A_star_cte_body_yields_unknown_columns()
    {
        var scope = At("WITH cte AS (SELECT * FROM t) SELECT | FROM cte c");
        var source = Assert.Single(scope.Sources);
        Assert.Null(source.Columns);
    }

    [Fact]
    public void Cte_names_are_offered_in_a_from_position()
    {
        var scope = At("WITH a AS (SELECT 1 x), b AS (SELECT 2 y) SELECT * FROM |");
        Assert.Equal(SqlClause.From, scope.Clause);
        Assert.Contains("a", scope.CteNames);
        Assert.Contains("b", scope.CteNames);
    }

    [Fact]
    public void Infers_cte_columns_through_aliased_select_items()
    {
        var scope = At("WITH cte AS (SELECT t.a AS first, count(*) AS n FROM t) SELECT | FROM cte");
        var source = Assert.Single(scope.Sources);
        Assert.Equal(["first", "n"], source.Columns);
    }

    // ---- derived tables ----------------------------------------------------------------------------

    [Fact]
    public void Resolves_a_derived_table_to_its_select_list()
    {
        var scope = At("SELECT | FROM (SELECT x, y FROM t) d");
        var source = Assert.Single(scope.Sources);
        Assert.Equal("d", source.Alias);
        Assert.Null(source.Table);
        Assert.Equal(["x", "y"], source.Columns);
    }

    // ---- subquery scope + statement boundary -------------------------------------------------------

    [Fact] // Inside a subquery the active scope is the subquery, not the outer query.
    public void Uses_the_innermost_subquery_scope_at_the_caret()
    {
        var scope = At("SELECT * FROM t WHERE id IN (SELECT | FROM other o)");

        var source = Assert.Single(scope.Sources);
        Assert.Equal("o", source.Alias);
        Assert.Equal("other", source.Table);
        Assert.Equal(SqlClause.Select, scope.Clause);
    }

    [Fact]
    public void Does_not_leak_sources_across_statement_boundaries()
    {
        var scope = At("SELECT * FROM a; SELECT | FROM b");

        var source = Assert.Single(scope.Sources);
        Assert.Equal("b", source.Table);
    }

    [Fact]
    public void Empty_input_is_an_empty_scope()
    {
        Assert.Same(SqlScope.Empty, SqlScopeAnalyzer.Analyze("", 0));
        Assert.Empty(SqlScopeAnalyzer.Analyze("   ", 3).Sources);
    }
}
