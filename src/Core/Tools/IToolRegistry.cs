using Lionear.SqlExplorer.Sdk.Schema;
using Lionear.SqlExplorer.Sdk.Tools;

namespace Lionear.SqlExplorer.Core.Tools;

/// <summary>All loaded tool plugins, plus the "which apply here" filter the context menu uses.</summary>
public interface IToolRegistry
{
    IReadOnlyList<IToolPlugin> All { get; }

    /// <summary>Tools applicable to a node: matched on the owning provider id and the node kind
    /// (<paramref name="nodeKind"/> null = the connection root).</summary>
    IReadOnlyList<IToolPlugin> Applicable(string providerId, DbNodeKind? nodeKind);
}

/// <inheritdoc />
public sealed class ToolRegistry : IToolRegistry
{
    private readonly List<IToolPlugin> _all;

    public ToolRegistry(IEnumerable<IToolPlugin> tools)
    {
        _all = tools.ToList();
    }

    public IReadOnlyList<IToolPlugin> All => _all;

    public IReadOnlyList<IToolPlugin> Applicable(string providerId, DbNodeKind? nodeKind) =>
        _all.Where(t => Matches(t.Target, providerId, nodeKind)).ToList();

    private static bool Matches(ToolTarget target, string providerId, DbNodeKind? nodeKind)
    {
        if (target.ProviderIds is { } ids && !ids.Contains(providerId, StringComparer.Ordinal))
        {
            return false;
        }

        // The connection root has no node kind; it's targeted via IncludeConnectionRoot (all providers) or
        // ConnectionRootProviderIds (schema-less engines whose root is the database, e.g. SQLite).
        if (nodeKind is null)
        {
            return target.IncludeConnectionRoot
                || target.ConnectionRootProviderIds is { } roots && roots.Contains(providerId, StringComparer.Ordinal);
        }

        return target.NodeKinds is not { } kinds || kinds.Contains(nodeKind.Value);
    }
}
