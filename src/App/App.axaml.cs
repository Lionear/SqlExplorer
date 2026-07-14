using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Lionear.SqlExplorer.App.DependencyInjection;
using Lionear.SqlExplorer.App.Theming;
using Lionear.SqlExplorer.App.ViewModels;
using Lionear.SqlExplorer.App.Views;
using Lionear.SqlExplorer.Core.Localization;
using Lionear.SqlExplorer.Core.Settings;
using Lionear.SqlExplorer.Core.Shortcuts;
using Microsoft.Extensions.DependencyInjection;

namespace Lionear.SqlExplorer.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var services = AppServices.Build();

        // Apply the saved theme/language before anything renders, so there's no flash of the
        // wrong theme or a Language toggle needed after every launch.
        var settingsStore = services.GetRequiredService<IAppSettingsStore>();
        var settings = settingsStore.Load();
        ThemeApplier.Apply(settings.Theme);
        if (settings.Language is { Length: > 0 } language)
        {
            services.GetRequiredService<ILocalizer>().SetCulture(CultureInfo.GetCultureInfo(language));
        }

        var viewModel = services.GetRequiredService<MainViewModel>();
        var keymap = services.GetRequiredService<KeymapService>();

        switch (ApplicationLifetime)
        {
            case IClassicDesktopStyleApplicationLifetime desktop:
                desktop.MainWindow = new MainWindow(settingsStore, keymap) { DataContext = viewModel };
                break;
            case ISingleViewApplicationLifetime singleView:
                singleView.MainView = new MainView { DataContext = viewModel };
                break;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
