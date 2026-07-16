namespace SqlExplorer.Sdk.Query;

/// <summary>
/// Which convenience query the host is asking a provider to generate for a table/collection node — the
/// tree's "SQL commands" submenu and "Select top 1000" action. A provider returns its own dialect (or
/// non-SQL) text from <see cref="IDbProvider.BuildNodeQuery"/>, or null to let the host generate SQL.
/// </summary>
public enum NodeQueryKind
{
    /// <summary>All rows/documents (the "SELECT *" scaffold).</summary>
    SelectAll,

    /// <summary>The first N rows/documents, run immediately ("Select top 1000").</summary>
    SelectTop,

    /// <summary>A row/document count ("COUNT(*)").</summary>
    Count,

    /// <summary>A SELECT naming each column ("SELECT columns").</summary>
    SelectColumns,

    /// <summary>An INSERT scaffold.</summary>
    Insert,

    /// <summary>An UPDATE scaffold.</summary>
    Update,

    /// <summary>A DELETE scaffold.</summary>
    Delete
}
