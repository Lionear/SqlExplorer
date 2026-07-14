using Lionear.SqlExplorer.Sdk.Schema;

namespace Lionear.SqlExplorer.Sdk.Tools;

/// <summary>
/// Where a tool is offered in the schema tree. The host shows the tool's menu item on a node only when
/// it matches: the node's owning provider is in <see cref="ProviderIds"/> (null = every provider — the
/// "universal" case) AND the node's kind is in <see cref="NodeKinds"/> (null = any kind). The connection
/// root has no node kind (<c>NodeKind == null</c>), so a tool that applies to the whole connection sets
/// <see cref="IncludeConnectionRoot"/> instead of trying to express it via <see cref="NodeKinds"/>.
/// </summary>
/// <param name="ConnectionRootProviderIds">Providers whose connection root ALSO matches (in addition to
/// <see cref="NodeKinds"/>). For schema-less engines like SQLite the connection root <em>is</em> the
/// database, so a per-database tool (targeting <c>DbNodeKind.Database</c>) can opt those providers' roots
/// in here without offering itself on multi-database server roots (Postgres/MySQL/SQL Server).</param>
public sealed record ToolTarget(
    IReadOnlyList<string>? ProviderIds = null,
    IReadOnlyList<DbNodeKind>? NodeKinds = null,
    bool IncludeConnectionRoot = false,
    IReadOnlyList<string>? ConnectionRootProviderIds = null);
