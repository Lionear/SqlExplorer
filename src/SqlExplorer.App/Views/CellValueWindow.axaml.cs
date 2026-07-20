using Avalonia.Controls;
using Avalonia.Interactivity;
using SqlExplorer.App.Controls;

namespace SqlExplorer.App.Views;

/// <summary>
/// A standalone, non-modal viewer for a single result-grid cell (SE-178 follow-up): opened by double-clicking
/// a cell so a long text / JSON value can be read comfortably, JSON pretty-printed. Several can be open at once
/// for side-by-side comparison — that's why it's a plain <see cref="Window"/> shown non-modally, not a dialog.
/// </summary>
public partial class CellValueWindow : Window
{
    private readonly string _value = string.Empty;
    private readonly string _copiedLabel = "Copied";

    public CellValueWindow() => InitializeComponent();

    public CellValueWindow(string column, string value, string copyLabel, string copiedLabel)
    {
        InitializeComponent();
        _value = value;
        _copiedLabel = copiedLabel;
        Title = string.IsNullOrEmpty(column) ? "Cell" : column;
        ValueBox.Text = value;
        CopyButton.Content = copyLabel;
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e) =>
        await CopyFeedback.CopyAsync(this, _value, _copiedLabel);
}
