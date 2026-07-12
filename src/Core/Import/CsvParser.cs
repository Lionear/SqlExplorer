namespace Lionear.SqlExplorer.Core.Import;

/// <summary>A parsed CSV document: the header row plus every data row, same column count as headers.</summary>
public sealed record CsvDocument(IReadOnlyList<string> Headers, IReadOnlyList<string?[]> Rows);

/// <summary>
/// Minimal RFC4180-ish CSV reader: quoted fields (doubled <c>""</c> escapes an embedded quote,
/// commas/newlines inside quotes are literal). No dialect options (delimiter is always <c>,</c>) —
/// this only needs to round-trip what <see cref="Lionear.SqlExplorer.Core.Export.ResultExporter.ToCsv"/>
/// produces plus typical spreadsheet-exported files.
/// </summary>
public static class CsvParser
{
    public static CsvDocument Parse(string text)
    {
        var records = ParseRecords(text);
        if (records.Count == 0)
        {
            return new CsvDocument([], []);
        }

        var headers = records[0].Select(h => h ?? string.Empty).ToList();
        var rows = records.Skip(1)
            .Select(r => PadOrTrim(r, headers.Count))
            .ToList();

        return new CsvDocument(headers, rows);
    }

    private static string?[] PadOrTrim(List<string?> row, int width)
    {
        var result = new string?[width];
        for (var i = 0; i < width; i++)
        {
            result[i] = i < row.Count ? row[i] : null;
        }

        return result;
    }

    private static List<List<string?>> ParseRecords(string text)
    {
        var records = new List<List<string?>>();
        var current = new List<string?>();
        var field = new System.Text.StringBuilder();
        var inQuotes = false;
        var i = 0;
        var fieldStarted = false;

        while (i < text.Length)
        {
            var c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i += 2;
                        continue;
                    }

                    inQuotes = false;
                    i++;
                    continue;
                }

                field.Append(c);
                i++;
                continue;
            }

            switch (c)
            {
                case '"' when field.Length == 0 && !fieldStarted:
                    inQuotes = true;
                    fieldStarted = true;
                    i++;
                    break;
                case ',':
                    current.Add(field.ToString());
                    field.Clear();
                    fieldStarted = false;
                    i++;
                    break;
                case '\r':
                    i++;
                    break;
                case '\n':
                    current.Add(field.ToString());
                    field.Clear();
                    fieldStarted = false;
                    if (current.Count > 1 || !string.IsNullOrEmpty(current[0]))
                    {
                        records.Add(current);
                    }

                    current = [];
                    i++;
                    break;
                default:
                    field.Append(c);
                    fieldStarted = true;
                    i++;
                    break;
            }
        }

        if (field.Length > 0 || current.Count > 0)
        {
            current.Add(field.ToString());
            if (current.Count > 1 || !string.IsNullOrEmpty(current[0]))
            {
                records.Add(current);
            }
        }

        return records;
    }
}
