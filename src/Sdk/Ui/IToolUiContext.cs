using Lionear.SqlExplorer.Sdk.Query;

namespace Lionear.SqlExplorer.Sdk.Ui;

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
}
