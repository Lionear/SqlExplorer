using Avalonia.Controls;
using Lionear.SqlExplorer.Core.Localization;
using CommunityToolkit.Mvvm.Input;

namespace Lionear.SqlExplorer.App.ViewModels;

/// <summary>
/// Backs the lightweight node-info dialog: chrome (title + Close) around a provider-supplied read-only
/// view (<c>ICustomNodeInfoUi.CreateInfoView</c>). Unlike <see cref="ToolDialogViewModel"/> there is no
/// Execute/progress/log — this is purely informational (e.g. SQL Server's Database Properties). Plain VM,
/// constructed directly per open (no DI dependencies beyond the shared localizer).
/// </summary>
public partial class NodeInfoDialogViewModel : ViewModelBase
{
    public NodeInfoDialogViewModel(string title, Control view, ILocalizer localizer)
    {
        Title = title;
        View = view;
        Loc = localizer;
    }

    public string Title { get; }

    public Control View { get; }

    public ILocalizer Loc { get; }

    /// <summary>Set by the view; called to close the window.</summary>
    public Action? CloseRequested { get; set; }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();
}
