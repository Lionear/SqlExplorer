using System.Diagnostics;
using SqlExplorer.Core.Update;

namespace SqlExplorer.Infrastructure.Update;

/// <summary>
/// Platform in-place updater (SE-137, Fase 2), hybrid strategy:
/// <list type="bullet">
///   <item><b>Linux</b>: swap the AppImage file beside the running one, keeping <c>&lt;app&gt;.prev</c> for a
///   reversible rollback — the same "stage next, demote current to .prev, promote" move as
///   <c>PluginMaintenance</c>, but on the single AppImage the app runs from (<c>$APPIMAGE</c>). Replacing the
///   file while the process runs is safe: the kernel keeps the old inode until exit.</item>
///   <item><b>Windows</b>: launch the downloaded per-user installer silently; it closes the app, replaces the
///   files and relaunches. No <c>.prev</c> here — rollback would mean reinstalling.</item>
///   <item><b>macOS</b>: open the DMG so the user drags the app over (guided; fully silent needs notarization,
///   Fase 3).</item>
/// </list>
/// Only stages files / launches processes; the relaunch-and-exit itself is the host's job (it owns the
/// desktop lifetime). A failed swap leaves <c>.prev</c> intact so the old build still runs.
/// </summary>
public sealed class UpdateApplier : IUpdateApplier
{
    // The AppImage path the app was launched from (set by the AppImage runtime; unset under `dotnet run`).
    private static string? AppImagePath => Environment.GetEnvironmentVariable("APPIMAGE");

    private static string PrevPath(string live) => live + ".prev";

    public bool CanApplyInPlace(UpdateAsset asset) => (asset.Kind ?? string.Empty).ToLowerInvariant() switch
    {
        "appimage" => OperatingSystem.IsLinux() && AppImagePath is { Length: > 0 },
        "installer" => OperatingSystem.IsWindows(),
        "dmg" => OperatingSystem.IsMacOS(),
        _ => false
    };

    public async Task<ApplyResult> ApplyAsync(string filePath, UpdateAsset asset, CancellationToken ct)
    {
        try
        {
            return (asset.Kind ?? string.Empty).ToLowerInvariant() switch
            {
                "appimage" => await ApplyAppImageAsync(filePath, ct),
                "installer" => ApplyWindowsInstaller(filePath),
                "dmg" => OpenDmg(filePath),
                _ => ApplyResult.Fail($"No in-place update for asset kind '{asset.Kind}'.")
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return ApplyResult.Fail(ex.Message);
        }
    }

    public bool CanRollback =>
        OperatingSystem.IsLinux() && AppImagePath is { Length: > 0 } live && File.Exists(PrevPath(live));

    public ApplyResult Rollback()
    {
        if (!OperatingSystem.IsLinux() || AppImagePath is not { Length: > 0 } live)
        {
            return ApplyResult.Fail("Rollback is only available on the Linux AppImage build.");
        }

        var prev = PrevPath(live);
        if (!File.Exists(prev))
        {
            return ApplyResult.Fail("No previous version to roll back to.");
        }

        try
        {
            // Swap live <-> prev, keeping a .prev pointing at the version we rolled back from, so a rollback
            // can itself be rolled forward.
            var tmp = live + ".swap";
            SafeDelete(tmp);
            File.Move(live, tmp);
            File.Move(prev, live);
            File.Move(tmp, prev);
            MakeExecutable(live);
            return ApplyResult.Relaunch(live);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ApplyResult.Fail(ex.Message);
        }
    }

    // Stage the new AppImage next to the live one (same dir => atomic renames), demote current to .prev,
    // promote the new one. The download may sit on another filesystem, so copy into place first.
    private static async Task<ApplyResult> ApplyAppImageAsync(string downloadedFile, CancellationToken ct)
    {
        if (AppImagePath is not { Length: > 0 } live)
        {
            return ApplyResult.Fail("Not running as an AppImage; download it manually instead.");
        }

        var next = live + ".next";
        var prev = PrevPath(live);
        SafeDelete(next);

        await using (var src = File.OpenRead(downloadedFile))
        await using (var dst = File.Create(next))
        {
            await src.CopyToAsync(dst, ct);
        }

        MakeExecutable(next);
        SafeDelete(prev);
        File.Move(live, prev);
        File.Move(next, live);
        return ApplyResult.Relaunch(live);
    }

    // Per-user silent install; Inno closes the app, replaces files and relaunches (see windows-installer.iss).
    private static ApplyResult ApplyWindowsInstaller(string installerPath)
    {
        Process.Start(new ProcessStartInfo(installerPath)
        {
            UseShellExecute = false,
            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART"
        });
        return ApplyResult.Installer();
    }

    private static ApplyResult OpenDmg(string dmgPath)
    {
        Process.Start(new ProcessStartInfo("open") { UseShellExecute = false, Arguments = $"\"{dmgPath}\"" });
        return ApplyResult.GuidedFlow();
    }

    private static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path,
                mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
    }

    private static void SafeDelete(string path)
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
            // Best-effort; a leftover .next/.swap is overwritten next time.
        }
    }
}
