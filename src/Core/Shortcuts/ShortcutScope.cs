namespace Lionear.SqlExplorer.Core.Shortcuts;

/// <summary>
/// Where a shortcut is dispatched. <see cref="Window"/> shortcuts become live
/// <c>Window.KeyBindings</c> on the main window; <see cref="Editor"/> shortcuts are handled inside the
/// SQL editor's own key handler (they must not fire while a non-editor control has focus).
/// </summary>
public enum ShortcutScope
{
    Window,
    Editor
}
