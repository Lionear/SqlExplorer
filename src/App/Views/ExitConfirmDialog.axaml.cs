using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Lionear.SqlExplorer.App.Views;

/// <summary>
/// Exit confirmation window: yes/no plus an "always close without asking" checkbox. Returns true (close)
/// or false (cancel/closed) via ShowDialog; the caller reads <see cref="Always"/> to decide whether to
/// clear the ConfirmOnExit setting.
/// </summary>
public partial class ExitConfirmDialog : Window
{
    // Parameterless ctor for the XAML loader; real use goes through the string ctor.
    public ExitConfirmDialog()
    {
        InitializeComponent();
    }

    public ExitConfirmDialog(string title, string message, string yesText, string noText, string alwaysText)
    {
        InitializeComponent();
        Title = title;
        Message = message;
        YesText = yesText;
        NoText = noText;
        AlwaysText = alwaysText;
        // Set last so the bindings resolve with the values in place.
        DataContext = this;
    }

    public string Message { get; init; } = string.Empty;
    public string YesText { get; init; } = "Quit";
    public string NoText { get; init; } = "Cancel";
    public string AlwaysText { get; init; } = "Always quit without asking";

    /// <summary>Bound to the "always" checkbox; read after the dialog closes.</summary>
    public bool Always { get; set; }

    private void OnYes(object? sender, RoutedEventArgs e) => Close(true);

    private void OnNo(object? sender, RoutedEventArgs e) => Close(false);
}
