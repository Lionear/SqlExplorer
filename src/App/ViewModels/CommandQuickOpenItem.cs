namespace Lionear.SqlExplorer.App.ViewModels;

/// <summary>A Ctrl+K hit that runs an app command instead of opening a schema object.</summary>
public sealed class CommandQuickOpenItem(string display, string subtitle, Action execute) : IQuickOpenItem
{
    public string Display { get; } = display;

    public string Subtitle { get; } = subtitle;

    public void Execute() => execute();
}
