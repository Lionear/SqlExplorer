using System.IO;

namespace Lionear.SqlExplorer.Sdk;

/// <summary>
/// Loads a provider's brand icon from an <c>icon.png</c> embedded in the provider assembly, falling
/// back to a glyph when none is embedded. Providers call this from their <see cref="IDbProvider.Icon"/>
/// so shipping a logo is just dropping <c>icon.png</c> next to the project — no code change.
/// </summary>
public static class ProviderIconLoader
{
    public static ProviderIcon Load(Type providerType, string glyphFallback)
    {
        using var stream = providerType.Assembly.GetManifestResourceStream("icon.png");
        if (stream is not null)
        {
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return ProviderIcon.FromImage(memory.ToArray(), "image/png", glyphFallback);
        }

        return ProviderIcon.FromGlyph(glyphFallback);
    }
}
