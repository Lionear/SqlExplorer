using System.Text;

namespace SqlExplorer.Tools.SchemaDiff;

/// <summary>
/// The per-engine SQL differences the writer needs: how identifiers are quoted, and how a column is
/// altered in place (the one operation whose syntax genuinely diverges — Postgres uses several
/// <c>ALTER COLUMN</c> sub-clauses, MySQL one <c>MODIFY COLUMN</c>, SQL Server an <c>ALTER COLUMN</c>
/// that can't carry a default). Everything else (CREATE/DROP TABLE, ADD/DROP COLUMN, constraints,
/// indexes) is close enough to standard SQL to share one renderer in <see cref="AlterScriptWriter"/>.
/// </summary>
public abstract class SqlDialect
{
    public static SqlDialect For(string providerId) => providerId switch
    {
        "postgres" => new PostgresDialect(),
        "mysql" => new MySqlDialect(),
        "sqlserver" => new SqlServerDialect(),
        _ => new GenericDialect()   // sqlite and any third-party engine: ANSI-ish, in-place column alter unsupported.
    };

    public abstract string Quote(string identifier);

    /// <summary>Keyword for adding a column — "ADD COLUMN" everywhere except SQL Server ("ADD").</summary>
    public virtual string AddColumnClause => "ADD COLUMN";

    public string QuoteTable(TableDef table) =>
        string.IsNullOrEmpty(table.Schema) ? Quote(table.Name) : $"{Quote(table.Schema)}.{Quote(table.Name)}";

    /// <summary>Render <c>col type [NOT NULL] [DEFAULT expr]</c> for a CREATE/ADD.</summary>
    public string ColumnSpec(ColumnDef c)
    {
        var sb = new StringBuilder();
        sb.Append(Quote(c.Name)).Append(' ').Append(c.DataType);
        if (!c.Nullable)
        {
            sb.Append(" NOT NULL");
        }

        if (!string.IsNullOrWhiteSpace(c.Default))
        {
            sb.Append(" DEFAULT ").Append(c.Default);
        }

        return sb.ToString();
    }

    /// <summary>The statements that turn column <paramref name="from"/> into <paramref name="to"/> on
    /// <paramref name="table"/>. May be several (Postgres) or one (MySQL); a dialect that can't do it in
    /// place returns a single explanatory comment.</summary>
    public abstract IEnumerable<string> AlterColumn(TableDef table, ColumnDef from, ColumnDef to);
}

public sealed class PostgresDialect : SqlDialect
{
    public override string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    public override IEnumerable<string> AlterColumn(TableDef table, ColumnDef from, ColumnDef to)
    {
        var t = QuoteTable(table);
        var col = Quote(to.Name);

        if (!string.Equals(from.DataType, to.DataType, StringComparison.OrdinalIgnoreCase))
        {
            yield return $"ALTER TABLE {t} ALTER COLUMN {col} TYPE {to.DataType};";
        }

        if (from.Nullable != to.Nullable)
        {
            yield return $"ALTER TABLE {t} ALTER COLUMN {col} {(to.Nullable ? "DROP NOT NULL" : "SET NOT NULL")};";
        }

        if (!string.Equals(from.Default ?? "", to.Default ?? "", StringComparison.OrdinalIgnoreCase))
        {
            yield return to.Default is { Length: > 0 }
                ? $"ALTER TABLE {t} ALTER COLUMN {col} SET DEFAULT {to.Default};"
                : $"ALTER TABLE {t} ALTER COLUMN {col} DROP DEFAULT;";
        }
    }
}

public sealed class MySqlDialect : SqlDialect
{
    public override string Quote(string identifier) => $"`{identifier.Replace("`", "``")}`";

    // MySQL restates the whole column definition in one MODIFY.
    public override IEnumerable<string> AlterColumn(TableDef table, ColumnDef from, ColumnDef to)
    {
        yield return $"ALTER TABLE {QuoteTable(table)} MODIFY COLUMN {ColumnSpec(to)};";
    }
}

public sealed class SqlServerDialect : SqlDialect
{
    public override string AddColumnClause => "ADD";

    public override string Quote(string identifier) => $"[{identifier.Replace("]", "]]")}]";

    public override IEnumerable<string> AlterColumn(TableDef table, ColumnDef from, ColumnDef to)
    {
        var t = QuoteTable(table);
        var nullability = to.Nullable ? "NULL" : "NOT NULL";
        yield return $"ALTER TABLE {t} ALTER COLUMN {Quote(to.Name)} {to.DataType} {nullability};";

        // A SQL Server column default is a separate named constraint, not part of ALTER COLUMN — surfacing
        // it as a comment keeps the generated script honest rather than silently dropping the change.
        if (!string.Equals(from.Default ?? "", to.Default ?? "", StringComparison.OrdinalIgnoreCase))
        {
            yield return $"-- NOTE: default for {Quote(to.Name)} changed to [{to.Default ?? "(none)"}]; " +
                         "add/drop the DEFAULT constraint manually (SQL Server keeps it as a named constraint).";
        }
    }
}

public sealed class GenericDialect : SqlDialect
{
    public override string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    public override IEnumerable<string> AlterColumn(TableDef table, ColumnDef from, ColumnDef to)
    {
        yield return $"-- NOTE: column {Quote(to.Name)} on {QuoteTable(table)} changed " +
                     $"({from.DataType}{(from.Nullable ? " NULL" : " NOT NULL")} -> " +
                     $"{to.DataType}{(to.Nullable ? " NULL" : " NOT NULL")}); this engine can't alter a " +
                     "column in place — recreate the table to apply.";
    }
}
