using SqlExplorer.Sdk;

namespace SqlExplorer.Core.Tests.Formatting;

/// <summary>A minimal <see cref="ISqlDialect"/> for formatter tests: ANSI double-quoted identifiers and
/// a small keyword set. Keeps the formatter tests independent of any real provider.</summary>
internal sealed class FakeDialect : ISqlDialect
{
    public IReadOnlySet<string> Keywords { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "GROUP", "BY", "ORDER", "HAVING", "LIMIT", "OFFSET",
        "JOIN", "LEFT", "RIGHT", "INNER", "OUTER", "FULL", "CROSS", "ON", "AND", "OR",
        "AS", "INSERT", "INTO", "UPDATE", "DELETE", "SET", "VALUES", "UNION"
    };

    public string QuoteIdentifier(string identifier) => $"\"{identifier}\"";

    public string QualifyName(string? database, string? schema, string table) => QuoteIdentifier(table);

    public string Paginate(string sql, int limit, int offset, string? orderBy = null) => sql;
}
