using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using SqlExplorer.Core.Update;

namespace SqlExplorer.App;

/// <summary>
/// Relaunches or shuts the application down. Used by <c>PluginMaintenance</c> (restart to apply staged
/// plugin changes) and by the in-app updater (SE-137) to finish an in-place update or rollback.
/// <see cref="Environment.ProcessPath"/> resolves to the app host (the executable inside the .app bundle on
/// macOS), so starting it relaunches cleanly.
/// </summary>
public static class AppRestart
{
    public static void Restart()
    {
        if (Environment.ProcessPath is { } exe)
        {
            RelaunchWith(exe);
        }
        else
        {
            Shutdown();
        }
    }

    /// <summary>Carries out an updater apply/rollback result: relaunch the new build and exit, exit for the
    /// installer to take over, or (guided/failed) leave the app running.</summary>
    public static void Execute(ApplyResult result)
    {
        switch (result.Action)
        {
            case ApplyAction.RelaunchAfterExit when result.RelaunchTarget is { } target:
                RelaunchWith(target);
                break;
            case ApplyAction.ExitForInstaller:
                Shutdown();
                break;
        }
    }

    /// <summary>The argument passed to the freshly-launched instance so it knows it is a relaunch and must
    /// take over the UI unconditionally — skipping the single-instance probe that would otherwise make it
    /// defer to the still-shutting-down old instance and exit, leaving no window (SE-125).</summary>
    public const string RelaunchArgument = "--relaunched";

    private static void RelaunchWith(string exe)
    {
        try
        {
            var psi = BuildRelaunchStartInfo(exe);
            RestartDiagnostics.Log(
                $"relaunch: exe={psi.FileName} args=[{string.Join(' ', psi.ArgumentList)}] cwd={psi.WorkingDirectory}");
            var child = Process.Start(psi);
            RestartDiagnostics.Log(child is null
                ? "relaunch: Process.Start returned null"
                : $"relaunch: started child pid={child.Id}");
        }
        catch (Exception ex)
        {
            RestartDiagnostics.Log($"relaunch: FAILED {ex.GetType().Name}: {ex.Message}");
        }

        Shutdown();
    }

    // Build the command that re-launches this app. The naive Process.Start(ProcessPath) breaks when the app
    // runs through the dotnet muxer (framework-dependent / `dotnet App.dll`): ProcessPath is then the muxer,
    // and starting it bare just prints help — no app. In that case relaunch the entry .dll through the muxer.
    // A --relaunched marker tells the new instance to skip the single-instance probe (SE-125).
    private static ProcessStartInfo BuildRelaunchStartInfo(string exe)
    {
        var psi = new ProcessStartInfo { FileName = exe, UseShellExecute = false };

        var entry = Environment.GetCommandLineArgs().FirstOrDefault();
        var viaMuxer = Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        if (viaMuxer && entry is not null && entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add(entry);
        }

        psi.ArgumentList.Add(RelaunchArgument);
        psi.WorkingDirectory = AppContext.BaseDirectory;
        RestartDiagnostics.Log(
            $"relaunch: ProcessPath={exe} entry={entry} viaMuxer={viaMuxer} argv=[{string.Join(' ', Environment.GetCommandLineArgs())}]");
        return psi;
    }

    private static void Shutdown()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }
}
