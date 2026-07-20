using SqlExplorer.Sdk;

namespace SqlExplorer.Providers.MySql;

public sealed class MySqlDialect : ISqlDialect
{
    public IReadOnlySet<string> Keywords { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "GROUP", "BY", "ORDER", "HAVING", "LIMIT", "OFFSET",
        "JOIN", "LEFT", "RIGHT", "INNER", "OUTER", "FULL", "CROSS", "ON", "USING",
        "AND", "OR", "NOT", "IN", "IS", "NULL", "LIKE", "REGEXP", "BETWEEN",
        "AS", "DISTINCT", "UNION", "ALL", "INSERT", "INTO", "VALUES", "UPDATE",
        "SET", "DELETE", "REPLACE", "WITH", "CASE", "WHEN", "THEN", "ELSE", "END",
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
        new("group_concat", "group_concat(expression [separator ...])", "Concatenate group values."),
        new("coalesce", "coalesce(value [, ...])", "First non-null argument."),
        new("ifnull", "ifnull(value, fallback)", "Fallback when the value is NULL."),
        new("nullif", "nullif(value1, value2)", "NULL when the two are equal, else value1."),
        new("greatest", "greatest(value [, ...])", "Largest of the arguments."),
        new("least", "least(value [, ...])", "Smallest of the arguments."),
        new("lower", "lower(string)", "Lower-case the string."),
        new("upper", "upper(string)", "Upper-case the string."),
        new("length", "length(string)", "Length in bytes."),
        new("char_length", "char_length(string)", "Length in characters."),
        new("trim", "trim(string)", "Strip leading/trailing spaces."),
        new("substring", "substring(string, start [, length])", "Extract a substring."),
        new("concat", "concat(value [, ...])", "Concatenate the arguments."),
        new("concat_ws", "concat_ws(separator, value [, ...])", "Concatenate with a separator."),
        new("replace", "replace(string, from, to)", "Replace all occurrences."),
        new("locate", "locate(substr, string)", "Position of a substring (1-based)."),
        new("abs", "abs(number)", "Absolute value."),
        new("round", "round(number [, decimals])", "Round to the given precision."),
        new("ceil", "ceil(number)", "Round up."),
        new("floor", "floor(number)", "Round down."),
        new("now", "now()", "Current date and time."),
        new("curdate", "curdate()", "Current date."),
        new("date_format", "date_format(date, format)", "Format a date as text."),
        new("datediff", "datediff(end, start)", "Days between two dates."),
        new("cast", "cast(expression as type)", "Convert to another type.")
    ];

    // MySQL/MariaDB quote identifiers with backticks; escape an embedded backtick by doubling it.
    public string QuoteIdentifier(string identifier) =>
        $"`{identifier.Replace("`", "``")}`";

    // MySQL's "database" IS the schema: two-part `db`.`table` (the tree exposes the db; schema is unused).
    // Naming the db explicitly also makes generated SQL resolve across databases on one connection.
    public string QualifyName(string? database, string? schema, string table) =>
        string.IsNullOrEmpty(database)
            ? QuoteIdentifier(table)
            : $"{QuoteIdentifier(database)}.{QuoteIdentifier(table)}";

    public string Paginate(string sql, int limit, int offset, string? orderBy = null)
    {
        var order = orderBy is null ? string.Empty : $"\nORDER BY {orderBy}";
        return $"{sql}{order}\nLIMIT {limit} OFFSET {offset}";
    }
}
