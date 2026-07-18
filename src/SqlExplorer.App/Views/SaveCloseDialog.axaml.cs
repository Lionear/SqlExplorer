using Avalonia.Controls;
using Avalonia.Interactivity;
using SqlExplorer.App.ViewModels;

namespace SqlExplorer.App.Views;

/// <summary>
/// Three-way "save this query before closing?" prompt (SE-154): Save / Don't save / Cancel. Returns the
/// chosen <see cref="SaveCloseChoice"/> via ShowDialog; closing the window (Esc/×) counts as Cancel so an
/// accidental dismissal never discards unsaved work.
/// </summary>
public partial class SaveCloseDialog : Window
{
    // Parameterless ctor for the XAML loader; real use goes through the string ctor.
    public SaveCloseDialog()
    {
        InitializeComponent();
    }

    public SaveCloseDialog(string title, string message, string saveText, string dontSaveText, string cancelText)
    {
        InitializeComponent();
        Title = title;
        Message = message;
        SaveText = saveText;
        DontSaveText = dontSaveText;
        CancelText = cancelText;
        // Set last so the bindings resolve with the values in place.
        DataContext = this;
    }

    public string Message { get; init; } = string.Empty;
    public string SaveText { get; init; } = "Save";
    public string DontSaveText { get; init; } = "Don't save";
    public string CancelText { get; init; } = "Cancel";

    private void OnSave(object? sender, RoutedEventArgs e) => Close(SaveCloseChoice.Save);

    private void OnDontSave(object? sender, RoutedEventArgs e) => Close(SaveCloseChoice.DontSave);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(SaveCloseChoice.Cancel);
}
