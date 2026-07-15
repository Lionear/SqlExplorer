using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using SqlExplorer.App.DependencyInjection;
using SqlExplorer.App.Theming;
using SqlExplorer.App.ViewModels;
using SqlExplorer.App.Views;
using SqlExplorer.Core.Localization;
using SqlExplorer.Core.Settings;
using SqlExplorer.Core.Shortcuts;
using Microsoft.Extensions.DependencyInjection;

namespace SqlExplorer.App;

public partial class App : Application
{
    // Held for the lifetime of the app so it is not garbage-collected while the app runs.
    private TrayIcon? _trayIcon;
    private readonly CancellationTokenSource _shutdownCts = new();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var services = AppServices.Build();

        // Apply the saved theme/language before anything renders, so there's no flash of the
        // wrong theme or a Language toggle needed after every launch.
        var settingsStore = services.GetRequiredService<IAppSettingsStore>();
        var settings = settingsStore.Load();
        ThemeApplier.Apply(settings.Theme);
        if (settings.Language is { Length: > 0 } language)
        {
            services.GetRequiredService<ILocalizer>().SetCulture(CultureInfo.GetCultureInfo(language));
        }

        // Apply the saved query-log policy before any query can run (SettingsViewModel re-applies on save).
        services.GetRequiredService<Core.Logging.IQueryLog>()
            .Configure(settings.QueryLogEnabled, settings.QueryLogApp, settings.QueryLogMcp);

        var viewModel = services.GetRequiredService<MainViewModel>();
        var keymap = services.GetRequiredService<KeymapService>();

        switch (ApplicationLifetime)
        {
            case IClassicDesktopStyleApplicationLifetime desktop:
                var mainWindow = new MainWindow(settingsStore, keymap) { DataContext = viewModel };
                desktop.MainWindow = mainWindow;

                // System-tray presence so the app can keep running in the background (notably the MCP
                // server) after a close-to-tray. The icon is always available while the app runs; whether
                // the window's close button hides or quits is governed by the CloseToTray setting, which
                // MainWindow reads live on each close. Quit from here or File > Exit routes through
                // desktop.Shutdown(), which closes with CloseReason=ApplicationShutdown (a real quit).
                // Opening the log from the tray surfaces the window first (it may be hidden), then runs the
                // same command the menu uses — which shows the non-modal, single-instance Query Log window.
                void OpenQueryLog()
                {
                    ShowWindow(mainWindow);
                    (mainWindow.DataContext as MainViewModel)?.OpenQueryLogCommand.Execute(null);
                }

                _trayIcon = BuildTrayIcon(desktop, mainWindow, services.GetRequiredService<ILocalizer>(), OpenQueryLog);

                // Single-instance listener: a second launch (e.g. clicking the app again while it's hidden
                // in the tray) signals us here instead of opening a new window. Marshal onto the UI thread.
                SingleInstance.StartServer(
                    () => Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowWindow(mainWindow)),
                    _shutdownCts.Token);

                desktop.Exit += (_, _) =>
                {
                    _shutdownCts.Cancel();
                    _trayIcon?.Dispose();
                };
                // Stop the MCP listener cleanly on exit so its loopback port is released promptly. Run it
                // OFF the UI thread with a timeout: awaiting StopAsync's continuations back onto the (blocked)
                // UI thread would deadlock and hang shutdown; the OS reclaims the port regardless, so this is
                // best-effort and must never block exit.
                desktop.ShutdownRequested += (_, _) =>
                {
                    try
                    {
                        Task.Run(() => services.GetRequiredService<Mcp.Hosting.McpService>().StopAsync())
                            .Wait(TimeSpan.FromSeconds(2));
                    }
                    catch
                    {
                        // Best-effort: never let a slow/failed stop hold the app open.
                    }
                };
                break;
            case ISingleViewApplicationLifetime singleView:
                singleView.MainView = new MainView { DataContext = viewModel };
                break;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static TrayIcon BuildTrayIcon(IClassicDesktopStyleApplicationLifetime desktop, Window window, ILocalizer loc, Action openQueryLog)
    {
        var show = new NativeMenuItem(loc["TrayShow"]);
        show.Click += (_, _) => ShowWindow(window);

        var queryLog = new NativeMenuItem(loc["QueryLogMenu"]);
        queryLog.Click += (_, _) => openQueryLog();

        var quit = new NativeMenuItem(loc["Exit"]);
        quit.Click += (_, _) => desktop.Shutdown();

        var menu = new NativeMenu();
        menu.Add(show);
        menu.Add(queryLog);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(quit);

        var tray = new TrayIcon
        {
            Icon = LoadTrayIcon(),
            ToolTipText = "SQL Explorer",
            IsVisible = true,
            Menu = menu
        };
        // Left-click the tray icon also restores the window (a common convention on Windows/Linux).
        tray.Clicked += (_, _) => ShowWindow(window);
        return tray;
    }

    private static void ShowWindow(Window window)
    {
        window.Show();
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }
        window.Activate();
    }

    private static WindowIcon LoadTrayIcon()
    {
        using var stream = AssetLoader.Open(new Uri("avares://SqlExplorer.App/Assets/icon.png"));
        return new WindowIcon(stream);
    }
}
