using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlExplorer.Core.Localization;
using SqlExplorer.Core.Plugins;
using SqlExplorer.Core.Settings;
using SqlExplorer.Core.Store;

namespace SqlExplorer.App.ViewModels;

/// <summary>
/// The proactive layer over the Plugin Store's update detection (SE-138): a background check on startup
/// and on the shared update interval that, per the Settings policy,
/// <list type="bullet">
/// <item><b>Notify</b> — surfaces an ambient badge + a persistent, actionable notification (phase 1/2);</item>
/// <item><b>Auto</b> — silently stages compatible, non-pinned updates for the next restart (phase 3);</item>
/// <item>always — flags <b>held-back</b> updates that need a newer host app, instead of hiding them (phase 4).</item>
/// </list>
/// Only host-API-compatible, non-pinned versions are ever installed; that gate lives in
/// <see cref="PluginUpdateService"/>, reused verbatim.
/// </summary>
public sealed partial class PluginUpdatesViewModel : ViewModelBase
{
    private readonly IStoreCatalog _catalog;
    private readonly PluginCatalogService _installed;
    private readonly PluginUpdateService _updates;
    private readonly IAppSettingsStore _settingsStore;

    private static readonly TimeSpan MinCheckInterval = TimeSpan.FromMinutes(30);

    // Keys of the update-set the notification was last surfaced for / the set last auto-staged, so an
    // unchanged set neither re-nags nor re-stages.
    private string? _notifiedKey;
    private string? _autoStagedKey;

    private IReadOnlyList<PluginUpdate> _pendingUpdates = [];
    private IReadOnlyList<string> _availableNames = [];
    private IReadOnlyList<string> _heldBackNames = [];

    public PluginUpdatesViewModel(
        IStoreCatalog catalog, PluginCatalogService installed, PluginUpdateService updates,
        IAppSettingsStore settingsStore, ILocalizer localizer)
    {
        _catalog = catalog;
        _installed = installed;
        _updates = updates;
        _settingsStore = settingsStore;
        Loc = localizer;
    }

    public ILocalizer Loc { get; }

    /// <summary>Opens the Plugin Store on its Installed tab. Wired by <see cref="MainViewModel"/>.</summary>
    public Func<Task>? OpenStoreRequested { get; set; }

    /// <summary>Shows the combined per-plugin changelog dialog. Wired by the view (it owns the window).</summary>
    public Func<PluginChangelogViewModel, Task>? ChangelogRequested { get; set; }

    /// <summary>Opens the app-update flow — the held-back notification's "Update app…". Wired by MainViewModel.</summary>
    public Func<Task>? UpdateAppRequested { get; set; }

    /// <summary>Raised after the Auto policy stages updates, so the host can light the restart-needed banner.</summary>
    public Action? PendingChangesStaged { get; set; }

    /// <summary>Info-level messages for the Output panel — the check cadence, its result, and auto-apply.
    /// Wired by <see cref="MainViewModel"/>.</summary>
    public Action<string>? Reported { get; set; }

    [ObservableProperty]
    private int _availableCount;

    [ObservableProperty]
    private int _heldBackCount;

    /// <summary>The plugin names shown as chips in the notification (available or held-back per variant).</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _pluginNames = [];

    /// <summary>The persistent notification's visibility. Shown once per set; hidden on dismiss or action.</summary>
    [ObservableProperty]
    private bool _isNotificationVisible;

    /// <summary>The badge count = actionable updates + held-back ones; the badge is the ambient cue.</summary>
    public int BadgeCount => AvailableCount + HeldBackCount;

    /// <summary>True when there's anything pending and the policy isn't Off — drives the badge.</summary>
    public bool HasUpdates => BadgeCount > 0 && Policy != PluginUpdatePolicy.Off;

    /// <summary>The notification shows the held-back variant when there's nothing installable but something
    /// is held back for a newer app; otherwise the normal "updates available" variant.</summary>
    public bool IsHeldBack => AvailableCount == 0 && HeldBackCount > 0;

    public string NotificationTitle => IsHeldBack
        ? Loc.Get("PluginUpdateHeldBackTitle", HeldBackCount)
        : Loc.Get("PluginUpdatesToast", AvailableCount);

    partial void OnAvailableCountChanged(int value) => RaiseDerived();

    partial void OnHeldBackCountChanged(int value) => RaiseDerived();

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(BadgeCount));
        OnPropertyChanged(nameof(HasUpdates));
        OnPropertyChanged(nameof(IsHeldBack));
        OnPropertyChanged(nameof(NotificationTitle));
    }

    private PluginUpdatePolicy Policy => _settingsStore.Load().PluginUpdatePolicy;

    /// <summary>Report an "N plugins updated" summary for anything the Auto policy staged in a previous run
    /// and the last restart applied (phase 3). Call once at startup; clears the marker.</summary>
    public void ReportRestartSummaryIfAny()
    {
        var settings = _settingsStore.Load();
        if (settings.PendingAutoUpdateNotice is not { Count: > 0 } applied)
        {
            return;
        }

        Reported?.Invoke(Loc.Get("PluginUpdateLogAutoApplied", applied.Count, string.Join(", ", applied)));
        settings.PendingAutoUpdateNotice = null;
        try { _settingsStore.Save(settings); } catch { /* never block startup on a preference write */ }
    }

    public async Task CheckOnStartupAsync(CancellationToken ct)
    {
        if (Policy != PluginUpdatePolicy.Off)
        {
            await CheckAsync(ct);
        }
    }

    /// <summary>Re-check on the shared update interval while the app stays open. Interval + policy re-read
    /// each pass, so a Settings change applies without a restart; policy Off idles at the floor.</summary>
    public async Task RunPeriodicChecksAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var minutes = _settingsStore.Load().UpdateCheckIntervalMinutes;
                var delay = minutes <= 0
                    ? MinCheckInterval
                    : TimeSpan.FromMinutes(Math.Max(minutes, MinCheckInterval.TotalMinutes));
                await Task.Delay(delay, ct);

                if (Policy != PluginUpdatePolicy.Off)
                {
                    await CheckAsync(ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown — stop quietly.
        }
    }

    // Fault-tolerant: an offline/failed catalog fetch leaves state unchanged and never surfaces an error.
    private async Task CheckAsync(CancellationToken ct)
    {
        try
        {
            var catalog = await _catalog.FetchAsync(ct);
            var updates = _updates.DetectUpdates(_installed.Installed, catalog);
            var heldBack = _updates.DetectHeldBack(_installed.Installed, catalog);

            if (Policy == PluginUpdatePolicy.Auto && updates.Count > 0)
            {
                await AutoStageAsync(updates, ct);
                AvailableCount = 0; // staged silently — nothing for the user to act on
                _pendingUpdates = [];
                _availableNames = [];
            }
            else
            {
                AvailableCount = updates.Count;
                _pendingUpdates = updates;
                _availableNames = updates.Select(u => u.Entry.Name).Distinct(StringComparer.Ordinal).ToList();
            }

            HeldBackCount = heldBack.Count;
            _heldBackNames = heldBack.Select(h => h.Name).Distinct(StringComparer.Ordinal).ToList();

            LogResult(updates.Count, heldBack.Count);
            UpdateNotification();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            Reported?.Invoke(Loc["PluginUpdateLogFailed"]);
        }
    }

    private async Task AutoStageAsync(IReadOnlyList<PluginUpdate> updates, CancellationToken ct)
    {
        var key = KeyOf(updates);
        if (key == _autoStagedKey)
        {
            return; // already staged this exact set — don't re-stage every interval
        }

        await _updates.UpdateAllAsync(updates, progress: null, ct);
        _autoStagedKey = key;

        // Remember what was staged so the next startup can confirm it (post-restart summary).
        var names = updates.Select(u => $"{u.Entry.Name} {u.Target.Version}").ToList();
        var settings = _settingsStore.Load();
        settings.PendingAutoUpdateNotice = (settings.PendingAutoUpdateNotice ?? [])
            .Concat(names).Distinct(StringComparer.Ordinal).ToList();
        try { _settingsStore.Save(settings); } catch { /* best-effort */ }

        Reported?.Invoke(Loc.Get("PluginUpdateLogAutoStaged", updates.Count));
        PendingChangesStaged?.Invoke(); // light the restart-needed banner
    }

    private void LogResult(int updateCount, int heldBackCount)
    {
        if (updateCount == 0 && heldBackCount == 0)
        {
            Reported?.Invoke(Loc["PluginUpdateLogNone"]);
        }
        else if (updateCount > 0)
        {
            Reported?.Invoke(Loc.Get("PluginUpdateLogAvailable", updateCount, string.Join(", ", _availableNames)));
        }
        else
        {
            Reported?.Invoke(Loc.Get("PluginUpdateLogHeldBack", heldBackCount));
        }
    }

    private void UpdateNotification()
    {
        if (AvailableCount == 0 && HeldBackCount == 0)
        {
            _pendingUpdates = [];
            _notifiedKey = null;
            IsNotificationVisible = false;
            PluginNames = [];
            return;
        }

        var showHeld = IsHeldBack;
        PluginNames = showHeld ? _heldBackNames : _availableNames;

        // Surface once per (variant + set); an unchanged set leaves the badge as the only cue.
        var key = (showHeld ? "H:" : "U:") + string.Join(",", PluginNames);
        if (key != _notifiedKey)
        {
            _notifiedKey = key;
            IsNotificationVisible = true;
        }
    }

    private static string KeyOf(IReadOnlyList<PluginUpdate> updates) =>
        string.Join(",", updates.Select(u => $"{u.Id}@{u.Target.Version}").OrderBy(x => x, StringComparer.Ordinal));

    // Badge / "View updates" → open the Store on Installed; dismiss the notification (badge stays).
    [RelayCommand]
    private async Task OpenStore()
    {
        IsNotificationVisible = false;
        if (OpenStoreRequested is not null)
        {
            await OpenStoreRequested();
        }
    }

    [RelayCommand]
    private void Dismiss() => IsNotificationVisible = false;

    // Held-back variant → open the app-update flow so the user can update the host, then dismiss.
    [RelayCommand]
    private async Task UpdateApp()
    {
        IsNotificationVisible = false;
        if (UpdateAppRequested is not null)
        {
            await UpdateAppRequested();
        }
    }

    // Opens the combined changelog dialog: one section per pending update (name + version + notes).
    [RelayCommand]
    private async Task ViewChangelog()
    {
        if (ChangelogRequested is null || _pendingUpdates.Count == 0)
        {
            return;
        }

        var sections = _pendingUpdates
            .Select(u => new PluginChangelogViewModel.Section($"{u.Entry.Name} {u.Target.Version}", u.Target.Notes))
            .ToList();

        await ChangelogRequested(new PluginChangelogViewModel(Loc["PluginChangelogTitle"], sections, Loc));
    }
}
