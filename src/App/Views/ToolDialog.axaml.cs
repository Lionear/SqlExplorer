using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Lionear.SqlExplorer.App.ViewModels;
using Lionear.SqlExplorer.Sdk.Tools;

namespace Lionear.SqlExplorer.App.Views;

public partial class ToolDialog : Window
{
    public ToolDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ToolDialogViewModel vm)
            {
                // The VM is the IToolHost; the view supplies the pickers (it owns the StorageProvider).
                vm.SaveFilePicker = PickSaveFileAsync;
                vm.OpenFilePicker = PickOpenFileAsync;
                vm.ConfirmRequested = ShowConfirmAsync;
                vm.CloseRequested = Close;
            }
        };
    }

    // Browse for a File tool-field: save or open picker per the field's SaveFile flag, via the VM host.
    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ToolFieldInput input } || DataContext is not IToolHost host)
        {
            return;
        }

        var extensions = input.Field.FileExtensions?.ToArray() ?? [];
        var path = input.Field.SaveFile
            ? await host.PickSaveFileAsync(SuggestedName(input), extensions)
            : await host.PickOpenFileAsync(extensions);

        if (path is not null)
        {
            input.Value = path;
        }
    }

    private static string SuggestedName(ToolFieldInput input) =>
        input.Value is { Length: > 0 } value ? Path.GetFileName(value) : input.Field.Default ?? "backup";

    private async Task<string?> PickSaveFileAsync(string suggestedName, string[] extensions)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName,
            FileTypeChoices = BuildTypes(extensions)
        });
        return file?.TryGetLocalPath() ?? file?.Path.ToString();
    }

    private async Task<string?> PickOpenFileAsync(string[] extensions)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = BuildTypes(extensions)
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() ?? files[0].Path.ToString() : null;
    }

    private static IReadOnlyList<FilePickerFileType>? BuildTypes(string[] extensions) =>
        extensions.Length == 0
            ? null
            : [new FilePickerFileType(string.Join("/", extensions).ToUpperInvariant()) { Patterns = extensions.Select(e => $"*.{e}").ToArray() }];

    private async Task<bool> ShowConfirmAsync(string title, string message)
    {
        var loc = (DataContext as ToolDialogViewModel)?.Loc;
        var dialog = new ConfirmDialog(title, message, loc?["Yes"] ?? "Yes", loc?["No"] ?? "No");
        return await dialog.ShowDialog<bool>(this);
    }
}
