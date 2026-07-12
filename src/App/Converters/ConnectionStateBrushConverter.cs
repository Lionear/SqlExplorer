using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Lionear.SqlExplorer.App.ViewModels;

namespace Lionear.SqlExplorer.App.Converters;

/// <summary>
/// Maps a <see cref="ConnectionState"/> to the status-dot colour: green connected, blue connecting,
/// red error, muted grey disconnected. Colours mirror the theme status tokens and read on both variants.
/// </summary>
public sealed class ConnectionStateBrushConverter : IValueConverter
{
    public static readonly ConnectionStateBrushConverter Instance = new();

    private static readonly IImmutableBrush Connected = new ImmutableSolidColorBrush(Color.FromRgb(0x5A, 0xA5, 0x76));
    private static readonly IImmutableBrush Connecting = new ImmutableSolidColorBrush(Color.FromRgb(0x35, 0x74, 0xF0));
    private static readonly IImmutableBrush Error = new ImmutableSolidColorBrush(Color.FromRgb(0xD6, 0x45, 0x45));
    private static readonly IImmutableBrush Disconnected = new ImmutableSolidColorBrush(Color.FromRgb(0x6F, 0x73, 0x7A));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ConnectionState.Connected => Connected,
        ConnectionState.Connecting => Connecting,
        ConnectionState.Error => Error,
        _ => Disconnected
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
