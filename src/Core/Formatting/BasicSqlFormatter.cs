using System.Text;
using Lionear.SqlExplorer.Sdk;

namespace Lionear.SqlExplorer.Core.Formatting;

/// <summary>
/// Dialect-aware token formatter: normalises keyword casing and breaks major
/// clauses onto their own lines. Deliberately simple — good enough for the spike
/// and a drop-in seam for per-dialect parsers later (see Notes.md §6).
/// </summary>
public sealed class BasicSqlFormatter : ISqlFormatter
{
    private static readonly HashSet<string> ClauseStarters = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "GROUP", "ORDER", "HAVING", "LIMIT", "OFFSET",
        "UNION", "INSERT", "UPDATE", "DELETE", "SET", "VALUES",
        "JOIN", "LEFT", "RIGHT", "INNER", "OUTER", "FULL", "CROSS"
    };

    private static readonly HashSet<string> JoinPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "LEFT", "RIGHT", "INNER", "OUTER", "FULL", "CROSS"
    };

    public string Format(string sql, ISqlDialect dialect, SqlFormatOptions options)
    {
        var builder = new StringBuilder();
        string? previous = null;
        var (openQuote, closeQuote) = IdentifierQuotes(dialect);

        foreach (var token in Tokenize(sql, openQuote, closeQuote))
        {
            var isWord = char.IsLetterOrDigit(token[0]) || token[0] == '_';
            var rendered = isWord && dialect.Keywords.Contains(token)
                ? ApplyCasing(token, options.KeywordCasing)
                : token;

            var startsClause = isWord
                && ClauseStarters.Contains(token)
                && !(IsJoinKeyword(token) && previous is not null && JoinPrefixes.Contains(previous));

            if (builder.Length == 0)
            {
                builder.Append(rendered);
            }
            else if (startsClause)
            {
                builder.Append('\n').Append(rendered);
            }
            else if (token is "," or ";" or ")" or ".")
            {
                builder.Append(rendered);
            }
            else if (previous is "(" or ".")
            {
                builder.Append(rendered);
            }
            else
            {
                builder.Append(' ').Append(rendered);
            }

            previous = token;
        }

        return builder.ToString();
    }

    private static bool IsJoinKeyword(string token) =>
        string.Equals(token, "JOIN", StringComparison.OrdinalIgnoreCase);

    private static string ApplyCasing(string token, KeywordCasing casing) => casing switch
    {
        KeywordCasing.Upper => token.ToUpperInvariant(),
        KeywordCasing.Lower => token.ToLowerInvariant(),
        _ => token
    };

    // The dialect's identifier delimiters, derived from how it quotes a sample name:
    // "x" (Postgres/SQLite), `x` (MySQL) or [x] (SQL Server).
    private static (char Open, char Close) IdentifierQuotes(ISqlDialect dialect)
    {
        var quoted = dialect.QuoteIdentifier("x");
        return quoted.Length >= 2 ? (quoted[0], quoted[^1]) : ('"', '"');
    }

    private static IEnumerable<string> Tokenize(string sql, char idOpen, char idClose)
    {
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            // String literal ('...') — preserved verbatim, doubled-quote escapes honoured.
            if (c == '\'')
            {
                yield return ReadDelimited(sql, ref i, '\'', '\'');
                continue;
            }

            // Quoted identifier in the dialect's own delimiters ("...", `...` or [...]) — preserved
            // verbatim so a quoted keyword (e.g. `order`, [select]) is never re-cased or split.
            if (c == idOpen)
            {
                yield return ReadDelimited(sql, ref i, idOpen, idClose);
                continue;
            }

            if (char.IsLetterOrDigit(c) || c == '_')
            {
                var start = i;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_'))
                {
                    i++;
                }

                yield return sql[start..i];
                continue;
            }

            yield return sql[i++].ToString();
        }
    }

    // Read an open..close delimited region, honouring a doubled close char as an escape
    // (e.g. "" inside "...", `` inside `...`, ]] inside [...]). Advances i past the close.
    private static string ReadDelimited(string sql, ref int i, char open, char close)
    {
        var start = i++;
        while (i < sql.Length)
        {
            if (sql[i] == close)
            {
                if (i + 1 < sql.Length && sql[i + 1] == close)
                {
                    i += 2;
                    continue;
                }

                i++;
                break;
            }

            i++;
        }

        return sql[start..i];
    }
}
