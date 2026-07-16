using Avalonia.Controls;
using SqlExplorer.App.ViewModels;

namespace SqlExplorer.App.Views;

// Hosts a provider-owned security view (ICustomSecurityUi) full-bleed — the view brings its own action
// buttons, so unlike NodeInfoDialog there is no host Close bar. Reuses NodeInfoDialogViewModel (title + view).
public partial class SecurityDialog : Window
{
    public SecurityDialog()
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
