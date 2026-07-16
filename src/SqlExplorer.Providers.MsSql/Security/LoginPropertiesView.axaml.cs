using Avalonia.Controls;
using Avalonia.Interactivity;
using SqlExplorer.Sdk.Ui;

namespace SqlExplorer.Providers.MsSql.Security;

/// <summary>
/// Compiled-AXAML Route-B view for SQL Server logins (the first plugin view shipped as XAML rather than
/// hand-built controls). Self-contained: the view model reads databases and runs the DDL itself; the view
/// just wires the buttons and closes its host window on success.
/// </summary>
public partial class LoginPropertiesView : UserControl
{
    private LoginPropertiesViewModel? _viewModel;

    // Parameterless ctor required by the Avalonia XAML compiler; the real entry point is the context one.
    public LoginPropertiesView()
    {
        InitializeComponent();
        ApplyButton.Click += OnApply;
        CancelButton.Click += OnCancel;
    }

    public LoginPropertiesView(SecurityUiContext context) : this()
    {
        _viewModel = new LoginPropertiesViewModel(context);
        DataContext = _viewModel;
        _ = _viewModel.InitializeAsync();
    }

    private async void OnApply(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null && await _viewModel.ApplyAsync())
        {
            CloseWindow();
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => CloseWindow();

    private void CloseWindow()
    {
        if (TopLevel.GetTopLevel(this) is Window window)
        {
            window.Close();
        }
    }
}
