using Lionear.SqlExplorer.Core.Connections;
using Lionear.SqlExplorer.Core.Schema;

namespace Lionear.SqlExplorer.App.ViewModels;

/// <summary>One quick-open hit: a table/view, matched either by its own name or one of its columns.</summary>
public sealed class SchemaSearchResult(SavedConnection connection, SchemaObject schemaObject, string? matchedColumn) : IQuickOpenItem
{
    public SavedConnection Connection { get; } = connection;

    public string? Database => schemaObject.Database;

    public string? Schema => schemaObject.Schema;

    public string Name => schemaObject.Name;

    public string Display => schemaObject.QualifiedName;

    public string Subtitle => matchedColumn is { } column
        ? $"{Connection.Name} · {column}"
        : Connection.Name;
}
