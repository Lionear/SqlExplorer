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

    // Lucide "container" (https://lucide.dev/icons/container, ISC — already bundled/attributed via the host's
    // Lucide assets) for the Containers panel toggle. Its five sub-paths sit in a 24×24 box; the panel toggle
    // draws them Stretch="Uniform". The third sub-path's leading relative moveto ("m10 14") is absolutised to
    // "M10 14" so the concatenated string is self-anchored (a standalone path starts at 0,0, so it's exact);
    // the following lineto stays relative ("l").
    public const string Container =
        "M22 7.7c0-.6-.4-1.2-.8-1.5l-6.3-3.9a1.72 1.72 0 0 0-1.7 0l-10.3 6c-.5.2-.9.8-.9 1.4v6.6c0 .5.4 1.2.8 1.5l6.3 3.9a1.72 1.72 0 0 0 1.7 0l10.3-6c.5-.3.9-1 .9-1.5Z "
        + "M10 21.9V14L2.1 9.1 "
        + "M10 14 l11.9 -6.9 "
        + "M14 19.8v-8.1 "
        + "M18 17.5V9.4";

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
