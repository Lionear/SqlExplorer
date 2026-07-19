using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;

namespace SqlExplorer.Backends.Docker;

/// <summary>
/// The container logs dialog content (SE-164), following the mockup: a header with the container name +
/// Copy/Refresh over a monospace, syntax-tinted view of <c>docker logs</c> — dimmed timestamps, amber
/// warnings, red errors. Selectable (like a <c>&lt;pre&gt;</c>), fetches on open and on Refresh. Fixed status
/// colours read in both themes; nothing else is hardcoded.
/// </summary>
internal static class ContainerLogsView
{
    private static readonly Regex Prefix = new(@"^(\S+\s+[\d:.]+(?:\s+UTC)?)", RegexOptions.Compiled);
    private static readonly IBrush Faint = new SolidColorBrush(Color.Parse("#8A909A"));
    private static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#B9791F"));
    private static readonly IBrush Error = new SolidColorBrush(Color.Parse("#C4362F"));

    public static Control Build(string containerName, Func<CancellationToken, Task<string>> fetch)
    {
        var view = new SelectableTextBlock
        {
            FontFamily = new FontFamily("Cascadia Code,Consolas,Menlo,monospace"),
            FontSize = 11,
            LineHeight = 16,
            Margin = new Thickness(12, 8, 12, 12)
        };
        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            MinWidth = 680,
            MinHeight = 400,
            Content = view
        };
        var raw = "";

        var copyButton = new Button { Content = "Copy", Margin = new Thickness(0, 0, 6, 0) };
        var refreshButton = new Button { Content = "↻ Refresh" };
        var title = new TextBlock { Text = containerName, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };

        var headerInner = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(12, 8, 10, 8) };
        Grid.SetColumn(title, 0);
        Grid.SetColumn(copyButton, 1);
        Grid.SetColumn(refreshButton, 2);
        headerInner.Children.Add(title);
        headerInner.Children.Add(copyButton);
        headerInner.Children.Add(refreshButton);
        var header = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = new SolidColorBrush(Color.Parse("#33808080")),
            Child = headerInner
        };

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(header, 0);
        Grid.SetRow(scroller, 1);
        root.Children.Add(header);
        root.Children.Add(scroller);

        void Render(string text)
        {
            var inlines = new InlineCollection();
            foreach (var line in text.Replace("\r", "").Split('\n'))
            {
                AppendLine(inlines, line);
            }

            view.Inlines = inlines;
        }

        async Task Load()
        {
            try
            {
                raw = await fetch(CancellationToken.None);
                Render(string.IsNullOrWhiteSpace(raw) ? "(no output)" : raw);
            }
            catch (Exception ex)
            {
                raw = $"Could not read logs: {ex.Message}";
                Render(raw);
            }
        }

        refreshButton.Click += async (_, _) => await Load();
        copyButton.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(view)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(raw);
            }
        };

        _ = Load();
        return root;
    }

    private static void AppendLine(InlineCollection inlines, string line)
    {
        var match = Prefix.Match(line);
        string prefix = "", rest = line;
        if (match.Success && match.Index == 0)
        {
            prefix = match.Value;
            rest = line[match.Length..];
        }

        // Severity: Dragonfly/glog prefix a W/E/F letter; Postgres puts WARNING/ERROR in the message.
        var lead = prefix.Length > 0 ? prefix[0] : ' ';
        var severity =
            lead is 'E' or 'F' || rest.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || rest.Contains("FATAL", StringComparison.OrdinalIgnoreCase)
                ? Error
                : lead == 'W' || rest.Contains("WARN", StringComparison.OrdinalIgnoreCase)
                    ? Warn
                    : null;

        if (prefix.Length > 0)
        {
            inlines.Add(new Run(prefix) { Foreground = Faint });
        }

        // Leave Foreground unset for normal lines so it inherits the theme text colour (setting it to null
        // would render the text invisible).
        var run = new Run(rest);
        if (severity is not null)
        {
            run.Foreground = severity;
        }

        inlines.Add(run);
        inlines.Add(new LineBreak());
    }
}
