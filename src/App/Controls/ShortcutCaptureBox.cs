using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Lionear.SqlExplorer.App.Controls;

/// <summary>
/// A button that captures a key combination instead of typing it. Click it (or focus + Space/Enter) to
/// enter capture mode — the next key press is recorded as an Avalonia gesture string and written to
/// <see cref="Gesture"/>. Modifier-only presses preview but don't commit; <c>Esc</c> cancels, <c>Back</c>/
/// <c>Delete</c> clears the binding. Exposes a <c>:capturing</c> pseudo-class for styling.
/// </summary>
public sealed class ShortcutCaptureBox : Button
{
    public static readonly StyledProperty<string?> GestureProperty =
        AvaloniaProperty.Register<ShortcutCaptureBox, string?>(
            nameof(Gesture), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>Placeholder shown while capturing (localized "Press keys…").</summary>
    public static readonly StyledProperty<string?> CapturingTextProperty =
        AvaloniaProperty.Register<ShortcutCaptureBox, string?>(nameof(CapturingText));

    /// <summary>Text shown when <see cref="Gesture"/> is empty (localized "Unbound").</summary>
    public static readonly StyledProperty<string?> UnboundTextProperty =
        AvaloniaProperty.Register<ShortcutCaptureBox, string?>(nameof(UnboundText));

    private bool _capturing;

    public string? Gesture
    {
        get => GetValue(GestureProperty);
        set => SetValue(GestureProperty, value);
    }

    public string? CapturingText
    {
        get => GetValue(CapturingTextProperty);
        set => SetValue(CapturingTextProperty, value);
    }

    public string? UnboundText
    {
        get => GetValue(UnboundTextProperty);
        set => SetValue(UnboundTextProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(Button);

    public ShortcutCaptureBox()
    {
        // Leaving the control mid-capture (Tab away, click elsewhere) silently cancels.
        LostFocus += (_, _) => EndCapture();
    }

    protected override void OnClick()
    {
        // Don't invoke a command on click — a click just arms capture.
        BeginCapture();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == GestureProperty
            || change.Property == CapturingTextProperty
            || change.Property == UnboundTextProperty)
        {
            UpdateContent();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!_capturing)
        {
            base.OnKeyDown(e);
            return;
        }

        e.Handled = true;

        switch (e.Key)
        {
            case Key.Escape:
                EndCapture();
                return;
            case Key.Back:
            case Key.Delete:
                Gesture = null;
                EndCapture();
                return;
        }

        if (IsModifierKey(e.Key))
        {
            // Live preview of modifiers held so far; wait for a non-modifier to commit.
            UpdateContent();
            return;
        }

        Gesture = new KeyGesture(e.Key, e.KeyModifiers).ToString();
        EndCapture();
    }

    private void BeginCapture()
    {
        _capturing = true;
        PseudoClasses.Set(":capturing", true);
        Focus();
        UpdateContent();
    }

    private void EndCapture()
    {
        _capturing = false;
        PseudoClasses.Set(":capturing", false);
        // Refresh here too: on commit the Gesture is set while still capturing, so this is what
        // repaints the chip from "Press keys…" to the newly bound gesture.
        UpdateContent();
    }

    private void UpdateContent()
    {
        Content = _capturing
            ? (CapturingText ?? "Press keys…")
            : string.IsNullOrWhiteSpace(Gesture) ? (UnboundText ?? "Unbound") : Prettify(Gesture);
    }

    // Display-only: turn the raw Avalonia key name into the symbol a user expects (e.g. "Ctrl+OemQuestion"
    // → "Ctrl+/"). The stored/parsed Gesture keeps the raw form so it stays round-trippable.
    private static string Prettify(string gesture)
    {
        var parts = gesture.Split('+');
        var key = parts[^1];
        if (KeySymbols.TryGetValue(key, out var symbol))
        {
            parts[^1] = symbol;
        }

        return string.Join("+", parts);
    }

    private static readonly Dictionary<string, string> KeySymbols = new(StringComparer.Ordinal)
    {
        ["OemQuestion"] = "/",
        ["OemComma"] = ",",
        ["OemPeriod"] = ".",
        ["OemPlus"] = "+",
        ["OemMinus"] = "-",
        ["OemTilde"] = "`",
        ["OemOpenBrackets"] = "[",
        ["OemCloseBrackets"] = "]",
        ["OemPipe"] = "\\",
        ["OemBackslash"] = "\\",
        ["OemSemicolon"] = ";",
        ["OemQuotes"] = "'",
        ["Divide"] = "/",
        ["Multiply"] = "*",
        ["Subtract"] = "-",
        ["Add"] = "+",
        ["Decimal"] = "."
    };

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftShift or Key.RightShift or
        Key.LeftAlt or Key.RightAlt or
        Key.LWin or Key.RWin or
        Key.System;
}
