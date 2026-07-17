using System.Text.RegularExpressions;

namespace SqlExplorer.Core.Security;

/// <summary>
/// A provider-independent defence layer that redacts secrets out of query-result cell values before they
/// leave the host for an MCP client (the AI). Two heuristics, deliberately conservative (SE-145):
/// <list type="number">
///   <item>a <b>field-name</b> match (column called <c>password</c>, <c>client_secret</c>, …) redacts the
///   <b>whole cell</b> — the column exists to hold a secret;</item>
///   <item>a <b>value-pattern</b> match (a JWT, a bearer token, a PEM block, …) redacts only the
///   <b>matched span</b> inside the text, so surrounding context in a log line survives.</item>
/// </list>
/// Pattern detection is never complete: this is a safety net, not a guarantee. Nothing here logs the value
/// it redacts — only a count is surfaced. Entropy-based detection is intentionally omitted (too many false
/// positives on ids/hashes); it can become an opt-in strict mode later.
/// </summary>
public sealed partial class SecretScrubber
{
    /// <summary>Placeholder written where the whole cell is a secret (field-name heuristic).</summary>
    public const string FieldPlaceholder = "«redacted:field»";

    /// <summary>Placeholder written in place of a matched secret span inside free text.</summary>
    public const string TokenPlaceholder = "«redacted:token»";

    // Column names that, by their very name, hold a secret — the whole value is redacted regardless of shape.
    [GeneratedRegex(@"(password|passwd|pwd|secret|client[_-]?secret|token|refresh[_-]?token|access[_-]?token|api[_-]?key|apikey|credential|private[_-]?key|authorization)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FieldNameRegex();

    // Value shapes that are almost always a secret wherever they appear. Only the matched span is replaced.
    private static readonly Regex[] ValuePatterns =
    [
        // PEM private-key block (multi-line).
        new(@"-----BEGIN (?:[A-Z ]+ )?PRIVATE KEY-----[\s\S]*?-----END (?:[A-Z ]+ )?PRIVATE KEY-----",
            RegexOptions.CultureInvariant),
        // JWT: three base64url segments separated by dots, header starts with the classic {"alg":… => eyJ.
        new(@"eyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}", RegexOptions.CultureInvariant),
        // Authorization: Bearer <token> / a bare "Bearer <token>".
        new(@"Bearer\s+[A-Za-z0-9._\-~+/]{12,}=*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        // client_secret / access_token / api_key given as key=value or key: value.
        new(@"(?:client[_-]?secret|access[_-]?token|refresh[_-]?token|api[_-]?key|apikey)[""']?\s*[:=]\s*[""']?[A-Za-z0-9._\-~+/]{8,}=*",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        // AWS access key id.
        new(@"AKIA[0-9A-Z]{16}", RegexOptions.CultureInvariant),
        // GitHub tokens.
        new(@"gh[pousr]_[A-Za-z0-9]{20,}", RegexOptions.CultureInvariant),
        // Slack tokens.
        new(@"xox[baprs]-[A-Za-z0-9-]{10,}", RegexOptions.CultureInvariant),
        // Google API key.
        new(@"AIza[A-Za-z0-9_\-]{35}", RegexOptions.CultureInvariant),
    ];

    /// <summary>
    /// Redacts secrets in a materialised result. Returns the (possibly rewritten) rows and the number of
    /// cells that were altered. Column-name matches are computed once; value patterns run only on string
    /// cells of the remaining columns.
    /// </summary>
    public ScrubOutcome Scrub(IReadOnlyList<string> columnNames, IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        var secretColumn = new bool[columnNames.Count];
        var anySecretColumn = false;
        for (var c = 0; c < columnNames.Count; c++)
        {
            if (FieldNameRegex().IsMatch(columnNames[c]))
            {
                secretColumn[c] = true;
                anySecretColumn = true;
            }
        }

        var redacted = 0;
        var outRows = new List<IReadOnlyList<object?>>(rows.Count);
        foreach (var row in rows)
        {
            object?[]? rewritten = null;
            for (var c = 0; c < row.Count; c++)
            {
                var value = row[c];

                if (c < secretColumn.Length && secretColumn[c])
                {
                    if (value is null) continue; // nothing to hide
                    (rewritten ??= row.ToArray())[c] = FieldPlaceholder;
                    redacted++;
                    continue;
                }

                if (value is string s && s.Length > 0)
                {
                    var scrubbed = RedactValue(s, out var hit);
                    if (hit)
                    {
                        (rewritten ??= row.ToArray())[c] = scrubbed;
                        redacted++;
                    }
                }
            }

            outRows.Add(rewritten ?? row);
        }

        return new ScrubOutcome(outRows, redacted, anySecretColumn);
    }

    private static string RedactValue(string value, out bool hit)
    {
        var result = value;
        hit = false;
        foreach (var pattern in ValuePatterns)
        {
            if (!pattern.IsMatch(result)) continue;
            result = pattern.Replace(result, TokenPlaceholder);
            hit = true;
        }
        return result;
    }
}

/// <summary>The result of a scrub pass: the rewritten rows, how many cells changed, and whether any column
/// was redacted wholesale by name (useful for the audit line).</summary>
public sealed record ScrubOutcome(IReadOnlyList<IReadOnlyList<object?>> Rows, int RedactedCount, bool HadSecretColumn);
