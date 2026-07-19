namespace SqlExplorer.Core.Mcp;

/// <summary>One recorded MCP audit event (SE-159): when, which tool, against which connection, whether it was
/// allowed, and the host's reason. The structured form behind the <c>[MCP ALLOW|DENY]</c> Trace line, so the
/// "AI activity" panel can render columns instead of parsing text.</summary>
public sealed record McpActivityEntry(DateTime TimestampUtc, string Tool, string? ConnectionId, bool Allowed, string? Reason);

/// <summary>
/// In-memory ring of recent MCP audit events (SE-159) so the "AI activity" panel can show what an AI is doing
/// over MCP — extra relevant now MCP can create connections (SE-155). Session-only (never persisted) and
/// bounded. Thread-safe: MCP calls arrive on the server's background threads while the UI reads on the UI
/// thread, so writes lock and <see cref="Recorded"/> fires off the calling (non-UI) thread — subscribers
/// marshal to the UI thread themselves.
/// </summary>
public sealed class McpActivityLog
{
    private const int Capacity = 500;
    private readonly LinkedList<McpActivityEntry> _entries = new();
    private readonly object _gate = new();

    /// <summary>Raised after each recorded entry (on the recording thread, not the UI thread).</summary>
    public event Action<McpActivityEntry>? Recorded;

    /// <summary>Record one event, evicting the oldest past the capacity cap.</summary>
    public void Record(McpActivityEntry entry)
    {
        lock (_gate)
        {
            _entries.AddFirst(entry);
            while (_entries.Count > Capacity)
            {
                _entries.RemoveLast();
            }
        }

        Recorded?.Invoke(entry);
    }

    /// <summary>A snapshot of the retained entries, newest first.</summary>
    public IReadOnlyList<McpActivityEntry> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToList();
        }
    }
}
