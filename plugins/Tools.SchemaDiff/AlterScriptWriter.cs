using System.Text;

namespace SqlExplorer.Tools.SchemaDiff;

/// <summary>
/// Renders an ordered <see cref="SchemaChange"/> list (from <see cref="SchemaDiffer"/>) into a runnable
/// migration script for one engine (<see cref="SqlDialect"/>). Pure string work, so it is unit-tested
/// per dialect without a database. A created table carries its columns, primary key, uniques and indexes
/// inline/immediately; foreign keys are always emitted last (as separate <c>ALTER TABLE … ADD CONSTRAINT</c>)
/// so every table and column they reference already exists.
///
/// <para>The individual statements are also exposed via <see cref="Statements"/> so the tool can apply them
/// one at a time and report per-statement progress. Comment lines (SQL Server default notes, unsupported
/// ops) are skipped when applying.</para>
/// </summary>
public sealed class AlterScriptWriter(SqlDialect dialect)
{
    public IReadOnlyList<string> Statements(IReadOnlyList<SchemaChange> changes)
    {
        var statements = new List<string>();
        foreach (var change in changes)
        {
            statements.AddRange(Render(change));
        }

        return statements;
    }

    public string Script(IReadOnlyList<SchemaChange> changes) => string.Join("\n", Statements(changes));

    private IEnumerable<string> Render(SchemaChange change) => change switch
    {
        CreateTable c => RenderCreateTable(c.Def),
        DropTable c => [$"DROP TABLE {dialect.QuoteTable(c.Def)};"],
        AddColumn c => [$"ALTER TABLE {dialect.QuoteTable(c.Table)} {dialect.AddColumnClause} {dialect.ColumnSpec(c.Column)};"],
        DropColumn c => [$"ALTER TABLE {dialect.QuoteTable(c.Table)} DROP COLUMN {dialect.Quote(c.Column.Name)};"],
        AlterColumn c => dialect.AlterColumn(c.Table, c.From, c.To),
        AddPrimaryKey c => [Constraint(c.Table, "add primary key", c.Key.Name,
            $"ALTER TABLE {dialect.QuoteTable(c.Table)} ADD {dialect.PrimaryKeyClause(c.Key, Cols(c.Key.Columns))};")],
        DropPrimaryKey c => [Constraint(c.Table, "drop primary key", c.Key.Name,
            $"ALTER TABLE {dialect.QuoteTable(c.Table)} DROP CONSTRAINT {dialect.Quote(c.Key.Name)};")],
        AddUnique c => [Constraint(c.Table, "add unique constraint", c.Unique.Name,
            $"ALTER TABLE {dialect.QuoteTable(c.Table)} ADD CONSTRAINT {dialect.Quote(c.Unique.Name)} UNIQUE ({Cols(c.Unique.Columns)});")],
        DropUnique c => [Constraint(c.Table, "drop unique constraint", c.Unique.Name,
            $"ALTER TABLE {dialect.QuoteTable(c.Table)} DROP CONSTRAINT {dialect.Quote(c.Unique.Name)};")],
        AddIndex c => [RenderCreateIndex(c.Table, c.Index)],
        DropIndex c => [dialect.DropIndex(c.Table, c.Index)],
        AddForeignKey c => [Constraint(c.Table, "add foreign key", c.ForeignKey.Name,
            RenderAddForeignKey(c.Table, c.ForeignKey))],
        DropForeignKey c => [Constraint(c.Table, "drop foreign key", c.ForeignKey.Name,
            $"ALTER TABLE {dialect.QuoteTable(c.Table)} DROP CONSTRAINT {dialect.Quote(c.ForeignKey.Name)};")],
        _ => []
    };

    // An engine that can't ALTER a constraint (SQLite) gets a note instead of DDL that would fail on run.
    private string Constraint(TableDef table, string what, string name, string statement) =>
        dialect.SupportsAlterConstraint
            ? statement
            : $"-- NOTE: cannot {what} {dialect.Quote(name)} on {dialect.QuoteTable(table)} — this engine " +
              "only supports adding and dropping columns; recreate the table to apply.";

    private IEnumerable<string> RenderCreateTable(TableDef t)
    {
        var body = new List<string>();
        body.AddRange(t.Columns.OrderBy(c => c.Ordinal).Select(dialect.ColumnSpec));

        if (t.PrimaryKey is { } pk)
        {
            body.Add(dialect.PrimaryKeyClause(pk, Cols(pk.Columns)));
        }

        body.AddRange(t.Uniques.Select(u => $"CONSTRAINT {dialect.Quote(u.Name)} UNIQUE ({Cols(u.Columns)})"));

        var sb = new StringBuilder();
        sb.Append("CREATE TABLE ").Append(dialect.QuoteTable(t)).Append(" (\n    ");
        sb.Append(string.Join(",\n    ", body));
        sb.Append("\n);");
        yield return sb.ToString();

        // Non-unique (and any secondary) indexes are separate CREATE INDEX statements after the table exists.
        foreach (var index in t.Indexes)
        {
            yield return RenderCreateIndex(t, index);
        }
    }

    private string RenderCreateIndex(TableDef t, IndexDef index) =>
        $"CREATE {(index.Unique ? "UNIQUE " : "")}INDEX {dialect.Quote(index.Name)} " +
        $"ON {dialect.QuoteTable(t)} ({Cols(index.Columns)});";

    private string RenderAddForeignKey(TableDef t, ForeignKeyDef fk)
    {
        var refTable = string.IsNullOrEmpty(fk.RefSchema)
            ? dialect.Quote(fk.RefTable)
            : $"{dialect.Quote(fk.RefSchema)}.{dialect.Quote(fk.RefTable)}";
        return $"ALTER TABLE {dialect.QuoteTable(t)} ADD CONSTRAINT {dialect.Quote(fk.Name)} " +
               $"FOREIGN KEY ({Cols(fk.Columns)}) REFERENCES {refTable} ({Cols(fk.RefColumns)});";
    }

    private string Cols(IReadOnlyList<string> columns) => string.Join(", ", columns.Select(dialect.Quote));
}
