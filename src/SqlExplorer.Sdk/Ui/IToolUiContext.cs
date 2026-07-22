using SqlExplorer.Sdk.Connections;
using SqlExplorer.Sdk.Query;
using SqlExplorer.Sdk.Schema;
using SqlExplorer.Sdk.Tools;

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

    /// <summary>The user's saved connections that share the target connection's provider — the same list a
    /// Route A <see cref="ToolFieldType.ConnectionPicker"/> offers, so a custom view can build its own
    /// destination dropdown (Copy Table). Excludes the launched connection. Empty default so a view that
    /// ignores it — and an older host — are unaffected.</summary>
    IReadOnlyList<ToolConnectionInfo> ListConnections() => [];

    /// <summary>The databases/catalogs on one of those connections, to fill a custom view's database dropdown
    /// once its connection is chosen (mirrors <see cref="IToolHost.ListDatabasesAsync"/>). Empty default.</summary>
    Task<IReadOnlyList<string>> ListDatabasesAsync(string connectionId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    // ── Lifecycle-owning views (IToolDialogLifecycle) ─────────────────────────────────────────────────

    /// <summary>The plugin's own localizer, so a custom view can translate its labels the same way the
    /// host resolves a <c>ToolField</c>'s <c>*Key</c>. Falls back to a no-op (key-as-text) localizer.</summary>
    Localization.IPluginLocalizer Localizer => Localization.EmptyPluginLocalizer.Instance;

    /// <summary>Start the run — the same thing the host's own Run button does. A view that renders its own
    /// action bar (see <see cref="IToolDialogLifecycle"/>) calls this from its primary button. Calling it
    /// again after a finished run starts a fresh one ("Copy another").</summary>
    Task RunAsync() => Task.CompletedTask;

    /// <summary>Cancel the run in flight (no-op when nothing is running).</summary>
    void CancelRun() { }

    /// <summary>Close the tool dialog.</summary>
    void CloseDialog() { }
}
