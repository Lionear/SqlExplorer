namespace Lionear.SqlExplorer.Sdk.Branding;

/// <summary>
/// A provider's icon, shown on its connection nodes in the tree. A provider may supply a
/// lightweight text <see cref="Glyph"/> (emoji or icon-font character), raw image bytes
/// (<see cref="ImageData"/>, e.g. a brand logo), or both. The host prefers the image when it
/// can render it and falls back to the glyph — so shipping a glyph alongside an image gives a
/// safe fallback on hosts/platforms that can't decode the image format.
/// </summary>
public sealed record ProviderIcon
{
    /// <summary>A single glyph/emoji to render as text, e.g. "🐘". Optional.</summary>
    public string? Glyph { get; init; }

    /// <summary>Raw image bytes (see <see cref="ImageMediaType"/>). Optional.</summary>
    public byte[]? ImageData { get; init; }

    /// <summary>MIME type of <see cref="ImageData"/>, e.g. "image/png" or "image/svg+xml".</summary>
    public string? ImageMediaType { get; init; }

    public static ProviderIcon FromGlyph(string glyph) => new() { Glyph = glyph };

    public static ProviderIcon FromImage(byte[] data, string mediaType, string? glyphFallback = null) =>
        new() { ImageData = data, ImageMediaType = mediaType, Glyph = glyphFallback };
}
