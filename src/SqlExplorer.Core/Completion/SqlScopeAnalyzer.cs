using SqlExplorer.Core.Sql;

namespace SqlExplorer.Core.Completion;

/// <summary>The SQL clause the caret sits in, within its query scope — drives what completion offers.</summary>
public enum SqlClause
{
    /// <summary>Not inside a recognisable clause (or before any keyword) — the provider falls back to a broad mix.</summary>
    Unknown,
    Select,
    From,
    Where,
    On,
    GroupBy,
    Having,
    OrderBy
}

/// <summary>
/// One source visible in a query scope's FROM/JOIN list. <see cref="Alias"/> is how it is referenced
/// (its explicit alias, or the relation's own name when unaliased). <see cref="Table"/> is the underlying
/// table/view name to look up in the schema snapshot — <c>null</c> for a CTE or a derived table, whose
/// columns instead come from <see cref="Columns"/>. <see cref="Columns"/> is <c>null</c> when they could not
/// be inferred (e.g. a <c>SELECT *</c> body), signalling the provider to fall back.
/// </summary>
public sealed record SqlScopeSource(string Alias, string? Table, IReadOnlyList<string>? Columns);

/// <summary>
/// The resolved completion context at the caret: the <see cref="Clause"/> it sits in, the FROM/JOIN
/// <see cref="Sources"/> visible in that scope (resolved through CTEs and derived tables), and the
/// <see cref="CteNames"/> in scope (offered after FROM/JOIN alongside real tables).
/// </summary>
public sealed record SqlScope(
    SqlClause Clause,
    IReadOnlyList<SqlScopeSource> Sources,
    IReadOnlyList<string> CteNames)
{
    public static readonly SqlScope Empty = new(SqlClause.Unknown, [], []);
}

/// <summary>
/// A parser-free-ish, dialect-tolerant scope tracker for SQL completion (SE-149 phase 1). It replaces the old
/// FROM/JOIN regex with a shallow, bracket- and quote-aware model: it tokenises the <em>statement under the
/// caret</em> (never leaking across statement boundaries), understands CTEs (<c>WITH x AS (...)</c>),
/// subqueries and derived tables (<c>(SELECT ...) alias</c>), and reports which clause the caret is in plus the
/// sources visible there. It is deliberately not a full grammar — a hand-written structural tracker gives the
/// bulk of the accuracy for a fraction of the cost — and stays schema-free: it names tables/CTEs/derived
/// columns and lets <see cref="SqlCompletionProvider"/> resolve them against the schema snapshot.
/// </summary>
public static class SqlScopeAnalyzer
{
    public static SqlScope Analyze(string sql, int caret)
    {
        if (string.IsNullOrEmpty(sql))
        {
            return SqlScope.Empty;
        }

        caret = Math.Clamp(caret, 0, sql.Length);

        // Confine everything to the statement under the caret so a query never sees another's tables.
        var (stmt, caretInStmt) = StatementUnderCaret(sql, caret);
        var tokens = Tokenize(stmt);
        if (tokens.Count == 0)
        {
            return SqlScope.Empty;
        }

        var depth = DepthMap(tokens);
        var pairs = MatchParens(tokens);
        var caretPos = CaretTokenIndex(tokens, caretInStmt);

        // CTEs are visible to every scope in the statement, so gather them once up front.
        var ctes = ParseCtes(tokens, depth, pairs);

        // The innermost subquery ( SELECT ... ) that encloses the caret is the active scope; else the statement.
        var (lo, hi, baseDepth) = ScopeAtCaret(tokens, depth, pairs, caretPos);

        var clause = ClauseAt(tokens, depth, lo, hi, baseDepth, caretPos);
        var sources = ParseFrom(tokens, depth, pairs, lo, hi, baseDepth, ctes);

        return new SqlScope(clause, sources, ctes.Keys.ToList());
    }

    // ---- statement boundary --------------------------------------------------------------------------

    // The span containing the caret, and the caret re-based into it. Reuses the shared splitter so the
    // notion of a "statement" matches execute-at-cursor exactly (GO batches, ;-splitting, quotes/comments).
    private static (string Statement, int Caret) StatementUnderCaret(string sql, int caret)
    {
        foreach (var span in SqlStatementSplitter.Split(sql))
        {
            if (caret >= span.Start && caret <= span.End)
            {
                return (sql[span.Start..span.End], caret - span.Start);
            }
        }

        return (sql, caret);
    }

    // ---- tokenizer -----------------------------------------------------------------------------------

    private enum TokType { Word, LParen, RParen, Comma, Dot, Star, Semicolon, Other }

    private readonly record struct Token(TokType Type, string Text, int Start);

    // Words (bare or quoted with " ` or [ ]), the structural punctuation completion cares about, and a single
    // Other token for everything else. String literals, comments and Postgres dollar-quoting are skipped whole
    // (they never contain identifiers we complete), mirroring SqlStatementSplitter's scanner.
    private static List<Token> Tokenize(string s)
    {
        var tokens = new List<Token>();
        int i = 0, n = s.Length;
        while (i < n)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
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
            else if (c == '\'')
            {
                i = SkipSingleQuote(s, i);
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
            else if (c == '"')
            {
                i = ReadDelimitedIdent(s, i, '"', tokens);
            }
            else if (c == '`')
            {
                i = ReadDelimitedIdent(s, i, '`', tokens);
            }
            else if (c == '[')
            {
                i = ReadDelimitedIdent(s, i, ']', tokens);
            }
            else if (IsWordStart(c))
            {
                var start = i;
                while (i < n && IsWordChar(s[i]))
                {
                    i++;
                }

                tokens.Add(new Token(TokType.Word, s[start..i], start));
            }
            else
            {
                tokens.Add(new Token(Punct(c), c.ToString(), i));
                i++;
            }
        }

        return tokens;
    }

    private static int SkipSingleQuote(string s, int i)
    {
        var n = s.Length;
        i++; // opening '
        while (i < n)
        {
            if (s[i] == '\'')
            {
                if (i + 1 < n && s[i + 1] == '\'')
                {
                    i += 2; // escaped '' inside the literal
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return n;
    }

    // Reads a delimited identifier ("x", `x` or [x]) as a single Word token whose Start points at the opening
    // delimiter, so a caret sitting on a quoted alias still maps correctly.
    private static int ReadDelimitedIdent(string s, int open, char close, List<Token> tokens)
    {
        var n = s.Length;
        var i = open + 1;
        while (i < n && s[i] != close)
        {
            i++;
        }

        tokens.Add(new Token(TokType.Word, s[(open + 1)..Math.Min(i, n)], open));
        return Math.Min(n, i + 1);
    }

    private static TokType Punct(char c) => c switch
    {
        '(' => TokType.LParen,
        ')' => TokType.RParen,
        ',' => TokType.Comma,
        '.' => TokType.Dot,
        '*' => TokType.Star,
        ';' => TokType.Semicolon,
        _ => TokType.Other
    };

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

    // ---- structural maps -----------------------------------------------------------------------------

    // Paren depth at each token: a '(' and its matching ')' share the outer depth, tokens between them are one
    // deeper. Lets a scope treat its own top level as relative depth 0 while stepping over nested subqueries.
    private static int[] DepthMap(List<Token> tokens)
    {
        var depth = new int[tokens.Count];
        var d = 0;
        for (var k = 0; k < tokens.Count; k++)
        {
            if (tokens[k].Type == TokType.RParen)
            {
                d = Math.Max(0, d - 1);
            }

            depth[k] = d;

            if (tokens[k].Type == TokType.LParen)
            {
                d++;
            }
        }

        return depth;
    }

    // open-token-index → matching close-token-index (and vice versa), for balanced parens.
    private static Dictionary<int, int> MatchParens(List<Token> tokens)
    {
        var pairs = new Dictionary<int, int>();
        var stack = new Stack<int>();
        for (var k = 0; k < tokens.Count; k++)
        {
            if (tokens[k].Type == TokType.LParen)
            {
                stack.Push(k);
            }
            else if (tokens[k].Type == TokType.RParen && stack.Count > 0)
            {
                var open = stack.Pop();
                pairs[open] = k;
                pairs[k] = open;
            }
        }

        return pairs;
    }

    // Number of tokens fully before the caret — the caret sits just after token caretPos-1.
    private static int CaretTokenIndex(List<Token> tokens, int caret)
    {
        var pos = 0;
        while (pos < tokens.Count && tokens[pos].Start < caret)
        {
            pos++;
        }

        return pos;
    }

    // The innermost ( SELECT ... ) enclosing the caret is the active query scope; when none encloses it, the
    // whole statement is the scope. Returns the token range [lo, hi) of the scope body and its base depth.
    private static (int Lo, int Hi, int BaseDepth) ScopeAtCaret(
        List<Token> tokens, int[] depth, Dictionary<int, int> pairs, int caretPos)
    {
        var best = (Lo: 0, Hi: tokens.Count, BaseDepth: 0);
        foreach (var (open, close) in pairs)
        {
            if (tokens[open].Type != TokType.LParen)
            {
                continue; // only iterate open→close entries
            }

            if (open < caretPos && close >= caretPos // encloses the caret
                && open + 1 < tokens.Count && IsWord(tokens[open + 1], "SELECT")
                && open + 1 >= best.Lo) // innermost so far
            {
                best = (open + 1, close, depth[open] + 1);
            }
        }

        return best;
    }

    // ---- clause detection ----------------------------------------------------------------------------

    // The last clause keyword at the scope's top level before the caret decides the clause. Two-word clauses
    // (GROUP BY / ORDER BY) key off their first word; JOIN shares FROM's "expecting a relation" behaviour.
    private static SqlClause ClauseAt(
        List<Token> tokens, int[] depth, int lo, int hi, int baseDepth, int caretPos)
    {
        var clause = SqlClause.Unknown;
        for (var k = lo; k < hi && k < caretPos; k++)
        {
            if (depth[k] - baseDepth != 0 || tokens[k].Type != TokType.Word)
            {
                continue;
            }

            clause = tokens[k].Text.ToUpperInvariant() switch
            {
                "SELECT" => SqlClause.Select,
                "FROM" or "JOIN" => SqlClause.From,
                "ON" => SqlClause.On,
                "WHERE" => SqlClause.Where,
                "GROUP" => SqlClause.GroupBy,
                "HAVING" => SqlClause.Having,
                "ORDER" => SqlClause.OrderBy,
                _ => clause
            };
        }

        return clause;
    }

    // ---- FROM / JOIN sources -------------------------------------------------------------------------

    private static readonly HashSet<string> JoinWords = new(StringComparer.OrdinalIgnoreCase)
        { "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "OUTER", "NATURAL", "LATERAL" };

    // Keywords that end the FROM/JOIN region at the scope's top level.
    private static readonly HashSet<string> FromEnders = new(StringComparer.OrdinalIgnoreCase)
        { "WHERE", "GROUP", "HAVING", "ORDER", "WINDOW", "LIMIT", "OFFSET", "FETCH", "UNION", "INTERSECT", "EXCEPT", "RETURNING", "FOR" };

    private static List<SqlScopeSource> ParseFrom(
        List<Token> tokens, int[] depth, Dictionary<int, int> pairs,
        int lo, int hi, int baseDepth, Dictionary<string, IReadOnlyList<string>?> ctes)
    {
        var sources = new List<SqlScopeSource>();

        // Locate FROM at the scope's top level.
        var from = -1;
        for (var k = lo; k < hi; k++)
        {
            if (depth[k] - baseDepth == 0 && IsWord(tokens[k], "FROM"))
            {
                from = k + 1;
                break;
            }
        }

        if (from < 0)
        {
            return sources;
        }

        var k2 = from;
        while (k2 < hi)
        {
            var t = tokens[k2];
            var rel = depth[k2] - baseDepth;

            if (rel == 0 && t.Type == TokType.Word && FromEnders.Contains(t.Text))
            {
                break; // end of the FROM/JOIN region
            }

            // Separators and join-type noise between refs.
            if (rel == 0 && (t.Type == TokType.Comma || (t.Type == TokType.Word && JoinWords.Contains(t.Text))))
            {
                k2++;
                continue;
            }

            // An ON condition belongs to the preceding join, not the source list — skip it wholesale.
            if (rel == 0 && IsWord(t, "ON"))
            {
                k2++;
                while (k2 < hi && !(depth[k2] - baseDepth == 0 &&
                       (tokens[k2].Type == TokType.Comma ||
                        (tokens[k2].Type == TokType.Word && (JoinWords.Contains(tokens[k2].Text) || FromEnders.Contains(tokens[k2].Text))))))
                {
                    k2++;
                }

                continue;
            }

            if (rel == 0 && t.Type == TokType.LParen && pairs.TryGetValue(k2, out var close))
            {
                // Derived table: (SELECT ...) [AS] alias  — infer its output columns from the subquery body.
                var columns = IsWord(tokens[k2 + 1], "SELECT")
                    ? InferSelectColumns(tokens, depth, pairs, k2 + 1, close, depth[k2] + 1)
                    : null;
                var next = SkipAs(tokens, close + 1, hi);
                var alias = next < hi && tokens[next].Type == TokType.Word ? tokens[next].Text : "";
                if (alias.Length > 0)
                {
                    sources.Add(new SqlScopeSource(alias, null, columns));
                    k2 = next + 1;
                }
                else
                {
                    k2 = close + 1;
                }

                continue;
            }

            if (rel == 0 && t.Type == TokType.Word)
            {
                // Base ref: name(.name)* [ [AS] alias ]. The last dotted part is the relation; the alias is the
                // next plain word unless it's a keyword/join/comma.
                var relName = tokens[k2].Text;
                var j = k2 + 1;
                while (j + 1 < hi && tokens[j].Type == TokType.Dot && tokens[j + 1].Type == TokType.Word)
                {
                    relName = tokens[j + 1].Text;
                    j += 2;
                }

                j = SkipAs(tokens, j, hi);
                var alias = relName;
                if (j < hi && tokens[j].Type == TokType.Word && !IsClauseNoise(tokens[j].Text))
                {
                    alias = tokens[j].Text;
                    j++;
                }

                if (ctes.TryGetValue(relName, out var cteColumns))
                {
                    sources.Add(new SqlScopeSource(alias, null, cteColumns)); // resolves through the CTE
                }
                else
                {
                    sources.Add(new SqlScopeSource(alias, relName, null));
                }

                k2 = j;
                continue;
            }

            k2++;
        }

        return sources;
    }

    private static int SkipAs(List<Token> tokens, int i, int hi) =>
        i < hi && IsWord(tokens[i], "AS") ? i + 1 : i;

    private static bool IsClauseNoise(string word) =>
        JoinWords.Contains(word) || FromEnders.Contains(word) || word.Equals("ON", StringComparison.OrdinalIgnoreCase);

    // ---- CTEs ----------------------------------------------------------------------------------------

    // Parse a leading WITH [RECURSIVE] name [(cols)] AS ( body ) [, ...] into name → columns (explicit list, or
    // inferred from the body's SELECT list; null when they can't be inferred — e.g. SELECT *).
    private static Dictionary<string, IReadOnlyList<string>?> ParseCtes(
        List<Token> tokens, int[] depth, Dictionary<int, int> pairs)
    {
        var ctes = new Dictionary<string, IReadOnlyList<string>?>(StringComparer.OrdinalIgnoreCase);
        if (tokens.Count == 0 || !IsWord(tokens[0], "WITH"))
        {
            return ctes;
        }

        var i = IsWord(tokens[1 % tokens.Count], "RECURSIVE") ? 2 : 1;
        while (i < tokens.Count)
        {
            if (tokens[i].Type != TokType.Word || depth[i] != 0)
            {
                break;
            }

            var name = tokens[i].Text;
            i++;

            IReadOnlyList<string>? explicitCols = null;
            if (i < tokens.Count && tokens[i].Type == TokType.LParen && pairs.TryGetValue(i, out var colsClose))
            {
                explicitCols = tokens.Skip(i + 1).Take(colsClose - i - 1)
                    .Where(t => t.Type == TokType.Word).Select(t => t.Text).ToList();
                i = colsClose + 1;
            }

            i = SkipAs(tokens, i, tokens.Count);
            if (i >= tokens.Count || tokens[i].Type != TokType.LParen || !pairs.TryGetValue(i, out var bodyClose))
            {
                break; // malformed / mid-typing — stop gracefully
            }

            ctes[name] = explicitCols is { Count: > 0 }
                ? explicitCols
                : IsWord(tokens[i + 1], "SELECT")
                    ? InferSelectColumns(tokens, depth, pairs, i + 1, bodyClose, depth[i] + 1)
                    : null;

            i = bodyClose + 1;
            if (i < tokens.Count && tokens[i].Type == TokType.Comma && depth[i] == 0)
            {
                i++; // next CTE
                continue;
            }

            break; // end of the WITH list — the main statement follows
        }

        return ctes;
    }

    // ---- SELECT-list column inference ----------------------------------------------------------------

    // Best-effort output column names of a subquery body: the identifiers of its top-level SELECT list. Returns
    // null when a * is present (can't enumerate without the schema) or nothing nameable is found, so the caller
    // falls back rather than offering a wrong, partial set.
    private static IReadOnlyList<string>? InferSelectColumns(
        List<Token> tokens, int[] depth, Dictionary<int, int> pairs, int selectTok, int bodyClose, int baseDepth)
    {
        // The SELECT list runs from just after SELECT to the body's top-level FROM (or the body end).
        var start = selectTok + 1;
        var end = bodyClose;
        for (var k = start; k < bodyClose; k++)
        {
            if (depth[k] - baseDepth == 0 && IsWord(tokens[k], "FROM"))
            {
                end = k;
                break;
            }
        }

        var columns = new List<string>();
        var itemStart = start;
        for (var k = start; k <= end; k++)
        {
            var atSep = k == end || (depth[k] - baseDepth == 0 && tokens[k].Type == TokType.Comma);
            if (!atSep)
            {
                continue;
            }

            if (!TryNameSelectItem(tokens, depth, baseDepth, itemStart, k, out var name))
            {
                return null; // a * or an unnameable expression — bail to the caller's fallback
            }

            if (name.Length > 0)
            {
                columns.Add(name);
            }

            itemStart = k + 1;
        }

        return columns.Count > 0 ? columns : null;
    }

    // Names one SELECT-list item [itemStart, itemEnd): the word after a top-level AS, else the trailing
    // identifier of a plain (optionally dotted) column. Signals failure on a * so the whole inference bails.
    private static bool TryNameSelectItem(
        List<Token> tokens, int[] depth, int baseDepth, int itemStart, int itemEnd, out string name)
    {
        name = "";
        for (var k = itemStart; k < itemEnd; k++)
        {
            if (depth[k] - baseDepth == 0 && tokens[k].Type == TokType.Star)
            {
                return false; // SELECT * / t.* — unknowable here
            }

            if (depth[k] - baseDepth == 0 && IsWord(tokens[k], "AS") && k + 1 < itemEnd && tokens[k + 1].Type == TokType.Word)
            {
                name = tokens[k + 1].Text;
                return true;
            }
        }

        // No AS: a bare `col` or `t.col` names itself; anything else (function call, arithmetic) stays unnamed.
        var last = -1;
        var wordCount = 0;
        var onlyIdentifier = true;
        for (var k = itemStart; k < itemEnd; k++)
        {
            switch (tokens[k].Type)
            {
                case TokType.Word:
                    last = k;
                    wordCount++;
                    break;
                case TokType.Dot:
                    break;
                default:
                    onlyIdentifier = false;
                    break;
            }
        }

        if (last >= 0 && onlyIdentifier && wordCount >= 1)
        {
            name = tokens[last].Text;
        }

        return true; // unnamed expression → contributes nothing, but doesn't fail the whole list
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private static bool IsWord(Token t, string keyword) =>
        t.Type == TokType.Word && t.Text.Equals(keyword, StringComparison.OrdinalIgnoreCase);

    private static bool IsWordStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
