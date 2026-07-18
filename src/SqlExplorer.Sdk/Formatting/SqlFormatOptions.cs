namespace SqlExplorer.Sdk.Formatting;

/// <summary>How SQL keywords are cased when formatting.</summary>
public enum KeywordCasing
{
    Upper,
    Lower,
    Preserve
}

/// <summary>User-tunable formatting options, surfaced in Settings.</summary>
public sealed record SqlFormatOptions
{
    public KeywordCasing KeywordCasing { get; init; } = KeywordCasing.Upper;

    public int IndentSize { get; init; } = 4;

    public static SqlFormatOptions Default { get; } = new();
}
