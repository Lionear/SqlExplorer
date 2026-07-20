using SqlExplorer.Sdk;

namespace SqlExplorer.Providers.Sqlite;

public sealed class SqliteDialect : ISqlDialect
{
    public IReadOnlySet<string> Keywords { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "GROUP", "BY", "ORDER", "HAVING", "LIMIT", "OFFSET",
        "JOIN", "LEFT", "RIGHT", "INNER", "OUTER", "FULL", "CROSS", "ON", "USING",
        "AND", "OR", "NOT", "IN", "IS", "NULL", "LIKE", "GLOB", "BETWEEN",
        "AS", "DISTINCT", "UNION", "ALL", "INSERT", "INTO", "VALUES", "UPDATE",
        "SET", "DELETE", "RETURNING", "WITH", "CASE", "WHEN", "THEN", "ELSE", "END",
        "ASC", "DESC", "PRAGMA", "COUNT", "SUM", "AVG", "MIN", "MAX"
    };

    // A representative built-in catalogue offered by completion (SE-149 phase 2) — not exhaustive.
    public IReadOnlyList<SqlFunction> Functions { get; } =
    [
        new("count", "count(* | expression)", "Number of rows / non-null values."),
        new("sum", "sum(expression)", "Total of the values."),
        new("avg", "avg(expression)", "Arithmetic mean."),
        new("min", "min(expression)", "Smallest value."),
        new("max", "max(expression)", "Largest value."),
        new("total", "total(expression)", "Sum as a floating-point value."),
        new("group_concat", "group_concat(expression [, separator])", "Concatenate group values."),
        new("coalesce", "coalesce(value [, ...])", "First non-null argument."),
        new("ifnull", "ifnull(value, fallback)", "Fallback when the value is NULL."),
        new("nullif", "nullif(value1, value2)", "NULL when the two are equal, else value1."),
        new("lower", "lower(string)", "Lower-case the string."),
        new("upper", "upper(string)", "Upper-case the string."),
        new("length", "length(string)", "Number of characters."),
        new("trim", "trim(string [, chars])", "Strip leading/trailing characters."),
        new("substr", "substr(string, start [, length])", "Extract a substring."),
        new("replace", "replace(string, from, to)", "Replace all occurrences."),
        new("instr", "instr(string, substr)", "Position of a substring (1-based)."),
        new("abs", "abs(number)", "Absolute value."),
        new("round", "round(number [, decimals])", "Round to the given precision."),
        new("printf", "printf(format, ...)", "Format a string, C printf-style."),
        new("date", "date(timestring [, modifier ...])", "Compute a date."),
        new("time", "time(timestring [, modifier ...])", "Compute a time."),
        new("datetime", "datetime(timestring [, modifier ...])", "Compute a date and time."),
        new("strftime", "strftime(format, timestring [, ...])", "Format a date/time."),
        new("cast", "cast(expression as type)", "Convert to another type."),
        new("typeof", "typeof(expression)", "Datatype of the expression.")
    ];

    // SQLite accepts double-quoted identifiers (the SQL standard form); escape by doubling.
    public string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"")}\"";

    // SQLite has no database/schema layer on a normal connection: just the table.
    public string QualifyName(string? database, string? schema, string table) => QuoteIdentifier(table);

    public string Paginate(string sql, int limit, int offset, string? orderBy = null)
    {
        var order = orderBy is null ? string.Empty : $"\nORDER BY {orderBy}";
        return $"{sql}{order}\nLIMIT {limit} OFFSET {offset}";
    }
}
