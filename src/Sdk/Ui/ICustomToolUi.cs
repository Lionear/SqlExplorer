using Avalonia.Controls;

namespace Lionear.SqlExplorer.Sdk.Ui;

/// <summary>
/// Optional capability a tool plugin may also implement to supply its own Avalonia view for the tool
/// dialog, instead of the host-generated form built from its declared <c>ToolField</c>s (Route B).
/// Mirrors <see cref="ICustomConnectionUi"/>. Values still flow through <see cref="IToolUiContext"/>, so
/// the host collects and runs the tool the same way regardless of route. This assembly and Avalonia are
/// shared across the plugin ALC boundary, so the returned control has one type identity with the host.
/// </summary>
public interface ICustomToolUi
{
    Control CreateView(IToolUiContext context);
}
