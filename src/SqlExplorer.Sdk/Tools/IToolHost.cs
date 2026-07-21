using SqlExplorer.Sdk.Connections;

namespace SqlExplorer.Sdk.Tools;

/// <summary>Identity of a saved connection the user could pick as a tool's secondary target — enough to
/// show it in a picker (<see cref="Name"/>) and open it (<see cref="Id"/>). Non-secret; the keychain
/// secrets are only merged in when <see cref="IToolHost.OpenConnection"/> resolves a runnable profile.</summary>
public sealed record ToolConnectionInfo(string Id, string Name, string ProviderId);

/// <summary>A secondary connection a tool opened via <see cref="IToolHost.OpenConnection"/>: a runnable
/// <see cref="ConnectionProfile"/> (keychain secrets merged in) paired with the <see cref="IDbProvider"/>
/// and provider id to run against it — the same trio a tool already has for its primary connection on
/// <see cref="ToolExecutionContext"/>.</summary>
public sealed record ToolConnection(ConnectionProfile Profile, IDbProvider Provider, string ProviderId);

/// <summary>
/// Host-provided services a tool may need while running — things only the host (which owns the window)
/// can do, like showing a file picker or resolving a second connection the user picked. The resolved
/// connection string for the <b>primary</b> connection is not here: it is already on
/// <c>ToolExecutionContext.Profile.ConnectionString</c> (the host resolves secrets from the keychain when
/// it builds the profile), so a tool that talks to its own driver reads it from there.
/// </summary>
public interface IToolHost
{
    /// <summary>Show a save-file picker; returns the chosen path, or null if cancelled.</summary>
    Task<string?> PickSaveFileAsync(string suggestedName, params string[] extensions);

    /// <summary>Show an open-file picker; returns the chosen path, or null if cancelled.</summary>
    Task<string?> PickOpenFileAsync(params string[] extensions);

    /// <summary>
    /// Read one of this tool's persistent plugin settings (declared via <c>IPluginSettings</c> and edited
    /// in Settings ▸ Plugins), by its field key — e.g. a configured default folder. Returns null when unset.
    /// </summary>
    string? GetPluginSetting(string key);

    /// <summary>The user's saved connections that share the primary connection's provider — the candidate
    /// targets for a <see cref="ToolFieldType.ConnectionPicker"/> field. The launched connection itself is
    /// excluded. Empty default so an older host (or a non-dialog host) degrades gracefully.</summary>
    IReadOnlyList<ToolConnectionInfo> ListConnections() => [];

    /// <summary>Turn a connection id (a <see cref="ToolFieldType.ConnectionPicker"/> field's collected value)
    /// into a runnable <see cref="ToolConnection"/> — keychain secrets merged in — or null if no such
    /// connection exists or its provider plugin isn't installed. <paramref name="database"/> targets a
    /// specific database/catalog on the server (a <see cref="ToolFieldType.DatabasePicker"/> value); null
    /// uses the connection's default.</summary>
    ToolConnection? OpenConnection(string connectionId, string? database = null) => null;

    /// <summary>The databases/catalogs on a saved connection, for a <see cref="ToolFieldType.DatabasePicker"/>
    /// dropdown. Empty default so an older host degrades gracefully.</summary>
    Task<IReadOnlyList<string>> ListDatabasesAsync(string connectionId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>Open a new query tab on the tool's <b>primary</b> connection and database (the one the tool
    /// was launched on), pre-filled with <paramref name="sql"/>, for the user to review and run in the normal
    /// editor. Lets a tool hand off generated SQL instead of executing DDL itself. No-op default.</summary>
    void OpenQueryEditor(string sql) { }

    /// <summary>Open a new query tab on a <b>secondary</b> connection the user picked (a
    /// <see cref="ToolFieldType.ConnectionPicker"/> value) and one of its databases, pre-filled with
    /// <paramref name="sql"/>. The destination counterpart of <see cref="OpenQueryEditor"/>: a copy/migration
    /// tool can hand its generated script to the connection it targets rather than the one it launched on.
    /// No-op default so older hosts degrade gracefully.</summary>
    void OpenQueryEditorOn(string connectionId, string? database, string sql) { }

    /// <summary>Persist one of this tool's plugin settings (the write counterpart of
    /// <see cref="GetPluginSetting"/>): the host stores <paramref name="value"/> under <paramref name="key"/>
    /// in the tool's settings, so a later run can read back a remembered choice (e.g. the last-used mode).
    /// No-op default so older hosts degrade gracefully — a tool must not depend on the value surviving.</summary>
    void SetPluginSetting(string key, string? value) { }
}
