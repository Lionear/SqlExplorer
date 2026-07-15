using SqlExplorer.Core.History;

namespace SqlExplorer.Core.Logging;

/// <summary>
/// Opt-in audit log of executed queries, separate from the always-on, re-runnable query history. Whether
/// an entry is actually written is decided here from the live policy set through <see cref="Configure"/>:
/// a master switch plus per-source scope (application vs AI/MCP), so the user can record only one side,
/// both, or nothing. Like the history it stores only the SQL and timing/outcome metadata carried by a
/// <see cref="QueryHistoryEntry"/> — never result rows or connection secrets.
/// </summary>
public interface IQueryLog
{
    /// <summary>Raised after the log changes (an entry was written, or the log was cleared) so an open
    /// viewer can refresh.</summary>
    event Action? Changed;

    /// <summary>Set the live logging policy. Called once at startup and again whenever settings are saved,
    /// so toggling it takes effect without a restart.</summary>
    void Configure(bool enabled, bool logApp, bool logMcp);

    /// <summary>Persist the entry when logging is enabled and its <see cref="QueryHistoryEntry.Source"/> is
    /// in scope; a no-op otherwise. Never throws — logging must not break query execution.</summary>
    void Record(QueryHistoryEntry entry);

    /// <summary>Most-recent-first entries matching <paramref name="filter"/>.</summary>
    IReadOnlyList<QueryHistoryEntry> Read(QueryLogFilter filter);

    /// <summary>Delete the whole log (current file and its rotated backup).</summary>
    void Clear();
}
