using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Lionear.SqlExplorer.Core.Editing;

/// <summary>
/// One cell in an <see cref="EditableRow"/>. Editing binds two-way to <see cref="Value"/> — a
/// plain property, so the DataGrid commits edits reliably (a bare row-indexer binding does not).
/// <see cref="IsModified"/> drives the "changed until save/discard" cell highlight.
/// </summary>
public sealed class EditableCell : INotifyPropertyChanged
{
    private readonly EditableRow _row;
    private object? _value;

    internal EditableCell(EditableRow row, object? original, object? value)
    {
        _row = row;
        Original = original;
        _value = value;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public object? Original { get; private set; }

    public object? Value
    {
        get => _value;
        set
        {
            if (Equals(_value, value))
            {
                return;
            }

            _value = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsModified));
            _row.NotifyCellEdited();
        }
    }

    /// <summary>True when this cell of an existing row differs from its loaded value.</summary>
    public bool IsModified => _row.State != RowState.Added && !SameValue(_value, Original);

    // Re-baseline after a save (unused when the host re-queries, but keeps the model consistent).
    internal void AcceptChanges()
    {
        Original = _value;
        OnPropertyChanged(nameof(IsModified));
    }

    internal void RaiseModifiedChanged() => OnPropertyChanged(nameof(IsModified));

    // Compare by invariant string form so an edit that re-types the same value (e.g. "3" over 3L)
    // doesn't light up as changed — good enough for the highlight; the save-flow coerces properly.
    private static bool SameValue(object? a, object? b)
    {
        if (a is null && b is null)
        {
            return true;
        }

        if (a is null || b is null)
        {
            return false;
        }

        return string.Equals(
            Convert.ToString(a, CultureInfo.InvariantCulture),
            Convert.ToString(b, CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
