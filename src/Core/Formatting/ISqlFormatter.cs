using Lionear.SqlExplorer.Sdk;

namespace Lionear.SqlExplorer.Core.Formatting;

/// <summary>
/// Pretty-prints SQL. The dialect supplies the keyword set and casing rules so
/// the same formatter serves every engine; per-dialect parsers can be layered in
/// later where clause structures diverge (see Notes.md §6).
/// </summary>
public interface ISqlFormatter
{
    string Format(string sql, ISqlDialect dialect, SqlFormatOptions options);
}
