using Avalonia.Controls;
using Lionear.SqlExplorer.Sdk.Connections;
using Lionear.SqlExplorer.Sdk.Schema;

namespace Lionear.SqlExplorer.Sdk.Ui;

/// <summary>
/// Optional capability an <c>IDbProvider</c> may also implement to supply a read-only "properties"/"info"
/// view for a schema-tree node — e.g. SQL Server's Database Properties dialog. A third Route-B capability
/// alongside <see cref="ICustomConnectionUi"/> (advanced connection settings) and <c>ICustomToolUi</c>
/// (tool dialog): the provider owns an Avalonia <see cref="Control"/> that queries its own live data, and
/// the host shows it in generic dialog chrome (title + content + Close). Unlike a tool there is no
/// Execute/progress/log — it is purely informational, so it does not go through the tool registry.
/// </summary>
/// <remarks>
/// This assembly and Avalonia are shared across the plugin ALC boundary (<c>ProviderLoadContext</c>) so
/// the returned control has a single type identity with the host. Providers that don't implement this
/// simply offer no "Properties…" item. Additive optional-interface check — no host API bump needed, same
/// precedent as <see cref="ICustomConnectionUi"/>.
/// </remarks>
public interface ICustomNodeInfoUi
{
    /// <summary>True when this provider offers an info view for the given node (e.g. only Database nodes).</summary>
    bool HasInfoFor(DbNodeRef node);

    /// <summary>Dialog title for the node's info view (e.g. "Database Properties").</summary>
    string InfoTitle(DbNodeRef node);

    /// <summary>Build the read-only info view. The view queries its own live data via <paramref name="context"/>.</summary>
    Control CreateInfoView(NodeInfoContext context);
}

/// <summary>
/// Everything a node-info view needs to query its own live data: the connection profile (already resolved
/// to the target database by the host), the node it was opened on, and the provider itself. Read-only —
/// no host services, mirroring how <c>NodeInfoContext</c> stays lighter than <c>ToolExecutionContext</c>.
/// </summary>
public sealed record NodeInfoContext(ConnectionProfile Profile, DbNodeRef Node, IDbProvider Provider);
