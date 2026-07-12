namespace Lionear.SqlExplorer.Core.Settings;

/// <summary>Loads and persists <see cref="AppSettings"/> for the current user.</summary>
public interface IAppSettingsStore
{
    /// <summary>Returns the stored settings, or a fresh empty instance if none exist yet.</summary>
    AppSettings Load();

    void Save(AppSettings settings);
}
