namespace Lionear.SqlExplorer.App.ViewModels;

/// <summary>
/// Live state of a connection root in the sidebar tree, surfaced as a coloured status dot and
/// driving whether the context menu offers Connect or Disconnect.
/// </summary>
public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Error
}
