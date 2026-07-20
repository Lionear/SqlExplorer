using SqlExplorer.Core.Sql;
using SqlExplorer.Providers.MsSql;
using SqlExplorer.Providers.Postgres;
using SqlExplorer.Sdk;

namespace SqlExplorer.Core.Tests.Sql;

public class QueryPagingTests
{
    [Fact]
    public void Recognises_a_plain_unbounded_select()
    {
        Assert.True(QueryPaging.TryGetPageableSelect("SELECT * FROM big", out var stmt, out var ordered));
        Assert.Equal("SELECT * FROM big", stmt);
        Assert.False(ordered);
    }

    [Fact]
    public void Reports_a_top_level_order_by()
    {
        Assert.True(QueryPaging.TryGetPageableSelect("SELECT * FROM big ORDER BY id", out _, out var ordered));
        Assert.True(ordered);
    }

    [Fact]
    public void Pages_a_cte_that_ends_in_a_select()
    {
        Assert.True(QueryPaging.TryGetPageableSelect("WITH c AS (SELECT 1 x) SELECT * FROM c", out _, out _));
    }

    [Fact]
    public void Is_case_insensitive()
    {
        Assert.True(QueryPaging.TryGetPageableSelect("select * from big", out _, out _));
    }

    [Theory]
    [InlineData("SELECT TOP 10 * FROM big")]
    [InlineData("SELECT * FROM big LIMIT 5")]
    [InlineData("SELECT * FROM big OFFSET 5 ROWS FETCH NEXT 5 ROWS ONLY")]
    [InlineData("SELECT * INTO copy FROM big")]
    [InlineData("SELECT * FROM big FOR UPDATE")]
    [InlineData("UPDATE big SET x = 1")]
    [InlineData("INSERT INTO big VALUES (1)")]
    [InlineData("WITH c AS (SELECT 1) DELETE FROM big")]     // CTE driving DML
    [InlineData("SELECT * FROM a; SELECT * FROM b")]          // multi-statement
    public void Rejects_bounded_special_or_non_select_statements(string sql) =>
        Assert.False(QueryPaging.TryGetPageableSelect(sql, out _, out _));

    [Fact] // A blocker keyword hidden in a string / quoted identifier / subquery must not reject a pageable query.
    public void Ignores_keywords_inside_strings_identifiers_and_subqueries()
    {
        Assert.True(QueryPaging.TryGetPageableSelect("SELECT 'top 5' AS s FROM big", out _, out _));
        Assert.True(QueryPaging.TryGetPageableSelect("SELECT \"limit\" FROM big", out _, out _));
        // ORDER BY inside a subquery is not the outer query's ordering.
        Assert.True(QueryPaging.TryGetPageableSelect("SELECT * FROM (SELECT * FROM t ORDER BY id) s", out _, out var ordered));
        Assert.False(ordered);
    }

    [Fact]
    public void Empty_or_blank_is_not_pageable()
    {
        Assert.False(QueryPaging.TryGetPageableSelect("", out _, out _));
        Assert.False(QueryPaging.TryGetPageableSelect("   ", out _, out _));
    }

    // ---- dialect PageQuery -------------------------------------------------------------------------

    [Fact]
    public void Postgres_pages_with_limit_offset_regardless_of_ordering()
    {
        ISqlDialect pg = new PostgresDialect(); // PageQuery is a default-interface member
        Assert.Equal("SELECT * FROM big\nLIMIT 500 OFFSET 1000", pg.PageQuery("SELECT * FROM big", 500, 1000));
        // ORDER BY already present → LIMIT/OFFSET still appends validly.
        Assert.Contains("LIMIT 500 OFFSET 0", pg.PageQuery("SELECT * FROM big ORDER BY id", 500, 0, alreadyOrdered: true));
    }

    [Fact]
    public void SqlServer_appends_offset_fetch_to_an_existing_order_by_or_adds_a_null_sort()
    {
        var ms = new MsSqlDialect();

        var unordered = ms.PageQuery("SELECT * FROM big", 500, 0);
        Assert.Contains("ORDER BY (SELECT NULL)", unordered);
        Assert.Contains("OFFSET 0 ROWS FETCH NEXT 500 ROWS ONLY", unordered);

        var ordered = ms.PageQuery("SELECT * FROM big ORDER BY id", 500, 500, alreadyOrdered: true);
        Assert.DoesNotContain("(SELECT NULL)", ordered); // no second ORDER BY
        Assert.EndsWith("ORDER BY id\nOFFSET 500 ROWS FETCH NEXT 500 ROWS ONLY", ordered);
    }
}
