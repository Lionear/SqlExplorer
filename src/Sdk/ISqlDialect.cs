namespace Lionear.SqlExplorer.Sdk;

/// <summary>
/// Dialect-specific SQL rules: identifier quoting, paging syntax and the keyword
/// set the formatter uses. Reached only via <see cref="IDbProvider.Dialect"/>, so it
/// needs no identity of its own.
/// </summary>
public interface ISqlDialect
{
    IReadOnlySet<string> Keywords { get; }

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
}
