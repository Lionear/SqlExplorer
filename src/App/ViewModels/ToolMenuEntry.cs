using System.Windows.Input;

namespace Lionear.SqlExplorer.App.ViewModels;

/// <summary>One applicable-tool entry in the sidebar context menu: its title and the command that runs it.</summary>
public sealed record ToolMenuEntry(string Title, ICommand Run);
