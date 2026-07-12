using System.Globalization;
using Lionear.SqlExplorer.Sdk;

namespace Lionear.SqlExplorer.Core.Editing;

/// <summary>
/// Turns the pending changes in an <see cref="EditableResultSet"/> into parameterised
/// INSERT/UPDATE/DELETE statements. Identifier quoting comes from the provider's
/// <see cref="ISqlDialect"/>; values are bound as <c>@p0…</c> parameters so nothing is
/// ever string-concatenated into SQL. The statements are shown for review and then run
/// as one transaction via <see cref="IDbProvider.ExecuteBatchAsync"/> (Notes §8).
/// </summary>
public static class CrudStatementBuilder
{
    public static IReadOnlyList<SqlStatement> Build(EditableResultSet resultSet, ISqlDialect dialect)
    {
        if (resultSet.Target is not { } target)
        {
            return [];
        }

        var columns = resultSet.Columns;
        var writable = Enumerable.Range(0, columns.Count)
            .Where(i => IsWritable(columns[i], target))
            .ToArray();
        var keys = Enumerable.Range(0, columns.Count)
            .Where(i => columns[i].IsKey && columns[i].BaseTable == target.Table)
            .ToArray();

        var table = QualifiedName(target, dialect);
        var statements = new List<SqlStatement>();

        foreach (var row in resultSet.Rows)
        {
            var statement = row.State switch
            {
                RowState.Added => BuildInsert(row, columns, writable, table, dialect),
                RowState.Modified => BuildUpdate(row, columns, writable, keys, table, dialect),
                RowState.Deleted => BuildDelete(row, columns, keys, table, dialect),
                _ => null
            };

            if (statement is not null)
            {
                statements.Add(statement);
            }
        }

        return statements;
    }

    private static SqlStatement? BuildInsert(
        EditableRow row,
        IReadOnlyList<ResultColumn> columns,
        int[] writable,
        string table,
        ISqlDialect dialect)
    {
        var names = new List<string>();
        var placeholders = new List<string>();
        var parameters = new List<SqlParam>();

        foreach (var i in writable)
        {
            var value = Coerce(row.CurrentAt(i), columns[i].ClrType);
            // Leave unset columns (e.g. auto-increment keys, defaulted columns) to the database.
            if (value is null)
            {
                continue;
            }

            var name = $"p{parameters.Count}";
            names.Add(dialect.QuoteIdentifier(ColumnName(columns[i])));
            placeholders.Add($"@{name}");
            parameters.Add(new SqlParam(name, value));
        }

        var columnList = names.Count == 0 ? string.Empty : $" ({string.Join(", ", names)})";
        var valueList = names.Count == 0 ? "DEFAULT VALUES" : $"VALUES ({string.Join(", ", placeholders)})";
        return new SqlStatement($"INSERT INTO {table}{columnList} {valueList}", parameters);
    }

    private static SqlStatement? BuildUpdate(
        EditableRow row,
        IReadOnlyList<ResultColumn> columns,
        int[] writable,
        int[] keys,
        string table,
        ISqlDialect dialect)
    {
        var assignments = new List<string>();
        var parameters = new List<SqlParam>();

        foreach (var i in writable)
        {
            if (Array.IndexOf(keys, i) >= 0)
            {
                continue;
            }

            var value = Coerce(row.CurrentAt(i), columns[i].ClrType);
            if (Equals(value, row.OriginalAt(i)))
            {
                continue;
            }

            var name = $"p{parameters.Count}";
            assignments.Add($"{dialect.QuoteIdentifier(ColumnName(columns[i]))} = @{name}");
            parameters.Add(new SqlParam(name, value));
        }

        // Nothing actually changed on a writable column (e.g. only a read-only cell touched).
        if (assignments.Count == 0)
        {
            return null;
        }

        var where = BuildKeyPredicate(row, columns, keys, parameters, dialect);
        return new SqlStatement($"UPDATE {table} SET {string.Join(", ", assignments)} WHERE {where}", parameters);
    }

    private static SqlStatement BuildDelete(
        EditableRow row,
        IReadOnlyList<ResultColumn> columns,
        int[] keys,
        string table,
        ISqlDialect dialect)
    {
        var parameters = new List<SqlParam>();
        var where = BuildKeyPredicate(row, columns, keys, parameters, dialect);
        return new SqlStatement($"DELETE FROM {table} WHERE {where}", parameters);
    }

    // WHERE clause on the primary-key columns, matched against their original values.
    private static string BuildKeyPredicate(
        EditableRow row,
        IReadOnlyList<ResultColumn> columns,
        int[] keys,
        List<SqlParam> parameters,
        ISqlDialect dialect)
    {
        var predicates = new List<string>(keys.Length);
        foreach (var i in keys)
        {
            var column = dialect.QuoteIdentifier(ColumnName(columns[i]));
            var original = row.OriginalAt(i);
            if (original is null)
            {
                predicates.Add($"{column} IS NULL");
                continue;
            }

            var name = $"p{parameters.Count}";
            predicates.Add($"{column} = @{name}");
            parameters.Add(new SqlParam(name, original));
        }

        return string.Join(" AND ", predicates);
    }

    // The real column name behind a result column. Some drivers (SQL Server) only set BaseColumn when
    // it differs from the display name, so an unaliased column reports it as null — fall back to Name.
    private static string ColumnName(ResultColumn column) => column.BaseColumn ?? column.Name;

    private static bool IsWritable(ResultColumn column, EditTarget target) =>
        column.BaseTable == target.Table && !column.IsReadOnly;

    private static string QualifiedName(EditTarget target, ISqlDialect dialect) =>
        target.Schema is { } schema
            ? $"{dialect.QuoteIdentifier(schema)}.{dialect.QuoteIdentifier(target.Table)}"
            : dialect.QuoteIdentifier(target.Table);

    // Grid edits arrive as strings; coerce them back to the column's CLR type so drivers
    // that type-check parameters (Postgres) accept them. SQLite is loosely typed and tolerates
    // either way. On a failed parse we pass the value through and let the driver report it.
    private static object? Coerce(object? value, Type target)
    {
        if (value is null || target.IsInstanceOfType(value))
        {
            return value;
        }

        if (value is not string text)
        {
            return value;
        }

        var underlying = Nullable.GetUnderlyingType(target) ?? target;
        if (text.Length == 0)
        {
            return underlying == typeof(string) ? text : null;
        }

        try
        {
            if (underlying == typeof(Guid))
            {
                return Guid.Parse(text);
            }

            if (underlying.IsEnum)
            {
                return Enum.Parse(underlying, text, ignoreCase: true);
            }

            return Convert.ChangeType(text, underlying, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            return value;
        }
    }
}
