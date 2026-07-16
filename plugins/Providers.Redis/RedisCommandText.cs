namespace SqlExplorer.Providers.Redis;

/// <summary>
/// Tokenizes one Redis command line the way <c>redis-cli</c> does: whitespace-separated words, with
/// single/double-quoted segments kept as one token (so a value containing spaces can be passed as
/// <c>SET mykey "hello world"</c>).
/// </summary>
internal static class RedisCommandText
{
    public static IReadOnlyList<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var inToken = false;
        char? quote = null;

        foreach (var c in text)
        {
            if (quote is { } q)
            {
                if (c == q)
                {
                    quote = null;
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                inToken = true;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (inToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    inToken = false;
                }

                continue;
            }

            current.Append(c);
            inToken = true;
        }

        if (inToken)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
