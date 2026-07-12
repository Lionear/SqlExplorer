using Avalonia.Controls;
using Avalonia.Interactivity;
using Lionear.SqlExplorer.App.ViewModels;
using Lionear.SqlExplorer.Core.Localization;

namespace Lionear.SqlExplorer.App.Views;

/// <summary>
/// Picks CSV/JSON/SQL and shows how many rows will go out (selection, or the whole result set).
/// Closes with the chosen <see cref="ExportFormat"/>, or null on cancel — the caller (DocumentView)
/// builds the actual text and runs the save-file dialog, same split as SaveReviewDialog/DocumentViewModel.
/// </summary>
public partial class ExportDialog : Window
{
    public ExportDialog()
    {
        InitializeComponent();
    }

    public ExportDialog(ILocalizer loc, int rowCount, bool isSelection) : this()
    {
        Title = loc["Export"];
        HeaderText.Text = loc["ExportFormat"];
        RowsText.Text = isSelection ? loc.Get("ExportRowsSelected", rowCount) : loc.Get("ExportRowsAll", rowCount);
        CancelButton.Content = loc["Cancel"];
        ExportButton.Content = loc["Export"];
    }

    private void OnExport(object? sender, RoutedEventArgs e)
    {
        var format = SqlOption.IsChecked == true ? ExportFormat.Sql
            : JsonOption.IsChecked == true ? ExportFormat.Json
            : ExportFormat.Csv;
        Close(format);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
