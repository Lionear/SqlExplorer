using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace SqlExplorer.Tools.MsSqlBacpac;

/// <summary>Small shared view helpers so the four dialogs share one look (preview header box + warn/danger
/// banners) matching the signed-off mockup. Colours come from the host's theme brushes via
/// <see cref="Bind"/> (light/dark-aware) rather than hardcoded values — a hardcoded light panel reads as a
/// flashbang in dark mode. Banner accents use semi-transparent status colours that sit fine on either
/// background, with theme-foreground text so they stay readable.</summary>
internal static class MsSqlBacpacUi
{
    private static readonly FontFamily Mono = new("Cascadia Code, Consolas, Menlo, monospace");

    // Status accents (mockup tokens). Used only as translucent fills/borders + full-opacity icon, so they
    // work against both a light and a dark panel.
    private static readonly Color Danger = Color.FromRgb(0xC4, 0x36, 0x2F);
    private static readonly Color Warn = Color.FromRgb(0xB9, 0x79, 0x1F);

    /// <summary>Bind a control property to a host theme resource so it tracks light/dark switches.</summary>
    private static T Themed<T>(this T control, AvaloniaProperty property, string resourceKey) where T : Control
    {
        control.Bind(property, control.GetResourceObservable(resourceKey));
        return control;
    }

    /// <summary>A read-only monospace box for the package header (BACPAC/DACPAC preview). Starts empty;
    /// set <see cref="TextBox.Text"/> to populate.</summary>
    public static TextBox PreviewBox()
    {
        var box = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = Mono,
            FontSize = 11.5,
            MinHeight = 84
        };
        box.Themed(TextBox.BackgroundProperty, "SESecondaryBgBrush");
        box.Themed(TextBox.ForegroundProperty, "SETextSecondaryBrush");
        box.Themed(TextBox.BorderBrushProperty, "SEHairlineBrush");
        return box;
    }

    /// <summary>Muted read-only foreground for an informational field (e.g. the fixed publish target).</summary>
    public static TextBox ReadonlyField(string text)
    {
        var box = new TextBox { Text = text, IsReadOnly = true };
        box.Themed(TextBox.ForegroundProperty, "SETextSecondaryBrush");
        return box;
    }

    /// <summary>A warn (amber) or danger (red) banner. Translucent accent fill/border + full-opacity icon +
    /// theme-foreground text, so it reads correctly in light and dark.</summary>
    public static Control Banner(string text, bool danger)
    {
        var accent = danger ? Danger : Warn;
        var icon = danger ? "!" : "⚠";

        var body = new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center
        }.Themed(TextBlock.ForegroundProperty, "SETextPrimaryBrush");

        return new Border
        {
            Background = new SolidColorBrush(accent, 0.14),
            BorderBrush = new SolidColorBrush(accent, 0.55),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8),
            Margin = new Thickness(0, 4, 0, 0),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                Children =
                {
                    new TextBlock { Text = icon, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(accent), Margin = new Thickness(0, 0, 8, 0) },
                    Column(body, 1)
                }
            }
        };
    }

    private static Control Column(Control c, int column)
    {
        Grid.SetColumn(c, column);
        return c;
    }
}
