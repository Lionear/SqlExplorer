using Avalonia.Controls;
using Lionear.SqlExplorer.App.ViewModels;

namespace Lionear.SqlExplorer.App.Views;

public partial class NodeInfoDialog : Window
{
    public NodeInfoDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is NodeInfoDialogViewModel vm)
            {
                vm.CloseRequested = Close;
            }
        };
    }
}
