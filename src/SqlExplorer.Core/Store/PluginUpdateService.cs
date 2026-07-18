using SqlExplorer.Core.Plugins;

namespace SqlExplorer.Core.Store;

/// <summary>An available update for an installed plugin: the highest compatible catalog version that is
/// newer than what's installed.</summary>
public sealed record PluginUpdate(string Id, string? CurrentVersion, StoreEntry Entry, StoreVersion Target);

/// <summary>A newer version exists for an installed plugin, but its <see cref="RequiredHostApiVersion"/> is
/// beyond what this build provides — so it's withheld until the host app is updated (SE-138 phase 4).</summary>
public sealed record HeldBackUpdate(string Id, string Name, string? CurrentVersion, string TargetVersion, int RequiredHostApiVersion);

/// <summary>
/// Cross-references installed plugins against the merged catalog to find updates, and drives
/// "Update all". Only user-installed plugins are considered (bundled ones ship with the app); a plugin is
/// updatable when the catalog's highest host-compatible version is a newer SemVer than the installed one.
/// <see cref="UpdateAllAsync"/> reuses the single-plugin install step per update and keeps going when one
/// fails, returning a per-plugin outcome.
/// </summary>
public sealed class PluginUpdateService(IPluginInstaller installer, IPluginPinStore pins)
{
    public IReadOnlyList<PluginUpdate> DetectUpdates(IReadOnlyList<InstalledPlugin> installed, StoreCatalog catalog)
    {
        var byId = catalog.Entries.ToDictionary(e => e.Entry.Id, e => e.Entry, StringComparer.Ordinal);
        var allPins = pins.GetAll();
        var updates = new List<PluginUpdate>();

        foreach (var plugin in installed)
        {
            if (plugin.Origin != PluginOrigin.UserInstalled || !byId.TryGetValue(plugin.Id, out var entry))
            {
                continue;
            }

            // Pinned = user opted out of auto-updates; skip regardless of what's on the catalog. Clearing
            // the pin (Store UI) re-enables updates on the next detect pass.
            if (allPins.ContainsKey(plugin.Id))
            {
                continue;
            }

            var target = entry.HighestCompatibleVersion(HostApiVersions.CompatFor(entry.Type));
            if (target is not null && SemVer.Compare(target.Version, plugin.Version) > 0)
            {
                updates.Add(new PluginUpdate(plugin.Id, plugin.Version, entry, target));
            }
        }

        return updates;
    }

    /// <summary>Finds installed plugins whose newest catalog version is newer than what's installed but
    /// needs a host API this build doesn't have — surfaced as "update the app first" instead of hidden
    /// (SE-138 phase 4). Same user-installed + pin filters as <see cref="DetectUpdates"/>.</summary>
    public IReadOnlyList<HeldBackUpdate> DetectHeldBack(IReadOnlyList<InstalledPlugin> installed, StoreCatalog catalog)
    {
        var byId = catalog.Entries.ToDictionary(e => e.Entry.Id, e => e.Entry, StringComparer.Ordinal);
        var allPins = pins.GetAll();
        var held = new List<HeldBackUpdate>();

        foreach (var plugin in installed)
        {
            if (plugin.Origin != PluginOrigin.UserInstalled || !byId.TryGetValue(plugin.Id, out var entry))
            {
                continue;
            }

            if (allPins.ContainsKey(plugin.Id))
            {
                continue;
            }

            // The highest catalog version newer than what's installed (compatible or not).
            StoreVersion? highest = null;
            foreach (var version in entry.Versions)
            {
                if (SemVer.Compare(version.Version, plugin.Version) > 0
                    && (highest is null || SemVer.Compare(version.Version, highest.Version) > 0))
                {
                    highest = version;
                }
            }

            // Held back only when that newest version needs a host API this build doesn't accept.
            if (highest is not null && !HostApiVersions.CompatFor(entry.Type).Accepts(highest.MinHostApiVersion))
            {
                held.Add(new HeldBackUpdate(plugin.Id, entry.Name, plugin.Version, highest.Version, highest.MinHostApiVersion));
            }
        }

        return held;
    }

    public async Task<IReadOnlyList<InstallOutcome>> UpdateAllAsync(
        IReadOnlyList<PluginUpdate> updates, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var outcomes = new List<InstallOutcome>();
        foreach (var update in updates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                outcomes.Add(await installer.InstallAsync(update.Entry, update.Target, progress, ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One plugin failing must not abort the batch.
                outcomes.Add(InstallOutcome.Fail(update.Id, ex.Message));
            }
        }

        return outcomes;
    }
}
