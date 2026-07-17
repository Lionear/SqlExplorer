using System.Text.Json;
using SqlExplorer.Core.Store;
using SqlExplorer.Core.Update;

namespace SqlExplorer.Infrastructure.Update;

/// <summary>
/// Fetches <c>update.json</c> from the channel's rolling GitHub release. Follows the Store's transport rule
/// (https-only via <see cref="StoreUrl"/>) and caps the download, so a mis-served manifest can't stream
/// forever. Every expected failure (offline, 404, malformed JSON, oversize) resolves to <c>null</c>: an
/// update check that can't reach the network is a silent no-op, never a crash or a dialog.
/// </summary>
public sealed class HttpUpdateManifestSource(HttpClient http, string? baseUrl = null) : IUpdateManifestSource
{
    // Where the rolling per-channel releases live. The tag is appended per channel (nightly/preview/stable).
    private const string DefaultBaseUrl = "https://github.com/Lionear/SqlExplorer/releases/download";

    // A manifest is small; anything larger than this is not the file we asked for.
    private const long MaxBytes = 1L * 1024 * 1024;

    private readonly string _baseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');

    public async Task<UpdateManifest?> FetchAsync(UpdateChannel channel, CancellationToken ct)
    {
        var url = $"{_baseUrl}/{channel.Tag()}/update.json";
        try
        {
            StoreUrl.EnsureAllowed(url);

            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var declared = response.Content.Headers.ContentLength;
            if (declared is { } len && len > MaxBytes)
            {
                return null;
            }

            var json = await ReadCappedAsync(response, ct);
            return json is null ? null : UpdateManifest.Parse(json);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is HttpRequestException or TaskCanceledException or InvalidDataException
                or JsonException or IOException)
        {
            return null;
        }
    }

    private static async Task<string?> ReadCappedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int n;
        while ((n = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + n > MaxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, n);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}
