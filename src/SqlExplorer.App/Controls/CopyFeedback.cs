using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace SqlExplorer.App.Controls;

/// <summary>
/// A shared, non-modal "Copied" confirmation. Writes text to the clipboard and floats a small toast at the
/// bottom-centre of the anchor's window via its <see cref="OverlayLayer"/>, so every copy action gets the
/// same feedback without each window carrying its own toast markup. Only opacity is animated (no positional
/// motion), which keeps it unobtrusive and reduced-motion-safe.
/// </summary>
public static class CopyFeedback
{
    private static readonly TimeSpan Dwell = TimeSpan.FromMilliseconds(1600);
    private static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(150);

    /// <summary>Copy <paramref name="text"/> to the clipboard, then show the <paramref name="message"/> toast
    /// in <paramref name="anchor"/>'s window. No-op if there's no top-level (e.g. during teardown).</summary>
    public static async Task CopyAsync(Visual? anchor, string text, string message)
    {
        if (anchor is null)
        {
            return;
        }

        if (TopLevel.GetTopLevel(anchor)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }

        Show(anchor, message);
    }

    /// <summary>Float the confirmation toast without touching the clipboard — for callers that copied through
    /// another path (e.g. a built-in editor command) but still want the shared feedback.</summary>
    public static void Show(Visual? anchor, string message)
    {
        if (anchor is null
            || TopLevel.GetTopLevel(anchor) is not { } top
            || OverlayLayer.GetOverlayLayer(anchor) is not { } layer)
        {
            return;
        }

        IBrush Brush(string key, Color fallback) =>
            top.TryFindResource(key, top.ActualThemeVariant, out var value) && value is IBrush brush
                ? brush
                : new SolidColorBrush(fallback);

        var pill = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 56),
            Padding = new Thickness(18, 10),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Background = Brush("SEPanelBgBrush", Color.FromArgb(0xF2, 0x2A, 0x2A, 0x2A)),
            BorderBrush = Brush("SEHairlineBrush", Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0, OffsetY = 3, Blur = 14, Color = Color.FromArgb(0x66, 0, 0, 0),
            }),
            Child = new TextBlock
            {
                Text = message,
                FontSize = 13,
                FontWeight = FontWeight.Medium,
                Foreground = Brush("SETextPrimaryBrush", Colors.White),
            },
        };

        // The overlay arranges each child at its desired size from the top-left, so alignment alone won't
        // position the pill. Host it in a panel stretched to the overlay's bounds; inside a real panel the
        // pill's bottom-centre alignment then places it against the window, not the corner. The toast is
        // transient (~1.6s), so a one-off size is fine — no need to track a mid-toast window resize.
        var host = new Panel
        {
            IsHitTestVisible = false,
            Width = layer.Bounds.Width,
            Height = layer.Bounds.Height,
            Opacity = 0,
            Transitions = [new DoubleTransition { Property = Visual.OpacityProperty, Duration = Fade }],
            Children = { pill },
        };

        layer.Children.Add(host);
        // Flip opacity on the next render pass so the transition animates from 0 → 1 (setting it in the same
        // pass as the add wouldn't animate).
        Dispatcher.UIThread.Post(() => host.Opacity = 1, DispatcherPriority.Render);

        DispatcherTimer.RunOnce(() =>
        {
            host.Opacity = 0;
            DispatcherTimer.RunOnce(() => layer.Children.Remove(host), Fade);
        }, Dwell);
    }
}
