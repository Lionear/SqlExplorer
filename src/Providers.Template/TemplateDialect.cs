using Lionear.SqlExplorer.Sdk;

namespace Lionear.SqlExplorer.Providers.Template;

/// <summary>Minimal ANSI-ish dialect for the example provider — enough to satisfy the contract.</summary>
public sealed class TemplateDialect : ISqlDialect
{
    public IReadOnlySet<string> Keywords { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    public string QualifyName(string? database, string? schema, string table) => QuoteIdentifier(table);

    public string Paginate(string sql, int limit, int offset, string? orderBy = null)
    {
        var order = string.IsNullOrEmpty(orderBy) ? string.Empty : $" ORDER BY {orderBy}";
        return $"{sql}{order} LIMIT {limit} OFFSET {offset}";
    }
}
