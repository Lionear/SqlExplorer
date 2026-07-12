using System.Collections.ObjectModel;
using Lionear.SqlExplorer.Sdk;

namespace Lionear.SqlExplorer.Core.Editing;

/// <summary>The single table a result set can be saved back to.</summary>
public sealed record EditTarget(string? Schema, string Table);

/// <summary>Number of pending row changes by kind.</summary>
public readonly record struct PendingCounts(int Modified, int Added, int Deleted)
{
    public int Total => Modified + Added + Deleted;
}

/// <summary>
/// Wraps a <see cref="QueryResult"/> with change tracking so the grid can be edited and
/// saved (Notes §8). A result is only editable when every column that maps to a table maps
/// to the <em>same</em> table and at least one of its primary-key columns is present — otherwise
/// there is no safe WHERE clause and the grid stays read-only with <see cref="ReadOnlyReason"/>.
/// </summary>
public sealed class EditableResultSet
{
    private EditableResultSet(
        IReadOnlyList<ResultColumn> columns,
        ObservableCollection<EditableRow> rows,
        EditTarget? target,
        string? readOnlyReason)
    {
        Columns = columns;
        Rows = rows;
        Target = target;
        ReadOnlyReason = readOnlyReason;
    }

    public IReadOnlyList<ResultColumn> Columns { get; }

    public ObservableCollection<EditableRow> Rows { get; }

    /// <summary>The table changes are saved to, or null when the result is not editable.</summary>
    public EditTarget? Target { get; }

    public bool IsEditable => Target is not null;

    /// <summary>Why the result is read-only, when <see cref="IsEditable"/> is false.</summary>
    public string? ReadOnlyReason { get; }

    public static EditableResultSet From(QueryResult result)
    {
        var rows = new ObservableCollection<EditableRow>(
            result.Rows.Select(EditableRow.Existing));

        var (target, reason) = ResolveTarget(result.Columns);
        return new EditableResultSet(result.Columns, rows, target, reason);
    }

    public bool HasChanges => Rows.Any(r => r.State != RowState.Unchanged);

    public PendingCounts Pending => new(
        Rows.Count(r => r.State == RowState.Modified),
        Rows.Count(r => r.State == RowState.Added),
        Rows.Count(r => r.State == RowState.Deleted));

    /// <summary>Append a new, empty row to be inserted on save.</summary>
    public EditableRow AddRow()
    {
        var row = EditableRow.New(Columns.Count);
        Rows.Add(row);
        return row;
    }

    /// <summary>Delete a row: a never-saved new row is just dropped; an existing row is marked.</summary>
    public void DeleteRow(EditableRow row)
    {
        if (row.State == RowState.Added)
        {
            Rows.Remove(row);
        }
        else
        {
            row.MarkDeleted();
        }
    }

    /// <summary>Drop deleted rows and re-baseline the rest (used when not re-querying after save).</summary>
    public void AcceptChanges()
    {
        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (Rows[i].State == RowState.Deleted)
            {
                Rows.RemoveAt(i);
            }
            else
            {
                Rows[i].AcceptChanges();
            }
        }
    }

    // Determine the single writable table behind the result, or a reason it has none.
    private static (EditTarget?, string?) ResolveTarget(IReadOnlyList<ResultColumn> columns)
    {
        var tables = columns
            .Where(c => !string.IsNullOrEmpty(c.BaseTable))
            .Select(c => new EditTarget(NullIfEmpty(c.BaseSchema), c.BaseTable!))
            .Distinct()
            .ToList();

        if (tables.Count == 0)
        {
            return (null, "Result has no updatable table columns.");
        }

        if (tables.Count > 1)
        {
            return (null, "Result spans multiple tables.");
        }

        var target = tables[0];
        var hasKey = columns.Any(c => c.IsKey && c.BaseTable == target.Table);
        return hasKey
            ? (target, null)
            : (null, "Result has no primary-key column to identify rows.");
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;
}
