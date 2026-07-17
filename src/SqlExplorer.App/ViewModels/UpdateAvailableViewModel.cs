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

    public UpdateAvailableViewModel(UpdateManifest manifest, UpdateAsset? asset, UpdateDownloader downloader, ILocalizer localizer)
    {
        _manifest = manifest;
        _asset = asset;
        _downloader = downloader;
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

    /// <summary>Set by the view: opens/reveals the downloaded file (platform shell). The hand-off to the user.</summary>
    public Func<string, Task>? OpenRequested { get; set; }

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private string? _statusMessage;

    private bool CanDownload => HasAsset && !IsDownloading;

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task Download()
    {
        if (_asset is null)
        {
            return;
        }

        IsDownloading = true;
        DownloadCommand.NotifyCanExecuteChanged();
        DownloadProgress = 0;
        StatusMessage = Loc["UpdateDialogDownloading"];

        var progress = new Progress<double>(p => DownloadProgress = p);
        try
        {
            var outcome = await _downloader.DownloadAsync(_asset, progress, CancellationToken.None);
            if (outcome is { Success: true, FilePath: { } path })
            {
                StatusMessage = Loc["UpdateDialogDownloaded"];
                if (OpenRequested is not null)
                {
                    await OpenRequested(path);
                }
            }
            else
            {
                StatusMessage = outcome.Error ?? Loc["UpdateDialogDownloadFailed"];
            }
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsDownloading = false;
            DownloadCommand.NotifyCanExecuteChanged();
        }
    }
}
