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

    [Theory]
    [InlineData("SELECT * FROM [dbo].[Donations];")]
    [InlineData("SELECT * FROM [dbo].[Donations] ;")]
    [InlineData("SELECT * FROM [dbo].[Donations];  \n")]
    [InlineData("SELECT * FROM [dbo].[Donations];;")]
    public void A_terminating_semicolon_is_dropped_because_paging_appends_to_the_statement(string sql)
    {
        // Paging appends (ORDER BY … OFFSET … FETCH, LIMIT …), so a statement that still ends in a semicolon
        // produces "SELECT …; OFFSET 0 ROWS" — a syntax error on every engine. Typing the semicolon you would
        // type anywhere else must not break the page bar.
        Assert.True(QueryPaging.TryGetPageableSelect(sql, out var stmt, out _));
        Assert.Equal("SELECT * FROM [dbo].[Donations]", stmt);
    }

    [Fact]
    public void A_semicolon_terminated_select_pages_into_runnable_sql()
    {
        Assert.True(QueryPaging.TryGetPageableSelect("SELECT * FROM Donations;", out var stmt, out var ordered));

        var sqlServer = new MsSqlDialect().PageQuery(stmt, 200, 0, ordered);
        Assert.Equal(
            "SELECT * FROM Donations\nORDER BY (SELECT NULL)\nOFFSET 0 ROWS FETCH NEXT 200 ROWS ONLY",
            sqlServer);
        Assert.DoesNotContain(";", sqlServer);
    }

    [Fact]
    public void A_semicolon_after_an_order_by_keeps_the_ordered_flag()
    {
        Assert.True(QueryPaging.TryGetPageableSelect("SELECT * FROM t ORDER BY id;", out var stmt, out var ordered));
        Assert.True(ordered);
        Assert.Equal("SELECT * FROM t ORDER BY id", stmt);
        Assert.Equal(
            "SELECT * FROM t ORDER BY id\nOFFSET 0 ROWS FETCH NEXT 200 ROWS ONLY",
            new MsSqlDialect().PageQuery(stmt, 200, 0, ordered));
    }

    [Fact]
    public void A_semicolon_inside_a_literal_is_not_a_terminator()
    {
        Assert.True(QueryPaging.TryGetPageableSelect("SELECT * FROM t WHERE s = ';'", out var stmt, out _));
        Assert.Equal("SELECT * FROM t WHERE s = ';'", stmt);
    }

    // --- Capping a script's SELECTs (a script can't be paged, but it shouldn't run wide open either) ---

    private static readonly ISqlDialect SqlServer = new MsSqlDialect();

    [Fact]
    public void Every_unbounded_select_in_a_script_is_bounded()
    {
        var sql = "SELECT * FROM [dbo].[Character];\nSELECT * FROM [dbo].[Corporation];";

        var capped = QueryPaging.CapPageableStatements(sql, SqlServer, 200, out var count);

        Assert.Equal(2, count);
        Assert.Equal(
            "SELECT * FROM [dbo].[Character]\nORDER BY (SELECT NULL)\nOFFSET 0 ROWS FETCH NEXT 200 ROWS ONLY;\n" +
            "SELECT * FROM [dbo].[Corporation]\nORDER BY (SELECT NULL)\nOFFSET 0 ROWS FETCH NEXT 200 ROWS ONLY;",
            capped);
    }

    [Fact]
    public void A_statement_that_already_bounds_itself_is_left_alone()
    {
        var sql = "SELECT TOP 10 * FROM a;\nSELECT * FROM b;";

        var capped = QueryPaging.CapPageableStatements(sql, SqlServer, 200, out var count);

        Assert.Equal(1, count);
        Assert.Contains("SELECT TOP 10 * FROM a;", capped);
        Assert.DoesNotContain("TOP 10 * FROM a\nORDER BY", capped);
    }

    [Fact]
    public void Non_selects_run_exactly_as_written()
    {
        // Rewriting a statement we chose not to bound would be a change nobody asked for — and an UPDATE
        // with an OFFSET clause bolted on is not the same statement.
        var sql = "UPDATE a SET x = 1;\nDELETE FROM b;\nINSERT INTO c VALUES (1);";

        var capped = QueryPaging.CapPageableStatements(sql, SqlServer, 200, out var count);

        Assert.Equal(0, count);
        Assert.Equal(sql, capped);
    }

    [Fact]
    public void A_script_with_nothing_to_cap_comes_back_untouched()
    {
        var sql = "-- just a comment\nUPDATE a SET x = 1;";
        Assert.Equal(sql, QueryPaging.CapPageableStatements(sql, SqlServer, 200, out var count));
        Assert.Equal(0, count);
    }

    [Fact]
    public void An_order_by_in_a_script_is_kept_and_paged_without_a_second_one()
    {
        var capped = QueryPaging.CapPageableStatements(
            "SELECT * FROM a ORDER BY id;\nSELECT * FROM b;", SqlServer, 50, out var count);

        Assert.Equal(2, count);
        Assert.Contains("SELECT * FROM a ORDER BY id\nOFFSET 0 ROWS FETCH NEXT 50 ROWS ONLY;", capped);
        Assert.DoesNotContain("ORDER BY id\nORDER BY", capped);
    }

    [Fact]
    public void A_limit_of_zero_disables_capping_entirely()
    {
        var sql = "SELECT * FROM a;\nSELECT * FROM b;";
        Assert.Equal(sql, QueryPaging.CapPageableStatements(sql, SqlServer, 0, out var count));
        Assert.Equal(0, count);
    }

    // --- A script that is nothing but SELECTs pages per result tab ---

    [Fact]
    public void An_all_select_script_is_pageable_per_statement()
    {
        Assert.True(QueryPaging.TryGetPageableScript(
            "SELECT * FROM [dbo].[Character];\nSELECT * FROM [dbo].[Corporation] ORDER BY id;",
            out var statements));

        Assert.Equal(2, statements.Count);
        Assert.Equal("SELECT * FROM [dbo].[Character]", statements[0].Sql);
        Assert.False(statements[0].Ordered);
        Assert.Equal("SELECT * FROM [dbo].[Corporation] ORDER BY id", statements[1].Sql);
        Assert.True(statements[1].Ordered);
    }

    [Theory]
    [InlineData("SELECT * FROM a;\nUPDATE b SET x = 1;")]          // mixed: result sets stop lining up
    [InlineData("SELECT * FROM a;\nSELECT TOP 10 * FROM b;")]      // one already bounds itself
    [InlineData("SELECT * FROM a;")]                                // single statement = the other path
    [InlineData("")]
    public void A_script_that_is_not_all_pageable_selects_gets_no_page_bar(string sql) =>
        Assert.False(QueryPaging.TryGetPageableScript(sql, out _));

    [Fact]
    public void A_mixed_script_still_gets_its_selects_bounded()
    {
        // The two features are complementary: no page bar, but not unbounded either.
        const string sql = "SELECT * FROM a;\nUPDATE b SET x = 1;";

        Assert.False(QueryPaging.TryGetPageableScript(sql, out _));
        QueryPaging.CapPageableStatements(sql, SqlServer, 200, out var capped);
        Assert.Equal(1, capped);
    }

    [Fact]
    public void Postgres_gets_its_own_limit_syntax()
    {
        var capped = QueryPaging.CapPageableStatements(
            "SELECT * FROM a;\nSELECT * FROM b;", new PostgresDialect(), 200, out var count);

        Assert.Equal(2, count);
        Assert.Contains("LIMIT 200", capped);
        Assert.DoesNotContain("FETCH NEXT", capped);
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
