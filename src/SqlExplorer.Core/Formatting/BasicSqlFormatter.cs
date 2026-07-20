using System.Text;
using SqlExplorer.Sdk;
using SqlExplorer.Sdk.Extensibility;
using SqlExplorer.Sdk.Formatting;

namespace SqlExplorer.Core.Formatting;

/// <summary>
/// The host's generic SQL formatter: a dialect-parameterised indentation engine. It normalises keyword
/// casing, breaks each major clause onto its own line, lays SELECT columns out one-per-line, indents
/// parenthesised subqueries, and puts JOIN/AND/OR on their own indented lines — all driven by
/// <see cref="SqlFormatOptions.IndentSize"/>. Providers may replace it with a dialect-specialised
/// formatter via <see cref="IDbProvider.Formatter"/>; this stays the fallback for every engine.
/// </summary>
public sealed class BasicSqlFormatter : ISqlFormatter, ISingletonService
{
    private static readonly HashSet<string> JoinPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "LEFT", "RIGHT", "INNER", "OUTER", "FULL", "CROSS", "NATURAL"
    };

    public string Format(string sql, ISqlDialect dialect, SqlFormatOptions options)
    {
        var (openQuote, closeQuote) = IdentifierQuotes(dialect);
        var tokens = Tokenize(sql, openQuote, closeQuote).ToList();
        if (tokens.Count == 0)
        {
            return string.Empty;
        }

        return new Printer(tokens, dialect.Keywords, options).Run();
    }

    // Walks the token stream once, emitting indented lines. State that must not leak across a
    // parenthesised subquery (the comma-breaking "list mode") is local to each FormatScope call.
    private sealed class Printer(List<string> tokens, IReadOnlySet<string> keywords, SqlFormatOptions options)
    {
        private readonly StringBuilder _out = new();
        private readonly StringBuilder _line = new();
        private int _index;
        private int _indent;
        private string? _prev;

        public string Run()
        {
            FormatScope(0);
            FlushFinal();
            return _out.ToString();
        }

        // Formats one query scope (a full statement, or a subquery inside parentheses) at baseIndent,
        // stopping at the ')' that closes it (left for the caller to consume) or at end of input.
        private void FormatScope(int baseIndent)
        {
            var listMode = false; // SELECT column list: break each item onto its own indented line

            while (_index < tokens.Count)
            {
                var token = tokens[_index];
                if (token == ")")
                {
                    return;
                }

                var upper = token.ToUpperInvariant();

                if (TryMajorClause(_index, out var clauseLen, out var isSelectList))
                {
                    NewLine(baseIndent);
                    for (var k = 0; k < clauseLen; k++)
                    {
                        Append(tokens[_index + k]);
                    }

                    _index += clauseLen;

                    if (isSelectList)
                    {
                        // Keep SELECT modifiers (DISTINCT/ALL) on the SELECT line, then drop the columns
                        // onto their own indented lines.
                        while (_index < tokens.Count && tokens[_index].ToUpperInvariant() is "DISTINCT" or "ALL")
                        {
                            Append(tokens[_index++]);
                        }

                        NewLine(baseIndent + 1);
                        listMode = true;
                    }
                    else
                    {
                        listMode = false;
                    }

                    continue;
                }

                if (TryJoin(_index, out var joinLen))
                {
                    NewLine(baseIndent + 1);
                    for (var k = 0; k < joinLen; k++)
                    {
                        Append(tokens[_index + k]);
                    }

                    _index += joinLen;
                    listMode = false;
                    continue;
                }

                if (upper is "AND" or "OR")
                {
                    NewLine(baseIndent + 1);
                    Append(token);
                    _index++;
                    continue;
                }

                if (token == "(")
                {
                    if (NextIsSubquery(_index))
                    {
                        Append("(");
                        _index++; // consume '('
                        NewLine(baseIndent + 1);
                        FormatScope(baseIndent + 1);
                        NewLine(baseIndent);
                        if (_index < tokens.Count && tokens[_index] == ")")
                        {
                            Append(")");
                            _index++;
                        }
                    }
                    else
                    {
                        Append("(");
                        _index++; // consume '('
                        AppendInlineUntilClose();
                    }

                    continue;
                }

                if (token == ",")
                {
                    Append(",");
                    _index++;
                    if (listMode)
                    {
                        NewLine(baseIndent + 1);
                    }

                    continue;
                }

                Append(token);
                _index++;
            }
        }

        // Copies tokens verbatim (inline) through to the matching ')', e.g. a function call or IN-list
        // that is not a subquery. Handles nested parentheses.
        private void AppendInlineUntilClose()
        {
            var depth = 1;
            while (_index < tokens.Count)
            {
                var token = tokens[_index++];
                Append(token);
                if (token == "(")
                {
                    depth++;
                }
                else if (token == ")" && --depth == 0)
                {
                    return;
                }
            }
        }

        private bool TryMajorClause(int index, out int length, out bool isSelectList)
        {
            length = 1;
            isSelectList = false;
            switch (tokens[index].ToUpperInvariant())
            {
                case "SELECT":
                    isSelectList = true;
                    return true;
                case "FROM":
                case "WHERE":
                case "HAVING":
                case "LIMIT":
                case "OFFSET":
                case "SET":
                case "VALUES":
                case "UNION":
                case "INTERSECT":
                case "EXCEPT":
                case "INSERT":
                case "UPDATE":
                case "DELETE":
                    return true;
                case "GROUP":
                case "ORDER":
                    if (index + 1 < tokens.Count && tokens[index + 1].ToUpperInvariant() == "BY")
                    {
                        length = 2;
                    }

                    return true;
                default:
                    return false;
            }
        }

        // A JOIN, optionally preceded by prefixes (LEFT/INNER/…). Returns the token count of the phrase.
        private bool TryJoin(int index, out int length)
        {
            var k = index;
            while (k < tokens.Count && JoinPrefixes.Contains(tokens[k].ToUpperInvariant()))
            {
                k++;
            }

            if (k < tokens.Count && tokens[k].ToUpperInvariant() == "JOIN")
            {
                length = k - index + 1;
                return true;
            }

            length = 0;
            return false;
        }

        private bool NextIsSubquery(int parenIndex) =>
            parenIndex + 1 < tokens.Count && tokens[parenIndex + 1].ToUpperInvariant() is "SELECT" or "WITH";

        // Appends a token to the current line with dialect-agnostic spacing, casing keywords as configured.
        private void Append(string token)
        {
            if (_line.Length > 0 && NeedsSpace(_prev, token))
            {
                _line.Append(' ');
            }

            _line.Append(Render(token));
            _prev = token;
        }

        private static bool NeedsSpace(string? prev, string token)
        {
            if (token is "," or ";" or ")" or ".")
            {
                return false;
            }

            return prev is not ("(" or ".");
        }

        private string Render(string token)
        {
            var isWord = token.Length > 0 && (char.IsLetterOrDigit(token[0]) || token[0] == '_');
            return isWord && keywords.Contains(token)
                ? ApplyCasing(token, options.KeywordCasing)
                : token;
        }

        // Finish the current line at its indent and start a new one at newIndent. An empty current line
        // produces no blank line — it just moves the indent.
        private void NewLine(int newIndent)
        {
            if (_line.Length > 0)
            {
                _out.Append(' ', _indent * options.IndentSize).Append(_line).Append('\n');
                _line.Clear();
            }

            _indent = newIndent;
            _prev = null;
        }

        private void FlushFinal()
        {
            if (_line.Length > 0)
            {
                _out.Append(' ', _indent * options.IndentSize).Append(_line);
            }
        }
    }

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
