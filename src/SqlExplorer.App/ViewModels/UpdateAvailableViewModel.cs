using SqlExplorer.Core.Localization;
using SqlExplorer.Core.Update;
using SqlExplorer.Infrastructure.Update;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SqlExplorer.App.ViewModels;

/// <summary>
/// Backs the changelog dialog for an available update (SE-137, Fase 1): shows the new build's version,
/// date, commit and release notes, and downloads the right asset for this platform — verified by SHA-256 —
/// then hands the file to the user (opens the installer / reveals the folder). No in-place swap yet; that's
/// Fase 2.
/// </summary>
public sealed partial class UpdateAvailableViewModel : ViewModelBase
{
    private readonly UpdateManifest _manifest;
    private readonly UpdateAsset? _asset;
    private readonly UpdateDownloader _downloader;
    private readonly IUpdateApplier _applier;

    public UpdateAvailableViewModel(
        UpdateManifest manifest, UpdateAsset? asset, UpdateDownloader downloader, IUpdateApplier applier, ILocalizer localizer)
    {
        _manifest = manifest;
        _asset = asset;
        _downloader = downloader;
        _applier = applier;
        Loc = localizer;
    }

    public ILocalizer Loc { get; }

    public string VersionLine => Loc.Get("UpdateDialogVersion", _manifest.Version);

    public string? PublishedLine =>
        string.IsNullOrWhiteSpace(_manifest.PublishedAt) ? null : Loc.Get("UpdateDialogPublished", _manifest.PublishedAt);

    public string? CommitLine =>
        string.IsNullOrWhiteSpace(_manifest.Commit) ? null : Loc.Get("UpdateDialogCommit", _manifest.Commit);

    public bool HasPublished => PublishedLine is not null;

    public bool HasCommit => CommitLine is not null;

    /// <summary>Raw markdown notes; the view renders them via <c>MiniMarkdown</c>.</summary>
    public string Notes => _manifest.Notes ?? string.Empty;

    /// <summary>True when there's a downloadable asset for this platform (else only the changelog is shown).</summary>
    public bool HasAsset => _asset is not null;

    /// <summary>True when this platform can install in place (Linux swap / Windows installer / macOS DMG).</summary>
    public bool CanInstall => _asset is not null && _applier.CanApplyInPlace(_asset);

    /// <summary>Show the plain "Download" hand-off only when an in-place install isn't available.</summary>
    public bool ShowDownloadOnly => HasAsset && !CanInstall;

    /// <summary>Set by the view: opens/reveals the downloaded file (platform shell). The hand-off to the user.</summary>
    public Func<string, Task>? OpenRequested { get; set; }

    /// <summary>Set by the view: carries out the apply result (relaunch/exit) via the desktop lifetime.</summary>
    public Func<ApplyResult, Task>? ApplyRequested { get; set; }

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private string? _statusMessage;

    private bool CanAct => HasAsset && !IsDownloading;

    // Plain hand-off: download + verify, then reveal the file for the user to run themselves.
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task Download()
    {
        var path = await DownloadVerifiedAsync();
        if (path is not null && OpenRequested is not null)
        {
            StatusMessage = Loc["UpdateDialogDownloaded"];
            await OpenRequested(path);
        }
    }

    // In-place: download + verify, then apply per platform and let the host relaunch/exit.
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task InstallAndRestart()
    {
        var path = await DownloadVerifiedAsync();
        if (path is null || _asset is null)
        {
            return;
        }

        StatusMessage = Loc["UpdateDialogInstalling"];
        var result = await _applier.ApplyAsync(path, _asset, CancellationToken.None);
        if (result.Action == ApplyAction.Failed)
        {
            StatusMessage = result.Message ?? Loc["UpdateDialogDownloadFailed"];
            return;
        }

        if (result.Action == ApplyAction.Guided)
        {
            StatusMessage = result.Message ?? Loc["UpdateDialogGuided"];
        }

        if (ApplyRequested is not null)
        {
            await ApplyRequested(result);
        }
    }

    // Shared download + SHA-256 verify; returns the local path or null (with the reason in StatusMessage).
    private async Task<string?> DownloadVerifiedAsync()
    {
        if (_asset is null)
        {
            return null;
        }

        IsDownloading = true;
        NotifyActions();
        DownloadProgress = 0;
        StatusMessage = Loc["UpdateDialogDownloading"];

        var progress = new Progress<double>(p => DownloadProgress = p);
        try
        {
            var outcome = await _downloader.DownloadAsync(_asset, progress, CancellationToken.None);
            if (outcome is { Success: true, FilePath: { } path })
            {
                return path;
            }

            StatusMessage = outcome.Error ?? Loc["UpdateDialogDownloadFailed"];
            return null;
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            return null;
        }
        finally
        {
            IsDownloading = false;
            NotifyActions();
        }
    }

    private void NotifyActions()
    {
        DownloadCommand.NotifyCanExecuteChanged();
        InstallAndRestartCommand.NotifyCanExecuteChanged();
    }
}
