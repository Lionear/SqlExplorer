using SqlExplorer.Sdk;

namespace SqlExplorer.Providers.Postgres;

public sealed class PostgresDialect : ISqlDialect
{
    public IReadOnlySet<string> Keywords { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "GROUP", "BY", "ORDER", "HAVING", "LIMIT", "OFFSET",
        "JOIN", "LEFT", "RIGHT", "INNER", "OUTER", "FULL", "CROSS", "ON", "USING",
        "AND", "OR", "NOT", "IN", "IS", "NULL", "LIKE", "ILIKE", "BETWEEN",
        "AS", "DISTINCT", "UNION", "ALL", "INSERT", "INTO", "VALUES", "UPDATE",
        "SET", "DELETE", "RETURNING", "WITH", "CASE", "WHEN", "THEN", "ELSE", "END",
        "ASC", "DESC", "TRUE", "FALSE", "COUNT", "SUM", "AVG", "MIN", "MAX"
    };

    // A representative built-in catalogue offered by completion (SE-149 phase 2) — not exhaustive.
    public IReadOnlyList<SqlFunction> Functions { get; } =
    [
        new("count", "count(* | expression)", "Number of rows / non-null values."),
        new("sum", "sum(expression)", "Total of the values."),
        new("avg", "avg(expression)", "Arithmetic mean."),
        new("min", "min(expression)", "Smallest value."),
        new("max", "max(expression)", "Largest value."),
        new("string_agg", "string_agg(expression, delimiter)", "Concatenate values with a separator."),
        new("array_agg", "array_agg(expression)", "Aggregate values into an array."),
        new("coalesce", "coalesce(value [, ...])", "First non-null argument."),
        new("nullif", "nullif(value1, value2)", "NULL when the two are equal, else value1."),
        new("greatest", "greatest(value [, ...])", "Largest of the arguments."),
        new("least", "least(value [, ...])", "Smallest of the arguments."),
        new("lower", "lower(string)", "Lower-case the string."),
        new("upper", "upper(string)", "Upper-case the string."),
        new("length", "length(string)", "Number of characters."),
        new("trim", "trim([chars from] string)", "Strip leading/trailing characters."),
        new("substring", "substring(string from start [for count])", "Extract a substring."),
        new("replace", "replace(string, from, to)", "Replace all occurrences."),
        new("concat", "concat(value [, ...])", "Concatenate the arguments."),
        new("split_part", "split_part(string, delimiter, n)", "Nth field of a delimited string."),
        new("to_char", "to_char(value, format)", "Format a value as text."),
        new("abs", "abs(number)", "Absolute value."),
        new("round", "round(number [, decimals])", "Round to the given precision."),
        new("ceil", "ceil(number)", "Round up."),
        new("floor", "floor(number)", "Round down."),
        new("now", "now()", "Current transaction timestamp."),
        new("current_date", "current_date", "Today's date."),
        new("date_trunc", "date_trunc(field, source)", "Truncate a timestamp to precision."),
        new("extract", "extract(field from source)", "Get a subfield of a date/time."),
        new("cast", "cast(expression as type)", "Convert to another type.")
    ];

    public string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"")}\"";

    // Postgres can't reference another database from one connection, so the database is not part of a
    // qualified name — the connection is already scoped to one database. Two-part schema.table.
    public string QualifyName(string? database, string? schema, string table) =>
        string.IsNullOrEmpty(schema)
            ? QuoteIdentifier(table)
            : $"{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}";

    public string Paginate(string sql, int limit, int offset, string? orderBy = null)
    {
        var order = orderBy is null ? string.Empty : $"\nORDER BY {orderBy}";
        return $"{sql}{order}\nLIMIT {limit} OFFSET {offset}";
    }
}
