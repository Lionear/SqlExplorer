using System.Text.RegularExpressions;
using MongoDB.Bson;

namespace SqlExplorer.Providers.MongoDb;

/// <summary>What kind of native Mongo operation a parsed query resolves to.</summary>
internal enum MongoQueryKind
{
    Find,
    Aggregate
}

/// <summary>
/// A query-editor string parsed into a native MongoDB operation. The collection comes from the text;
/// the database always comes from the connection context (<see cref="ConnectionProfile.Database"/>),
/// never from the query — mirroring how the mongo shell binds <c>db</c> to the current database.
/// </summary>
/// <remarks>
/// Three input shapes are accepted (see <see cref="Parse"/>):
/// <list type="bullet">
/// <item>Shell style: <c>db.users.find({ age: { $gt: 21 } }).sort({ name: 1 }).skip(10).limit(50)</c>
///   or <c>db.orders.aggregate([ { $match: {...} }, { $group: {...} } ])</c>.</item>
/// <item>The host's generated browse text: <c>SELECT * FROM users LIMIT 1000 OFFSET 0</c> → a plain
///   <c>find</c> honouring the limit/offset (any WHERE clause is ignored — the grid's own filtering
///   isn't translated to a Mongo filter).</item>
/// <item>A bare collection name: <c>users</c> → find everything (capped by a default limit).</item>
/// </list>
/// </remarks>
internal sealed class MongoQuery
{
    public required MongoQueryKind Kind { get; init; }
    public required string Collection { get; init; }
    public BsonDocument Filter { get; init; } = new();
    public BsonDocument? Projection { get; init; }
    public BsonDocument? Sort { get; init; }
    public int? Limit { get; init; }
    public int? Skip { get; init; }
    public BsonArray Pipeline { get; init; } = [];

    public static MongoQuery Parse(string text)
    {
        var t = StripComments(text).Trim().TrimEnd(';').Trim();
        if (t.Length == 0)
        {
            throw new FormatException("Empty query.");
        }

        if (t.StartsWith("db.", StringComparison.Ordinal))
        {
            return ParseShell(t);
        }

        if (t.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            return ParseBrowse(t);
        }

        // Anything else: treat the whole (single-token) text as a collection name and find everything.
        return new MongoQuery { Kind = MongoQueryKind.Find, Collection = Unquote(t) };
    }

    // --- db.<collection>.<method>(...)[.sort(...)][.skip(n)][.limit(n)] -----------------------------
    private static MongoQuery ParseShell(string t)
    {
        var afterDb = 3; // past "db."
        var dot = IndexOfTopLevel(t, '.', afterDb);
        if (dot < 0)
        {
            throw new FormatException("Expected db.<collection>.find(...) or .aggregate([...]).");
        }

        var collection = Unquote(t[afterDb..dot]);
        var open = t.IndexOf('(', dot);
        if (open < 0)
        {
            throw new FormatException("Expected a method call after the collection name.");
        }

        var method = t[(dot + 1)..open].Trim();
        var (args, afterCall) = BalancedSlice(t, open, '(', ')');

        int? limit = null, skip = null;
        BsonDocument? sort = null;

        // Parse any chained .sort({...}) / .skip(n) / .limit(n) modifiers.
        var i = afterCall;
        while (i < t.Length)
        {
            while (i < t.Length && char.IsWhiteSpace(t[i])) i++;
            if (i >= t.Length) break;
            if (t[i] != '.')
            {
                throw new FormatException($"Unexpected character '{t[i]}' after the query.");
            }

            var callOpen = t.IndexOf('(', i);
            if (callOpen < 0)
            {
                throw new FormatException("Malformed method chain.");
            }

            var chained = t[(i + 1)..callOpen].Trim();
            var (chainArg, next) = BalancedSlice(t, callOpen, '(', ')');
            switch (chained)
            {
                case "sort": sort = ParseDoc(chainArg); break;
                case "skip": skip = ParseInt(chainArg); break;
                case "limit": limit = ParseInt(chainArg); break;
                default: throw new FormatException($"Unsupported modifier '.{chained}()'.");
            }

            i = next;
        }

        switch (method)
        {
            case "find":
            {
                // find(filter, projection) — both optional.
                var (filterArg, projArg) = SplitTopLevelComma(args);
                return new MongoQuery
                {
                    Kind = MongoQueryKind.Find,
                    Collection = collection,
                    Filter = string.IsNullOrWhiteSpace(filterArg) ? new BsonDocument() : ParseDoc(filterArg),
                    Projection = string.IsNullOrWhiteSpace(projArg) ? null : ParseDoc(projArg),
                    Sort = sort,
                    Skip = skip,
                    Limit = limit
                };
            }

            case "aggregate":
                return new MongoQuery
                {
                    Kind = MongoQueryKind.Aggregate,
                    Collection = collection,
                    Pipeline = ParseArray(args)
                };

            default:
                throw new FormatException($"Unsupported operation '.{method}()'. Use find or aggregate.");
        }
    }

    // --- SELECT * FROM <collection> [WHERE <filter>] [ORDER BY <cols>] [LIMIT n] [OFFSET m] ---------
    // This is the text the host generates for "Browse Table". The WHERE clause carries whatever the
    // user typed into the filter box (see the class remarks for the accepted forms) plus any per-column
    // inline LIKE filters; ORDER BY carries a clicked column header.
    private static MongoQuery ParseBrowse(string t)
    {
        var fromIdx = IndexOfWordTopLevel(t, "FROM", 0);
        if (fromIdx < 0)
        {
            throw new FormatException("Expected FROM <collection>.");
        }

        var pos = fromIdx + 4;
        while (pos < t.Length && char.IsWhiteSpace(t[pos])) pos++;
        var (collectionToken, afterCollection) = ReadToken(t, pos);
        var collection = Unquote(collectionToken);

        // Locate the trailing clause keywords, ignoring any that appear inside the WHERE filter's
        // JSON/strings (hence the depth-aware search).
        var whereIdx = IndexOfWordTopLevel(t, "WHERE", afterCollection);
        var orderIdx = IndexOfWordTopLevel(t, "ORDER", afterCollection);
        var limitIdx = IndexOfWordTopLevel(t, "LIMIT", afterCollection);
        var offsetIdx = IndexOfWordTopLevel(t, "OFFSET", afterCollection);

        BsonDocument filter = new();
        if (whereIdx >= 0)
        {
            var end = ClauseEnd(whereIdx, t.Length, orderIdx, limitIdx, offsetIdx);
            filter = ParseWhere(t[(whereIdx + 5)..end].Trim());
        }

        BsonDocument? sort = null;
        if (orderIdx >= 0)
        {
            var end = ClauseEnd(orderIdx, t.Length, limitIdx, offsetIdx);
            var orderText = t[(orderIdx + 5)..end].Trim();
            if (orderText.StartsWith("BY", StringComparison.OrdinalIgnoreCase)) orderText = orderText[2..].Trim();
            sort = ParseOrderBy(orderText);
        }

        return new MongoQuery
        {
            Kind = MongoQueryKind.Find,
            Collection = collection,
            Filter = filter,
            Sort = sort,
            Limit = limitIdx >= 0 ? ReadIntAt(t, limitIdx + 5) : null,
            Skip = offsetIdx >= 0 ? ReadIntAt(t, offsetIdx + 6) : null
        };
    }

    // Translate a browse WHERE clause into a Mongo filter. Two kinds of conjunct are understood:
    // a MongoDB filter document typed into the general filter box (e.g. { "age": { "$gt": 30 } }), and
    // the host's per-column inline filter (CAST("col" AS ...) LIKE '%val%'), mapped to a case-insensitive
    // $regex. Multiple conjuncts (joined by AND) combine under $and.
    private static BsonDocument ParseWhere(string whereText)
    {
        var conditions = new List<BsonDocument>();
        foreach (var raw in SplitTopLevelAnd(whereText))
        {
            var part = raw.Trim();
            if (part.Length == 0) continue;

            if (part[0] == '{')
            {
                conditions.Add(BsonDocument.Parse(part));
                continue;
            }

            var like = LikeClause.Match(part);
            if (like.Success)
            {
                var column = Unquote(like.Groups["col"].Value);
                var pattern = like.Groups["pat"].Value.Replace("''", "'");
                conditions.Add(new BsonDocument(column,
                    new BsonDocument { { "$regex", LikeToRegex(pattern) }, { "$options", "i" } }));
                continue;
            }

            throw new FormatException(
                "The filter box expects a MongoDB filter document, e.g. { \"age\": { \"$gt\": 30 } }. " +
                $"Couldn't interpret: {part}");
        }

        return conditions.Count switch
        {
            0 => new BsonDocument(),
            1 => conditions[0],
            _ => new BsonDocument("$and", new BsonArray(conditions))
        };
    }

    // ORDER BY "col" DESC[, "col2" ASC] → { col: -1, col2: 1 }.
    private static BsonDocument ParseOrderBy(string orderText)
    {
        var sort = new BsonDocument();
        foreach (var segment in SplitTopLevel(orderText, ','))
        {
            var seg = segment.Trim();
            if (seg.Length == 0) continue;

            var direction = 1;
            if (seg.EndsWith("DESC", StringComparison.OrdinalIgnoreCase))
            {
                direction = -1;
                seg = seg[..^4].TrimEnd();
            }
            else if (seg.EndsWith("ASC", StringComparison.OrdinalIgnoreCase))
            {
                seg = seg[..^3].TrimEnd();
            }

            sort[Unquote(seg)] = direction;
        }

        return sort;
    }

    // The host's per-column filter shape: an optional CAST(...) wrapper around a (possibly quoted)
    // column, then LIKE '<sql-escaped pattern>'.
    private static readonly Regex LikeClause = new(
        """^(?:CAST\s*\(\s*)?(?<col>"[^"]+"|`[^`]+`|\[[^\]]+\]|[A-Za-z_][\w$.]*)\s*(?:AS\s+[^)]+\))?\s+LIKE\s+'(?<pat>(?:[^']|'')*)'$""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // SQL LIKE → anchored regex: % = any run, _ = any single char, everything else literal.
    private static string LikeToRegex(string like)
    {
        var sb = new System.Text.StringBuilder("^");
        foreach (var c in like)
        {
            switch (c)
            {
                case '%': sb.Append(".*"); break;
                case '_': sb.Append('.'); break;
                default:
                    if ("\\.^$*+?()[]{}|".IndexOf(c) >= 0) sb.Append('\\');
                    sb.Append(c);
                    break;
            }
        }

        return sb.Append('$').ToString();
    }

    // Smallest clause-keyword index strictly after start (else the string length).
    private static int ClauseEnd(int start, int length, params int[] candidates)
    {
        var end = length;
        foreach (var c in candidates)
        {
            if (c > start && c < end) end = c;
        }

        return end;
    }

    // Read the (quoted, bracketed, or bare) collection token starting at pos; returns it plus the index
    // just past it.
    private static (string Token, int End) ReadToken(string s, int pos)
    {
        if (pos >= s.Length)
        {
            throw new FormatException("Expected a collection name after FROM.");
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

    // Index of a whole-word keyword at bracket-depth 0 (outside string literals), from start.
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

    // Split on top-level " AND " (case-insensitive), ignoring AND inside brackets or strings.
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
        var start = 0;
        while (true)
        {
            var idx = IndexOfTopLevel(s, separator, start);
            if (idx < 0)
            {
                parts.Add(s[start..]);
                return parts;
            }

            parts.Add(s[start..idx]);
            start = idx + 1;
        }
    }

    // --- BSON parsing helpers ----------------------------------------------------------------------
    private static BsonDocument ParseDoc(string json) => BsonDocument.Parse(json.Trim());

    // BsonArray has no direct Parse; wrap it in a throwaway document and lift the value back out.
    private static BsonArray ParseArray(string json) =>
        BsonDocument.Parse($"{{\"_\":{json.Trim()}}}")["_"].AsBsonArray;

    private static int ParseInt(string s) =>
        int.Parse(s.Trim(), System.Globalization.CultureInfo.InvariantCulture);

    // --- Text scanning helpers (quote- and nesting-aware) ------------------------------------------

    /// <summary>Return the substring inside the brackets that open at <paramref name="openIndex"/> and
    /// the index just past their matching close. Respects string literals and nested brackets.</summary>
    private static (string Inner, int End) BalancedSlice(string s, int openIndex, char open, char close)
    {
        var depth = 0;
        for (var i = openIndex; i < s.Length; i++)
        {
            var c = s[i];
            if (c is '"' or '\'')
            {
                i = SkipString(s, i);
                continue;
            }

            if (c == open) depth++;
            else if (c == close)
            {
                depth--;
                if (depth == 0)
                {
                    return (s[(openIndex + 1)..i], i + 1);
                }
            }
        }

        throw new FormatException($"Unbalanced '{open}{close}'.");
    }

    /// <summary>Split a find(...) argument list into its first two top-level, comma-separated args.</summary>
    private static (string First, string Second) SplitTopLevelComma(string args)
    {
        var comma = IndexOfTopLevel(args, ',', 0);
        return comma < 0 ? (args, string.Empty) : (args[..comma], args[(comma + 1)..]);
    }

    private static int IndexOfTopLevel(string s, char target, int start)
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
            else if (c == target && depth == 0) return i;
        }

        return -1;
    }

    // Index of the closing quote of a string literal starting at openQuote (handles backslash escapes).
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

        // Also tolerate [bracketed] identifiers the host might emit.
        if (s.Length >= 2 && s[0] == '[' && s[^1] == ']')
        {
            return s[1..^1];
        }

        return s;
    }

    // Strip // line comments and /* block */ comments outside string literals.
    private static string StripComments(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c is '"' or '\'')
            {
                var end = SkipString(s, i);
                sb.Append(s, i, end - i + 1);
                i = end;
                continue;
            }

            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n') i++;
                sb.Append('\n');
                continue;
            }

            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/')) i++;
                i++; // land on '/', loop increments past it
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}
