namespace Lionear.SqlExplorer.Core.Session;

/// <summary>One restored query tab: its connection, selected database, and the SQL it held. Browse tabs
/// aren't persisted — they reopen from the tree.</summary>
public sealed record OpenTabState(string ConnectionId, string? Database, string Sql);
