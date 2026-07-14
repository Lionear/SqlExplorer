using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Lionear.SqlExplorer.App.Converters;

/// <summary>Maps a bool health flag to a status-dot colour: green when ok, red otherwise. Colours mirror
/// the theme status tokens and read on both variants — used for source-reachability dots.</summary>
public sealed class BoolToStatusBrushConverter : IValueConverter
{
    public static readonly BoolToStatusBrushConverter Instance = new();

    private static readonly IImmutableBrush Ok = new ImmutableSolidColorBrush(Color.FromRgb(0x5A, 0xA5, 0x76));
    private static readonly IImmutableBrush Bad = new ImmutableSolidColorBrush(Color.FromRgb(0xD6, 0x45, 0x45));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Ok : Bad;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
