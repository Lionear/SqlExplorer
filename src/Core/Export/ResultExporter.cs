using System.Text;
using System.Text.Json;
using Lionear.SqlExplorer.Sdk;

namespace Lionear.SqlExplorer.Core.Export;

/// <summary>
/// Renders a result set as CSV/JSON/SQL-INSERT text. Takes plain columns + rows (the shape
/// <see cref="QueryResult.Rows"/> and <c>EditableRow</c> both reduce to) rather than an
/// <c>EditableResultSet</c>, so it has no dependency on the editable-grid machinery.
/// </summary>
public static class ResultExporter
{
    public static string ToCsv(IReadOnlyList<ResultColumn> columns, IEnumerable<object?[]> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", columns.Select(c => CsvField(c.Name))));
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(",", row.Select(v => CsvField(v is null or DBNull ? "" : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)))));
        }

        return sb.ToString();
    }

    private static string CsvField(string? value)
    {
        value ??= "";
        var needsQuoting = value.IndexOfAny([',', '"', '\n', '\r']) >= 0;
        return needsQuoting ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }

    public static string ToJson(IReadOnlyList<ResultColumn> columns, IEnumerable<object?[]> rows)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartArray();
            foreach (var row in rows)
            {
                writer.WriteStartObject();
                for (var i = 0; i < columns.Count; i++)
                {
                    writer.WritePropertyName(columns[i].Name);
                    WriteValue(writer, i < row.Length ? row[i] : null);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null or DBNull:
                writer.WriteNullValue();
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                writer.WriteRawValue(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!, skipInputValidation: true);
                break;
            case float or double or decimal:
                writer.WriteRawValue(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!, skipInputValidation: true);
                break;
            default:
                writer.WriteStringValue(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
                break;
        }
    }

    /// <summary>
    /// One <c>INSERT</c> statement per row with literal (not parameterised) values, meant to be
    /// copy-pasted/run elsewhere — unlike <c>CrudStatementBuilder</c>'s parameterised INSERT built for
    /// direct execution. Every column is included (no writable/PK filtering), values NULL-safe.
    /// </summary>
    /// <param name="qualifiedTable">Already dialect-quoted (and schema-qualified where applicable) table
    /// reference — the caller resolves qualification the same way <c>CrudStatementBuilder.QualifiedName</c>
    /// does, since this exporter has no notion of schema.</param>
    public static string ToSqlInserts(
        IReadOnlyList<ResultColumn> columns,
        IEnumerable<object?[]> rows,
        ISqlDialect dialect,
        string providerId,
        string qualifiedTable)
    {
        var columnList = string.Join(", ", columns.Select(c => dialect.QuoteIdentifier(c.Name)));

        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            var values = string.Join(", ", row.Select(v => SqlLiteralFormatter.Format(v, providerId)));
            sb.AppendLine($"INSERT INTO {qualifiedTable} ({columnList}) VALUES ({values});");
        }

        return sb.ToString();
    }
}
