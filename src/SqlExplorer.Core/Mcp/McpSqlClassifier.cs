using System.Text;
using SqlExplorer.Core.Connections;

namespace SqlExplorer.Core.Mcp;

/// <summary>What a submitted SQL string is, for the MCP write-guard (CRIT-2). Classification is the
/// security boundary that keeps an AI client from writing/DDL-ing beyond what a connection's
/// <see cref="AiAccessMode"/> permits, so it is deliberately conservative: anything it can't prove is a
/// single read/DML statement is <see cref="Unknown"/> (rejected).</summary>
public enum SqlStatementKind
{
    /// <summary>Nothing but whitespace/comments.</summary>
    Empty,

    /// <summary>A single read: SELECT / EXPLAIN / SHOW / a WITH…SELECT with no write keyword.</summary>
    Read,

    /// <summary>A single data-modifying statement: INSERT / UPDATE / DELETE / MERGE / REPLACE.</summary>
    Dml,

    /// <summary>A single schema/permission statement: CREATE / ALTER / DROP / TRUNCATE / GRANT / … Always
    /// rejected over MCP regardless of access mode (plan §1 non-goal).</summary>
    Ddl,

    /// <summary>More than one statement in the payload (e.g. <c>SELECT 1; DROP TABLE x</c>) — rejected
    /// wholesale so a benign leading statement can't smuggle a second one past the whitelist.</summary>
    Multiple,

    /// <summary>Unrecognised leading keyword, or anything the classifier can't prove safe — rejected.</summary>
    Unknown
}

/// <summary>
/// Host-side SQL classification for the MCP write-guard. Strips comments and string/identifier literals
/// first (so <c>SELECT 1 -- ; DROP</c> or <c>';'</c> inside a literal can't fool the keyword/semicolon
/// scan), rejects multi-statement payloads, then classifies the single remaining statement by its leading
/// keyword. Not a full SQL parser — a whitelist-by-leading-keyword, defaulting to rejection on any doubt.
/// </summary>
public static class McpSqlClassifier
{
    private static readonly HashSet<string> ReadKeywords =
        new(StringComparer.OrdinalIgnoreCase) { "SELECT", "EXPLAIN", "SHOW", "DESCRIBE", "DESC", "PRAGMA", "VALUES", "TABLE" };

    private static readonly HashSet<string> DmlKeywords =
        new(StringComparer.OrdinalIgnoreCase) { "INSERT", "UPDATE", "DELETE", "MERGE", "REPLACE", "UPSERT" };

    private static readonly HashSet<string> DdlKeywords =
        new(StringComparer.OrdinalIgnoreCase)
        { "CREATE", "ALTER", "DROP", "TRUNCATE", "GRANT", "REVOKE", "RENAME", "COMMENT", "USE", "SET", "EXEC", "EXECUTE", "CALL", "DECLARE", "BEGIN", "COMMIT", "ROLLBACK", "VACUUM", "ATTACH", "DETACH", "REINDEX", "ANALYZE", "COPY", "LOAD", "IMPORT", "BACKUP", "RESTORE", "KILL", "SHUTDOWN" };

    // Write keywords that make a WITH (CTE) statement a data-modification rather than a read.
    private static readonly HashSet<string> WithWriteKeywords =
        new(StringComparer.OrdinalIgnoreCase) { "INSERT", "UPDATE", "DELETE", "MERGE", "REPLACE" };

    /// <summary>Classify <paramref name="sql"/>. Never throws — anything unclear returns
    /// <see cref="SqlStatementKind.Unknown"/> or <see cref="SqlStatementKind.Multiple"/>.</summary>
    public static SqlStatementKind Classify(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return SqlStatementKind.Empty;
        }

        var stripped = StripCommentsAndLiterals(sql);
        if (string.IsNullOrWhiteSpace(stripped))
        {
            return SqlStatementKind.Empty;
        }

        // Reject anything with a second statement: split on top-level ';' (literals already blanked), and
        // if more than one non-empty segment remains, it's multi-statement.
        var segments = stripped.Split(';').Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (segments.Count > 1)
        {
            return SqlStatementKind.Multiple;
        }

        var statement = segments.Count == 0 ? string.Empty : segments[0].Trim();
        var leading = LeadingKeyword(statement);
        if (leading.Length == 0)
        {
            return SqlStatementKind.Unknown;
        }

        if (DmlKeywords.Contains(leading))
        {
            return SqlStatementKind.Dml;
        }

        if (DdlKeywords.Contains(leading))
        {
            return SqlStatementKind.Ddl;
        }

        if (leading.Equals("WITH", StringComparison.OrdinalIgnoreCase))
        {
            // A CTE is a read unless it drives a write (WITH x AS (…) DELETE/INSERT/UPDATE/MERGE …).
            return ContainsWordFrom(statement, WithWriteKeywords) ? SqlStatementKind.Dml : SqlStatementKind.Read;
        }

        if (ReadKeywords.Contains(leading))
        {
            return SqlStatementKind.Read;
        }

        return SqlStatementKind.Unknown;
    }

    /// <summary>Whether <paramref name="sql"/> is permitted for a connection at <paramref name="mode"/>,
    /// applying the plan §5 table: reads need ReadOnly+, DML needs ReadWrite, DDL needs Sandbox (transient
    /// loopback only, gated at creation), and multi/unknown are always rejected. None permits nothing.</summary>
    public static bool IsAllowed(string? sql, AiAccessMode mode) => mode switch
    {
        AiAccessMode.None => false,
        AiAccessMode.ReadOnly => Classify(sql) == SqlStatementKind.Read,
        AiAccessMode.ReadWrite => Classify(sql) is SqlStatementKind.Read or SqlStatementKind.Dml,
        AiAccessMode.Sandbox => Classify(sql) is SqlStatementKind.Read or SqlStatementKind.Dml or SqlStatementKind.Ddl,
        _ => false
    };

    // Blank out -- line comments, /* */ block comments (SQL Server allows nesting), and the contents of
    // '…' string literals and "…"/[…]/`…` quoted identifiers — replacing them with spaces so keyword and
    // semicolon scanning can't be fooled by a semicolon or keyword hidden inside them.
    private static string StripCommentsAndLiterals(string sql)
    {
        var sb = new StringBuilder(sql.Length);
        var i = 0;
        var block = 0; // block-comment nesting depth
        while (i < sql.Length)
        {
            var c = sql[i];

            if (block > 0)
            {
                if (c == '/' && Next(sql, i) == '*') { block++; sb.Append("  "); i += 2; continue; }
                if (c == '*' && Next(sql, i) == '/') { block--; sb.Append("  "); i += 2; continue; }
                sb.Append(' ');
                i++;
                continue;
            }

            switch (c)
            {
                case '-' when Next(sql, i) == '-':
                    while (i < sql.Length && sql[i] != '\n') { sb.Append(' '); i++; }
                    continue;
                case '/' when Next(sql, i) == '*':
                    block = 1; sb.Append("  "); i += 2;
                    continue;
                case '\'':
                    i = SkipLiteral(sql, i, '\'', sb);
                    continue;
                case '"':
                    i = SkipLiteral(sql, i, '"', sb);
                    continue;
                case '`':
                    i = SkipLiteral(sql, i, '`', sb);
                    continue;
                case '[':
                    i = SkipBracket(sql, i, sb);
                    continue;
                default:
                    sb.Append(c);
                    i++;
                    continue;
            }
        }

        return sb.ToString();
    }

    private static char Next(string s, int i) => i + 1 < s.Length ? s[i + 1] : '\0';

    // Blank a '…'/"…"/`…` literal, honouring doubled-quote escapes ('' inside '…'), keeping length so
    // offsets are irrelevant. Returns the index just past the closing quote (or end of string).
    private static int SkipLiteral(string s, int start, char quote, StringBuilder sb)
    {
        sb.Append(' ');
        var i = start + 1;
        while (i < s.Length)
        {
            if (s[i] == quote)
            {
                if (Next(s, i) == quote) { sb.Append("  "); i += 2; continue; } // escaped quote
                sb.Append(' ');
                return i + 1;
            }

            sb.Append(' ');
            i++;
        }

        return i;
    }

    // SQL Server [quoted identifier]: ']' is escaped by ']]'.
    private static int SkipBracket(string s, int start, StringBuilder sb)
    {
        sb.Append(' ');
        var i = start + 1;
        while (i < s.Length)
        {
            if (s[i] == ']')
            {
                if (Next(s, i) == ']') { sb.Append("  "); i += 2; continue; }
                sb.Append(' ');
                return i + 1;
            }

            sb.Append(' ');
            i++;
        }

        return i;
    }

    private static string LeadingKeyword(string statement)
    {
        var start = 0;
        while (start < statement.Length && !char.IsLetter(statement[start]))
        {
            start++;
        }

        var end = start;
        while (end < statement.Length && (char.IsLetter(statement[end]) || statement[end] == '_'))
        {
            end++;
        }

        return statement[start..end];
    }

    private static bool ContainsWordFrom(string text, HashSet<string> words)
    {
        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = token.Trim('(', ')', ',', ';');
            if (words.Contains(trimmed))
            {
                return true;
            }
        }

        return false;
    }
}
