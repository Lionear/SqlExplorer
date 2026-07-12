using Lionear.SqlExplorer.Sdk;

namespace Lionear.SqlExplorer.Providers.Postgres;

public sealed class PostgresDialect : ISqlDialect
{
    public DatabaseKind Kind => DatabaseKind.PostgreSql;

    public IReadOnlySet<string> Keywords { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "GROUP", "BY", "ORDER", "HAVING", "LIMIT", "OFFSET",
        "JOIN", "LEFT", "RIGHT", "INNER", "OUTER", "FULL", "CROSS", "ON", "USING",
        "AND", "OR", "NOT", "IN", "IS", "NULL", "LIKE", "ILIKE", "BETWEEN",
        "AS", "DISTINCT", "UNION", "ALL", "INSERT", "INTO", "VALUES", "UPDATE",
        "SET", "DELETE", "RETURNING", "WITH", "CASE", "WHEN", "THEN", "ELSE", "END",
        "ASC", "DESC", "TRUE", "FALSE", "COUNT", "SUM", "AVG", "MIN", "MAX"
    };

    public string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"")}\"";

    public string Paginate(string sql, int limit, int offset, string? orderBy = null)
    {
        var order = orderBy is null ? string.Empty : $"\nORDER BY {orderBy}";
        return $"{sql}{order}\nLIMIT {limit} OFFSET {offset}";
    }
}
