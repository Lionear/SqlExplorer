using System.ComponentModel;

namespace Lionear.SqlExplorer.Core.Editing;

/// <summary>
/// A single row in the editable result grid. Each column is an <see cref="EditableCell"/> that
/// keeps its original value alongside the current one; the row tracks its own <see cref="RowState"/>.
/// The DataGrid binds to the cells' <see cref="EditableCell.Value"/>, so an in-place edit flips an
/// unchanged row to <see cref="RowState.Modified"/> (see Notes §8).
/// </summary>
public sealed class EditableRow : INotifyPropertyChanged
{
    private RowState _state;

    private EditableRow(IReadOnlyList<EditableCell> cells, RowState state)
    {
        Cells = cells;
        _state = state;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<EditableCell> Cells { get; }

    /// <summary>Wrap a row that already exists in the database.</summary>
    public static EditableRow Existing(object?[] values)
    {
        var row = Empty(values.Length, RowState.Unchanged, out var cells);
        for (var i = 0; i < values.Length; i++)
        {
            cells[i] = new EditableCell(row, values[i], values[i]);
        }

        return row;
    }

    /// <summary>A brand-new, all-null row to be inserted.</summary>
    public static EditableRow New(int columnCount)
    {
        var row = Empty(columnCount, RowState.Added, out var cells);
        for (var i = 0; i < columnCount; i++)
        {
            cells[i] = new EditableCell(row, null, null);
        }

        return row;
    }

    public RowState State
    {
        get => _state;
        private set
        {
            if (_state == value)
            {
                return;
            }

            _state = value;
            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(IsDeleted));
            // An Added row never shows the "modified" highlight; refresh cells if that flips.
            foreach (var cell in Cells)
            {
                cell.RaiseModifiedChanged();
            }
        }
    }

    public bool IsDeleted => _state == RowState.Deleted;

    /// <summary>Convenience accessor over the cells (used by the CRUD builder and tests).</summary>
    public object? this[int index]
    {
        get => Cells[index].Value;
        set => Cells[index].Value = value;
    }

    public object? OriginalAt(int index) => Cells[index].Original;

    public object? CurrentAt(int index) => Cells[index].Value;

    /// <summary>Mark an existing row for deletion (new rows are just dropped by the caller).</summary>
    public void MarkDeleted() => State = RowState.Deleted;

    /// <summary>Adopt the current values as the new baseline after a successful save.</summary>
    public void AcceptChanges()
    {
        foreach (var cell in Cells)
        {
            cell.AcceptChanges();
        }

        State = RowState.Unchanged;
    }

    // Called by a cell when its value changes: an untouched row becomes Modified.
    internal void NotifyCellEdited()
    {
        if (_state == RowState.Unchanged)
        {
            State = RowState.Modified;
        }
    }

    private static EditableRow Empty(int columnCount, RowState state, out EditableCell[] cells)
    {
        cells = new EditableCell[columnCount];
        return new EditableRow(cells, state);
    }

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
