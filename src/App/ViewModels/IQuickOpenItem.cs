namespace Lionear.SqlExplorer.App.ViewModels;

/// <summary>A row in the Ctrl+K overlay — either a schema object or a command.</summary>
public interface IQuickOpenItem
{
    string Display { get; }

    string Subtitle { get; }
}
