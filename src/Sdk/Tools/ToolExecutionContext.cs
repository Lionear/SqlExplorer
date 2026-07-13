using Lionear.SqlExplorer.Sdk.Connections;
using Lionear.SqlExplorer.Sdk.Schema;

namespace Lionear.SqlExplorer.Sdk.Tools;

/// <summary>
/// Everything a tool needs to run against the selected connection/node. <see cref="Provider"/> (not just
/// its dialect) is handed over so a generic tool can walk the schema, run queries and recreate objects
/// through the same interfaces the host uses — the "universal" tools rely on this. <see cref="Node"/> is
/// the tree node the tool was launched on, or null when launched on the connection root.
/// </summary>
public sealed record ToolExecutionContext(
    ConnectionProfile Profile,
    DbNodeRef? Node,
    IDbProvider Provider,
    string ProviderId,
    IToolHost Host);
