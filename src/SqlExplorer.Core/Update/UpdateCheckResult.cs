namespace SqlExplorer.Core.Update;

public enum UpdateStatus
{
    /// <summary>The running build is the newest the channel offers.</summary>
    UpToDate,

    /// <summary>A newer build is available (see <see cref="UpdateCheckResult.Manifest"/>).</summary>
    Available,

    /// <summary>The check couldn't complete (offline, fetch/parse failure). Treated as silent.</summary>
    Failed
}

/// <summary>
/// Outcome of <see cref="AppUpdateService.CheckAsync"/>. When <see cref="Status"/> is
/// <see cref="UpdateStatus.Available"/>, <see cref="Manifest"/> is set and <see cref="Asset"/> is the file
/// matching the running RID (null if the manifest offers nothing for this platform — the changelog can
/// still be shown, but there's nothing to download here).
/// </summary>
public sealed record UpdateCheckResult(UpdateStatus Status, UpdateManifest? Manifest = null, UpdateAsset? Asset = null)
{
    public static readonly UpdateCheckResult UpToDate = new(UpdateStatus.UpToDate);
    public static readonly UpdateCheckResult Failed = new(UpdateStatus.Failed);

    public static UpdateCheckResult Available(UpdateManifest manifest, UpdateAsset? asset) =>
        new(UpdateStatus.Available, manifest, asset);

    public bool IsAvailable => Status == UpdateStatus.Available;
}
