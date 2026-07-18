namespace SqlExplorer.Sdk.Formatting;

/// <summary>
/// Pretty-prints SQL. The dialect supplies the keyword set and identifier quoting so a single generic
/// formatter serves every engine; a provider may return its own dialect-specialised implementation via
/// <see cref="IDbProvider.Formatter"/>, with the host's generic formatter as the fallback.
/// </summary>
public interface ISqlFormatter
{
    string Format(string sql, ISqlDialect dialect, SqlFormatOptions options);
}
