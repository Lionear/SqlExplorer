using Avalonia;

namespace Lionear.SqlExplorer.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Lionear.SqlExplorer.App.App>()
            .UsePlatformDetect()
            .LogToTrace();
}
