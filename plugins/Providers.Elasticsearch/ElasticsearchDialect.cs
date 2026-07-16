using SqlExplorer.Sdk;

namespace SqlExplorer.Providers.Elasticsearch;

/// <summary>
/// Elasticsearch has no SQL dialect — this exists only to satisfy the <see cref="IDbProvider.Dialect"/>
/// contract and to shape the "browse index" text the host generates when an index node is opened. That
/// generated text (<c>SELECT * FROM &lt;index&gt; LIMIT n OFFSET m</c>) is parsed back by
/// <see cref="ElasticsearchProvider"/> (<see cref="ElasticQuery"/>) into a native <c>_search</c> with
/// <c>match_all</c>, <c>size</c>/<c>from</c> paging and an optional <c>sort</c> — the same "blessed
/// pseudo-SQL, recognised only by the provider's own parser" convention as the MongoDB dialect.
/// </summary>
public sealed class ElasticsearchDialect : ISqlDialect
{
    public IReadOnlySet<string> Keywords { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // Index names are lowercase and can't contain most punctuation; double-quote for the generated browse
    // text. ElasticQuery's FROM-parser strips the quotes again before hitting the REST API.
    public string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    // No database.schema.index qualification: an index name is globally addressable in the REST path, so
    // only the bare index name matters.
    public string QualifyName(string? database, string? schema, string table) => QuoteIdentifier(table);

    public string Paginate(string sql, int limit, int offset, string? orderBy = null)
    {
        var order = string.IsNullOrEmpty(orderBy) ? string.Empty : $" ORDER BY {orderBy}";
        return $"{sql}{order} LIMIT {limit} OFFSET {offset}";
    }
}
