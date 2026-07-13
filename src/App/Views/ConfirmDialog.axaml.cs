using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Lionear.SqlExplorer.App.Views;

/// <summary>A small yes/no confirmation window. Returns true (Yes) or false (No/closed) via ShowDialog.</summary>
public partial class ConfirmDialog : Window
{
    // Parameterless ctor for the XAML designer/loader; real use goes through the four-arg ctor.
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    public ConfirmDialog(string title, string message, string yesText, string noText)
    {
        InitializeComponent();
        Title = title;
        Message = message;
        YesText = yesText;
        NoText = noText;
        // Set last so the Message/YesText/NoText bindings resolve with the values in place.
        DataContext = this;
    }

    public string Message { get; init; } = string.Empty;
    public string YesText { get; init; } = "Yes";
    public string NoText { get; init; } = "No";

    private void OnYes(object? sender, RoutedEventArgs e) => Close(true);

    private void OnNo(object? sender, RoutedEventArgs e) => Close(false);
}
