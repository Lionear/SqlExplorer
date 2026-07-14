namespace Lionear.SqlExplorer.Core.Shortcuts;

/// <summary>
/// Metadata for one bindable command: a stable <paramref name="Id"/> (the persisted key, never
/// localized), the resx keys for its display label and group header, its dispatch
/// <paramref name="Scope"/>, and the factory-default gesture in Avalonia <c>KeyGesture</c> syntax
/// (e.g. <c>"Ctrl+Shift+T"</c>). The gesture is a plain string here so Core stays UI-agnostic; the
/// App layer parses it into a real gesture.
/// </summary>
public sealed record ShortcutCommand(
    string Id,
    string LabelKey,
    string GroupKey,
    ShortcutScope Scope,
    string DefaultGesture);
