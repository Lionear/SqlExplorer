using System.Text.RegularExpressions;

namespace SqlExplorer.Providers.DragonflyDb;

internal enum DragonflyDbQueryKind { Browse, Command }

/// <summary>
/// A query-editor string parsed into either a "browse this key" request or a literal Redis/Dragonfly
/// command line. DragonflyDB is RESP-compatible with Redis, so the accepted input shapes are identical to
/// the Redis provider's <c>RedisQuery</c>:
/// <list type="bullet">
/// <item>The host's generated browse text: <c>SELECT * FROM "mykey" LIMIT 1000 OFFSET 0</c> — a
///   type-agnostic peek at the key (the actual command depends on a live <c>TYPE</c> lookup, since
///   a key's shape isn't known until queried).</item>
/// <item>A bare key name: <c>mykey</c> — same as above (except for the handful of genuinely zero-argument
///   commands like <c>PING</c>/<c>DBSIZE</c>).</item>
/// <item>A literal command line: <c>HGETALL mykey</c>, <c>SET mykey "hello world"</c>, etc. — tokenized by
///   <see cref="DragonflyDbCommandText"/> and run via the driver's low-level <c>Execute</c>.</item>
/// </list>
/// </summary>
internal sealed class DragonflyDbQuery
{
    public required DragonflyDbQueryKind Kind { get; init; }
    public string Key { get; init; } = string.Empty;
    public int? Limit { get; init; }
    public int? Offset { get; init; }
    public string Command { get; init; } = string.Empty;
    public IReadOnlyList<string> Args { get; init; } = [];

    private static readonly Regex BrowseText = new(
        @"^SELECT\s+\*\s+FROM\s+(?<key>""(?:[^""]|"""")*""|\S+)(?:\s+LIMIT\s+(?<limit>\d+))?(?:\s+OFFSET\s+(?<offset>\d+))?\s*;?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Genuinely zero-argument commands — without this list, a bare "PING" would be mistaken for the
    // "bare key name" browse convenience below.
    private static readonly HashSet<string> ZeroArgCommands =
        new(StringComparer.OrdinalIgnoreCase) { "PING", "DBSIZE", "FLUSHDB", "FLUSHALL" };

    public static DragonflyDbQuery Parse(string text)
    {
        var t = text.Trim().TrimEnd(';').Trim();
        if (t.Length == 0)
        {
            throw new FormatException("Empty command.");
        }

        var browse = BrowseText.Match(t);
        if (browse.Success)
        {
            return new DragonflyDbQuery
            {
                Kind = DragonflyDbQueryKind.Browse,
                Key = Unquote(browse.Groups["key"].Value),
                Limit = browse.Groups["limit"] is { Success: true } l ? int.Parse(l.Value) : null,
                Offset = browse.Groups["offset"] is { Success: true } o ? int.Parse(o.Value) : null
            };
        }

        if (!t.Contains(' ') && !t.Contains('\t'))
        {
            if (ZeroArgCommands.Contains(t))
            {
                return new DragonflyDbQuery { Kind = DragonflyDbQueryKind.Command, Command = t.ToUpperInvariant() };
            }

            return new DragonflyDbQuery { Kind = DragonflyDbQueryKind.Browse, Key = Unquote(t) };
        }

        var tokens = DragonflyDbCommandText.Tokenize(t);
        if (tokens.Count == 0)
        {
            throw new FormatException("Empty command.");
        }

        return new DragonflyDbQuery
        {
            Kind = DragonflyDbQueryKind.Command,
            Command = tokens[0].ToUpperInvariant(),
            Args = tokens.Skip(1).ToList()
        };
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        return s.Length >= 2 && s[0] == s[^1] && s[0] is '"' or '\'' or '`' ? s[1..^1] : s;
    }
}
