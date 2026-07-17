using System;
using System.Diagnostics;
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

    private static void RelaunchWith(string exe)
    {
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
        Shutdown();
    }

    private static void Shutdown()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }
}
