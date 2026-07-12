using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Lionear.SqlExplorer.App.Views;

public partial class ImportCsvDialog : Window
{
    public ImportCsvDialog()
    {
        InitializeComponent();
    }

    private void OnImport(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
