using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Lionear.SqlExplorer.App.DependencyInjection;
using Lionear.SqlExplorer.App.ViewModels;
using Lionear.SqlExplorer.App.Views;
using Lionear.SqlExplorer.Core.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Lionear.SqlExplorer.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var services = AppServices.Build();
        var viewModel = services.GetRequiredService<MainViewModel>();

        switch (ApplicationLifetime)
        {
            case IClassicDesktopStyleApplicationLifetime desktop:
                var settingsStore = services.GetRequiredService<IAppSettingsStore>();
                desktop.MainWindow = new MainWindow(settingsStore) { DataContext = viewModel };
                break;
            case ISingleViewApplicationLifetime singleView:
                singleView.MainView = new MainView { DataContext = viewModel };
                break;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
