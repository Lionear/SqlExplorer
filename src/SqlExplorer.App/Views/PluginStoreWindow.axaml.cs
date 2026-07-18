using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using SqlExplorer.App.ViewModels;

namespace SqlExplorer.App.Views;

public partial class PluginStoreWindow : Window
{
    public PluginStoreWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is PluginStoreViewModel vm)
            {
                vm.CloseRequested = Close;
                vm.RestartRequested = AppRestart.Restart;
                vm.InstallFromFileRequested = PickPluginZipAsync;
                vm.ChangelogRequested = ShowChangelogAsync;
            }
        };

        // Load the catalog once the window is up (fetch is async + fault-tolerant).
        Opened += async (_, _) =>
        {
            if (DataContext is PluginStoreViewModel vm)
            {
                await vm.RefreshCommand.ExecuteAsync(null);
            }
        };
    }

    // Clicking a Browse card selects it into the detail pane.
    private void OnBrowseCardPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: StoreListItem item } && DataContext is PluginStoreViewModel vm)
        {
            vm.SelectedBrowseItem = item;
        }
    }

    // SE-138 phase 2: a row's "changelog" link opens the per-plugin changelog dialog, owned by this window.
    private async Task ShowChangelogAsync(PluginChangelogViewModel viewModel)
    {
        var dialog = new PluginChangelogWindow(viewModel);
        await dialog.ShowDialog(this);
    }

    private async Task<string?> PickPluginZipAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Plugin package") { Patterns = ["*.zip"] }]
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() ?? files[0].Path.ToString() : null;
    }
}
