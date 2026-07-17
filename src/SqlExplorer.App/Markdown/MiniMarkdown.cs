using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace SqlExplorer.App.Markdown;

/// <summary>
/// A deliberately small markdown-to-Avalonia renderer for the update changelog (SE-137). The notes are
/// generated from conventional-commit messages, so the grammar they use is narrow: ATX headings
/// (<c>#</c>…<c>###</c>), <c>-</c>/<c>*</c> bullet lists, blank-line-separated paragraphs and <c>**bold**</c>
/// spans. Anything else renders as its literal text. Not a general markdown engine — the app pulls in no
/// markdown dependency (keeps the SE-135 supply-chain surface flat); if the notes ever grow richer, this is
/// the one place to revisit.
/// </summary>
public static class MiniMarkdown
{
    public static Control Render(string? markdown)
    {
        var panel = new StackPanel { Spacing = 6 };
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return panel;
        }

        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                continue;
            }

            panel.Children.Add(RenderLine(line));
        }

        return panel;
    }

    private static Control RenderLine(string line)
    {
        if (TryHeading(line, out var level, out var headingText))
        {
            return new TextBlock
            {
                Text = headingText,
                FontWeight = FontWeight.SemiBold,
                FontSize = level <= 1 ? 15 : level == 2 ? 13.5 : 12.5,
                Margin = new Avalonia.Thickness(0, level <= 2 ? 6 : 2, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
        }

        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
        {
            var bullet = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(10, 0, 0, 0) };
            bullet.Inlines!.Add(new Run("•  "));
            AppendInlines(bullet, trimmed[2..]);
            return bullet;
        }

        var paragraph = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AppendInlines(paragraph, line);
        return paragraph;
    }

    private static bool TryHeading(string line, out int level, out string text)
    {
        level = 0;
        while (level < line.Length && line[level] == '#')
        {
            level++;
        }

        if (level is > 0 and <= 3 && level < line.Length && line[level] == ' ')
        {
            text = line[(level + 1)..].Trim();
            return true;
        }

        level = 0;
        text = string.Empty;
        return false;
    }

    // Splits on ** to alternate normal / bold runs. Bolding needs balanced markers (an odd number of
    // segments); an unmatched ** means the line isn't really bold, so render it verbatim.
    private static void AppendInlines(TextBlock target, string text)
    {
        var segments = text.Split("**");
        if (segments.Length % 2 == 0)
        {
            target.Inlines!.Add(new Run(text));
            return;
        }

        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i].Length == 0)
            {
                continue;
            }

            if (i % 2 == 1)
            {
                var bold = new Bold();
                bold.Inlines.Add(new Run(segments[i]));
                target.Inlines!.Add(bold);
            }
            else
            {
                target.Inlines!.Add(new Run(segments[i]));
            }
        }
    }
}
