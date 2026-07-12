namespace Lionear.SqlExplorer.Core.Formatting;

public enum KeywordCasing
{
    Upper,
    Lower,
    Preserve
}

public sealed record SqlFormatOptions
{
    public KeywordCasing KeywordCasing { get; init; } = KeywordCasing.Upper;

    public int IndentSize { get; init; } = 4;

    public static SqlFormatOptions Default { get; } = new();
}
