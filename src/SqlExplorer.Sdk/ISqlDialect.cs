namespace SqlExplorer.Sdk;

/// <summary>
/// Dialect-specific SQL rules: identifier quoting, paging syntax and the keyword
/// set the formatter uses. Reached only via <see cref="IDbProvider.Dialect"/>, so it
/// needs no identity of its own.
/// </summary>
public interface ISqlDialect
{
    IReadOnlySet<string> Keywords { get; }

    /// <summary>The dialect's built-in functions, offered by completion in expression positions with their
    /// signature (SE-149 phase 2). Default empty — a dialect that declares none, or a plugin built against an
    /// older host, simply contributes no function suggestions.</summary>
    IReadOnlyList<SqlFunction> Functions => [];

    string QuoteIdentifier(string identifier);

    /// <summary>
    /// Build a fully-qualified relation name for generated SQL that will run WITHOUT a database
    /// context (e.g. a free query tab). Dialects that can reference another database from one
    /// connection include it (SQL Server's three-part <c>[db].[schema].[table]</c>); dialects that
    /// cannot (Postgres) omit the database. <paramref name="database"/>/<paramref name="schema"/>
    /// are null when the engine has no such layer.
    /// </summary>
    string QualifyName(string? database, string? schema, string table);

    /// <summary>
    /// Wrap <paramref name="sql"/> with this dialect's paging syntax. When <paramref name="orderBy"/>
    /// is given (already dialect-quoted, e.g. <c>"name" DESC</c>) it becomes the ORDER BY clause —
    /// required for engines whose paging demands an ordering (SQL Server's OFFSET/FETCH).
    /// </summary>
    string Paginate(string sql, int limit, int offset, string? orderBy = null);

    /// <summary>
    /// Page a complete, standalone <c>SELECT</c> for query-result paging (SE-178) — unlike <see cref="Paginate"/>,
    /// <paramref name="sql"/> may already carry its own <c>ORDER BY</c>. <paramref name="alreadyOrdered"/> says so,
    /// which only matters where paging demands an ordering (SQL Server appends <c>OFFSET/FETCH</c> to the existing
    /// <c>ORDER BY</c> instead of adding a second one). The default suits <c>LIMIT</c>/<c>OFFSET</c> dialects,
    /// where appending after an optional ORDER BY is always valid; SQL-Server-like dialects override it.
    /// </summary>
    string PageQuery(string sql, int limit, int offset, bool alreadyOrdered = false) => Paginate(sql, limit, offset);
}
