using System.Globalization;

namespace Lionear.SqlExplorer.Sdk.Schema;

/// <summary>A table's on-disk size (bytes) and estimated row count, used to build its tree badge/tooltip.</summary>
public readonly record struct TableStats(long Size, long Rows)
{
    /// <summary>Hover text for a row estimate ("1,234,567 rows"), or null when unknown/zero.</summary>
    public static string? RowTooltip(long rows) =>
        rows > 0 ? rows.ToString("N0", CultureInfo.InvariantCulture) + " rows" : null;
}
