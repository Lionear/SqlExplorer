using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace SqlExplorer.Backends.Docker;

/// <summary>
/// The "New local Docker instance" dialog the plugin builds and the host shows modally (SE-164 menu seam),
/// following the approved mockup. Driven by an existing connection: pick one (containerisable engines only),
/// the engine + fields prefill from it, then either "Generate only" (a compose/run snippet, no Docker) or
/// "Create &amp; run" (runs <see cref="ContainerService.CreateAndRunAsync"/> with a live step checklist). On
/// success it calls <paramref name="onCreated"/> (the plugin's reconcile, which links the host connection).
/// Code-built, no hardcoded background colours → theme-safe.
/// </summary>
internal static class CreateContainerView
{
    public static Control Build(
        DockerComposeBuilder builder,
        ContainerService service,
        IReadOnlyList<ManagedConnectionInfo> connections,
        ManagedConnectionInfo? preselected,
        Action onCreated,
        Action<string> log)
    {
        var containerisable = connections.Where(c => builder.Supports(c.ProviderId)).ToList();

        var engineLabel = new TextBlock { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var nameBox = new TextBox();
        var tagBox = new TextBox();
        var portBox = new TextBox();
        var databaseBox = new TextBox { PlaceholderText = "(optional)" };
        var userBox = new TextBox();
        var passBox = new TextBox();

        var connectionBox = new ComboBox
        {
            ItemsSource = containerisable,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = new FuncDataTemplate<ManagedConnectionInfo>((c, _) =>
                new TextBlock { Text = c is null ? "" : $"{c.Name}   ·   {c.ProviderId}" })
        };

        void PrefillFrom(ManagedConnectionInfo c)
        {
            engineLabel.Text = $"{c.ProviderId}";
            nameBox.Text = $"{c.ProviderId}-local";
            tagBox.Text = builder.DefaultTag(c.ProviderId) ?? "latest";
            portBox.Text = Get(c.Values, "port") ?? builder.ContainerPort(c.ProviderId)?.ToString(CultureInfo.InvariantCulture) ?? "";
            databaseBox.Text = Get(c.Values, "database") ?? "";
            userBox.Text = Get(c.Values, "username") ?? builder.DefaultUser(c.ProviderId) ?? "";
            passBox.Text = builder.DefaultPassword(c.ProviderId) ?? "";
        }

        connectionBox.SelectionChanged += (_, _) =>
        {
            if (connectionBox.SelectedItem is ManagedConnectionInfo c)
            {
                PrefillFrom(c);
            }
        };

        // Format toggle (compose / run) for "Generate only".
        var composeToggle = new RadioButton { Content = "docker-compose", IsChecked = true, GroupName = "fmt" };
        var runToggle = new RadioButton { Content = "docker run", GroupName = "fmt" };
        var format = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { composeToggle, runToggle } };

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.85, Margin = new Thickness(0, 4, 0, 0) };
        var output = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, IsVisible = false,
            FontFamily = new FontFamily("Cascadia Code,Consolas,Menlo,monospace"), FontSize = 11, MinHeight = 150
        };

        var grid = new Grid
        {
            Margin = new Thickness(16, 14, 16, 8),
            ColumnDefinitions = new ColumnDefinitions("Auto,12,300"),
            RowDefinitions = new RowDefinitions("Auto,10,Auto,8,Auto,8,Auto,8,Auto,8,Auto,8,Auto,8,Auto,12,Auto,8,Auto")
        };

        void Row(int r, string label, Control input)
        {
            var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(t, r); Grid.SetColumn(t, 0);
            Grid.SetRow(input, r); Grid.SetColumn(input, 2);
            grid.Children.Add(t); grid.Children.Add(input);
        }

        Row(0, "From connection", connectionBox);
        Row(2, "Engine", engineLabel);
        Row(4, "Container name", nameBox);
        Row(6, "Image tag", tagBox);
        Row(8, "Host port", portBox);
        Row(10, "Database", databaseBox);
        Row(12, "Username", userBox);
        Row(14, "Password", passBox);
        Row(16, "Output format", format);

        Grid.SetRow(output, 18); Grid.SetColumnSpan(output, 3);
        grid.Children.Add(output);

        var generateButton = new Button { Content = "Generate only" };
        var createButton = new Button { Content = "Create & run", IsDefault = true };
        var closeButton = new Button { Content = "Close" };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16, 0, 16, 4), Children = { generateButton, createButton, closeButton }
        };

        var body = new StackPanel { Children = { grid, buttons, WrapStatus(status) } };
        var root = new ScrollViewer { Content = body, MaxHeight = 640 };

        // Preselect a connection (from a tree action) or default to the first one.
        if (preselected is not null && containerisable.Any(c => c.Id == preselected.Id))
        {
            connectionBox.SelectedItem = containerisable.First(c => c.Id == preselected.Id);
        }
        else if (containerisable.Count > 0)
        {
            connectionBox.SelectedIndex = 0;
        }
        else
        {
            engineLabel.Text = "—";
            status.Text = "No containerisable connections yet — add a Postgres/MySQL/… connection first.";
            createButton.IsEnabled = false;
            generateButton.IsEnabled = false;
        }

        CreateContainerRequest? BuildRequest()
        {
            if (connectionBox.SelectedItem is not ManagedConnectionInfo c)
            {
                status.Text = "Pick a connection first.";
                return null;
            }

            var name = (nameBox.Text ?? "").Trim();
            if (name.Length == 0) { status.Text = "A container name is required."; return null; }
            if (!int.TryParse((portBox.Text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) || port <= 0)
            {
                status.Text = "Host port must be a positive number."; return null;
            }

            var database = (databaseBox.Text ?? "").Trim();
            return new CreateContainerRequest(
                c.ProviderId,
                new Dictionary<string, string?> { ["username"] = userBox.Text, ["password"] = passBox.Text },
                ContainerName: name,
                HostPort: port,
                Tag: string.IsNullOrWhiteSpace(tagBox.Text) ? (builder.DefaultTag(c.ProviderId) ?? "latest") : tagBox.Text!.Trim(),
                Database: database.Length == 0 ? null : database);
        }

        generateButton.Click += (_, _) =>
        {
            if (BuildRequest() is not { } request)
            {
                return;
            }

            var fmt = runToggle.IsChecked == true ? SnippetFormat.Run : SnippetFormat.Compose;
            output.Text = service.BuildSnippet(request, fmt);
            output.IsVisible = true;
            status.Text = "Generated — copy it into a terminal or a docker-compose.yaml.";
        };

        createButton.Click += async (_, _) =>
        {
            if (BuildRequest() is not { } request)
            {
                return;
            }

            output.IsVisible = false;
            createButton.IsEnabled = false;
            generateButton.IsEnabled = false;
            try
            {
                var progress = new Progress<string>(msg => status.Text = msg);
                await service.CreateAndRunAsync(request, progress, CancellationToken.None);
                onCreated();
                log($"Local Containers: created container '{request.ContainerName}'.");
                status.Text = $"Created '{request.ContainerName}'. It's in your connection list under Local Containers.";
                createButton.Content = "Create another";
            }
            catch (Exception ex)
            {
                status.Text = ex.Message;
            }
            finally
            {
                createButton.IsEnabled = true;
                generateButton.IsEnabled = true;
            }
        };

        closeButton.Click += (_, _) => (TopLevel.GetTopLevel(root) as Window)?.Close();

        return root;
    }

    private static Control WrapStatus(TextBlock status) => new Border { Margin = new Thickness(16, 0, 16, 12), Child = status };

    private static string? Get(IReadOnlyDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
}
