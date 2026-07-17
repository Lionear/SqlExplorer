using SqlExplorer.Core.Localization;
using SqlExplorer.Core.Settings;
using SqlExplorer.Core.Update;
using SqlExplorer.Infrastructure.Update;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SqlExplorer.App.ViewModels;

/// <summary>
/// Owns the "update available" banner on the main window (SE-137, Fase 1): runs the startup check against
/// the user's channel, and — when a newer build is offered and hasn't been dismissed — shows a banner that
/// opens the changelog dialog or is snoozed with "Later". All the update logic lives here so
/// <see cref="MainViewModel"/> only has to surface the banner.
/// </summary>
public sealed partial class AppUpdateViewModel : ViewModelBase
{
    private readonly AppUpdateService _service;
    private readonly IAppSettingsStore _settingsStore;
    private readonly UpdateDownloader _downloader;

    private UpdateCheckResult? _current;

    public AppUpdateViewModel(
        AppUpdateService service, IAppSettingsStore settingsStore, UpdateDownloader downloader, ILocalizer localizer)
    {
        _service = service;
        _settingsStore = settingsStore;
        _downloader = downloader;
        Loc = localizer;
    }

    public ILocalizer Loc { get; }

    /// <summary>Set by the view: shows the changelog dialog for the offered build.</summary>
    public Func<UpdateAvailableViewModel, Task>? ChangelogRequested { get; set; }

    [ObservableProperty]
    private bool _hasUpdate;

    [ObservableProperty]
    private string _bannerText = string.Empty;

    /// <summary>Runs once at startup when the setting is on: a fetch failure is silent (offline = no banner).</summary>
    public async Task CheckOnStartupAsync(CancellationToken ct)
    {
        var settings = _settingsStore.Load();
        if (!settings.CheckForUpdatesOnStartup)
        {
            return;
        }

        var result = await _service.CheckAsync(settings.UpdateChannel, ct);
        if (!result.IsAvailable || result.Manifest is null)
        {
            return;
        }

        if (string.Equals(result.Manifest.Version, settings.DismissedUpdateVersion, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _current = result;
        BannerText = Loc.Get("UpdateBannerAvailable", result.Manifest.Version);
        HasUpdate = true;
    }

    [RelayCommand]
    private async Task ViewChangelog()
    {
        if (_current is not { Manifest: { } manifest } || ChangelogRequested is null)
        {
            return;
        }

        var dialog = new UpdateAvailableViewModel(manifest, _current.Asset, _downloader, Loc);
        await ChangelogRequested(dialog);
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
