using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SqlExplorer.App.ViewModels;

namespace SqlExplorer.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SettingsViewModel vm)
            {
                vm.CloseRequested = Close;
                // Master-password prompts (Set/Change/Disable) — no inline validator; the service verifies.
                vm.PromptMasterPassword = mode =>
                    new MasterPasswordDialog(mode, vm.Loc, null).ShowDialog<MasterPasswordDialogResult?>(this);
                // Update rollback (SE-137): relaunch the previous build and exit via the desktop lifetime.
                vm.RollbackRequested = result =>
                {
                    AppRestart.Execute(result);
                    return System.Threading.Tasks.Task.CompletedTask;
                };
                // "What's new" from the Updates pane opens the changelog dialog owned by this window.
                vm.ChangelogRequested = dialog => new UpdateAvailableWindow(dialog).ShowDialog(this);
            }
        };
    }

    // A File/Folder plugin setting: pick a path (a binary like mysqldump, or a default folder).
    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PluginSettingFieldInput input })
        {
            return;
        }

        if (input.IsFolder)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
            if (folders.Count > 0)
            {
                input.Value = folders[0].TryGetLocalPath() ?? folders[0].Path.ToString();
            }

            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false });
        if (files.Count > 0)
        {
            input.Value = files[0].TryGetLocalPath() ?? files[0].Path.ToString();
        }
    }

    // Copy the MCP bearer token to the clipboard.
    private async void OnCopyMcpTokenClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel { McpToken: { Length: > 0 } token }
            && TopLevel.GetTopLevel(this) is { Clipboard: { } clipboard })
        {
            await clipboard.SetTextAsync(token);
        }
    }

    // Copy the MCP server URL to the clipboard.
    private async void OnCopyMcpUrlClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel { McpUrl: { Length: > 0 } url }
            && TopLevel.GetTopLevel(this) is { Clipboard: { } clipboard })
        {
            await clipboard.SetTextAsync(url);
        }
    }
}
