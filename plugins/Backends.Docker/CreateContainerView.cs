using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace SqlExplorer.Backends.Docker;

/// <summary>
/// The "New Local Container" dialog content the plugin builds and the host shows modally (SE-164 menu seam).
/// A plain code-built form (no XAML, no hardcoded colours → theme-safe): pick an engine, tweak name/tag/port/
/// credentials, and it runs <see cref="ContainerService.CreateAndRunAsync"/> with live progress. On success it
/// hands off to <paramref name="onCreated"/> (the plugin's reconcile, which links a host connection) and lets
/// the user close. The connection link is done there, not here, so a restart never double-links.
/// </summary>
internal static class CreateContainerView
{
    public static Control Build(
        DockerComposeBuilder builder, ContainerService service, Action onCreated, Action<string> log)
    {
        var engineBox = new ComboBox { ItemsSource = builder.SupportedProviderIds, HorizontalAlignment = HorizontalAlignment.Stretch };
        var nameBox = new TextBox();
        var tagBox = new TextBox();
        var portBox = new TextBox();
        var databaseBox = new TextBox { PlaceholderText = "(optional)" };
        var userBox = new TextBox();
        var passBox = new TextBox();
        var status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = 0.85 };

        void ApplyEngineDefaults(string providerId)
        {
            nameBox.Text = $"{providerId}-local";
            tagBox.Text = builder.DefaultTag(providerId) ?? "latest";
            portBox.Text = builder.ContainerPort(providerId)?.ToString(CultureInfo.InvariantCulture) ?? "";
            userBox.Text = builder.DefaultUser(providerId) ?? "";
            passBox.Text = builder.DefaultPassword(providerId) ?? "";
        }

        engineBox.SelectionChanged += (_, _) =>
        {
            if (engineBox.SelectedItem is string providerId)
            {
                ApplyEngineDefaults(providerId);
            }
        };
        engineBox.SelectedIndex = 0;

        var createButton = new Button { Content = "Create", IsDefault = true };
        var closeButton = new Button { Content = "Close" };

        var grid = new Grid
        {
            Margin = new Thickness(16),
            ColumnDefinitions = new ColumnDefinitions("Auto,12,260"),
            RowDefinitions = new RowDefinitions("Auto,8,Auto,8,Auto,8,Auto,8,Auto,8,Auto,8,Auto,14,Auto,10,Auto")
        };

        void Row(int row, string label, Control input)
        {
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(text, row);
            Grid.SetColumn(text, 0);
            Grid.SetRow(input, row);
            Grid.SetColumn(input, 2);
            grid.Children.Add(text);
            grid.Children.Add(input);
        }

        Row(0, "Engine", engineBox);
        Row(2, "Name", nameBox);
        Row(4, "Tag", tagBox);
        Row(6, "Host port", portBox);
        Row(8, "Database", databaseBox);
        Row(10, "Username", userBox);
        Row(12, "Password", passBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { createButton, closeButton }
        };
        Grid.SetRow(buttons, 14);
        Grid.SetColumnSpan(buttons, 3);
        grid.Children.Add(buttons);

        Grid.SetRow(status, 16);
        Grid.SetColumnSpan(status, 3);
        grid.Children.Add(status);

        closeButton.Click += (_, _) => (TopLevel.GetTopLevel(grid) as Window)?.Close();

        createButton.Click += async (_, _) =>
        {
            if (engineBox.SelectedItem is not string providerId)
            {
                return;
            }

            var name = (nameBox.Text ?? "").Trim();
            if (name.Length == 0)
            {
                status.Text = "A container name is required.";
                return;
            }

            if (!int.TryParse((portBox.Text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) || port <= 0)
            {
                status.Text = "Host port must be a positive number.";
                return;
            }

            var database = (databaseBox.Text ?? "").Trim();
            var request = new CreateContainerRequest(
                providerId,
                new Dictionary<string, string?> { ["username"] = userBox.Text, ["password"] = passBox.Text },
                ContainerName: name,
                HostPort: port,
                Tag: string.IsNullOrWhiteSpace(tagBox.Text) ? (builder.DefaultTag(providerId) ?? "latest") : tagBox.Text!.Trim(),
                Database: database.Length == 0 ? null : database);

            createButton.IsEnabled = false;
            try
            {
                // Progress<T> captures this (UI) sync context, so Report marshals back to the UI thread.
                var progress = new Progress<string>(msg => status.Text = msg);
                await service.CreateAndRunAsync(request, progress, CancellationToken.None);

                onCreated();
                log($"Local Containers: created container '{name}'.");
                status.Text = $"Created '{name}'. It's in your connection list under Local Containers.";
                createButton.Content = "Create another";
                createButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                status.Text = ex.Message;
                createButton.IsEnabled = true;
            }
        };

        return new ScrollViewer { Content = grid, MaxHeight = 560 };
    }
}
