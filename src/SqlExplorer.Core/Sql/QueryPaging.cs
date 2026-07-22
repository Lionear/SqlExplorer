using SqlExplorer.Sdk;

namespace SqlExplorer.Core.Sql;

/// <summary>
/// Decides whether an ad-hoc query can be shown as pageable results (SE-178): a DataGrip/DBeaver-style
/// first-page + next/prev experience instead of dumping every row of a stray <c>SELECT * FROM big_table</c>.
/// A statement is pageable when it is a <em>single</em> row-returning query — its first top-level keyword is
/// <c>SELECT</c> (or a <c>WITH … SELECT</c> CTE with no DML) and it carries no <c>TOP</c>/<c>LIMIT</c>/
/// <c>OFFSET</c>/<c>FETCH</c>/<c>INTO</c>/<c>FOR</c> of its own (those already bound it, or aren't a plain
/// result set). An existing top-level <c>ORDER BY</c> is fine and reported via <c>ordered</c> so the caller can
/// page it correctly per dialect. Multi-statement scripts and non-SELECT statements are not pageable and run
/// verbatim. Quote/comment/paren-aware, so a keyword inside a string, a quoted identifier or a subquery never
/// misleads the check.
/// </summary>
public static class QueryPaging
{
    // Keywords whose top-level presence means the statement already bounds itself or isn't a plain result set.
    private static readonly HashSet<string> Blockers = new(StringComparer.OrdinalIgnoreCase)
        { "TOP", "LIMIT", "OFFSET", "FETCH", "INTO", "FOR" };

    // DML that a leading WITH may drive instead of a SELECT — not pageable.
    private static readonly HashSet<string> Dml = new(StringComparer.OrdinalIgnoreCase)
        { "INSERT", "UPDATE", "DELETE", "MERGE" };

    /// <summary>True when <paramref name="sql"/> is a single pageable SELECT. <paramref name="statement"/> is the
    /// trimmed statement to page, and <paramref name="ordered"/> is whether it has a top-level <c>ORDER BY</c>.</summary>
    public static bool TryGetPageableSelect(string sql, out string statement, out bool ordered)
    {
        statement = string.Empty;
        ordered = false;
        if (string.IsNullOrWhiteSpace(sql))
        {
            return false;
        }

        // Spans holding nothing but separators don't count: a stray extra semicolon ("SELECT * FROM t;;")
        // splits into the statement plus a lone ";", and losing the page bar over a typo is the same surprise
        // the semicolon itself used to be.
        var statements = SqlStatementSplitter.Split(sql)
            .Where(s => StripTerminators(s.Text).Length > 0)
            .ToList();

        if (statements.Count != 1)
        {
            return false; // a whole script — pages would be meaningless
        }

        var text = statements[0].Text;
        var words = TopLevelWords(text);
        if (words.Count == 0)
        {
            return false;
        }

        var first = words[0];
        var isSelect = first.Equals("SELECT", StringComparison.OrdinalIgnoreCase);
        var isCteSelect = first.Equals("WITH", StringComparison.OrdinalIgnoreCase)
            && words.Any(w => w.Equals("SELECT", StringComparison.OrdinalIgnoreCase))
            && !words.Any(Dml.Contains);

        if ((!isSelect && !isCteSelect) || words.Any(Blockers.Contains))
        {
            return false;
        }

        var trimmed = StripTerminators(text);
        if (trimmed.Length == 0)
        {
            return false;
        }

        statement = trimmed;
        ordered = words.Any(w => w.Equals("ORDER", StringComparison.OrdinalIgnoreCase));
        return true;
    }

    /// <summary>
    /// Bounds every unbounded <c>SELECT</c> in a multi-statement script to <paramref name="limit"/> rows,
    /// leaving everything else exactly as written.
    ///
    /// <para>A script is not pageable — its statements aren't all result sets, and a prev/next bar can only
    /// drive one of them — so it used to run wide open: <c>SELECT * FROM a; SELECT * FROM b;</c> pulled both
    /// tables in full. Capping is not paging, but it is the part that matters: the rows never leave the
    /// server, so the cost is bounded rather than merely hidden. The caller is expected to say that the
    /// results were capped; a silently shortened result set is worse than a slow one.</para>
    ///
    /// <para>Statements that already bound themselves (<c>TOP</c>/<c>LIMIT</c>/<c>OFFSET</c>/<c>FETCH</c>),
    /// non-SELECTs and anything the parser isn't sure about are passed through untouched — the same
    /// conservative test <see cref="TryGetPageableSelect"/> uses.</para>
    /// </summary>
    /// <param name="capped">How many statements were bounded, so the caller can report it.</param>
    public static string CapPageableStatements(string sql, ISqlDialect dialect, int limit, out int capped)
    {
        capped = 0;
        if (limit <= 0)
        {
            return sql;
        }

        var spans = SqlStatementSplitter.Split(sql);
        if (spans.Count == 0)
        {
            return sql;
        }

        var parts = new List<string>(spans.Count);
        foreach (var span in spans)
        {
            if (TryGetPageableSelect(span.Text, out var statement, out var ordered))
            {
                parts.Add(dialect.PageQuery(statement, limit, 0, ordered) + ";");
                capped++;
            }
            else
            {
                // Verbatim, terminator and all — the splitter keeps it, and rewriting a statement we chose
                // not to bound would be a change nobody asked for.
                parts.Add(span.Text);
            }
        }

        return capped == 0 ? sql : string.Join("\n", parts);
    }

    /// <summary>
    /// The statement without its terminating semicolon(s). Every dialect pages by <em>appending</em> to the
    /// statement (<c>ORDER BY … OFFSET … FETCH</c>, <c>LIMIT …</c>), so a semicolon left on the end produces
    /// <c>SELECT * FROM t; OFFSET 0 ROWS</c> — a syntax error on every engine. The splitter is quote-aware,
    /// so a semicolon still standing at the end here is a terminator, not part of a literal or a delimited
    /// identifier.
    /// </summary>
    private static string StripTerminators(string text)
    {
        var trimmed = text.TrimEnd();
        while (trimmed.EndsWith(';'))
        {
            trimmed = trimmed[..^1].TrimEnd();
        }

        return trimmed;
    }

    // The bare identifier/keyword words that sit at paren depth 0, outside strings, comments, dollar-quotes and
    // delimited identifiers (" ", ` `, [ ]). A keyword hidden in a string literal or used as a quoted column
    // name therefore never trips the checks.
    private static List<string> TopLevelWords(string s)
    {
        var words = new List<string>();
        int i = 0, n = s.Length, depth = 0;
        while (i < n)
        {
            var c = s[i];
            if (c == '\'')
            {
                i = SkipTo(s, i + 1, '\'', escapeDoubled: true);
            }
            else if (c == '"')
            {
                i = SkipTo(s, i + 1, '"', escapeDoubled: false);
            }
            else if (c == '`')
            {
                i = SkipTo(s, i + 1, '`', escapeDoubled: false);
            }
            else if (c == '[')
            {
                i = SkipTo(s, i + 1, ']', escapeDoubled: false);
            }
            else if (c == '-' && i + 1 < n && s[i + 1] == '-')
            {
                i += 2;
                while (i < n && s[i] != '\n')
                {
                    i++;
                }
            }
            else if (c == '/' && i + 1 < n && s[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < n && !(s[i] == '*' && s[i + 1] == '/'))
                {
                    i++;
                }

                i = Math.Min(n, i + 2);
            }
            else if (c == '$' && TryReadDollarTag(s, i, out var tag))
            {
                i += tag.Length;
                while (i < n && !s.AsSpan(i).StartsWith(tag))
                {
                    i++;
                }

                i = Math.Min(n, i + tag.Length);
            }
            else if (c == '(')
            {
                depth++;
                i++;
            }
            else if (c == ')')
            {
                depth = Math.Max(0, depth - 1);
                i++;
            }
            else if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < n && (char.IsLetterOrDigit(s[i]) || s[i] == '_'))
                {
                    i++;
                }

                if (depth == 0)
                {
                    words.Add(s[start..i]);
                }
            }
            else
            {
                i++;
            }
        }

        return words;
    }

    // Index just past the closing delimiter. For single quotes, a doubled delimiter ('') is an escaped quote.
    private static int SkipTo(string s, int i, char close, bool escapeDoubled)
    {
        var n = s.Length;
        while (i < n)
        {
            if (s[i] == close)
            {
                if (escapeDoubled && i + 1 < n && s[i + 1] == close)
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return n;
    }

    // "$$" or "$tag$" opener; returns the full tag including both '$'. Mirrors SqlStatementSplitter.
    private static bool TryReadDollarTag(string s, int start, out string tag)
    {
        tag = "";
        var end = s.IndexOf('$', start + 1);
        if (end < 0)
        {
            return false;
        }

        for (var j = start + 1; j < end; j++)
        {
            if (!char.IsLetterOrDigit(s[j]) && s[j] != '_')
            {
                return false;
            }
        }

        tag = s[start..(end + 1)];
        return true;
    }
}
