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
/// The proactive layer over the Plugin Store's existing update detection (SE-138 phase 1): a background
/// check on startup and on the shared update interval that surfaces an ambient top-bar badge and a
/// persistent, actionable notification when compatible plugin updates exist — without the user opening
/// the Store. Sibling of <see cref="AppUpdateViewModel"/> (which does the same for the host app). Only
/// host-API-compatible, non-pinned versions are ever counted — that gate lives in
/// <see cref="PluginUpdateService.DetectUpdates"/>, reused here verbatim.
/// </summary>
public sealed partial class PluginUpdatesViewModel : ViewModelBase
{
    private readonly IStoreCatalog _catalog;
    private readonly PluginCatalogService _installed;
    private readonly PluginUpdateService _updates;
    private readonly IAppSettingsStore _settingsStore;

    // Floor on the re-check cadence so a mis-set interval can't hammer the catalog host (matches the app-updater).
    private static readonly TimeSpan MinCheckInterval = TimeSpan.FromMinutes(30);

    // The update-set we last surfaced the notification for, so an unchanged set doesn't re-nag: the badge
    // stays as the ambient cue, but the notification only re-opens when the set of pending updates changes.
    private string? _notifiedKey;

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

    [ObservableProperty]
    private int _availableCount;

    /// <summary>The display names of the plugins with a pending update — shown as chips in the notification.</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _pluginNames = [];

    /// <summary>The persistent notification's visibility. Shown once per update-set; hidden on dismiss or
    /// after "View updates". The badge (<see cref="HasUpdates"/>) stays regardless as the ambient cue.</summary>
    [ObservableProperty]
    private bool _isNotificationVisible;

    /// <summary>True when there are compatible updates and the policy isn't Off — drives the badge.</summary>
    public bool HasUpdates => AvailableCount > 0 && Policy != PluginUpdatePolicy.Off;

    public string NotificationTitle => Loc.Get("PluginUpdatesToast", AvailableCount);

    partial void OnAvailableCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasUpdates));
        OnPropertyChanged(nameof(NotificationTitle));
    }

    private PluginUpdatePolicy Policy => _settingsStore.Load().PluginUpdatePolicy;

    public async Task CheckOnStartupAsync(CancellationToken ct)
    {
        if (Policy != PluginUpdatePolicy.Off)
        {
            await CheckAsync(ct);
        }
    }

    /// <summary>Re-check on the shared update interval (<see cref="AppSettings.UpdateCheckIntervalMinutes"/>)
    /// while the app stays open. The interval and policy are re-read every pass, so a Settings change takes
    /// effect without a restart; policy Off idles at the floor and re-reads.</summary>
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

    // Fault-tolerant: an offline/failed catalog fetch leaves the badge unchanged and never surfaces an error.
    private async Task CheckAsync(CancellationToken ct)
    {
        try
        {
            var catalog = await _catalog.FetchAsync(ct);
            var updates = _updates.DetectUpdates(_installed.Installed, catalog);

            AvailableCount = updates.Count;

            if (updates.Count == 0)
            {
                _notifiedKey = null;
                IsNotificationVisible = false;
                return;
            }

            PluginNames = updates.Select(u => u.Entry.Name).Distinct(StringComparer.Ordinal).ToList();

            var key = string.Join(",", updates
                .Select(u => $"{u.Id}@{u.Target.Version}")
                .OrderBy(x => x, StringComparer.Ordinal));

            // New set → surface the notification once; an unchanged set leaves the badge only (no re-nag).
            if (key != _notifiedKey)
            {
                _notifiedKey = key;
                IsNotificationVisible = true;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Offline or a source error — stay silent, exactly like the app-updater.
        }
    }

    // Badge click and the notification's "View updates" both land here: open the Store on Installed, and
    // dismiss the notification (the badge remains while updates are pending).
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
}
