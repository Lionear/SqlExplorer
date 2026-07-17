using System.Runtime.InteropServices;
using SqlExplorer.Core.Store;

namespace SqlExplorer.Core.Update;

/// <summary>
/// Decides whether a newer build of the host app is available on a chosen channel, mirroring the Store's
/// update detection (<c>PluginUpdateService</c>) at app level. Fetches the channel's <c>update.json</c>,
/// compares it to the running build, and picks the download asset for this platform. Fault-tolerant: a
/// fetch that fails (offline, 404) yields <see cref="UpdateCheckResult.Failed"/> and is treated as silent.
/// </summary>
public sealed class AppUpdateService
{
    private readonly IUpdateManifestSource _source;
    private readonly string _runningVersion;
    private readonly string _runningRid;

    /// <param name="runningVersion">The running build's informational version stamp (as About reads it).</param>
    /// <param name="runningRid">Override the detected RID (win-x64/linux-x64/…); defaults to this process's.</param>
    public AppUpdateService(IUpdateManifestSource source, string runningVersion, string? runningRid = null)
    {
        _source = source;
        _runningVersion = runningVersion;
        _runningRid = runningRid ?? CurrentRid();
    }

    public async Task<UpdateCheckResult> CheckAsync(UpdateChannel channel, CancellationToken ct)
    {
        var manifest = await _source.FetchAsync(channel, ct);
        if (manifest is null)
        {
            return UpdateCheckResult.Failed;
        }

        return IsNewer(channel, manifest.Version)
            ? UpdateCheckResult.Available(manifest, SelectAsset(manifest))
            : UpdateCheckResult.UpToDate;
    }

    /// <summary>
    /// Is <paramref name="offeredVersion"/> on <paramref name="channel"/> something the user should update
    /// to? Switching to a <em>different</em> channel offers that channel's build outright (unless it's the
    /// identical stamp). On the same channel: a higher numeric core wins; on an equal core the newer build
    /// (date, run) wins — the ordinal SemVer pre-release order isn't trustworthy here (see <see cref="ChannelStamp"/>).
    /// </summary>
    private bool IsNewer(UpdateChannel channel, string offeredVersion)
    {
        if (string.Equals(offeredVersion, _runningVersion, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ChannelStamp.TryParse(_runningVersion, out var running);
        ChannelStamp.TryParse(offeredVersion, out var offered);

        // The user deliberately switched channels: offer whatever that channel currently ships.
        if (channel != running.Channel)
        {
            return true;
        }

        var byCore = SemVer.Compare(offered.Core, running.Core);
        if (byCore != 0)
        {
            return byCore > 0;
        }

        return offered.CompareBuildTo(running) > 0;
    }

    /// <summary>The asset for this platform: on Windows prefer the installer over the portable zip.</summary>
    private UpdateAsset? SelectAsset(UpdateManifest manifest)
    {
        if (_runningRid.StartsWith("win-", StringComparison.OrdinalIgnoreCase)
            && manifest.Assets.TryGetValue(_runningRid + "-setup", out var installer))
        {
            return installer;
        }

        return manifest.Assets.GetValueOrDefault(_runningRid);
    }

    /// <summary>The RID naming used by the build assets (win-x64, win-arm64, linux-x64, osx-arm64).</summary>
    private static string CurrentRid()
    {
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx"
            : "linux";
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            var other => other.ToString().ToLowerInvariant()
        };
        return $"{os}-{arch}";
    }
}
