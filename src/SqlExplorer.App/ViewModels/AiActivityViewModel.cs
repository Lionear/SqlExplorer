using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlExplorer.Core.Connections;
using SqlExplorer.Core.Localization;
using SqlExplorer.Core.Mcp;

namespace SqlExplorer.App.ViewModels;

/// <summary>One row in the AI-activity panel (SE-159): a single MCP audit event, with its connection resolved
/// to a display name and its verdict/time formatted for the grid.</summary>
public sealed record AiActivityRow(DateTime TimeUtc, string Tool, string Connection, bool Allowed, string? Reason)
{
    public string TimeText => TimeUtc.ToLocalTime().ToString("HH:mm:ss");
    public string VerdictText => Allowed ? "ALLOW" : "DENY";
    public bool IsDeny => !Allowed;
}

/// <summary>
/// Backs the "AI activity" tool panel (SE-159): a live, read-only view of what an AI is doing over MCP —
/// each allow/deny the host audited, newest first. Seeds from the <see cref="McpActivityLog"/> snapshot and
/// then appends as events arrive (marshalled to the UI thread, since MCP calls run on background threads).
/// Session-only, like the log itself.
/// </summary>
public sealed partial class AiActivityViewModel : ObservableObject
{
    private const int MaxRows = 500;
    private readonly McpActivityLog _log;
    private readonly ConnectionService _connections;

    public AiActivityViewModel(McpActivityLog log, ConnectionService connections, ILocalizer localizer)
    {
        _log = log;
        _connections = connections;
        Loc = localizer;

        foreach (var entry in _log.Snapshot()) // snapshot is newest-first; keep that order
        {
            Rows.Add(ToRow(entry));
        }

        _log.Recorded += OnRecorded;
    }

    public ILocalizer Loc { get; }

    /// <summary>Recorded MCP events, newest first.</summary>
    public ObservableCollection<AiActivityRow> Rows { get; } = [];

    private void OnRecorded(McpActivityEntry entry) =>
        Dispatcher.UIThread.Post(() =>
        {
            Rows.Insert(0, ToRow(entry));
            while (Rows.Count > MaxRows)
            {
                Rows.RemoveAt(Rows.Count - 1);
            }
        });

    private AiActivityRow ToRow(McpActivityEntry entry)
    {
        var connection = entry.ConnectionId is null
            ? "—"
            : _connections.List().Concat(_connections.ListTransient())
                  .FirstOrDefault(c => c.Id == entry.ConnectionId)?.Name
              ?? entry.ConnectionId;
        return new AiActivityRow(entry.TimestampUtc, entry.Tool, connection, entry.Allowed, entry.Reason);
    }

    [RelayCommand]
    private void Clear() => Rows.Clear();
}
