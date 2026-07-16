using SqlExplorer.Sdk;

namespace SqlExplorer.Providers.MongoDb;

/// <summary>
/// MongoDB has no SQL dialect — this exists only to satisfy the <see cref="IDbProvider.Dialect"/>
/// contract and to shape the "browse collection" text the host generates when a collection node is
/// opened. That generated text (<c>SELECT * FROM &lt;collection&gt; LIMIT n OFFSET m</c>) is parsed
/// back by <see cref="MongoDbProvider"/> into a native <c>find</c>; see the query notes there.
/// </summary>
public sealed class MongoDbDialect : ISqlDialect
{
    public IReadOnlySet<string> Keywords { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // Collection names can contain almost anything except NUL and '$'; double-quote for the generated
    // browse text. The provider's FROM-parser strips quotes/backticks again before hitting the driver.
    public string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    // No database.schema.table qualification in Mongo: a query already runs against a chosen database
    // (ConnectionProfile.Database), so only the bare collection name matters.
    public string QualifyName(string? database, string? schema, string table) => QuoteIdentifier(table);

    public string Paginate(string sql, int limit, int offset, string? orderBy = null)
    {
        var order = string.IsNullOrEmpty(orderBy) ? string.Empty : $" ORDER BY {orderBy}";
        return $"{sql}{order} LIMIT {limit} OFFSET {offset}";
    }
}
