namespace SqlExplorer.Core.Plugins;

/// <summary>
/// One row in the host's view of installed plugins: what's on disk (from discovery), whether it loaded
/// this run, and any change staged for next startup. This is the model the Store's Installed tab binds to.
/// </summary>
public sealed record InstalledPlugin(
    string Id,
    string? Name,
    string? Version,
    string? Type,
    PluginOrigin Origin,
    bool Enabled,
    PluginPendingAction Pending,
    bool Loaded,
    string? LoadError,
    string Directory)
{
    /// <summary>Bundled plugins are part of the app — they can't be disabled or uninstalled.</summary>
    public bool CanManage => Origin == PluginOrigin.UserInstalled;
}

/// <summary>How one plugin folder fared with its loader this run, flattened to the only two facts the
/// catalog cares about. Every plugin kind (provider/tool/mcp/extension) projects its own load-result into
/// this, so the catalog learns a plugin loaded regardless of which loader owned it — a new plugin kind that
/// forgets to feed its outcomes here would wrongly read as "enabled but not loaded" and pin the restart
/// banner (SE-164).</summary>
public readonly record struct PluginLoadOutcome(string PluginDirectory, bool Succeeded, string? Error);

/// <summary>
/// The host's authoritative view of installed plugins: merges what <see cref="PluginDiscovery"/> found on
/// disk with how it loaded (<see cref="PluginLoadOutcome"/> per plugin kind) and its persisted
/// <see cref="PluginStateEntry"/>. Enable/disable/uninstall stage a change in the state store that takes
/// effect on next startup (the non-collectible load contexts can't swap live) — so the mutations here update
/// the row's <see cref="InstalledPlugin.Pending"/>/<see cref="InstalledPlugin.Enabled"/> but never touch the
/// running plugin. The Store shows a "restart needed" banner off the back of that.
/// </summary>
public sealed class PluginCatalogService(
    IPluginStateStore stateStore,
    IReadOnlyList<DiscoveredPlugin> discovered,
    IEnumerable<PluginLoadOutcome> loadOutcomes)
{
    private List<InstalledPlugin> _plugins = Build(stateStore, discovered, loadOutcomes);

    public IReadOnlyList<InstalledPlugin> Installed => _plugins;

    /// <summary>
    /// True when something is staged that a restart would apply: a pending install/remove, or a user
    /// plugin whose enabled-state no longer matches how it actually loaded this run (enabled-but-not-loaded
    /// or disabled-but-still-loaded). Drives the "restart needed" banner in both the Store and the main window.
    /// </summary>
    public bool HasPendingChanges => _plugins.Any(p =>
        p.Pending != PluginPendingAction.None
        || (p.CanManage && p.LoadError is null && p.Enabled != p.Loaded));

    /// <summary>Stage a disable (user plugins only); applied on next startup.</summary>
    public void RequestDisable(string id) => SetEnabled(id, enabled: false);

    /// <summary>Stage a re-enable (user plugins only); applied on next startup.</summary>
    public void RequestEnable(string id) => SetEnabled(id, enabled: true);

    /// <summary>Stage an uninstall (user plugins only); the folder is deleted on next startup.</summary>
    public void RequestUninstall(string id)
    {
        var plugin = RequireManageable(id);
        stateStore.Save(id, stateStore.Get(id) with { Pending = PluginPendingAction.Remove });
        Replace(plugin with { Pending = PluginPendingAction.Remove });
    }

    private void SetEnabled(string id, bool enabled)
    {
        var plugin = RequireManageable(id);
        stateStore.Save(id, stateStore.Get(id) with { Enabled = enabled });
        Replace(plugin with { Enabled = enabled });
    }

    private InstalledPlugin RequireManageable(string id)
    {
        var plugin = _plugins.FirstOrDefault(p => p.Id == id)
            ?? throw new InvalidOperationException($"No installed plugin with id '{id}'.");
        if (!plugin.CanManage)
        {
            throw new InvalidOperationException($"Plugin '{id}' is bundled and cannot be changed.");
        }

        return plugin;
    }

    private void Replace(InstalledPlugin updated)
    {
        var index = _plugins.FindIndex(p => p.Id == updated.Id);
        if (index >= 0)
        {
            _plugins[index] = updated;
        }
    }

    private static List<InstalledPlugin> Build(
        IPluginStateStore stateStore,
        IReadOnlyList<DiscoveredPlugin> discovered,
        IEnumerable<PluginLoadOutcome> loadOutcomes)
    {
        // How each folder loaded, keyed by its directory (unique per discovered plugin). A disabled
        // plugin is never handed to a loader, so it simply has no outcome here.
        var outcomes = new Dictionary<string, (bool Loaded, string? Error)>(StringComparer.Ordinal);
        foreach (var r in loadOutcomes)
        {
            outcomes[r.PluginDirectory] = (r.Succeeded, r.Error);
        }

        var state = stateStore.GetAll();
        var rows = new List<InstalledPlugin>();

        foreach (var plugin in discovered)
        {
            // Unreadable manifest: fall back to the folder name as id so the row is still addressable.
            var id = plugin.Id ?? Path.GetFileName(plugin.Directory);
            var entry = state.TryGetValue(id, out var s) ? s : new PluginStateEntry();
            var (loaded, error) = outcomes.TryGetValue(plugin.Directory, out var o)
                ? o
                : (false, plugin.ManifestError);

            rows.Add(new InstalledPlugin(
                id,
                plugin.Manifest?.Name,
                plugin.Manifest?.Version,
                plugin.Manifest?.Type,
                plugin.Origin,
                entry.Enabled,
                entry.Pending,
                loaded,
                error,
                plugin.Directory));
        }

        return rows;
    }
}
