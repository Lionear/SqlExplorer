using Avalonia.Controls;
using SqlExplorer.Sdk.Connections;
using SqlExplorer.Sdk.Schema;

namespace SqlExplorer.Sdk.Ui;

/// <summary>
/// Optional Route-B capability an <c>IDbProvider</c> may also implement to own a security-management view
/// the host can't model generically — SQL Server's server-Login flow (create/drop, server-role membership,
/// login→database-user mapping, SQL + Windows auth) is the first user. The host offers the entry points
/// (New Login… on a <see cref="DbNodeKind.LoginFolder"/>, Properties… on a <see cref="DbNodeKind.Login"/>)
/// and hosts the returned <c>Control</c> in a dialog; the view is otherwise self-contained (it reads
/// databases/roles/membership and runs its DDL through the provider it is handed).
/// A fifth Route-B capability alongside <see cref="ICustomConnectionUi"/>, <see cref="ICustomNodeInfoUi"/>,
/// <see cref="ICustomCellActionUi"/> and <c>ICustomToolUi</c>.
/// </summary>
/// <remarks>
/// Host detection is the usual optional-interface check: a provider that implements this gets its own view
/// for the actions in <see cref="SecurityUiAction"/>; one that doesn't falls back to the generic flows.
/// </remarks>
public interface ICustomSecurityUi
{
    /// <summary>Build the provider-owned security view for the requested <see cref="SecurityUiAction"/>.</summary>
    Control CreateSecurityView(SecurityUiContext context);
}

/// <summary>Which security view the host is asking the provider to build.</summary>
public enum SecurityUiAction
{
    /// <summary>Create a new server login (invoked on a <see cref="DbNodeKind.LoginFolder"/>).</summary>
    NewLogin,

    /// <summary>Edit an existing server login (invoked on a <see cref="DbNodeKind.Login"/>).</summary>
    LoginProperties
}

/// <summary>Everything a custom security view needs: the action, the resolved profile, the node it was
/// opened on (its ancestry gives the target server/database), the target principal for an edit action
/// (null for <see cref="SecurityUiAction.NewLogin"/>), and the provider itself (to read metadata and run
/// the resulting DDL).</summary>
public sealed record SecurityUiContext(
    SecurityUiAction Action,
    ConnectionProfile Profile,
    IReadOnlyList<DbNodeRef> Ancestors,
    DbNodeRef? Target,
    IDbProvider Provider);
