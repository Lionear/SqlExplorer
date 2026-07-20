using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using SqlExplorer.Core.Completion;
using SqlExplorer.Sdk.Ui;

namespace SqlExplorer.App.Completion;

/// <summary>
/// Turns a <see cref="CompletionKind"/> into the small icon shown beside each suggestion in the
/// AvaloniaEdit completion popup. It maps each kind onto the same Lucide glyph the schema tree uses
/// (SE-167/SE-172) — so a table in the popup reads as the table in the tree — and renders that
/// stroked <see cref="Geometry"/> into a <see cref="DrawingImage"/>, because
/// <c>ICompletionData.Image</c> needs an <see cref="IImage"/>, not a geometry.
///
/// The stroke colour follows the theme: it resolves the same <c>SETextSecondaryBrush</c> the tree
/// icons stroke with, for the active variant, and caches one image per (kind, variant). A theme
/// switch just resolves the other variant's entry on next paint; completion popups are transient, so
/// there's nothing live to re-theme.
/// </summary>
internal static class CompletionIcons
{
    // Geometry native box is 24x24 (Lucide); Lucide strokes at 2px there, which scales down to the
    // ~1.3px the tree/toolbar paths use once the popup draws the image at ~16px. Round caps/joins
    // match the rest of the icon set.
    private const double StrokeThickness = 2;

    private static readonly ConcurrentDictionary<(CompletionKind Kind, ThemeVariant Variant), DrawingImage> Cache = new();

    public static IImage? For(CompletionKind kind)
    {
        var variant = Application.Current?.ActualThemeVariant ?? ThemeVariant.Default;
        return Cache.GetOrAdd((kind, variant), key => Build(key.Kind, key.Variant));
    }

    private static DrawingImage Build(CompletionKind kind, ThemeVariant variant)
    {
        var pen = new Pen(StrokeBrush(variant), StrokeThickness)
        {
            LineJoin = PenLineJoin.Round,
            LineCap = PenLineCap.Round
        };
        return new DrawingImage { Drawing = new GeometryDrawing { Geometry = GeometryFor(kind), Pen = pen } };
    }

    // Same glyphs as NodeIcons for the shared concepts; Key stands in for a FK-derived join condition
    // (SE-149 phase 3), and a plain Square marks a bare keyword.
    private static Geometry GeometryFor(CompletionKind kind) => kind switch
    {
        CompletionKind.Table => Icons.Table,
        CompletionKind.Column => Icons.RectangleVertical,
        CompletionKind.Function => Icons.SquareFunction,
        CompletionKind.Join => Icons.Key,
        _ => Icons.Square
    };

    private static IBrush StrokeBrush(ThemeVariant variant) =>
        Application.Current?.TryGetResource("SETextSecondaryBrush", variant, out var value) == true && value is IBrush brush
            ? brush
            : Brushes.Gray;
}
