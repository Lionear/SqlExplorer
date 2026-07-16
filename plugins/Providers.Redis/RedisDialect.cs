using SqlExplorer.Sdk;

namespace SqlExplorer.Providers.Redis;

/// <summary>
/// Redis has no SQL dialect — this exists only to satisfy the <see cref="IDbProvider.Dialect"/> contract
/// and to shape the "browse key" text the host generates when a key node is double-clicked. That
/// generated text (<c>SELECT * FROM "key" LIMIT n OFFSET m</c>) is parsed back by
/// <see cref="RedisProvider"/> (<see cref="RedisQuery"/>) into a live <c>TYPE</c> lookup followed by the
/// matching typed command (<c>GET</c>/<c>HGETALL</c>/<c>LRANGE</c>/<c>SMEMBERS</c>/<c>ZRANGE</c>) — the
/// same "blessed pseudo-SQL, recognised only by the provider's own parser" convention as MongoDB's dialect.
/// </summary>
public sealed class RedisDialect : ISqlDialect
{
    public IReadOnlySet<string> Keywords { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // Key names can contain almost anything; double-quote for the generated browse text, same as Mongo.
    // RedisQuery strips the quotes again before treating it as a key name.
    public string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    // No database.schema.key qualification: a command already runs against the DB index chosen in the
    // switcher (ConnectionProfile.Database), so only the bare key matters.
    public string QualifyName(string? database, string? schema, string table) => QuoteIdentifier(table);

    public string Paginate(string sql, int limit, int offset, string? orderBy = null) =>
        $"{sql} LIMIT {limit} OFFSET {offset}";
}
