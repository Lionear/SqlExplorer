using System.Threading;
using SqlExplorer.Core.Localization;
using SqlExplorer.Core.Settings;
using SqlExplorer.Core.Update;
using SqlExplorer.Infrastructure.Update;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SqlExplorer.App.ViewModels;

/// <summary>
/// The shared brain for the in-app updater's UI (SE-137): a single instance behind both the main-window
/// banner and the Settings "Check for updates" button, so a manual check lights up the same banner and the
/// same "What's new" action. Runs the check on startup, then periodically while the app stays open, then
/// on demand — always fault-tolerant (offline is a silent no-op).
/// </summary>
public sealed partial class AppUpdateViewModel : ViewModelBase
{
    private readonly AppUpdateService _service;
    private readonly IAppSettingsStore _settingsStore;
    private readonly UpdateDownloader _downloader;
    private readonly IUpdateApplier _applier;

    private UpdateCheckResult? _current;

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

    /// <summary>Set by the view: shows the changelog dialog for the offered build.</summary>
    public Func<UpdateAvailableViewModel, Task>? ChangelogRequested { get; set; }

    /// <summary>The channel of the running build — the default a fresh install follows until one is chosen.</summary>
    public UpdateChannel RunningChannel => _service.RunningChannel;

    [ObservableProperty]
    private bool _hasUpdate;

    [ObservableProperty]
    private string _bannerText = string.Empty;

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

    /// <summary>While the app stays open (notably close-to-tray), re-check every <paramref name="interval"/>,
    /// gated on the same auto-check setting. Respects the "Later" dismissal so it never re-nags a version.</summary>
    public async Task RunPeriodicChecksAsync(TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var settings = _settingsStore.Load();
                if (settings.CheckForUpdatesOnStartup && !HasUpdate)
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

    /// <summary>Builds the changelog dialog VM for the current offer (or null if there's none).</summary>
    public UpdateAvailableViewModel? BuildDialog() =>
        _current is { Manifest: { } manifest }
            ? new UpdateAvailableViewModel(manifest, _current.Asset, _downloader, _applier, Loc)
            : null;

    private async Task CheckEffectiveAsync(AppSettings settings, CancellationToken ct)
    {
        var channel = settings.UpdateChannel ?? _service.RunningChannel;
        var result = await _service.CheckAsync(channel, ct);
        if (result is not { IsAvailable: true, Manifest: { } manifest })
        {
            return;
        }

        if (string.Equals(manifest.Version, settings.DismissedUpdateVersion, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Surface(result);
    }

    private void Surface(UpdateCheckResult result)
    {
        _current = result;
        BannerText = Loc.Get("UpdateBannerAvailable", result.Manifest!.Version);
        OnPropertyChanged(nameof(OfferedVersion));
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
}
