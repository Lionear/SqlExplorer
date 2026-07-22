using System.Threading;
using SqlExplorer.Core.Localization;
using SqlExplorer.Core.Settings;
using SqlExplorer.Core.Update;
using SqlExplorer.Infrastructure.Update;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SqlExplorer.App.ViewModels;

/// <summary>The update banner's state machine (SE-151): the download + install confirmation live in the
/// banner itself, so it walks Available → Downloading → ReadyToInstall (or Guided for a macOS/hand-off) →
/// (install &amp; restart), with Failed as the error branch.</summary>
public enum BannerState { Available, Downloading, ReadyToInstall, Guided, Failed }

/// <summary>
/// The shared brain for the in-app updater's UI (SE-137): a single instance behind both the main-window
/// banner and the Settings "Check for updates" button, so a manual check lights up the same banner and the
/// same "What's new" action. Runs the check on startup, then periodically while the app stays open, then
/// on demand — always fault-tolerant (offline is a silent no-op). Downloading and installing happen inline
/// in the banner (SE-151); the changelog dialog is notes-only.
/// </summary>
public sealed partial class AppUpdateViewModel : ViewModelBase
{
    private readonly AppUpdateService _service;
    private readonly IAppSettingsStore _settingsStore;
    private readonly UpdateDownloader _downloader;
    private readonly IUpdateApplier _applier;

    private UpdateCheckResult? _current;
    private CancellationTokenSource? _downloadCts;
    private string? _downloadedPath;

    public AppUpdateViewModel(
        AppUpdateService service, IAppSettingsStore settingsStore, UpdateDownloader downloader,
        IUpdateApplier applier, ILocalizer localizer)
    {
        _service = service;
        _settingsStore = settingsStore;
        _downloader = downloader;
        _applier = applier;
        Loc = localizer;
    }

    public ILocalizer Loc { get; }

    /// <summary>Info-level messages for the Output panel — the update-check cadence and its result. Wired by
    /// <see cref="MainViewModel"/>; null before wiring, so a check never fails on an unwired sink.</summary>
    public Action<string>? Reported { get; set; }

    /// <summary>Set by the view: shows the changelog dialog for the offered build.</summary>
    public Func<UpdateAvailableViewModel, Task>? ChangelogRequested { get; set; }

    /// <summary>Set by the view: carries out an apply result (relaunch/exit) via the desktop lifetime.</summary>
    public Func<ApplyResult, Task>? ApplyRequested { get; set; }

    /// <summary>Set by the view: opens/reveals the downloaded file in the platform shell.</summary>
    public Func<string, Task>? OpenRequested { get; set; }

    /// <summary>The channel of the running build — the default a fresh install follows until one is chosen.</summary>
    public UpdateChannel RunningChannel => _service.RunningChannel;

    [ObservableProperty]
    private bool _hasUpdate;

    [ObservableProperty]
    private string _bannerText = string.Empty;

    // The inline download/install state (SE-151). The IsX bools drive which banner variant shows.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAvailable), nameof(IsDownloading), nameof(IsReadyToInstall),
        nameof(IsGuided), nameof(IsFailed))]
    private BannerState _state = BannerState.Available;

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public bool IsAvailable => State == BannerState.Available;
    public bool IsDownloading => State == BannerState.Downloading;
    public bool IsReadyToInstall => State == BannerState.ReadyToInstall;
    public bool IsGuided => State == BannerState.Guided;
    public bool IsFailed => State == BannerState.Failed;

    /// <summary>The offered build's version, for Settings' inline status.</summary>
    public string? OfferedVersion => _current?.Manifest?.Version;

    /// <summary>Runs once at startup when auto-check is on; fetch failure is silent (offline = no banner).</summary>
    public async Task CheckOnStartupAsync(CancellationToken ct)
    {
        var settings = _settingsStore.Load();
        if (settings.CheckForUpdatesOnStartup)
        {
            await CheckEffectiveAsync(settings, ct);
        }
    }

    // Floor on the configurable re-check interval, so a mis-set value can't hammer the update server.
    private static readonly TimeSpan MinCheckInterval = TimeSpan.FromMinutes(30);

    /// <summary>While the app stays open (notably close-to-tray), re-check on the interval configured in
    /// Settings (<see cref="AppSettings.UpdateCheckIntervalMinutes"/>), gated on the same auto-check setting.
    /// The interval is re-read every iteration, so a change in Settings takes effect without a restart; 0
    /// disables periodic checks. Respects the "Later" dismissal so it never re-nags a version.</summary>
    public async Task RunPeriodicChecksAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var minutes = _settingsStore.Load().UpdateCheckIntervalMinutes;
                // When periodic checks are off, idle at the floor and re-read — so re-enabling in Settings
                // resumes without a restart, rather than blocking forever on a disabled interval.
                var delay = minutes <= 0 ? MinCheckInterval : TimeSpan.FromMinutes(Math.Max(minutes, MinCheckInterval.TotalMinutes));
                await Task.Delay(delay, ct);

                var settings = _settingsStore.Load();  // may have changed during the delay
                if (settings.CheckForUpdatesOnStartup && settings.UpdateCheckIntervalMinutes > 0 && !HasUpdate)
                {
                    await CheckEffectiveAsync(settings, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown — stop quietly.
        }
    }

    /// <summary>Manual check (Settings): surfaces the result even if previously dismissed, and returns the
    /// status for an inline message. Lights the banner too, so it's there once Settings closes.</summary>
    public async Task<UpdateStatus> RunCheckAsync(UpdateChannel channel, CancellationToken ct)
    {
        var result = await _service.CheckAsync(channel, ct);
        if (result is { IsAvailable: true, Manifest: not null })
        {
            Surface(result);
        }

        return result.Status;
    }

    /// <summary>What a channel currently offers, regardless of whether it's newer (SE-163). Settings uses it
    /// to tell the user what picking that channel would actually mean, before it means it.</summary>
    public Task<ChannelOffer?> PeekChannelAsync(UpdateChannel channel, CancellationToken ct) =>
        _service.PeekAsync(channel, ct);

    /// <summary>
    /// Put a channel offer the user deliberately chose into the banner's download/install flow — including a
    /// <b>downgrade</b>, which <see cref="CheckAsync"/> would never surface on its own.
    ///
    /// <para>That asymmetry is deliberate rather than an inconsistency: an automatic check must never present
    /// an older build as an update, but a user who confirmed "switch and downgrade" has already been told
    /// exactly what it means. The intent travels as this one call, so the rule in <c>IsNewer</c> stays as
    /// strict as it was.</para>
    /// </summary>
    public void SurfaceChosen(ChannelOffer offer) =>
        Surface(UpdateCheckResult.Available(offer.Manifest, offer.Asset));

    /// <summary>Builds the notes-only changelog dialog VM for the current offer (or null if there's none).</summary>
    public UpdateAvailableViewModel? BuildDialog() =>
        _current is { Manifest: { } manifest } ? new UpdateAvailableViewModel(manifest, Loc) : null;

    private async Task CheckEffectiveAsync(AppSettings settings, CancellationToken ct)
    {
        var channel = settings.UpdateChannel ?? _service.RunningChannel;
        var result = await _service.CheckAsync(channel, ct);

        if (result.Status == UpdateStatus.Failed)
        {
            Reported?.Invoke(Loc.Get("UpdateLogFailed", channel));
            return;
        }

        if (result is not { IsAvailable: true, Manifest: { } manifest })
        {
            Reported?.Invoke(Loc.Get("UpdateLogUpToDate", channel));
            return;
        }

        if (string.Equals(manifest.Version, settings.DismissedUpdateVersion, StringComparison.OrdinalIgnoreCase))
        {
            Reported?.Invoke(Loc.Get("UpdateLogDismissed", channel, manifest.Version));
            return;
        }

        Reported?.Invoke(Loc.Get("UpdateLogAvailable", channel, manifest.Version));
        Surface(result);
    }

    private void Surface(UpdateCheckResult result)
    {
        _current = result;
        _downloadedPath = null;
        BannerText = Loc.Get("UpdateBannerAvailable", result.Manifest!.Version);
        OnPropertyChanged(nameof(OfferedVersion));
        State = BannerState.Available;
        HasUpdate = true;
    }

    [RelayCommand]
    private async Task ViewChangelog()
    {
        var dialog = BuildDialog();
        if (dialog is not null && ChangelogRequested is not null)
        {
            await ChangelogRequested(dialog);
        }
    }

    /// <summary>Snooze: remember the version so the banner stays hidden until a newer build appears.</summary>
    [RelayCommand]
    private void Later()
    {
        if (_current?.Manifest is { } manifest)
        {
            var settings = _settingsStore.Load();
            settings.DismissedUpdateVersion = manifest.Version;
            _settingsStore.Save(settings);
        }

        HasUpdate = false;
    }

    // --- Inline download + install (SE-151) --------------------------------------------------------

    /// <summary>Download the offer's asset (SHA-256 verified) inline in the banner. Success → ready to
    /// install where in-place is possible, else a guided hand-off (folder opened). Cancel → back to Available.</summary>
    [RelayCommand]
    private async Task Download()
    {
        if (_current is not { Asset: { } asset, Manifest: { } manifest })
        {
            return;
        }

        _downloadCts = new CancellationTokenSource();
        var ct = _downloadCts.Token;
        State = BannerState.Downloading;
        DownloadProgress = 0;
        StatusMessage = Loc.Get("UpdateBannerDownloading", OfferedVersion ?? "");

        var progress = new Progress<double>(p => DownloadProgress = p);
        try
        {
            var outcome = await _downloader.DownloadAsync(asset, progress, ct);
            var offerAsset = asset;
            var refreshed = false;

            // SE-153: a rotating nightly asset can expire between the check and the download (404/410). Re-fetch
            // update.json once for the current asset + checksum and retry — capped at a single retry (no loop).
            if (outcome is { Success: false, AssetUnavailable: true })
            {
                var settings = _settingsStore.Load();
                var channel = settings.UpdateChannel ?? _service.RunningChannel;
                var fresh = await _service.CheckAsync(channel, ct);

                if (fresh.Status == UpdateStatus.UpToDate)
                {
                    // The offered build was superseded and the running build is now newest — nothing to install.
                    StatusMessage = Loc["UpdateBannerUpToDate"];
                    HasUpdate = false;
                    return;
                }

                if (fresh is { IsAvailable: true, Asset: { } freshAsset, Manifest: { } freshManifest })
                {
                    refreshed = !string.Equals(freshManifest.Version, manifest.Version, StringComparison.OrdinalIgnoreCase);
                    offerAsset = freshAsset;
                    _current = _current with { Manifest = freshManifest, Asset = freshAsset };
                    OnPropertyChanged(nameof(OfferedVersion));
                    if (refreshed) BannerText = Loc.Get("UpdateBannerAvailable", freshManifest.Version);
                    outcome = await _downloader.DownloadAsync(freshAsset, progress, ct);
                }
                // else the re-fetch failed (offline) — fall through with the original 404 outcome.
            }

            if (outcome is not { Success: true, FilePath: { } path })
            {
                State = BannerState.Failed;
                StatusMessage = outcome.Error ?? Loc["UpdateDialogDownloadFailed"];
                return;
            }

            _downloadedPath = path;
            if (_applier.CanApplyInPlace(offerAsset))
            {
                State = BannerState.ReadyToInstall;
                StatusMessage = refreshed
                    ? Loc.Get("UpdateBannerRefetched", OfferedVersion ?? "")
                    : Loc.Get("UpdateBannerReady", OfferedVersion ?? "");
            }
            else
            {
                // No in-place install for this asset/platform — reveal the file for the user to run.
                State = BannerState.Guided;
                StatusMessage = Loc["UpdateDialogDownloaded"];
                if (OpenRequested is not null) await OpenRequested(path);
            }
        }
        catch (OperationCanceledException)
        {
            State = BannerState.Available;
        }
        catch (Exception ex)
        {
            State = BannerState.Failed;
            StatusMessage = ex.Message;
        }
        finally
        {
            _downloadCts = null;
        }
    }

    /// <summary>Cancel an in-flight download.</summary>
    [RelayCommand]
    private void CancelDownload() => _downloadCts?.Cancel();

    /// <summary>Retry after a failed download.</summary>
    [RelayCommand]
    private Task Retry() => Download();

    /// <summary>Install the downloaded build in place and let the host relaunch/exit. A guided (macOS) or
    /// failed apply keeps the app running with a status message.</summary>
    [RelayCommand]
    private async Task InstallAndRestart()
    {
        if (_downloadedPath is not { } path || _current?.Asset is not { } asset)
        {
            return;
        }

        StatusMessage = Loc["UpdateDialogInstalling"];
        var result = await _applier.ApplyAsync(path, asset, CancellationToken.None);

        if (result.Action == ApplyAction.Failed)
        {
            State = BannerState.Failed;
            StatusMessage = result.Message ?? Loc["UpdateDialogDownloadFailed"];
            return;
        }

        if (result.Action == ApplyAction.Guided)
        {
            State = BannerState.Guided;
            StatusMessage = result.Message ?? Loc["UpdateDialogGuided"];
        }

        if (ApplyRequested is not null)
        {
            await ApplyRequested(result);
        }
    }

    /// <summary>Reveal the downloaded file in the platform shell (the guided hand-off's action).</summary>
    [RelayCommand]
    private async Task OpenFolder()
    {
        if (_downloadedPath is { } path && OpenRequested is not null)
        {
            await OpenRequested(path);
        }
    }
}
