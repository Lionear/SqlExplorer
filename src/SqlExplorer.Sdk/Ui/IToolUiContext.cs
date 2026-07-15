using SqlExplorer.Sdk.Connections;
using SqlExplorer.Sdk.Query;
using SqlExplorer.Sdk.Schema;

namespace SqlExplorer.Sdk.Ui;

/// <summary>
/// Read/write access to a tool's input values, handed to a tool's custom view
/// (<see cref="ICustomToolUi"/>). Mirrors <see cref="IConnectionUiContext"/>: the view reads/writes by
/// <c>ToolField.Key</c> so the host collects exactly what the plugin's own UI gathered.
/// </summary>
public interface IToolUiContext
{
    string? GetValue(string key);

    void SetValue(string key, string? value);

    /// <summary>
    /// Run a read-only query against the tool's target connection/database, for a Route B view that must
    /// show live data the moment it opens (e.g. the Shrink dialog's current file sizes) — data that can't
    /// be expressed as static <c>ToolField</c>s. The host runs it through the same provider/profile it
    /// will hand <see cref="IToolPlugin.ExecuteAsync"/>. Additive to the tool contract: existing plugins
    /// that never call it are unaffected, so it needs no host-API bump.
    /// </summary>
    Task<QueryResult> QueryAsync(string sql, CancellationToken ct);

    // ── Additive Route-B helpers (a view that ignores these is unaffected) ─────────────────────────────

    /// <summary>The same provider/profile/node the host will hand <see cref="IToolPlugin.ExecuteAsync"/>,
    /// so a custom view can walk the schema (e.g. build an object-selection tree) against the exact context
    /// the tool runs on. <see cref="Node"/> is the launch node, or null at the connection root.</summary>
    IDbProvider Provider { get; }
    ConnectionProfile Profile { get; }
    DbNodeRef? Node { get; }

    /// <summary>Show a save/open file picker (the same one a Route A File field uses), so a custom view can
    /// host its own Browse button. Returns the chosen path, or null if cancelled.</summary>
    Task<string?> PickSaveFileAsync(string suggestedName, params string[] extensions);
    Task<string?> PickOpenFileAsync(params string[] extensions);
}
