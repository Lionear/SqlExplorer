using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Lionear.SqlExplorer.App.ViewModels;

/// <summary>
/// One editable row in Settings › Keyboard: a command's current gesture plus its conflict state.
/// The parent <see cref="SettingsViewModel"/> watches <see cref="Gesture"/> changes to recompute
/// conflicts across the whole list; <see cref="ResetCommand"/> reverts this row to its factory default.
/// </summary>
public partial class ShortcutItem : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged))]
    private string? _gesture;

    [ObservableProperty]
    private bool _hasConflict;

    /// <summary>Label of the other command this row clashes with; null when there's no conflict.</summary>
    [ObservableProperty]
    private string? _conflictWith;

    public ShortcutItem(string id, string label, string defaultGesture, string? gesture)
    {
        Id = id;
        Label = label;
        DefaultGesture = defaultGesture;
        _gesture = gesture;
    }

    public string Id { get; }

    public string Label { get; }

    public string DefaultGesture { get; }

    /// <summary>True once the user has moved this binding away from its factory default.</summary>
    public bool IsChanged => !string.Equals(Gesture, DefaultGesture, StringComparison.Ordinal);

    [RelayCommand]
    private void Reset() => Gesture = DefaultGesture;
}
