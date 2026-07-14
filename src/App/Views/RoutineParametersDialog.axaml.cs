using Avalonia.Controls;
using Lionear.SqlExplorer.App.ViewModels;

namespace Lionear.SqlExplorer.App.Views;

public partial class RoutineParametersDialog : Window
{
    public RoutineParametersDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is RoutineParametersDialogViewModel vm)
            {
                vm.CloseRequested = Close;
            }
        };
    }
}
