using System.Collections.ObjectModel;
using System.Windows.Input;

namespace Lionear.SqlExplorer.App.ViewModels;

/// <summary>
/// One node in the tools context-menu tree. A leaf carries the command that runs a tool
/// (<see cref="Run"/> non-null, no <see cref="Children"/>); a group is a submenu label
/// (<see cref="Run"/> null, one or more <see cref="Children"/>). Built from each tool's
/// <c>IToolPlugin.MenuPath</c>, so tools sharing a path — even across plugins — nest together.
/// </summary>
public sealed class ToolMenuNode
{
    public ToolMenuNode(string title, ICommand? run)
    {
        Title = title;
        Run = run;
    }

    public string Title { get; }

    /// <summary>The run command for a leaf; null for a submenu group.</summary>
    public ICommand? Run { get; }

    public ObservableCollection<ToolMenuNode> Children { get; } = [];
}
