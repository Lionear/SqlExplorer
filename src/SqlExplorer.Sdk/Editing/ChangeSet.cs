namespace SqlExplorer.Sdk.Editing;

public enum RowChangeKind { Added, Modified, Deleted }

/// <summary>One changed, writable cell: its real column name (<c>BaseColumn ?? Name</c>) and new value.</summary>
public sealed record CellChange(string Column, object? Value);

/// <summary>
/// One row's pending change, keyed by its primary-key column(s) (<see cref="Query.ResultColumn.IsKey"/>)
/// so a provider can build a native filter/identity without any SQL. <see cref="Identity"/> holds the
/// key columns' *original* values (used to find the row on Modified/Deleted; empty on Added, since there
/// is nothing to find yet). <see cref="Cells"/> holds the writable, non-key columns that actually changed
/// (empty on Deleted).
/// </summary>
public sealed record RowChange(
    RowChangeKind Kind,
    IReadOnlyDictionary<string, object?> Identity,
    IReadOnlyList<CellChange> Cells);

/// <summary>
/// The structured, dialect-free counterpart to <see cref="Query.SqlStatement"/> — the write half of the
/// editable-grid save-flow for providers where <see cref="IDbProvider.IsSqlBased"/> is false (see
/// <see cref="IDbProvider.ApplyChangesAsync"/>). <see cref="Table"/> is the same table/collection concept
/// SQL providers key their generated CRUD statements on (<c>ResultColumn.BaseTable</c>).
/// </summary>
public sealed record ChangeSet(string? Schema, string Table, IReadOnlyList<RowChange> Rows);

/// <summary>
/// Outcome of <see cref="IDbProvider.ApplyChangesAsync"/>. Unlike the SQL path — always one all-or-nothing
/// transaction via <see cref="IDbProvider.ExecuteBatchAsync"/> — a non-SQL engine's native batch may not be
/// atomic (e.g. a bulk API applied best-effort per item). <see cref="IsAtomic"/> tells the host whether
/// "N rows affected" can mean "the rest silently failed"; <see cref="RowErrors"/> (empty on full success)
/// lets the UI report which rows did not make it.
/// </summary>
public sealed record WritebackResult(int AffectedCount, bool IsAtomic, IReadOnlyList<string> RowErrors);
