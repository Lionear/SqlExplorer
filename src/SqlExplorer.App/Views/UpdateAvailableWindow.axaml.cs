using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SqlExplorer.App.Markdown;
using SqlExplorer.App.ViewModels;

namespace SqlExplorer.App.Views;

public partial class UpdateAvailableWindow : Window
{
    public UpdateAvailableWindow()
    {
        InitializeComponent();
    }

    public UpdateAvailableWindow(UpdateAvailableViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.OpenRequested = RevealAsync;

        // The notes are markdown; render them once into the placeholder (there's no in-app markdown control).
        NotesHost.Child = MiniMarkdown.Render(viewModel.Notes);
    }

    // Hand-off (Fase 1): reveal the containing folder rather than launch the downloaded binary — the user
    // runs the installer themselves. Best-effort: a missing shell handler must not take the dialog down.
    private static Task RevealAsync(string filePath)
    {
        try
        {
            var folder = Path.GetDirectoryName(filePath) ?? filePath;
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception)
        {
        }

        return Task.CompletedTask;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
