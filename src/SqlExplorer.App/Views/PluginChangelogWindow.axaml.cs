using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using SqlExplorer.App.Markdown;
using SqlExplorer.App.ViewModels;

namespace SqlExplorer.App.Views;

public partial class PluginChangelogWindow : Window
{
    public PluginChangelogWindow()
    {
        InitializeComponent();
    }

    public PluginChangelogWindow(PluginChangelogViewModel viewModel) : this()
    {
        DataContext = viewModel;

        // The notes are markdown; render each plugin's section (heading + notes) into the placeholder,
        // reusing the same MiniMarkdown renderer the app-updater changelog uses.
        foreach (var section in viewModel.Sections)
        {
            SectionsHost.Children.Add(new TextBlock
            {
                Text = section.Heading,
                FontSize = 14,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });

            if (string.IsNullOrWhiteSpace(section.Notes))
            {
                SectionsHost.Children.Add(new TextBlock
                {
                    Text = viewModel.Loc["PluginChangelogNone"],
                    FontSize = 12,
                    Foreground = FaintBrush(),
                });
            }
            else
            {
                SectionsHost.Children.Add(MiniMarkdown.Render(section.Notes));
            }
        }
    }

    private IBrush FaintBrush()
    {
        if (this.TryFindResource("SETextFaintBrush", ActualThemeVariant, out var value) && value is IBrush brush)
        {
            return brush;
        }

        return Brushes.Gray;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
