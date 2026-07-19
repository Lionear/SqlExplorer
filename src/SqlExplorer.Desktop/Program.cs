using System;
using System.Linq;
using Avalonia;

namespace SqlExplorer.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // A relaunch (Restart-app button / in-app updater) must always take over the UI. Skip the
        // single-instance probe when relaunched, so the new instance doesn't connect to the old one's pipe
        // (still open while it shuts down), defer to it and exit — which left no window at all (SE-125).
        var relaunched = args.Contains(SqlExplorer.App.AppRestart.RelaunchArgument);
        SqlExplorer.App.RestartDiagnostics.Log(
            $"start: relaunched={relaunched} argv=[{string.Join(' ', args)}]");

        // Single instance: if the app is already running (possibly hidden in the tray), tell it to surface
        // its window and exit — don't open a second copy. The primary's listener is started in App.
        if (!relaunched && !SqlExplorer.App.SingleInstance.TryBecomePrimary())
        {
            SqlExplorer.App.RestartDiagnostics.Log("start: deferred to existing primary — exiting");
            return;
        }

        SqlExplorer.App.RestartDiagnostics.Log("start: becoming primary — launching UI");
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<SqlExplorer.App.App>()
            .UsePlatformDetect()
            .LogToTrace();
}
