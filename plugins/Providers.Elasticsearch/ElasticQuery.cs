using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SqlExplorer.Providers.Elasticsearch;

internal enum ElasticQueryKind
{
    /// <summary>A "browse this index" request — the host's generated pseudo-SQL, or a bare index name.</summary>
    Browse,

    /// <summary>A Kibana Dev-Tools-style raw request (<c>METHOD path { body }</c>).</summary>
    Console
}

/// <summary>
/// A query-editor string parsed into either a "browse this index" request or a raw REST request. Three
/// input shapes are accepted (mirroring MongoDB's <c>MongoQuery</c>):
/// <list type="bullet">
/// <item>Kibana Dev-Tools style: <c>GET myindex/_search { "query": { "match_all": {} } }</c>,
///   <c>POST myindex/_doc { ... }</c>, <c>GET _cat/indices?format=json</c>, <c>DELETE /myindex</c> —
///   the first line is <c>METHOD path</c>, everything after it is the (optional) JSON body.</item>
/// <item>The host's generated browse text: <c>SELECT * FROM myindex LIMIT 1000 OFFSET 0</c> — translated
///   to a <c>_search</c> with <c>match_all</c>, <c>size</c>/<c>from</c> paging, an optional <c>sort</c>
///   from ORDER BY, and an optional query from the WHERE filter box.</item>
/// <item>A bare index name: <c>myindex</c> → browse everything (capped by a default size).</item>
/// </list>
/// </summary>
internal sealed class ElasticQuery
{
    public required ElasticQueryKind Kind { get; init; }

    // Browse:
    public string Index { get; init; } = string.Empty;
    public int? Size { get; init; }
    public int? From { get; init; }
    public JsonNode? Query { get; init; }
    public JsonArray? Sort { get; init; }

    // Console:
    public string Method { get; init; } = "GET";
    public string Path { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;

    private static readonly HashSet<string> HttpMethods =
        new(StringComparer.OrdinalIgnoreCase) { "GET", "POST", "PUT", "DELETE", "HEAD" };

    public static ElasticQuery Parse(string text)
    {
        var t = text.Trim().TrimEnd(';').Trim();
        if (t.Length == 0)
        {
            throw new FormatException("Empty query.");
        }

        var firstWord = FirstWord(t);
        if (HttpMethods.Contains(firstWord))
        {
            return ParseConsole(t, firstWord.Length);
        }

        if (firstWord.Equals("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            return ParseBrowse(t);
        }

        // Anything else: treat the whole (single-token) text as an index name and browse everything.
        return new ElasticQuery { Kind = ElasticQueryKind.Browse, Index = Unquote(t) };
    }

    // --- METHOD path [\n { body }] ------------------------------------------------------------------
    private static ElasticQuery ParseConsole(string t, int methodLength)
    {
        var method = t[..methodLength].ToUpperInvariant();
        var rest = t[methodLength..].TrimStart();

        // The path runs to the end of the first line (or to the start of the JSON body on the same line).
        var newline = rest.IndexOf('\n');
        var brace = rest.IndexOfAny(['{', '[']);

        int pathEnd;
        if (newline < 0 && brace < 0) pathEnd = rest.Length;
        else if (newline < 0) pathEnd = brace;
        else if (brace < 0) pathEnd = newline;
        else pathEnd = Math.Min(newline, brace);

        var path = rest[..pathEnd].Trim().TrimStart('/');
        var body = rest[pathEnd..].Trim();

        if (path.Length == 0)
        {
            throw new FormatException("Expected a request path, e.g. GET myindex/_search.");
        }

        return new ElasticQuery { Kind = ElasticQueryKind.Console, Method = method, Path = path, Body = body };
    }

    // --- SELECT * FROM <index> [WHERE <filter>] [ORDER BY <cols>] [LIMIT n] [OFFSET m] --------------
    private static ElasticQuery ParseBrowse(string t)
    {
        var fromIdx = IndexOfWordTopLevel(t, "FROM", 0);
        if (fromIdx < 0)
        {
            throw new FormatException("Expected FROM <index>.");
        }

        var pos = fromIdx + 4;
        while (pos < t.Length && char.IsWhiteSpace(t[pos])) pos++;
        var (indexToken, afterIndex) = ReadToken(t, pos);
        var index = Unquote(indexToken);

        var whereIdx = IndexOfWordTopLevel(t, "WHERE", afterIndex);
        var orderIdx = IndexOfWordTopLevel(t, "ORDER", afterIndex);
        var limitIdx = IndexOfWordTopLevel(t, "LIMIT", afterIndex);
        var offsetIdx = IndexOfWordTopLevel(t, "OFFSET", afterIndex);

        JsonNode? query = null;
        if (whereIdx >= 0)
        {
            var end = ClauseEnd(whereIdx, t.Length, orderIdx, limitIdx, offsetIdx);
            query = ParseWhere(t[(whereIdx + 5)..end].Trim());
        }

        JsonArray? sort = null;
        if (orderIdx >= 0)
        {
            var end = ClauseEnd(orderIdx, t.Length, limitIdx, offsetIdx);
            var orderText = t[(orderIdx + 5)..end].Trim();
            if (orderText.StartsWith("BY", StringComparison.OrdinalIgnoreCase)) orderText = orderText[2..].Trim();
            sort = ParseOrderBy(orderText);
        }

        return new ElasticQuery
        {
            Kind = ElasticQueryKind.Browse,
            Index = index,
            Query = query,
            Sort = sort,
            Size = limitIdx >= 0 ? ReadIntAt(t, limitIdx + 5) : null,
            From = offsetIdx >= 0 ? ReadIntAt(t, offsetIdx + 6) : null
        };
    }

    // Translate a browse WHERE clause into an ES query. Two kinds of conjunct are understood: a raw ES
    // query object typed into the filter box (e.g. { "term": { "status": "active" } }), and the host's
    // per-column inline filter (CAST("col" AS ...) LIKE '%val%'), mapped to a case-insensitive wildcard.
    // Multiple conjuncts (joined by AND) combine under bool.must.
    private static JsonNode ParseWhere(string whereText)
    {
        var musts = new JsonArray();
        foreach (var raw in SplitTopLevelAnd(whereText))
        {
            var part = raw.Trim();
            if (part.Length == 0) continue;

            if (part[0] == '{')
            {
                musts.Add(JsonNode.Parse(part) ?? throw new FormatException($"Invalid query object: {part}"));
                continue;
            }

            var like = LikeClause.Match(part);
            if (like.Success)
            {
                var column = Unquote(like.Groups["col"].Value);
                var pattern = like.Groups["pat"].Value.Replace("''", "'");
                musts.Add(new JsonObject
                {
                    ["wildcard"] = new JsonObject
                    {
                        [column] = new JsonObject { ["value"] = LikeToWildcard(pattern), ["case_insensitive"] = true }
                    }
                });
                continue;
            }

            throw new FormatException(
                "The filter box expects an Elasticsearch query object, e.g. { \"term\": { \"status\": \"active\" } }. " +
                $"Couldn't interpret: {part}");
        }

        if (musts.Count == 0) return new JsonObject { ["match_all"] = new JsonObject() };
        if (musts.Count == 1) return musts[0]!.DeepClone();
        return new JsonObject { ["bool"] = new JsonObject { ["must"] = musts } };
    }

    // ORDER BY "col" DESC[, "col2" ASC] → [ { "col": { "order": "desc" } }, { "col2": { "order": "asc" } } ].
    private static JsonArray ParseOrderBy(string orderText)
    {
        var sort = new JsonArray();
        foreach (var segment in SplitTopLevel(orderText, ','))
        {
            var seg = segment.Trim();
            if (seg.Length == 0) continue;

            var order = "asc";
            if (seg.EndsWith("DESC", StringComparison.OrdinalIgnoreCase))
            {
                order = "desc";
                seg = seg[..^4].TrimEnd();
            }
            else if (seg.EndsWith("ASC", StringComparison.OrdinalIgnoreCase))
            {
                seg = seg[..^3].TrimEnd();
            }

            sort.Add(new JsonObject { [Unquote(seg)] = new JsonObject { ["order"] = order } });
        }

        return sort;
    }

    // SQL LIKE → ES wildcard: % = any run (*), _ = any single char (?), * and ? in the input are escaped.
    private static string LikeToWildcard(string like)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in like)
        {
            switch (c)
            {
                case '%': sb.Append('*'); break;
                case '_': sb.Append('?'); break;
                case '*': case '?': case '\\': sb.Append('\\').Append(c); break;
                default: sb.Append(c); break;
            }
        }

        return sb.ToString();
    }

    private static readonly Regex LikeClause = new(
        """^(?:CAST\s*\(\s*)?(?<col>"[^"]+"|`[^`]+`|\[[^\]]+\]|[A-Za-z_][\w$.]*)\s*(?:AS\s+[^)]+\))?\s+LIKE\s+'(?<pat>(?:[^']|'')*)'$""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // --- Text scanning helpers (quote- and nesting-aware) ------------------------------------------
    private static string FirstWord(string s)
    {
        var i = 0;
        while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] is not ('{' or '[' or '/')) i++;
        return s[..i];
    }

    private static int ClauseEnd(int start, int length, params int[] candidates)
    {
        var end = length;
        foreach (var c in candidates)
        {
            if (c > start && c < end) end = c;
        }

        return end;
    }

    private static (string Token, int End) ReadToken(string s, int pos)
    {
        if (pos >= s.Length)
        {
            throw new FormatException("Expected an index name after FROM.");
        }

        var c = s[pos];
        if (c is '"' or '`')
        {
            var close = SkipString(s, pos);
            return (s[pos..(close + 1)], close + 1);
        }

        if (c == '[')
        {
            var close = s.IndexOf(']', pos);
            if (close < 0) throw new FormatException("Unterminated [identifier].");
            return (s[pos..(close + 1)], close + 1);
        }

        var end = pos;
        while (end < s.Length && !char.IsWhiteSpace(s[end]) && s[end] != ';') end++;
        return (s[pos..end], end);
    }

    private static int? ReadIntAt(string s, int from)
    {
        var rest = s[from..].TrimStart();
        var end = 0;
        while (end < rest.Length && char.IsDigit(rest[end])) end++;
        return end == 0 ? null : int.Parse(rest[..end], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int IndexOfWordTopLevel(string s, string word, int start)
    {
        var depth = 0;
        for (var i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (c is '"' or '\'')
            {
                i = SkipString(s, i);
                continue;
            }

            if (c is '{' or '[' or '(') depth++;
            else if (c is '}' or ']' or ')') depth--;
            else if (depth == 0 && MatchesWord(s, i, word)) return i;
        }

        return -1;
    }

    private static bool MatchesWord(string s, int i, string word)
    {
        if (i + word.Length > s.Length) return false;
        if (string.Compare(s, i, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
        var before = i == 0 || !char.IsLetterOrDigit(s[i - 1]);
        var afterPos = i + word.Length;
        var after = afterPos >= s.Length || !char.IsLetterOrDigit(s[afterPos]);
        return before && after;
    }

    private static List<string> SplitTopLevelAnd(string s)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c is '"' or '\'')
            {
                i = SkipString(s, i);
                continue;
            }

            if (c is '{' or '[' or '(') depth++;
            else if (c is '}' or ']' or ')') depth--;
            else if (depth == 0 && i + 5 <= s.Length &&
                     string.Compare(s, i, " and ", 0, 5, StringComparison.OrdinalIgnoreCase) == 0)
            {
                parts.Add(s[start..i]);
                i += 4;
                start = i + 1;
            }
        }

        parts.Add(s[start..]);
        return parts;
    }

    private static List<string> SplitTopLevel(string s, char separator)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c is '"' or '\'')
            {
                i = SkipString(s, i);
                continue;
            }

            if (c is '{' or '[' or '(') depth++;
            else if (c is '}' or ']' or ')') depth--;
            else if (c == separator && depth == 0)
            {
                parts.Add(s[start..i]);
                start = i + 1;
            }
        }

        parts.Add(s[start..]);
        return parts;
    }

    private static int SkipString(string s, int openQuote)
    {
        var quote = s[openQuote];
        for (var i = openQuote + 1; i < s.Length; i++)
        {
            if (s[i] == '\\') { i++; continue; }
            if (s[i] == quote) return i;
        }

        throw new FormatException("Unterminated string literal.");
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && s[0] == s[^1] && s[0] is '"' or '\'' or '`')
        {
            return s[1..^1];
        }

        if (s.Length >= 2 && s[0] == '[' && s[^1] == ']')
        {
            return s[1..^1];
        }

        return s;
    }
}
