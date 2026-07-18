using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SqlExplorer.App.Controls;
using SqlExplorer.App.ViewModels;

namespace SqlExplorer.App.Views;

public partial class QueryLogWindow : Window
{
    public QueryLogWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    // Copy the selected entry's full SQL to the clipboard.
    private async void OnCopySqlClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is QueryLogViewModel { SelectedEntry.Entry.Sql: { Length: > 0 } sql } vm)
        {
            await CopyFeedback.CopyAsync(this, sql, vm.Loc["CopiedToClipboard"]);
        }
    }

    // Export the currently filtered rows to a CSV file the user picks.
    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not QueryLogViewModel vm)
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = vm.Loc["QueryLogExport"],
            SuggestedFileName = "query-log.csv",
            DefaultExtension = "csv",
            FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }]
        });

        if (file is null)
        {
            return;
        }

        var csv = BuildCsv(vm);
        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream, Encoding.UTF8);
        await writer.WriteAsync(csv);
    }

    private static string BuildCsv(QueryLogViewModel vm)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Time,Source,Connection,DurationMs,Rows,Status,Error,Sql");
        foreach (var row in vm.Entries)
        {
            var e = row.Entry;
            sb.Append(Csv(row.Time)).Append(',')
              .Append(Csv(row.Source)).Append(',')
              .Append(Csv(e.ConnectionName)).Append(',')
              .Append(e.DurationMs).Append(',')
              .Append(e.RowCount).Append(',')
              .Append(Csv(row.Status)).Append(',')
              .Append(Csv(e.Error ?? string.Empty)).Append(',')
              .Append(Csv(e.Sql)).Append('\n');
        }
        return sb.ToString();
    }

    // Minimal RFC-4180 quoting: wrap in quotes and double any embedded quote.
    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}
