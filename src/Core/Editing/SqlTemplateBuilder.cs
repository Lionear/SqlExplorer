using Lionear.SqlExplorer.Sdk;

namespace Lionear.SqlExplorer.Core.Editing;

/// <summary>
/// Generates ready-to-edit SQL templates for a relation (the "SQL commands" action): SELECT of
/// named columns, INSERT, UPDATE and DELETE. Identifiers are dialect-quoted; values are left as
/// <c>:name</c> placeholders for the user to fill in. Primary-key columns (from the live column
/// metadata) drive the UPDATE/DELETE WHERE clause.
/// </summary>
public static class SqlTemplateBuilder
{
    public static string Build(string kind, string qualifiedTable, ISqlDialect dialect, IReadOnlyList<ResultColumn> columns)
    {
        string Name(ResultColumn c) => c.BaseColumn ?? c.Name;
        string Quote(ResultColumn c) => dialect.QuoteIdentifier(Name(c));
        string Param(ResultColumn c) => ":" + Name(c);

        var keys = columns.Where(c => c.IsKey).ToList();
        var whereCols = keys.Count > 0 ? keys : columns;
        var writable = columns.Where(c => !c.IsReadOnly).ToList();

        return kind switch
        {
            "SelectColumns" =>
                $"SELECT {string.Join(", ", columns.Select(Quote))}\nFROM {qualifiedTable};",
            "Insert" =>
                $"INSERT INTO {qualifiedTable}\n" +
                $"({string.Join(", ", writable.Select(Quote))})\n" +
                $"VALUES\n({string.Join(", ", writable.Select(Param))});",
            "Update" =>
                $"UPDATE {qualifiedTable}\n" +
                $"SET {string.Join(", ", columns.Where(c => !c.IsKey && !c.IsReadOnly).Select(c => $"{Quote(c)} = {Param(c)}"))}\n" +
                $"WHERE {string.Join(" AND ", whereCols.Select(c => $"{Quote(c)} = {Param(c)}"))};",
            "Delete" =>
                $"DELETE FROM {qualifiedTable}\n" +
                $"WHERE {string.Join(" AND ", whereCols.Select(c => $"{Quote(c)} = {Param(c)}"))};",
            _ => $"SELECT * FROM {qualifiedTable};"
        };
    }
}
