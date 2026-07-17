namespace SqlExplorer.Core.Update;

/// <summary>
/// The release channel the user follows. Each maps to one rolling GitHub release tag that carries an
/// <c>update.json</c> manifest (see <see cref="AppUpdateService"/>). Ordered least-to-most bleeding-edge;
/// the default is <see cref="Stable"/>.
/// </summary>
public enum UpdateChannel
{
    /// <summary>Tagged <c>v*</c> releases — the <c>latest</c>, non-prerelease build.</summary>
    Stable,

    /// <summary>Every merge to <c>main</c>; rolling prerelease tag <c>preview</c>.</summary>
    Preview,

    /// <summary>Nightly build of <c>develop</c>; rolling prerelease tag <c>nightly</c>.</summary>
    Nightly
}

/// <summary>Maps a channel to the rolling release tag whose <c>update.json</c> is the source of truth.</summary>
public static class UpdateChannelExtensions
{
    public static string Tag(this UpdateChannel channel) => channel switch
    {
        UpdateChannel.Stable => "stable",
        UpdateChannel.Preview => "preview",
        UpdateChannel.Nightly => "nightly",
        _ => "stable"
    };
}
