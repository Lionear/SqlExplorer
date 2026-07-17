using System.IO.Compression;
using System.Security.Cryptography;
using SqlExplorer.Core.Plugins;
using SqlExplorer.Core.Store;
using SqlExplorer.Sdk;
using SqlExplorer.Sdk.Tools;

namespace SqlExplorer.Infrastructure.Store;

/// <summary>
/// Downloads, verifies and stages plugins under the per-user plugin folder. All file work happens in a
/// staging area on the same volume as the target so the final promotion is a rename, and nothing touches
/// a running plugin: an update stages a <c>&lt;id&gt;.next</c> replacement beside the live <c>&lt;id&gt;</c>,
/// and <c>PluginMaintenance</c> swaps them at the next startup (keeping one <c>&lt;id&gt;.prev</c> backup for
/// rollback). Security follows Notes §4.2: size-cap the download, SHA-256 against the index, zip-slip guard
/// the extract, and gate on the host-API version before anything lands.
/// </summary>
public sealed class PluginInstaller(HttpClient http, IPluginStateStore stateStore, string? userRoot = null)
    : IPluginInstaller
{
    // Ceiling used when the index doesn't declare a size, so a mis-configured entry can't stream forever.
    private const long DefaultMaxBytes = 256L * 1024 * 1024;

    private readonly string _userRoot = userRoot ?? PluginPaths.UserRoot;

    public async Task<InstallOutcome> InstallAsync(
        StoreEntry entry, StoreVersion version, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        if (!version.IsCompatible(HostApiVersions.CompatFor(entry.Type)))
        {
            return InstallOutcome.Fail(entry.Id,
                $"Version {version.Version} needs a host API this build doesn't provide.");
        }

        var work = NewWorkDir();
        try
        {
            var cap = version.Size > 0 ? version.Size : DefaultMaxBytes;
            var zipPath = Path.Combine(work, "download.zip");
            await DownloadAsync(version.DownloadUrl, zipPath, cap, entry.Id, progress, ct);

            progress?.Report(new InstallProgress(entry.Id, InstallPhase.Verifying, version.Size, version.Size));
            if (!await Sha256MatchesAsync(zipPath, version.Sha256, ct))
            {
                return InstallOutcome.Fail(entry.Id, "Checksum mismatch — the download was rejected.");
            }

            return await ExtractStageAsync(zipPath, work, entry.Id, entry.Type, progress, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or IOException)
        {
            return InstallOutcome.Fail(entry.Id, ex.Message);
        }
        finally
        {
            TryDelete(work);
        }
    }

    public async Task<InstallOutcome> InstallFromFileAsync(
        string zipPath, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var work = NewWorkDir();
        try
        {
            // No index here, so the expected id comes from the zip's own manifest (id == null skips the match).
            return await ExtractStageAsync(zipPath, work, expectedId: null, expectedType: null, progress, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return InstallOutcome.Fail(Path.GetFileNameWithoutExtension(zipPath), ex.Message);
        }
        finally
        {
            TryDelete(work);
        }
    }

    public InstallOutcome RequestRollback(string pluginId)
    {
        var prev = Path.Combine(_userRoot, pluginId + ".prev");
        if (!Directory.Exists(prev))
        {
            return InstallOutcome.Fail(pluginId, "No previous version to roll back to.");
        }

        // Stage the backup as the next version; the startup swap promotes it and demotes the current copy
        // back to .prev, so a rollback can itself be rolled forward. Instant and offline.
        var next = Path.Combine(_userRoot, pluginId + ".next");
        TryDelete(next);
        Directory.Move(prev, next);

        stateStore.Save(pluginId, stateStore.Get(pluginId) with { Pending = PluginPendingAction.Install });
        return InstallOutcome.Ok(pluginId, null);
    }

    // Extract (zip-slip guarded), validate the manifest + host-API gate, then promote into place.
    private async Task<InstallOutcome> ExtractStageAsync(
        string zipPath, string work, string? expectedId, string? expectedType,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var extractDir = Path.Combine(work, "extract");
        progress?.Report(new InstallProgress(expectedId ?? string.Empty, InstallPhase.Extracting, 0, null));
        ExtractZipSafe(zipPath, extractDir);
        ct.ThrowIfCancellationRequested();

        var manifestPath = Path.Combine(extractDir, "plugin.json");
        if (!File.Exists(manifestPath))
        {
            return InstallOutcome.Fail(expectedId ?? "plugin", "The archive has no plugin.json at its root.");
        }

        var manifest = PluginManifest.Load(manifestPath);
        if (expectedId is not null && manifest.Id != expectedId)
        {
            return InstallOutcome.Fail(expectedId, $"The archive declares id '{manifest.Id}', expected '{expectedId}'.");
        }

        if (expectedType is not null && manifest.Type != expectedType)
        {
            return InstallOutcome.Fail(manifest.Id, $"The archive declares type '{manifest.Type}', expected '{expectedType}'.");
        }

        if (CompatError(manifest) is { } compatError)
        {
            return InstallOutcome.Fail(manifest.Id, compatError);
        }

        progress?.Report(new InstallProgress(manifest.Id, InstallPhase.Staging, 0, null));
        Stage(extractDir, manifest.Id);
        stateStore.Save(manifest.Id, stateStore.Get(manifest.Id) with
        {
            Enabled = true,
            Pending = PluginPendingAction.Install
        });

        progress?.Report(new InstallProgress(manifest.Id, InstallPhase.Done, 0, null));
        return InstallOutcome.Ok(manifest.Id, manifest.Version);
    }

    // Fresh install writes straight to <id>/ (nothing loaded there yet); an update stages <id>.next beside
    // the running copy for the startup swap to promote.
    private void Stage(string extractDir, string id)
    {
        var target = Path.Combine(_userRoot, id);
        if (Directory.Exists(target))
        {
            var next = target + ".next";
            TryDelete(next);
            Directory.Move(extractDir, next);
        }
        else
        {
            Directory.CreateDirectory(_userRoot);
            Directory.Move(extractDir, target);
        }
    }

    private static string? CompatError(PluginManifest manifest) => manifest.Type switch
    {
        PluginManifest.Types.Provider when !ProviderHostApi.IsCompatible(manifest.HostApiVersion) =>
            $"Plugin targets provider host API v{manifest.HostApiVersion}, this host is v{ProviderHostApi.Version}.",
        PluginManifest.Types.Tool when !ToolHostApi.IsCompatible(manifest.HostApiVersion) =>
            $"Plugin targets tool host API v{manifest.HostApiVersion}, this host is v{ToolHostApi.Version}.",
        PluginManifest.Types.Provider or PluginManifest.Types.Tool => null,
        _ => $"Unknown plugin type '{manifest.Type}'."
    };

    private async Task DownloadAsync(
        string url, string destPath, long cap, string id, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        // Checked here and not only on the index URL: a DownloadUrl is a separate field and an https index is
        // free to point its downloads at http. That is the one hop where the checksum below stops meaning
        // anything, since an attacker rewriting the zip serves the matching hash too (SE-134).
        StoreUrl.EnsureAllowed(url);

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
            progress?.Report(new InstallProgress(id, InstallPhase.Downloading, read, declared));
        }
    }

    private static async Task<bool> Sha256MatchesAsync(string path, string expectedHex, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);
        return string.Equals(Convert.ToHexString(hash), expectedHex, StringComparison.OrdinalIgnoreCase);
    }

    // Extracts every entry, rejecting any whose resolved path escapes the destination (zip-slip).
    private static void ExtractZipSafe(string zipPath, string destDir)
    {
        Directory.CreateDirectory(destDir);
        var destFull = Path.GetFullPath(destDir + Path.DirectorySeparatorChar);

        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            var targetPath = Path.GetFullPath(Path.Combine(destDir, entry.FullName));
            if (!targetPath.StartsWith(destFull, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Archive entry '{entry.FullName}' escapes the target directory.");
            }

            // A directory entry (name ends in a separator) has an empty Name.
            if (entry.Name.Length == 0)
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            entry.ExtractToFile(targetPath, overwrite: true);
        }
    }

    private string NewWorkDir()
    {
        var dir = Path.Combine(_userRoot, ".staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup; a leftover staging folder is harmless and reused-by-guid next time.
        }
    }
}
