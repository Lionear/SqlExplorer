using System.Globalization;

namespace Lionear.SqlExplorer.Core.Export;

/// <summary>
/// Renders a CLR value as a literal SQL token — for human-readable export (<see cref="ResultExporter"/>)
/// and the per-column browse filter, both of which need text embedded directly into a statement rather
/// than bound as a parameter (unlike <c>CrudStatementBuilder</c>, which always parameterises).
/// </summary>
public static class SqlLiteralFormatter
{
    /// <summary>
    /// Escapes <paramref name="text"/> for embedding inside a single-quoted SQL string literal
    /// (doubles any <c>'</c>) — the caller supplies the surrounding quotes.
    /// </summary>
    public static string EscapeString(string text) => text.Replace("'", "''");

    /// <summary>
    /// <paramref name="providerId"/> only matters for <see cref="bool"/>: SQL Server's <c>BIT</c> has
    /// no <c>TRUE</c>/<c>FALSE</c> literal, so it needs <c>1</c>/<c>0</c>; the other three engines
    /// accept the keyword form. Every other branch is universal.
    /// </summary>
    public static string Format(object? value, string providerId)
    {
        if (value is null or DBNull)
        {
            return "NULL";
        }

        return value switch
        {
            bool b => providerId == "sqlserver" ? (b ? "1" : "0") : (b ? "TRUE" : "FALSE"),
            byte or sbyte or short or ushort or int or uint or long or ulong =>
                Convert.ToString(value, CultureInfo.InvariantCulture)!,
            float or double or decimal =>
                Convert.ToString(value, CultureInfo.InvariantCulture)!,
            DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss.fffffff}'",
            DateTimeOffset dto => $"'{dto:yyyy-MM-dd HH:mm:ss.fffffffzzz}'",
            Guid guid => $"'{guid}'",
            string s => $"'{EscapeString(s)}'",
            _ => $"'{EscapeString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)}'"
        };
    }
}
