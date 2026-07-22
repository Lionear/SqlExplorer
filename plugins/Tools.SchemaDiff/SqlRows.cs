namespace SqlExplorer.Tools.SchemaDiff;

/// <summary>
/// A thin case-insensitive view over a <see cref="QueryResult"/>, so a reader addresses cells by column
/// name regardless of how an engine cases its catalogue column headers (Postgres lower-cases, SQL Server
/// preserves, SQLite echoes the PRAGMA's own naming). Shared by every schema source.
/// </summary>
public sealed record SqlRows(IReadOnlyList<SqlRow> All)
{
    public static SqlRows From(QueryResult result)
    {
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < result.Columns.Count; i++)
        {
            index[result.Columns[i].Name] = i;
        }

        return new SqlRows(result.Rows.Select(cells => new SqlRow(cells, index)).ToList());
    }

    /// <summary>Builds a rowset from literal values — the readers' pure mapping halves are unit-tested
    /// through this, without a database.</summary>
    public static SqlRows Of(IReadOnlyList<string> columns, params object?[][] rows)
    {
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < columns.Count; i++)
        {
            index[columns[i]] = i;
        }

        return new SqlRows(rows.Select(cells => new SqlRow(cells, index)).ToList());
    }
}

public sealed class SqlRow(object?[] cells, Dictionary<string, int> index)
{
    /// <summary>The cell as text; empty for a missing column or a NULL, so callers never null-check.</summary>
    public string this[string column] =>
        index.TryGetValue(column, out var i) && i < cells.Length && cells[i] is { } v
            ? v.ToString() ?? string.Empty
            : string.Empty;

    public int Int(string column) => int.TryParse(this[column], out var n) ? n : 0;

    public int? NullableInt(string column) => int.TryParse(this[column], out var n) ? n : null;

    /// <summary>True for the several ways an engine spells a boolean catalogue flag: 1 / true / YES / t.</summary>
    public bool Flag(string column) => this[column].Trim() switch
    {
        "1" or "t" or "T" => true,
        var s => s.Equals("true", StringComparison.OrdinalIgnoreCase)
              || s.Equals("yes", StringComparison.OrdinalIgnoreCase)
    };

    public string? NullIfBlank(string column) =>
        string.IsNullOrWhiteSpace(this[column]) ? null : this[column];
}
