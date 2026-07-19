namespace SqlExplorer.Sdk.Extensibility;

/// <summary>One item a plugin adds to a connection's context menu in the tree: an <see cref="AppliesTo"/>
/// predicate (so it only shows for connections it can act on — e.g. containerisable engines) and the action to
/// run against that connection, handed an <see cref="IHostUi"/> so it can open a dialog.</summary>
public sealed record ConnectionMenuContribution(
    string Id,
    string Title,
    Func<ManagedConnectionInfo, bool> AppliesTo,
    Func<ManagedConnectionInfo, IHostUi, Task> InvokeAsync);

/// <summary>
/// Optional contribution a standing-subsystem plugin may implement to add items to a <em>connection's</em>
/// context menu in the tree — a bounded tree contribution (connection nodes only, not arbitrary nodes),
/// gated by the <see cref="PluginCapabilities.Menu"/> capability. Lets e.g. the Docker plugin offer
/// "Create local Docker instance…" on a right-clicked Postgres connection.
/// </summary>
public interface IConnectionMenuPlugin
{
    /// <summary>The connection-context-menu items this plugin contributes.</summary>
    IReadOnlyList<ConnectionMenuContribution> ConnectionMenuItems { get; }
}
