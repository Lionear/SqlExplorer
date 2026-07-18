namespace SqlExplorer.Core.Session;

/// <summary>One restored query tab: its connection, selected database, the SQL it held, and — when the
/// tab was backed by a file (SE-154) — the <c>.sql</c> file path so the association returns on the next
/// launch. Browse tabs aren't persisted — they reopen from the tree. <see cref="FilePath"/> is optional
/// and defaults to null, so open-tabs.json written before SE-154 (no such field) still loads.</summary>
public sealed record OpenTabState(string ConnectionId, string? Database, string Sql, string? FilePath = null);
