using System.Threading;
using System.Threading.Tasks;
using SqlExplorer.Core.Update;

namespace SqlExplorer.Core.Tests.Update;

/// <summary>
/// Covers <c>AppUpdateService.IsNewer</c> through the public <see cref="AppUpdateService.CheckAsync"/> seam
/// (a fake manifest source), with SE-162 as the anchor: a cross-channel check must never present a lower
/// core as an "update" (a downgrade). Equal core across channels stays a legitimate switch.
/// </summary>
public class AppUpdateServiceTests
{
    // Returns one fixed manifest regardless of channel — CheckAsync's channel argument is what IsNewer keys
    // off, so the test drives the channel there and the offered stamp here.
    private sealed class FixedSource(string version) : IUpdateManifestSource
    {
        public Task<UpdateManifest?> FetchAsync(UpdateChannel channel, CancellationToken ct) =>
            Task.FromResult<UpdateManifest?>(new UpdateManifest { Version = version });
    }

    private static async Task<bool> OffersUpdate(string runningVersion, UpdateChannel channel, string offeredVersion)
    {
        var service = new AppUpdateService(new FixedSource(offeredVersion), runningVersion, runningRid: "linux-x64");
        var result = await service.CheckAsync(channel, CancellationToken.None);
        return result.IsAvailable;
    }

    [Fact] // SE-162 core case: dev/stable 0.3.0 checking Preview must not "update" to the older 0.2.0-preview.
    public async Task CrossChannel_lower_core_is_not_offered() =>
        Assert.False(await OffersUpdate("0.3.0", UpdateChannel.Preview, "0.2.0-preview"));

    [Fact]
    public async Task CrossChannel_higher_core_is_offered() =>
        Assert.True(await OffersUpdate("0.2.0", UpdateChannel.Preview, "0.3.0-preview"));

    [Fact] // Equal core across channels = a deliberate, legitimate channel switch — still offered.
    public async Task CrossChannel_equal_core_is_offered() =>
        Assert.True(await OffersUpdate("0.3.0-preview.20260101.1", UpdateChannel.Stable, "0.3.0"));

    [Fact]
    public async Task SameChannel_higher_core_is_offered() =>
        Assert.True(await OffersUpdate("0.2.0", UpdateChannel.Stable, "0.3.0"));

    [Fact] // Same channel, equal core: the newer build (date/run) still wins — unchanged by the fix.
    public async Task SameChannel_equal_core_newer_build_is_offered() =>
        Assert.True(await OffersUpdate("0.2.0-nightly.20260101.1", UpdateChannel.Nightly, "0.2.0-nightly.20260201.2"));

    [Fact] // An identical stamp is never an update.
    public async Task Identical_version_is_not_offered() =>
        Assert.False(await OffersUpdate("0.3.0", UpdateChannel.Stable, "0.3.0"));
}
