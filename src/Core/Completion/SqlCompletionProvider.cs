using System.Text.RegularExpressions;
using Lionear.SqlExplorer.Core.Schema;
using Lionear.SqlExplorer.Sdk;

namespace Lionear.SqlExplorer.Core.Completion;

public enum CompletionKind
{
    Table,
    Column,
    Keyword
}

/// <summary>One completion suggestion: the text to insert, its kind, and a short detail
/// (column type, "table"/"view", or "keyword") shown alongside it.</summary>
public sealed record CompletionItem(string Text, CompletionKind Kind, string? Detail);

/// <summary><see cref="SqlCompletionProvider.Suggest"/>'s result: where the replacement starts
/// (caret minus the word already typed) and the ranked items to offer.</summary>
public sealed record CompletionResult(int ReplaceStart, IReadOnlyList<CompletionItem> Items);

/// <summary>
/// Light, parser-free SQL completion (1.3) driven by the 1.1 schema snapshot: "alias." suggests that
/// alias's columns, right after FROM/JOIN suggests tables, everywhere else suggests a broad mix of
/// tables + columns + dialect keywords. Ranking reuses <see cref="SchemaSearch"/> so results order the
/// same way quick-open (1.2) does. No SQL parser — a regex over FROM/JOIN clauses is enough for MVP.
/// </summary>
public static class SqlCompletionProvider
{
    private const int MaxItems = 200;

    // An identifier is a plain word OR a "quoted one" — needed because PascalCase table names (like
    // this app's own schema, "Accounts"/"Characters"/…) must be double-quoted in Postgres or the
    // engine folds them to lowercase, so real-world queries against such a schema are full of them.
    private const string IdentPattern = @"(?:""[^""]+""|[A-Za-z_]\w*)";

    // Captures "FROM/JOIN <table>[.<table>] [[AS] <alias>]"; the alias group may also (harmlessly)
    // capture a following keyword like WHERE when there is no real alias — filtered out via `keywords`.
    private static readonly Regex TableRefPattern = new(
        $@"(?:FROM|JOIN)\s+({IdentPattern}(?:\.{IdentPattern})?)(?:\s+(?:AS\s+)?({IdentPattern}))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static CompletionResult Suggest(string sql, int caret, SchemaSnapshot snapshot, IReadOnlySet<string> keywords)
    {
        caret = Math.Clamp(caret, 0, sql.Length);
        var (start, fragment, alias) = SplitWord(sql, caret);

        var items = alias is not null
            ? ColumnsFor(alias, fragment, sql, snapshot, keywords)
            : IsAfterFromOrJoin(sql, start)
                ? Tables(fragment, snapshot)
                : Broad(fragment, snapshot, keywords);

        return new CompletionResult(start, items.Take(MaxItems).ToList());
    }

    // The identifier fragment being typed at the caret, plus the alias before a "." if there is one
    // (e.g. caret after "u.na" in "u.name" → fragment "na", alias "u").
    private static (int Start, string Fragment, string? Alias) SplitWord(string sql, int caret)
    {
        var start = caret;
        while (start > 0 && IsWordChar(sql[start - 1]))
        {
            start--;
        }

        var fragment = sql[start..caret];

        var alias = start > 0 && sql[start - 1] == '.' ? ExtractIdentifierBefore(sql, start - 1) : null;
        return (start, fragment, alias);
    }

    // Scans backward from `end` (exclusive) over a plain identifier or a "quoted identifier" and
    // returns its unquoted text — the alias in "Accounts". just as much as in u. — or null when
    // there's nothing identifier-shaped there.
    private static string? ExtractIdentifierBefore(string sql, int end)
    {
        if (end <= 0)
        {
            return null;
        }

        if (sql[end - 1] == '"')
        {
            var closeQuote = end - 1;
            var openQuote = sql.LastIndexOf('"', closeQuote - 1);
            return openQuote >= 0 && closeQuote > openQuote + 1 ? sql[(openQuote + 1)..closeQuote] : null;
        }

        var start = end;
        while (start > 0 && IsWordChar(sql[start - 1]))
        {
            start--;
        }

        return end > start ? sql[start..end] : null;
    }

    private static bool IsAfterFromOrJoin(string sql, int wordStart)
    {
        var i = wordStart;
        while (i > 0 && char.IsWhiteSpace(sql[i - 1]))
        {
            i--;
        }

        var end = i;
        while (i > 0 && IsWordChar(sql[i - 1]))
        {
            i--;
        }

        var token = sql[i..end];
        return token.Equals("FROM", StringComparison.OrdinalIgnoreCase) || token.Equals("JOIN", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<CompletionItem> Tables(string fragment, SchemaSnapshot snapshot) =>
        RankBy(snapshot.Objects, o => o.QualifiedName, fragment)
            .Select(o => new CompletionItem(o.QualifiedName, CompletionKind.Table, o.Kind == DbNodeKind.View ? "view" : "table"))
            .ToList();

    // Columns of the aliased table when the alias resolves against the FROM/JOIN clauses; otherwise
    // (unknown alias, or mid-typing before FROM exists) fall back to every column so the box still
    // offers something useful, distinguishing them by owning table in Detail.
    private static IReadOnlyList<CompletionItem> ColumnsFor(
        string alias, string fragment, string sql, SchemaSnapshot snapshot, IReadOnlySet<string> keywords)
    {
        var table = ResolveAlias(alias, sql, snapshot, keywords);
        var candidates = table is not null
            ? table.Columns.Select(c => (Column: c, Table: table))
            : snapshot.Objects.SelectMany(o => o.Columns.Select(c => (Column: c, Table: o)));

        return RankBy(candidates.ToList(), t => t.Column.Name, fragment)
            .Select(t => new CompletionItem(t.Column.Name, CompletionKind.Column, t.Column.Type ?? t.Table.QualifiedName))
            .ToList();
    }

    // Each category is capped BEFORE concatenating, not after: a schema with hundreds of tables/columns
    // would otherwise fill Suggest's overall MaxItems on its own and starve keywords out of the list
    // entirely (they're always small in number, so this cap essentially never trims them).
    private const int BroadCategoryCap = 60;

    private static IReadOnlyList<CompletionItem> Broad(string fragment, SchemaSnapshot snapshot, IReadOnlySet<string> keywords)
    {
        var tables = RankBy(snapshot.Objects, o => o.QualifiedName, fragment)
            .Select(o => new CompletionItem(o.QualifiedName, CompletionKind.Table, o.Kind == DbNodeKind.View ? "view" : "table"))
            .Take(BroadCategoryCap);

        var columns = RankBy(
                snapshot.Objects.SelectMany(o => o.Columns.Select(c => (Column: c, Table: o))).ToList(),
                t => t.Column.Name,
                fragment)
            .Select(t => new CompletionItem(t.Column.Name, CompletionKind.Column, t.Column.Type ?? t.Table.QualifiedName))
            .Take(BroadCategoryCap);

        var kw = RankBy(keywords, k => k, fragment)
            .Select(k => new CompletionItem(k, CompletionKind.Keyword, "keyword"))
            .Take(BroadCategoryCap);

        return tables.Concat(columns).Concat(kw).ToList();
    }

    private static SchemaObject? ResolveAlias(string alias, string sql, SchemaSnapshot snapshot, IReadOnlySet<string> keywords)
    {
        var aliasMap = BuildAliasMap(sql, keywords);
        return aliasMap.TryGetValue(alias, out var tableName)
            ? snapshot.Objects.FirstOrDefault(o => o.Name.Equals(tableName, StringComparison.OrdinalIgnoreCase))
            : null;
    }

    // Every FROM/JOIN target maps to itself (its unqualified name is always a valid "alias"), plus
    // an explicit alias when one follows and isn't actually the next clause's keyword.
    private static Dictionary<string, string> BuildAliasMap(string sql, IReadOnlySet<string> keywords)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in TableRefPattern.Matches(sql))
        {
            var tableRef = m.Groups[1].Value;
            var lastPart = tableRef.Contains('.') ? tableRef[(tableRef.LastIndexOf('.') + 1)..] : tableRef;
            var tableName = Unquote(lastPart);
            map[tableName] = tableName;

            if (m.Groups[2].Success)
            {
                var aliasCandidate = Unquote(m.Groups[2].Value);
                if (!keywords.Contains(aliasCandidate.ToUpperInvariant()))
                {
                    map[aliasCandidate] = tableName;
                }
            }
        }

        return map;
    }

    private static string Unquote(string identifier) =>
        identifier is ['"', .., '"'] ? identifier[1..^1] : identifier;

    // Fragment-ranked subset via the same TryRank order quick-open (1.2) uses; an empty fragment
    // (Ctrl+Space with nothing typed yet) keeps every candidate, capped later by Suggest.
    private static IEnumerable<T> RankBy<T>(IEnumerable<T> items, Func<T, string> text, string fragment)
    {
        if (fragment.Length == 0)
        {
            return items;
        }

        return items
            .Select(item => (Item: item, Matched: SchemaSearch.TryRank(text(item), fragment, out var rank), Rank: rank))
            .Where(t => t.Matched)
            .OrderBy(t => t.Rank)
            .ThenBy(t => text(t.Item), StringComparer.OrdinalIgnoreCase)
            .Select(t => t.Item);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
