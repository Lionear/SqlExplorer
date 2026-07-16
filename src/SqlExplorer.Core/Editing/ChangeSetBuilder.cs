using SqlExplorer.Sdk;
using SqlExplorer.Sdk.Editing;

namespace SqlExplorer.Core.Editing;

/// <summary>
/// Turns the pending changes in an <see cref="EditableResultSet"/> into a dialect-free
/// <see cref="ChangeSet"/> — the non-SQL counterpart to <see cref="CrudStatementBuilder"/>, for providers
/// where <see cref="IDbProvider.IsSqlBased"/> is false. No identifier quoting or SQL text is generated;
/// the provider turns <see cref="RowChange"/>s into its own native write operations via
/// <see cref="IDbProvider.ApplyChangesAsync"/> (SE-114).
/// </summary>
public static class ChangeSetBuilder
{
    public static ChangeSet? Build(EditableResultSet resultSet)
    {
        if (resultSet.Target is not { } target)
        {
            return null;
        }

        var columns = resultSet.Columns;
        var writable = Enumerable.Range(0, columns.Count)
            .Where(i => columns[i].BaseTable == target.Table && !columns[i].IsReadOnly)
            .ToArray();
        var keys = Enumerable.Range(0, columns.Count)
            .Where(i => columns[i].IsKey && columns[i].BaseTable == target.Table)
            .ToArray();

        var rows = new List<RowChange>();
        foreach (var row in resultSet.Rows)
        {
            var change = row.State switch
            {
                RowState.Added => BuildAdded(row, columns, writable),
                RowState.Modified => BuildModified(row, columns, writable, keys),
                RowState.Deleted => BuildDeleted(row, columns, keys),
                _ => null
            };

            if (change is not null)
            {
                rows.Add(change);
            }
        }

        return new ChangeSet(target.Schema, target.Table, rows);
    }

    private static RowChange BuildAdded(EditableRow row, IReadOnlyList<ResultColumn> columns, int[] writable)
    {
        var cells = writable
            .Where(i => row.CurrentAt(i) is not null)
            .Select(i => new CellChange(ColumnName(columns[i]), row.CurrentAt(i)))
            .ToList();
        return new RowChange(RowChangeKind.Added, new Dictionary<string, object?>(), cells);
    }

    private static RowChange? BuildModified(
        EditableRow row,
        IReadOnlyList<ResultColumn> columns,
        int[] writable,
        int[] keys)
    {
        var cells = writable
            .Where(i => Array.IndexOf(keys, i) < 0 && !Equals(row.CurrentAt(i), row.OriginalAt(i)))
            .Select(i => new CellChange(ColumnName(columns[i]), row.CurrentAt(i)))
            .ToList();

        // Nothing actually changed on a writable column (e.g. only a read-only cell touched).
        return cells.Count == 0 ? null : new RowChange(RowChangeKind.Modified, Identity(row, columns, keys), cells);
    }

    private static RowChange BuildDeleted(EditableRow row, IReadOnlyList<ResultColumn> columns, int[] keys) =>
        new(RowChangeKind.Deleted, Identity(row, columns, keys), []);

    private static IReadOnlyDictionary<string, object?> Identity(
        EditableRow row,
        IReadOnlyList<ResultColumn> columns,
        int[] keys) =>
        keys.ToDictionary(i => ColumnName(columns[i]), i => row.OriginalAt(i));

    // The real column name behind a result column, same fallback as CrudStatementBuilder.
    private static string ColumnName(ResultColumn column) => column.BaseColumn ?? column.Name;
}
