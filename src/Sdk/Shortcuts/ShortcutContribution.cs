namespace Lionear.SqlExplorer.Sdk.Shortcuts;

/// <summary>
/// One keyboard shortcut a plugin contributes to the host. The host merges it into Settings ▸ Keyboard
/// (under the plugin's own section), lets the user rebind or clear it, applies live conflict detection
/// against every other binding, and invokes <see cref="ExecuteAsync"/> when the gesture fires.
/// </summary>
/// <param name="Id">
/// Stable id, unique within the plugin (the host namespaces it with the plugin id, so two plugins may use
/// the same local id without clashing). Persisted in the keymap, so keep it stable across versions.
/// </param>
/// <param name="Title">Human-readable label shown in the shortcut list (e.g. "Run backup").</param>
/// <param name="DefaultGesture">
/// Suggested default in Avalonia gesture syntax (e.g. <c>"Mod+Shift+B"</c>). The <c>Mod</c> token maps to
/// the platform primary modifier — Cmd on macOS, Ctrl elsewhere. Pass <c>null</c> to ship the command
/// unbound (the user assigns a key themselves).
/// </param>
/// <param name="ExecuteAsync">
/// The action to run when the shortcut fires. Self-contained: capture whatever state you need when you
/// build the contribution. Invoked on the UI thread; offload heavy work yourself.
/// </param>
public sealed record ShortcutContribution(
    string Id,
    string Title,
    string? DefaultGesture,
    Func<CancellationToken, Task> ExecuteAsync);
