namespace Lionear.SqlExplorer.Sdk.Tools;

/// <summary>
/// Host-provided services a tool may need while running — things only the host (which owns the window)
/// can do, like showing a file picker. The resolved connection string is not here: it is already on
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
}
