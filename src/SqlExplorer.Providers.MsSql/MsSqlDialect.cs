using SqlExplorer.Sdk;

namespace SqlExplorer.Providers.MsSql;

public sealed class MsSqlDialect : ISqlDialect
{
    public IReadOnlySet<string> Keywords { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "GROUP", "BY", "ORDER", "HAVING", "OFFSET", "FETCH", "NEXT", "ROWS", "ONLY",
        "JOIN", "LEFT", "RIGHT", "INNER", "OUTER", "FULL", "CROSS", "ON", "TOP",
        "AND", "OR", "NOT", "IN", "IS", "NULL", "LIKE", "BETWEEN",
        "AS", "DISTINCT", "UNION", "ALL", "INSERT", "INTO", "VALUES", "UPDATE",
        "SET", "DELETE", "MERGE", "WITH", "CASE", "WHEN", "THEN", "ELSE", "END",
        "ASC", "DESC", "COUNT", "SUM", "AVG", "MIN", "MAX"
    };

    // A representative built-in catalogue offered by completion (SE-149 phase 2) — not exhaustive.
    public IReadOnlyList<SqlFunction> Functions { get; } =
    [
        new("count", "COUNT(* | expression)", "Number of rows / non-null values."),
        new("sum", "SUM(expression)", "Total of the values."),
        new("avg", "AVG(expression)", "Arithmetic mean."),
        new("min", "MIN(expression)", "Smallest value."),
        new("max", "MAX(expression)", "Largest value."),
        new("string_agg", "STRING_AGG(expression, separator)", "Concatenate values with a separator."),
        new("coalesce", "COALESCE(value [, ...])", "First non-null argument."),
        new("isnull", "ISNULL(value, fallback)", "Fallback when the value is NULL."),
        new("nullif", "NULLIF(value1, value2)", "NULL when the two are equal, else value1."),
        new("iif", "IIF(condition, true_value, false_value)", "Inline conditional."),
        new("lower", "LOWER(string)", "Lower-case the string."),
        new("upper", "UPPER(string)", "Upper-case the string."),
        new("len", "LEN(string)", "Length excluding trailing spaces."),
        new("trim", "TRIM(string)", "Strip leading/trailing spaces."),
        new("substring", "SUBSTRING(string, start, length)", "Extract a substring."),
        new("replace", "REPLACE(string, from, to)", "Replace all occurrences."),
        new("concat", "CONCAT(value [, ...])", "Concatenate the arguments."),
        new("charindex", "CHARINDEX(substr, string)", "Position of a substring (1-based)."),
        new("left", "LEFT(string, n)", "Leftmost n characters."),
        new("right", "RIGHT(string, n)", "Rightmost n characters."),
        new("abs", "ABS(number)", "Absolute value."),
        new("round", "ROUND(number, decimals)", "Round to the given precision."),
        new("ceiling", "CEILING(number)", "Round up."),
        new("floor", "FLOOR(number)", "Round down."),
        new("getdate", "GETDATE()", "Current date and time."),
        new("dateadd", "DATEADD(part, number, date)", "Add an interval to a date."),
        new("datediff", "DATEDIFF(part, start, end)", "Difference between two dates."),
        new("datepart", "DATEPART(part, date)", "Get a subfield of a date."),
        new("format", "FORMAT(value, format)", "Format a value as text."),
        new("cast", "CAST(expression AS type)", "Convert to another type.")
    ];

    // SQL Server quotes identifiers with brackets; escape an embedded ] by doubling it.
    public string QuoteIdentifier(string identifier) =>
        $"[{identifier.Replace("]", "]]")}]";

    // Three-part [db].[schema].[table] so generated SQL resolves against the right catalog even from a
    // query tab connected to a different database. Omit any part the caller didn't supply.
    public string QualifyName(string? database, string? schema, string table)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrEmpty(database))
        {
            parts.Add(QuoteIdentifier(database));
        }

        if (!string.IsNullOrEmpty(schema))
        {
            parts.Add(QuoteIdentifier(schema));
        }

        parts.Add(QuoteIdentifier(table));
        return string.Join('.', parts);
    }

    // SQL Server's OFFSET/FETCH requires an ORDER BY; fall back to (SELECT NULL) for an unordered page.
    public string Paginate(string sql, int limit, int offset, string? orderBy = null) =>
        $"{sql}\nORDER BY {orderBy ?? "(SELECT NULL)"}\nOFFSET {offset} ROWS FETCH NEXT {limit} ROWS ONLY";
}
