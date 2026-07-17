namespace SqlExplorer.Core.Update;

/// <summary>
/// The version stamp CI writes into <c>AssemblyInformationalVersion</c>, taken apart into the parts that
/// actually order a build: the numeric core (<c>0.2.0</c>), the channel, and — for rolling prereleases —
/// the build date and run number (<c>0.2.0-nightly.20260717.42</c>).
/// </summary>
/// <remarks>
/// <c>Store.SemVer</c> is deliberately not used to order two builds on the same channel: its
/// pre-release comparison is ordinal, so <c>…20260717.42</c> would sort <em>above</em> <c>…20260717.100</c>
/// ('4' &gt; '1'). Within a channel the date and run number are what move forward, so this parses them out
/// and compares them numerically; across channels the numeric core still decides.
/// </remarks>
public sealed record ChannelStamp(string Core, UpdateChannel Channel, int Date, int Run)
{
    /// <summary>
    /// Parses a stamp such as <c>0.2.0-nightly.20260717.42</c> or a bare release <c>0.2.0</c>. Build
    /// metadata (<c>+&lt;sha&gt;</c>) is dropped first. A bare core with no pre-release suffix is treated as a
    /// <see cref="UpdateChannel.Stable"/> build with date/run 0. Always succeeds for a non-empty core — an
    /// unrecognised channel token falls back to <see cref="UpdateChannel.Stable"/> so a malformed stamp
    /// never throws.
    /// </summary>
    public static bool TryParse(string? version, out ChannelStamp stamp)
    {
        stamp = new ChannelStamp(string.Empty, UpdateChannel.Stable, 0, 0);
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        var value = version.Trim();
        var plus = value.IndexOf('+');
        if (plus >= 0)
        {
            value = value[..plus];
        }

        var dash = value.IndexOf('-');
        if (dash < 0)
        {
            stamp = new ChannelStamp(value, UpdateChannel.Stable, 0, 0);
            return true;
        }

        var core = value[..dash];
        var pre = value[(dash + 1)..];
        var parts = pre.Split('.');

        var channel = parts[0].ToLowerInvariant() switch
        {
            "nightly" => UpdateChannel.Nightly,
            "preview" => UpdateChannel.Preview,
            _ => UpdateChannel.Stable
        };

        var date = parts.Length > 1 && int.TryParse(parts[1], out var d) ? d : 0;
        var run = parts.Length > 2 && int.TryParse(parts[2], out var r) ? r : 0;

        stamp = new ChannelStamp(core, channel, date, run);
        return core.Length > 0;
    }

    /// <summary>Ordering within the same channel: the newer (date, run) wins. Undefined across channels.</summary>
    public int CompareBuildTo(ChannelStamp other)
    {
        var byDate = Date.CompareTo(other.Date);
        return byDate != 0 ? byDate : Run.CompareTo(other.Run);
    }
}
