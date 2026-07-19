using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;

namespace SqlExplorer.Backends.Docker;

/// <summary>The container logs dialog content (SE-164), following the mockup: a header with the container
/// name + Copy/Refresh, and a monospace read-only view of <c>docker logs</c>. Fetches on open and on Refresh;
/// no hardcoded background colours → theme-safe.</summary>
internal static class ContainerLogsView
{
    public static Control Build(string containerName, Func<CancellationToken, Task<string>> fetch)
    {
        var text = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Code,Consolas,Menlo,monospace"),
            FontSize = 11,
            MinWidth = 660,
            MinHeight = 380,
            Text = "Loading…"
        };

        var copyButton = new Button { Content = "Copy", Margin = new Thickness(0, 0, 6, 0) };
        var refreshButton = new Button { Content = "↻ Refresh" };
        var title = new TextBlock { Text = containerName, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(12, 10, 12, 8) };
        Grid.SetColumn(title, 0);
        Grid.SetColumn(copyButton, 1);
        Grid.SetColumn(refreshButton, 2);
        header.Children.Add(title);
        header.Children.Add(copyButton);
        header.Children.Add(refreshButton);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(header, 0);
        Grid.SetRow(text, 1);
        root.Children.Add(header);
        root.Children.Add(text);

        async Task Load()
        {
            try
            {
                var logs = await fetch(CancellationToken.None);
                text.Text = string.IsNullOrWhiteSpace(logs) ? "(no output)" : logs;
            }
            catch (Exception ex)
            {
                text.Text = $"Could not read logs: {ex.Message}";
            }
        }

        refreshButton.Click += async (_, _) => await Load();
        copyButton.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(text)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(text.Text ?? "");
            }
        };

        _ = Load();
        return root;
    }
}
