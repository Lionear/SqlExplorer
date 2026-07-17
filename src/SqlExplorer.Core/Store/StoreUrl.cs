namespace SqlExplorer.Core.Store;

/// <summary>
/// The transport rule for everything the Store fetches — index feeds and plugin downloads alike.
/// </summary>
/// <remarks>
/// A plugin is loaded in-process, so whoever controls the bytes controls the app and every stored
/// connection. The sha256 in an index is no defence on its own: it travels over the same channel as the
/// zip it describes, so anything that can rewrite the download can rewrite the hash to match. TLS is what
/// makes that hash mean something, which is why the scheme is enforced at every fetch rather than only
/// where a URL is entered — a URL can also arrive by hand-editing <c>store-sources.json</c>.
/// </remarks>
public static class StoreUrl
{
    /// <summary>True when <paramref name="url"/> may be fetched: absolute https, or plain http on the
    /// loopback interface (a local index while developing a plugin, where there is no network to attack).</summary>
    public static bool IsAllowed(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

    /// <summary>Throws when <paramref name="url"/> is not fetchable. <see cref="InvalidDataException"/> is
    /// deliberate: both the catalog fetch and the installer already turn it into a per-source error or a
    /// failed install instead of taking the whole app down.</summary>
    public static void EnsureAllowed(string? url)
    {
        if (!IsAllowed(url))
        {
            throw new InvalidDataException(
                $"Refusing to fetch over an insecure or malformed URL: {url}. Store sources must use https.");
        }
    }
}
