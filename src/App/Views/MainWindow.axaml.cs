using System;
using Avalonia;
using Avalonia.Controls;
using Lionear.SqlExplorer.Core.Settings;

namespace Lionear.SqlExplorer.App.Views;

public partial class MainWindow : Window
{
    private readonly IAppSettingsStore? _settingsStore;

    // Parameterless ctor keeps the XAML previewer happy; the real app uses the injected overload.
    public MainWindow() : this(null)
    {
    }

    public MainWindow(IAppSettingsStore? settingsStore)
    {
        _settingsStore = settingsStore;
        InitializeComponent();
        RestoreLayout();
    }

    private void RestoreLayout()
    {
        if (_settingsStore is null)
        {
            return;
        }

        var settings = _settingsStore.Load();

        if (settings.WindowWidth is { } w && settings.WindowHeight is { } h)
        {
            Width = w;
            Height = h;
        }

        // Only honour a stored position when both coordinates are present, so a partially
        // written file can't drop the window at an off-screen corner.
        if (settings.WindowX is { } x && settings.WindowY is { } y)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint((int)x, (int)y);
        }

        if (settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }

        Body.RestoreSidebarWidth(settings.SidebarWidth);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        PersistLayout();
        base.OnClosing(e);
    }

    private void PersistLayout()
    {
        if (_settingsStore is null)
        {
            return;
        }

        var settings = _settingsStore.Load();
        var maximized = WindowState == WindowState.Maximized;
        settings.WindowMaximized = maximized;

        // When maximized, Width/Height/Position describe the maximized frame; keep the last
        // normal-state values so restoring un-maximizes to a sane size and place.
        if (!maximized)
        {
            settings.WindowWidth = Width;
            settings.WindowHeight = Height;
            settings.WindowX = Position.X;
            settings.WindowY = Position.Y;
        }

        settings.SidebarWidth = Body.SidebarWidth;

        try
        {
            _settingsStore.Save(settings);
        }
        catch (Exception)
        {
            // Never block window close on a failed preference write.
        }
    }
}
