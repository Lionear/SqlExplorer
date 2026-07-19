using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;
using SqlExplorer.App.ViewModels;
using SqlExplorer.Core.Settings;
using SqlExplorer.Core.Shortcuts;

namespace SqlExplorer.App.Views;

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
                PopulateSubsystemMenu(vm);
                vm.AboutRequested = ShowAboutAsync;
                vm.Update.ChangelogRequested = ShowUpdateChangelogAsync;
                // SE-151: the banner now downloads + installs inline, so wire its apply/reveal callbacks here.
                vm.Update.ApplyRequested = result => { AppRestart.Execute(result); return Task.CompletedTask; };
                vm.Update.OpenRequested = RevealFolderAsync;
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

    private bool _subsystemMenuBuilt;

    // SE-164 menu seam: append the plugin-contributed Tools-menu items after the static ones. Built once —
    // the DataContextChanged handler also fires on the language-switch DataContext toggle, which would
    // otherwise duplicate them. Plugin titles come from the plugin's localizer at startup.
    private void PopulateSubsystemMenu(MainViewModel vm)
    {
        if (_subsystemMenuBuilt || vm.SubsystemMenuItems.Count == 0)
        {
            return;
        }

        _subsystemMenuBuilt = true;
        ToolsMenu.Items.Add(new Separator());
        foreach (var node in vm.SubsystemMenuItems)
        {
            ToolsMenu.Items.Add(new MenuItem { Header = node.Title, Command = node.Run });
        }
    }

    private async Task ShowAboutAsync(ViewModels.AboutViewModel viewModel) =>
        await new AboutWindow(viewModel).ShowDialog(this);

    private async Task ShowUpdateChangelogAsync(ViewModels.UpdateAvailableViewModel viewModel) =>
        await new UpdateAvailableWindow(viewModel).ShowDialog(this);

    // The guided hand-off (SE-151): reveal the containing folder rather than launch the binary — the user
    // runs the installer themselves. Best-effort: a missing shell handler must not take the window down.
    private static Task RevealFolderAsync(string filePath)
    {
        try
        {
            var folder = System.IO.Path.GetDirectoryName(filePath) ?? filePath;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception)
        {
        }

        return Task.CompletedTask;
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

    // A restored position (RestoreLayout) can land off every monitor after a display change/unplug, which
    // would show the window somewhere invisible. Once opened (Screens is reliable then), if the window's
    // top-left is on no screen, recentre it on the primary/first screen's working area.
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (Screens is not { All.Count: > 0 } screens || screens.All.Any(s => s.Bounds.Contains(Position)))
        {
            return;
        }

        var target = screens.Primary ?? screens.All[0];
        var area = target.WorkingArea;
        var width = (int)Math.Min(area.Width, FrameSize?.Width ?? area.Width);
        var height = (int)Math.Min(area.Height, FrameSize?.Height ?? area.Height);
        Position = new PixelPoint(
            area.X + Math.Max(0, (area.Width - width) / 2),
            area.Y + Math.Max(0, (area.Height - height) / 2));
    }

    private bool _forceClose;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        var settings = _settingsStore?.Load();

        // Close-to-tray: a user-initiated window close (the X button / Alt+F4) hides the window instead of
        // quitting, so the app — and the MCP server — keep running in the background. A real quit (File >
        // Exit, the tray's Quit item, or an OS shutdown) arrives with CloseReason != WindowClosing and falls
        // through to the normal close path below.
        if (!_forceClose
            && e.CloseReason == WindowCloseReason.WindowClosing
            && settings is { CloseToTray: true })
        {
            e.Cancel = true;
            Hide();
            return;
        }

        // Ask for confirmation first, unless the user turned it off (or already confirmed this close). Only
        // when the window is visible — a quit chosen from the tray while hidden has no visible owner for the
        // modal dialog, and is itself an explicit quit, so it closes without a second prompt.
        if (!_forceClose && IsVisible && settings is { ConfirmOnExit: true })
        {
            e.Cancel = true;
            _ = ConfirmExitAsync(_settingsStore!);
            return;
        }

        // Offer to save unsaved query files before the real close (SE-154). The prompt is async, so cancel
        // this close pass and re-close once it resolves (or stay open if the user cancels).
        if (!_dirtyHandled && DataContext is MainViewModel dirtyVm && dirtyVm.ShouldPromptSaveOnExit)
        {
            e.Cancel = true;
            _ = SaveDirtyThenCloseAsync(dirtyVm);
            return;
        }

        PersistLayout();
        (DataContext as MainViewModel)?.PersistOpenTabs();
        base.OnClosing(e);
    }

    private bool _dirtyHandled;

    private async Task SaveDirtyThenCloseAsync(MainViewModel vm)
    {
        if (!await vm.ConfirmCloseAllDirtyAsync())
        {
            _forceClose = false; // user cancelled — return to the normal (close-to-tray/confirm) behaviour
            return;
        }

        _dirtyHandled = true;
        _forceClose = true;
        Close();
    }

    private async Task ConfirmExitAsync(IAppSettingsStore store)
    {
        var loc = (DataContext as MainViewModel)?.Loc;
        var dialog = new ExitConfirmDialog(
            loc?["ExitConfirmTitle"] ?? "Quit",
            loc?["ExitConfirmMessage"] ?? "Are you sure you want to quit?",
            loc?["ExitConfirmQuit"] ?? "Quit",
            loc?["Cancel"] ?? "Cancel",
            loc?["ExitConfirmAlways"] ?? "Always quit without asking");

        var confirmed = await dialog.ShowDialog<bool>(this);
        if (!confirmed)
        {
            return;
        }

        // "Always" ticked → stop asking from now on (persist immediately, before the real close).
        if (dialog.Always)
        {
            var settings = store.Load();
            settings.ConfirmOnExit = false;
            try
            {
                store.Save(settings);
            }
            catch (Exception)
            {
                // Never block quitting on a failed preference write.
            }
        }

        _forceClose = true;
        Close();
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

        // Tool-window sizes (SE-123): read the live grid sizes back into the VM, then persist them
        // alongside the sidebar so a resize survives a restart.
        Body.CaptureToolWindowSizes();
        if (Body.DataContext is ViewModels.MainViewModel vm)
        {
            settings.OutputHeight = vm.OutputWindow.Size;
            settings.HistoryWidth = vm.HistoryWindow.Size;
        }

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
