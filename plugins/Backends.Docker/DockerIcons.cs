using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace SqlExplorer.Backends.Docker;

/// <summary>
/// Small stroked vector glyphs for the Docker panel/dialogs, built in code. The host draws its own icons as
/// Paths for a reason: Linux/Avalonia has no colour-emoji fallback, so symbol characters (↻, ⚠, …) render as
/// tofu boxes. This gives the plugin the same crisp, theme-aware icons without reaching into host resources.
/// Coordinates sit in a 16×16 box and are scaled uniformly into the requested size.
/// </summary>
internal static class DockerIcons
{
    // Circular arrow → refresh.
    public const string Refresh = "M11.5,4.2 A5,5 0 1 0 13,8 M11.5,4.2 L9,3.8 M11.5,4.2 L11.9,6.7";

    // Triangle with an exclamation → warning (the dot is a tiny round-capped segment).
    public const string Warning = "M8,2.5 L14.5,13.5 H1.5 Z M8,6 V9.7 M8,11.6 V11.8";

    // Tick → a completed step.
    public const string Check = "M4,8.5 L7,11.5 L12,5";

    // Cross → a failed step.
    public const string Cross = "M5,5 L11,11 M11,5 L5,11";

    /// <summary>A stroked icon painted with an explicit brush (e.g. a status colour), for use outside a button.</summary>
    public static Path Icon(string geometry, double size, IBrush stroke) => new()
    {
        Width = size,
        Height = size,
        Data = Geometry.Parse(geometry),
        Stretch = Stretch.Uniform,
        Stroke = stroke,
        StrokeThickness = 1.4,
        StrokeJoin = PenLineJoin.Round,
        StrokeLineCap = PenLineCap.Round,
        VerticalAlignment = VerticalAlignment.Center
    };

    /// <summary>A button label of icon + text; the icon's stroke follows the host theme by binding to the
    /// ancestor button's Foreground (the plugin can't reach the host's brush resources from code).</summary>
    public static Control Label(string geometry, string text)
    {
        var icon = new Path
        {
            Width = 12,
            Height = 12,
            Data = Geometry.Parse(geometry),
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.4,
            StrokeJoin = PenLineJoin.Round,
            StrokeLineCap = PenLineCap.Round,
            VerticalAlignment = VerticalAlignment.Center
        };
        icon.Bind(Shape.StrokeProperty, new Binding("Foreground")
        {
            RelativeSource = new RelativeSource { Mode = RelativeSourceMode.FindAncestor, AncestorType = typeof(Button) }
        });

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { icon, new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } }
        };
    }
}
