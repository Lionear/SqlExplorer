namespace SqlExplorer.Core.Update;

/// <summary>
/// What a channel currently offers, from <see cref="AppUpdateService.PeekAsync"/> — the answer to "what is on
/// that channel?" rather than "should I update?" (SE-163).
///
/// <para>The distinction is the point. An <i>automatic</i> update notification must never present a lower
/// build as an update, which is why <see cref="AppUpdateService.CheckAsync"/> refuses one. A user who picks a
/// channel in Settings has made a deliberate choice, and the honest response to "Stable is older than what
/// you run" is to say so and offer the switch — not silence, which is what they got before.</para>
/// </summary>
/// <param name="CoreComparedToRunning">Sign of the target core against the running one: negative means
/// switching is a <b>downgrade</b>, zero the same core on another channel, positive a normal update.</param>
public sealed record ChannelOffer(
    UpdateChannel Channel,
    UpdateManifest Manifest,
    UpdateAsset? Asset,
    string RunningVersion,
    int CoreComparedToRunning)
{
    /// <summary>True when taking this channel means moving to an older core — the case that needs saying out
    /// loud and confirming, rather than being applied or silently skipped.</summary>
    public bool IsDowngrade => CoreComparedToRunning < 0;

    /// <summary>True when the channel offers exactly the build already running, so switching changes the
    /// channel the app follows and nothing else.</summary>
    public bool IsSameBuild =>
        string.Equals(Manifest.Version, RunningVersion, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether this platform has a file to download for the offer. A manifest can legitimately
    /// carry no asset for the running RID; then the switch is a channel change, not an install.</summary>
    public bool CanInstall => Asset is not null;

    public string Version => Manifest.Version;
}
