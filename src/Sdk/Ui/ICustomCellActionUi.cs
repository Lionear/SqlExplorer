using Avalonia.Controls;
using Lionear.SqlExplorer.Sdk.Connections;

namespace Lionear.SqlExplorer.Sdk.Ui;

/// <summary>
/// Optional capability an <c>IDbProvider</c> may also implement to make individual result-grid cells
/// actionable: a cell the provider recognises (by column + value + row) renders as a link, and clicking it
/// opens a provider-owned dialog. A fourth Route-B capability alongside <see cref="ICustomConnectionUi"/>,
/// <see cref="ICustomNodeInfoUi"/> and <c>ICustomToolUi</c> — the provider owns an Avalonia
/// <see cref="Control"/> that queries its own live data and may act on it (e.g. a Kill button), shown in the
/// same generic dialog chrome as a node-info view.
/// </summary>
/// <remarks>
/// Deliberately generic so different cells can drive different dialogs: the Activity Monitor's
/// <c>blocking_session_id</c> opens "who is blocking me" with a Kill action, and a foreign-key value can
/// later open the referenced row — both are the same "recognise a cell, open a provider dialog" shape, so
/// the host needs no per-feature wiring. Additive optional-interface check (no host API bump), same
/// precedent as <see cref="ICustomNodeInfoUi"/>. This assembly and Avalonia are shared across the plugin
/// ALC boundary, so the returned control has a single type identity with the host.
/// </remarks>
public interface ICustomCellActionUi
{
    /// <summary>True when the cell described by <paramref name="context"/> has an action (renders as a link).</summary>
    bool HasCellAction(CellActionContext context);

    /// <summary>Dialog title for the cell's action (e.g. "Blocking session 59").</summary>
    string CellActionTitle(CellActionContext context);

    /// <summary>Build the provider-owned view for the action dialog. It queries its own live data through
    /// <see cref="CellActionContext.Profile"/> and may act on it (e.g. a Kill button).</summary>
    Control CreateCellActionView(CellActionContext context);
}

/// <summary>
/// Everything a cell action needs: the resolved connection profile, the provider itself, the clicked
/// cell's column name and value, and every value in that row keyed by column name (so the provider can read
/// sibling cells — e.g. a foreign-key action reading the row's key columns). Read-only, mirroring
/// <see cref="NodeInfoContext"/>.
/// </summary>
public sealed record CellActionContext(
    ConnectionProfile Profile,
    IDbProvider Provider,
    string ColumnName,
    object? CellValue,
    IReadOnlyDictionary<string, object?> Row);
