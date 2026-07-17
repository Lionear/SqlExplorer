using SqlExplorer.Core.Localization;
using SqlExplorer.Core.Update;

namespace SqlExplorer.App.ViewModels;

/// <summary>
/// Backs the changelog dialog for an available update (SE-137 / SE-151): the new build's version, date,
/// commit and release notes. Downloading and installing moved to the banner itself (<see cref="AppUpdateViewModel"/>),
/// so this dialog is notes-only — one source of truth for download status.
/// </summary>
public sealed class UpdateAvailableViewModel : ViewModelBase
{
    private readonly UpdateManifest _manifest;

    public UpdateAvailableViewModel(UpdateManifest manifest, ILocalizer localizer)
    {
        _manifest = manifest;
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
}
