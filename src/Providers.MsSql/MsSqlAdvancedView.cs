using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Lionear.SqlExplorer.Sdk.Ui;

namespace Lionear.SqlExplorer.Providers.MsSql;

/// <summary>
/// Route B demonstrator (Notes §4.4): SQL Server renders its own Advanced-connection view instead of
/// the host-generated form. It reads and writes the provider's declared field values through
/// <see cref="IConnectionUiContext"/>, so save/import/BuildConnectionString are untouched — this is
/// purely an alternative renderer. Built in code (no XAML) so the plugin stays self-contained, and
/// hosted by the host across the plugin ALC boundary via the shared Avalonia types.
/// </summary>
public sealed class MsSqlAdvancedView : UserControl
{
    public MsSqlAdvancedView(IConnectionUiContext context)
    {
        var encrypt = new ComboBox
        {
            ItemsSource = new[] { "Optional", "Mandatory", "Strict" },
            SelectedItem = context.GetValue("encrypt") ?? "Optional",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        encrypt.SelectionChanged += (_, _) => context.SetValue("encrypt", encrypt.SelectedItem as string);

        var trust = new CheckBox
        {
            Content = "Trust server certificate",
            IsChecked = context.GetValue("trustServerCertificate") is not "false"
        };
        trust.IsCheckedChanged += (_, _) =>
            context.SetValue("trustServerCertificate", trust.IsChecked == true ? "true" : "false");

        var appName = new TextBox { Text = context.GetValue("applicationName") };
        appName.TextChanged += (_, _) => context.SetValue("applicationName", appName.Text);

        var timeout = new TextBox { Text = context.GetValue("connectTimeout"), PlaceholderText = "15" };
        timeout.TextChanged += (_, _) => context.SetValue("connectTimeout", timeout.Text);

        // Command timeout: default "0" (no timeout) when the field was never set on this connection.
        var commandTimeout = new TextBox { Text = context.GetValue("commandTimeout") ?? "0", PlaceholderText = "0" };
        commandTimeout.TextChanged += (_, _) => context.SetValue("commandTimeout", commandTimeout.Text);

        var mars = new CheckBox
        {
            Content = "Multiple active result sets (MARS)",
            IsChecked = context.GetValue("multipleActiveResultSets") is "true"
        };
        mars.IsCheckedChanged += (_, _) =>
            context.SetValue("multipleActiveResultSets", mars.IsChecked == true ? "true" : "false");

        var pooling = new CheckBox
        {
            Content = "Connection pooling",
            // Default on (only an explicit "false" unchecks it), mirroring the Trust-certificate field.
            IsChecked = context.GetValue("pooling") is not "false"
        };
        pooling.IsCheckedChanged += (_, _) =>
            context.SetValue("pooling", pooling.IsChecked == true ? "true" : "false");

        Content = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "SSL / TLS", FontWeight = Avalonia.Media.FontWeight.SemiBold },
                new TextBlock { Text = "Encrypt", Opacity = 0.7 },
                encrypt,
                trust,
                new TextBlock { Text = "Connection", FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) },
                new TextBlock { Text = "Application name", Opacity = 0.7 },
                appName,
                new TextBlock { Text = "Connect timeout (s)", Opacity = 0.7 },
                timeout,
                new TextBlock { Text = "Command timeout (s) — 0 = no timeout", Opacity = 0.7 },
                commandTimeout,
                mars,
                pooling
            }
        };
    }
}
