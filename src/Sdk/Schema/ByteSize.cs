using System.Globalization;

namespace Lionear.SqlExplorer.Sdk.Schema;

/// <summary>
/// Formats a byte count as a short, DataGrip-style size badge — <c>720K</c>, <c>464M</c>, <c>1.8G</c> —
/// using binary (1024) units. One decimal below ten, whole numbers above, so the badge stays narrow.
/// </summary>
public static class ByteSize
{
    private static readonly string[] Units = ["B", "K", "M", "G", "T", "P"];

    /// <summary>Formats <paramref name="bytes"/>, or returns null for a non-positive/unknown size so the
    /// caller can simply skip the badge.</summary>
    public static string? Format(long bytes)
    {
        if (bytes <= 0)
        {
            return null;
        }

        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        // Bytes stay whole ("512B"); larger units get one decimal under ten ("1.8G") and none above ("464M").
        var format = unit == 0 || size >= 10 ? "0" : "0.0";
        return size.ToString(format, CultureInfo.InvariantCulture) + Units[unit];
    }
}
