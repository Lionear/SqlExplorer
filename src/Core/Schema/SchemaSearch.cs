namespace Lionear.SqlExplorer.Core.Schema;

/// <summary>
/// Quick-open ranking (1.2): 0 = starts-with, 1 = contains, 2 = fuzzy subsequence (characters of the
/// query appear in order, not necessarily adjacent — VSCode/DBeaver quick-open style). No match at
/// all returns false and leaves <paramref name="rank"/> unset.
/// </summary>
public static class SchemaSearch
{
    public static bool TryRank(string text, string query, out int rank)
    {
        if (text.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            rank = 0;
            return true;
        }

        if (text.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            rank = 1;
            return true;
        }

        if (IsSubsequence(text, query))
        {
            rank = 2;
            return true;
        }

        rank = default;
        return false;
    }

    public static bool IsSubsequence(string text, string query)
    {
        var i = 0;
        foreach (var ch in text)
        {
            if (i < query.Length && char.ToLowerInvariant(ch) == char.ToLowerInvariant(query[i]))
            {
                i++;
            }
        }

        return i == query.Length;
    }
}
