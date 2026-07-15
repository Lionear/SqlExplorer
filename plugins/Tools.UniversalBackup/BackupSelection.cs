using System.Text.Json;

namespace SqlExplorer.Tools.UniversalBackup;

/// <summary>
/// One chosen object in the backup/restore selection. <see cref="Kind"/> is "table" or a
/// <see cref="LbakObjectKind"/> name (lowercased). <see cref="IncludeData"/> is only meaningful for tables.
/// Serialized as a JSON array through <c>IToolUiContext</c> under the "selection" key.
/// </summary>
public sealed record SelectionEntry(string Kind, string Schema, string Name, bool IncludeSchema, bool IncludeData);

/// <summary>Serialize/parse the selection the Route-B view gathers, plus the filter helpers the tool uses so
/// the (testable) selection logic lives outside the Avalonia view.</summary>
public static class BackupSelection
{
    public const string TableKind = "table";

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string Serialize(IReadOnlyList<SelectionEntry> entries) => JsonSerializer.Serialize(entries, Options);

    /// <summary>Parse a selection string; null/blank/malformed → null, meaning "no selection captured"
    /// (the tool then falls back to backing everything up).</summary>
    public static IReadOnlyList<SelectionEntry>? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<SelectionEntry>>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>How a selected table should be backed up: null = not selected (skip); otherwise whether to
    /// include its row data. A table entry with neither schema nor data is treated as not selected.</summary>
    public static bool? TableDataChoice(IReadOnlyList<SelectionEntry>? selection, string? schema, string name)
    {
        if (selection is null)
        {
            return true; // no selection → include everything, with data (current default behaviour)
        }

        var entry = selection.FirstOrDefault(e =>
            e.Kind == TableKind
            && string.Equals(e.Name, name, StringComparison.Ordinal)
            && string.Equals(e.Schema, schema ?? string.Empty, StringComparison.Ordinal));

        if (entry is null || (!entry.IncludeSchema && !entry.IncludeData))
        {
            return null; // not selected
        }

        return entry.IncludeData;
    }

    /// <summary>Whether a non-table object is selected (schema-only; there is no data half).</summary>
    public static bool IsObjectSelected(IReadOnlyList<SelectionEntry>? selection, LbakObjectKind kind, string schema, string name)
    {
        if (selection is null)
        {
            return true; // no selection → include all objects
        }

        var kindName = kind.ToString().ToLowerInvariant();
        return selection.Any(e =>
            e.IncludeSchema
            && e.Kind == kindName
            && string.Equals(e.Name, name, StringComparison.Ordinal)
            && string.Equals(e.Schema, schema, StringComparison.Ordinal));
    }
}
