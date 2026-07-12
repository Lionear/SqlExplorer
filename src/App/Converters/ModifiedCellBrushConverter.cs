using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Lionear.SqlExplorer.App.Converters;

/// <summary>
/// Maps an <c>IsModified</c> flag to a cell highlight: a translucent amber that reads as
/// "changed but not yet saved" on both light and dark themes, or transparent when unchanged.
/// </summary>
public sealed class ModifiedCellBrushConverter : IValueConverter
{
    public static readonly ModifiedCellBrushConverter Instance = new();

    private static readonly IImmutableBrush Highlight =
        new ImmutableSolidColorBrush(Color.FromArgb(0x4D, 0xF5, 0xA6, 0x23));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Highlight : Brushes.Transparent;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
