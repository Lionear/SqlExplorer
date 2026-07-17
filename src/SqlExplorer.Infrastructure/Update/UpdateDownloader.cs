using System.Security.Cryptography;
using SqlExplorer.Core.Plugins;
using SqlExplorer.Core.Store;
using SqlExplorer.Core.Update;

namespace SqlExplorer.Infrastructure.Update;

/// <summary>Result of a <see cref="UpdateDownloader.DownloadAsync"/>: the verified file, or why it failed.</summary>
public sealed record UpdateDownloadOutcome(bool Success, string? FilePath, string? Kind, string? Error)
{
    public static UpdateDownloadOutcome Ok(string filePath, string? kind) => new(true, filePath, kind, null);
    public static UpdateDownloadOutcome Fail(string error) => new(false, null, null, error);
}

/// <summary>
/// Downloads an update asset into a per-user updates folder and verifies its SHA-256 before handing it to
/// the user — the same size-cap / https / checksum discipline the Plugin Store's installer uses
/// (<c>PluginInstaller</c>), minus the in-place swap (that's Fase 2 of SE-137). What happens to the
/// verified file (open the folder, launch the installer) is the caller's job.
/// </summary>
public sealed class UpdateDownloader(HttpClient http, string? downloadRoot = null)
{
    private const long DefaultMaxBytes = 512L * 1024 * 1024;

    // Sibling of the plugin root under the app's config directory: <config>/updates.
    private readonly string _downloadRoot = downloadRoot
        ?? Path.Combine(Path.GetDirectoryName(PluginPaths.UserRoot) ?? PluginPaths.UserRoot, "updates");

    public async Task<UpdateDownloadOutcome> DownloadAsync(
        UpdateAsset asset, IProgress<double>? progress, CancellationToken ct)
    {
        try
        {
            StoreUrl.EnsureAllowed(asset.Url);

            Directory.CreateDirectory(_downloadRoot);
            var fileName = SafeFileName(asset.Url);
            var destPath = Path.Combine(_downloadRoot, fileName);
            var cap = asset.Size > 0 ? asset.Size : DefaultMaxBytes;

            await DownloadToFileAsync(asset.Url, destPath, cap, progress, ct);

            if (!await Sha256MatchesAsync(destPath, asset.Sha256, ct))
            {
                TryDeleteFile(destPath);
                return UpdateDownloadOutcome.Fail("Checksum mismatch — the download was rejected.");
            }

            return UpdateDownloadOutcome.Ok(destPath, asset.Kind);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is HttpRequestException or TaskCanceledException or InvalidDataException or IOException)
        {
            return UpdateDownloadOutcome.Fail(ex.Message);
        }
    }

    private async Task DownloadToFileAsync(
        string url, string destPath, long cap, IProgress<double>? progress, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var declared = response.Content.Headers.ContentLength;
        if (declared is { } len && len > cap)
        {
            throw new InvalidDataException($"Download is larger than the allowed {cap} bytes.");
        }

        await using var src = await response.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(destPath);

        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            read += n;
            if (read > cap)
            {
                throw new InvalidDataException($"Download exceeded the allowed {cap} bytes.");
            }

            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            if (declared is { } total && total > 0)
            {
                progress?.Report((double)read / total);
            }
        }
    }

    private static async Task<bool> Sha256MatchesAsync(string path, string expectedHex, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);
        return string.Equals(Convert.ToHexString(hash), expectedHex, StringComparison.OrdinalIgnoreCase);
    }

    // The URL's last segment, stripped to a boring file name so nothing in it can escape the folder.
    private static string SafeFileName(string url)
    {
        var last = url.Split('/', '?', '#').LastOrDefault(s => s.Length > 0) ?? "update.bin";
        var cleaned = Path.GetFileName(last);
        return string.IsNullOrWhiteSpace(cleaned) ? "update.bin" : cleaned;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort; a leftover rejected download is harmless and overwritten next time.
        }
    }
}
