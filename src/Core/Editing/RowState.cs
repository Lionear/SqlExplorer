namespace Lionear.SqlExplorer.Core.Editing;

/// <summary>
/// Per-row edit state in the editable result grid. Nothing is persisted until
/// the user presses Save, at which point rows with a non-<see cref="Unchanged"/>
/// state are turned into parameterized CRUD statements (see Notes.md §8).
/// </summary>
public enum RowState
{
    Unchanged,
    Modified,
    Added,
    Deleted
}
