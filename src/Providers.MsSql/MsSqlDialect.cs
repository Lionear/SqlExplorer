using Lionear.SqlExplorer.Sdk;

namespace Lionear.SqlExplorer.Providers.MsSql;

public sealed class MsSqlDialect : ISqlDialect
{
    public DatabaseKind Kind => DatabaseKind.SqlServer;

    public IReadOnlySet<string> Keywords { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "GROUP", "BY", "ORDER", "HAVING", "OFFSET", "FETCH", "NEXT", "ROWS", "ONLY",
        "JOIN", "LEFT", "RIGHT", "INNER", "OUTER", "FULL", "CROSS", "ON", "TOP",
        "AND", "OR", "NOT", "IN", "IS", "NULL", "LIKE", "BETWEEN",
        "AS", "DISTINCT", "UNION", "ALL", "INSERT", "INTO", "VALUES", "UPDATE",
        "SET", "DELETE", "MERGE", "WITH", "CASE", "WHEN", "THEN", "ELSE", "END",
        "ASC", "DESC", "COUNT", "SUM", "AVG", "MIN", "MAX"
    };

    // SQL Server quotes identifiers with brackets; escape an embedded ] by doubling it.
    public string QuoteIdentifier(string identifier) =>
        $"[{identifier.Replace("]", "]]")}]";

    // SQL Server's OFFSET/FETCH requires an ORDER BY; fall back to (SELECT NULL) for an unordered page.
    public string Paginate(string sql, int limit, int offset, string? orderBy = null) =>
        $"{sql}\nORDER BY {orderBy ?? "(SELECT NULL)"}\nOFFSET {offset} ROWS FETCH NEXT {limit} ROWS ONLY";
}
