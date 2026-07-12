using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Lionear.SqlExplorer.App.ViewModels;
using Lionear.SqlExplorer.Core.Connections;

namespace Lionear.SqlExplorer.App.Views;

public partial class MainView : UserControl
{
    private MainViewModel? _viewModel;

    public MainView()
    {
        InitializeComponent();

        var schemaTree = this.FindControl<TreeView>("SchemaTree");
        if (schemaTree is not null)
        {
            schemaTree.DoubleTapped += OnTreeDoubleTapped;
        }

        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>Current width of the connection sidebar column, in pixels (for persistence).</summary>
    public double SidebarWidth => BodyGrid.ColumnDefinitions[0].Width.Value;

    /// <summary>Applies a persisted sidebar width; ignores null/non-positive values so the design default stands.</summary>
    public void RestoreSidebarWidth(double? width)
    {
        if (width is > 0)
        {
            BodyGrid.ColumnDefinitions[0].Width = new GridLength(width.Value);
        }
    }

    // Double-click: a connection root (re)connects; a table/view opens a browse tab.
    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel?.SelectedNode is not { } node)
        {
            return;
        }

        if (node.IsConnectionNode && _viewModel.ConnectCommand.CanExecute(null))
        {
            _viewModel.ConnectCommand.Execute(null);
        }
        else if (node.IsTableOrView && _viewModel.BrowseTableCommand.CanExecute(null))
        {
            _viewModel.BrowseTableCommand.Execute(null);
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _viewModel = DataContext as MainViewModel;
        if (_viewModel is not null)
        {
            _viewModel.ConnectionDialogRequested = ShowConnectionDialogAsync;
            _viewModel.ClipboardRequested = CopyToClipboardAsync;
        }
    }

    // The VM asks; the view owns the window, so it creates and shows the modal dialog.
    private async Task<SavedConnection?> ShowConnectionDialogAsync(ConnectionDialogViewModel dialogViewModel)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return null;
        }

        var dialog = new ConnectionDialog { DataContext = dialogViewModel };
        return await dialog.ShowDialog<SavedConnection?>(owner);
    }

    private async Task CopyToClipboardAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }
}
