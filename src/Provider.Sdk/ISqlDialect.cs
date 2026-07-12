namespace Lionear.SqlExplorer.Sdk;

/// <summary>
/// Dialect-specific SQL rules: identifier quoting, paging syntax and the keyword
/// set the formatter uses. One implementation per <see cref="DatabaseKind"/>.
/// </summary>
public interface ISqlDialect
{
    DatabaseKind Kind { get; }

    IReadOnlySet<string> Keywords { get; }

    string QuoteIdentifier(string identifier);

    /// <summary>
    /// Wrap <paramref name="sql"/> with this dialect's paging syntax. When <paramref name="orderBy"/>
    /// is given (already dialect-quoted, e.g. <c>"name" DESC</c>) it becomes the ORDER BY clause —
    /// required for engines whose paging demands an ordering (SQL Server's OFFSET/FETCH).
    /// </summary>
    string Paginate(string sql, int limit, int offset, string? orderBy = null);
}
