using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Lionear.SqlExplorer.App.ViewModels;

namespace Lionear.SqlExplorer.App.Views;

public partial class ConnectionDialog : Window
{
    public ConnectionDialog()
    {
        InitializeComponent();
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ConnectionDialogViewModel vm)
        {
            Close(vm.Save());
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ConnectionFieldInput input })
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false });
        if (files.Count > 0)
        {
            input.Value = files[0].TryGetLocalPath() ?? files[0].Path.ToString();
        }
    }
}
