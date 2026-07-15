using SqlExplorer.Core.History;

namespace SqlExplorer.App.ViewModels;

/// <summary>Display wrapper over a logged <see cref="QueryHistoryEntry"/> — pre-formats the fields the
/// Query Log grid and detail pane bind to, and keeps the raw entry for copy/export.</summary>
public sealed class QueryLogRow
{
    public required QueryHistoryEntry Entry { get; init; }

    public string Time => Entry.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public bool IsAi => Entry.Source == QueryHistorySource.Ai;
    public string Source => IsAi ? "AI · MCP" : "App";
    public string Connection => Entry.ConnectionName;
    public string Sql => Entry.Sql;
    public string SqlOneLine => OneLine(Entry.Sql);
    public string Duration => $"{Entry.DurationMs} ms";
    public string Rows => Entry.RowCount.ToString("N0");
    public bool Success => Entry.Success;
    public string Status => Entry.Success ? "Success" : "Error";
    public string? Error => Entry.Error;

    // Collapse newlines/tabs/runs of spaces so the grid cell stays a single tidy line.
    private static string OneLine(string sql)
    {
        var chars = new char[sql.Length];
        var n = 0;
        var lastSpace = false;
        foreach (var c in sql)
        {
            var isWs = c is ' ' or '\t' or '\r' or '\n';
            if (isWs)
            {
                if (n == 0 || lastSpace)
                {
                    continue;
                }
                chars[n++] = ' ';
                lastSpace = true;
            }
            else
            {
                chars[n++] = c;
                lastSpace = false;
            }
        }
        return new string(chars, 0, n).TrimEnd();
    }
}
