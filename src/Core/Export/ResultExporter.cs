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

    /// <summary>Tab-separated values for the clipboard (the everyday "Copy" — pastes cleanly into a
    /// spreadsheet). Tabs/newlines inside a cell are flattened to spaces so the grid stays intact.</summary>
    public static string ToTsv(IReadOnlyList<ResultColumn> columns, IEnumerable<object?[]> rows, bool includeHeaders)
    {
        var sb = new StringBuilder();
        if (includeHeaders)
        {
            sb.AppendLine(string.Join('\t', columns.Select(c => TsvField(c.Name))));
        }

        foreach (var row in rows)
        {
            sb.AppendLine(string.Join('\t', row.Select(v =>
                TsvField(v is null or DBNull ? "" : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)))));
        }

        return sb.ToString().TrimEnd('\r', '\n');
    }

    private static string TsvField(string? value) =>
        (value ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

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

    /// <summary>GitHub-flavoured Markdown table — meant for pasting into a PR/issue/chat, not for
    /// round-tripping (no escaping beyond making the source stay a single Markdown row).</summary>
    public static string ToMarkdown(IReadOnlyList<ResultColumn> columns, IEnumerable<object?[]> rows)
    {
        var sb = new StringBuilder();
        sb.Append('|').Append(string.Join('|', columns.Select(c => $" {MarkdownCell(c.Name)} "))).AppendLine("|");
        sb.Append('|').Append(string.Join('|', columns.Select(_ => "---"))).AppendLine("|");
        foreach (var row in rows)
        {
            sb.Append('|').Append(string.Join('|', row.Select(v => $" {MarkdownCell(FormatMarkdownValue(v))} "))).AppendLine("|");
        }

        return sb.ToString();
    }

    private static string FormatMarkdownValue(object? value) =>
        value is null or DBNull ? "" : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";

    /// <summary>An HTML <c>&lt;table&gt;</c> for pasting into rich-text targets (email, docs, chat). Column
    /// names are the header row; every cell is HTML-escaped.</summary>
    public static string ToHtml(IReadOnlyList<ResultColumn> columns, IEnumerable<object?[]> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<table>");
        sb.Append("  <thead><tr>");
        foreach (var c in columns)
        {
            sb.Append("<th>").Append(HtmlEscape(c.Name)).Append("</th>");
        }

        sb.AppendLine("</tr></thead>");
        sb.AppendLine("  <tbody>");
        foreach (var row in rows)
        {
            sb.Append("    <tr>");
            for (var i = 0; i < columns.Count; i++)
            {
                var value = i < row.Length ? row[i] : null;
                sb.Append("<td>").Append(HtmlEscape(value is null or DBNull
                    ? "" : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "")).Append("</td>");
            }

            sb.AppendLine("</tr>");
        }

        sb.AppendLine("  </tbody>");
        sb.AppendLine("</table>");
        return sb.ToString();
    }

    private static string HtmlEscape(string value) => value
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    // A table row is one line of Markdown source — an embedded "|" would end the cell early, and a
    // newline would end the row early, so both get replaced rather than escaped.
    private static string MarkdownCell(string value) =>
        value.Replace("|", "\\|").Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');

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
