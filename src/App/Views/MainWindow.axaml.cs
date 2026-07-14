using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;
using Lionear.SqlExplorer.App.ViewModels;
using Lionear.SqlExplorer.Core.Settings;
using Lionear.SqlExplorer.Core.Shortcuts;

namespace Lionear.SqlExplorer.App.Views;

public partial class MainWindow : Window
{
    private readonly IAppSettingsStore? _settingsStore;
    private readonly KeymapService? _keymap;

    // Parameterless ctor keeps the XAML previewer happy; the real app uses the injected overload.
    public MainWindow() : this(null, null)
    {
    }

    public MainWindow(IAppSettingsStore? settingsStore, KeymapService? keymap)
    {
        _settingsStore = settingsStore;
        _keymap = keymap;
        InitializeComponent();
        RestoreLayout();

        // Rebuild the window's key bindings whenever the user changes the keymap in Settings.
        if (_keymap is not null)
        {
            _keymap.Changed += RebuildKeyBindings;
        }

        // macOS gets its menu bar from NativeMenu.Menu (set in XAML) — the in-window Menu would
        // otherwise render a second, redundant bar underneath the title bar there.
        if (OperatingSystem.IsMacOS())
        {
            AppMenu.IsVisible = false;
        }

        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.AboutRequested = ShowAboutAsync;
                RebuildKeyBindings();

                // A language switch fires Loc.PropertyChanged(null) — the correct "everything on
                // this object changed" signal, and Loc[key] does return the fresh string right away,
                // but that alone does not repaint anything already on screen (confirmed: neither more
                // dispatcher pumps nor an explicit InvalidateVisual on every control in the tree makes
                // a difference — the bindings themselves never re-pull the new value, this isn't a
                // paint/layout problem). Toggling DataContext off and back forces every binding under
                // it to tear down and re-create from scratch, which does re-read the fresh value —
                // the same "reuse a control, swap its DataContext" mechanism DocumentView already
                // relies on for tab reuse, just applied here to force a refresh instead.
                vm.Loc.PropertyChanged += (_, _) =>
                {
                    var dataContext = DataContext;
                    DataContext = null;
                    DataContext = dataContext;
                };
            }
        };
    }

    // Materialize the live keymap into Window.KeyBindings. Called once the VM is attached and again on
    // every keymap change. Only Window-scoped commands land here; editor-scoped ones (toggle comment)
    // are handled by the SQL editor itself. Unparseable or unbound gestures are simply skipped.
    private void RebuildKeyBindings()
    {
        if (_keymap is null || DataContext is not MainViewModel vm)
        {
            return;
        }

        KeyBindings.Clear();
        foreach (var command in _keymap.Commands)
        {
            if (command.Scope != ShortcutScope.Window)
            {
                continue;
            }

            var gesture = _keymap.Resolve(command.Id);
            if (string.IsNullOrWhiteSpace(gesture)
                || vm.ResolveShortcut(command.Id) is not { } target
                || TryParseGesture(gesture) is not { } parsed)
            {
                continue;
            }

            KeyBindings.Add(new KeyBinding { Gesture = parsed, Command = target });
        }

        // Plugin-contributed shortcuts (always window-scoped): wrap each plugin action in a command.
        foreach (var plugin in _keymap.PluginShortcuts)
        {
            var gesture = _keymap.Resolve(plugin.Id);
            if (string.IsNullOrWhiteSpace(gesture) || TryParseGesture(gesture) is not { } parsed)
            {
                continue;
            }

            var action = plugin.ExecuteAsync;
            KeyBindings.Add(new KeyBinding
            {
                Gesture = parsed,
                Command = new AsyncRelayCommand(() => action(CancellationToken.None))
            });
        }
    }

    // KeyGesture.Parse throws on a malformed string; treat any bad persisted gesture as "no binding".
    private static KeyGesture? TryParseGesture(string gesture)
    {
        try
        {
            return KeyGesture.Parse(gesture);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task ShowAboutAsync()
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        await new AboutWindow(vm.Loc).ShowDialog(this);
    }

    private void RestoreLayout()
    {
        if (_settingsStore is null)
        {
            return;
        }

        var settings = _settingsStore.Load();

        // Guard against a zero/negative size: an interrupted first run (crash or a kill before the
        // window was ever measured) can persist 0x0, which would restore to an invisible window.
        if (settings.WindowWidth is { } w && settings.WindowHeight is { } h && w > 0 && h > 0)
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
        // normal-state values so restoring un-maximizes to a sane size and place. Also skip a
        // NaN/zero size — that means the window was never laid out (e.g. closed/killed during
        // startup), and persisting it would restore to an invisible 0x0 window next run.
        // (A relational pattern is already false for NaN, so `is > 0` covers the never-laid-out case.)
        if (!maximized && Width is > 0 && Height is > 0)
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
