using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace SqlExplorer.Backends.Docker;

/// <summary>
/// The "New local Docker instance" dialog the plugin builds and the host shows modally (SE-164 menu seam).
/// Two entry points: standalone (pick the <em>engine</em> from a dropdown — that's what you're creating) and
/// from a specific connection (right-click a connection → engine is fixed and the fields prefill from it).
/// Then either "Generate only" (a compose/run snippet, no Docker) or "Create &amp; run" (runs
/// <see cref="ContainerService.CreateAndRunAsync"/>). On success it calls <paramref name="onCreated"/> (the
/// plugin's reconcile, which links the host connection). Code-built, no hardcoded background colours → theme-safe.
/// </summary>
internal static class CreateContainerView
{
    public static Control Build(
        DockerComposeBuilder builder,
        ContainerService service,
        ManagedConnectionInfo? fromConnection,
        Action onCreated,
        Action<string> log)
    {
        var nameBox = new TextBox();
        var tagBox = new TextBox();
        var portBox = new TextBox();
        var databaseBox = new TextBox { PlaceholderText = "(optional)" };
        var userBox = new TextBox();
        var passBox = new TextBox();

        void ApplyEngineDefaults(string providerId)
        {
            nameBox.Text = $"{providerId}-local";
            tagBox.Text = builder.DefaultTag(providerId) ?? "latest";
            portBox.Text = builder.ContainerPort(providerId)?.ToString(CultureInfo.InvariantCulture) ?? "";
            databaseBox.Text = "";
            userBox.Text = builder.DefaultUser(providerId) ?? "";
            passBox.Text = builder.DefaultPassword(providerId) ?? "";
        }

        void PrefillFrom(ManagedConnectionInfo c)
        {
            nameBox.Text = $"{c.ProviderId}-local";
            tagBox.Text = builder.DefaultTag(c.ProviderId) ?? "latest";
            portBox.Text = Get(c.Values, "port") ?? builder.ContainerPort(c.ProviderId)?.ToString(CultureInfo.InvariantCulture) ?? "";
            databaseBox.Text = Get(c.Values, "database") ?? "";
            userBox.Text = Get(c.Values, "username") ?? builder.DefaultUser(c.ProviderId) ?? "";
            passBox.Text = builder.DefaultPassword(c.ProviderId) ?? "";
        }

        // Engine selector: a fixed label when launched from a connection, otherwise a dropdown of the
        // containerisable engines — because standalone you're choosing *what kind of* local database to create.
        Control engineControl;
        Func<string?> selectedProviderId;
        if (fromConnection is not null)
        {
            engineControl = new TextBlock { Text = fromConnection.ProviderId, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            selectedProviderId = () => fromConnection.ProviderId;
        }
        else
        {
            var engineBox = new ComboBox { ItemsSource = builder.SupportedProviderIds, HorizontalAlignment = HorizontalAlignment.Stretch };
            engineBox.SelectionChanged += (_, _) =>
            {
                if (engineBox.SelectedItem is string providerId)
                {
                    ApplyEngineDefaults(providerId);
                }
            };
            engineControl = engineBox;
            selectedProviderId = () => engineBox.SelectedItem as string;
        }

        // Format toggle (compose / run) for "Generate only".
        var composeToggle = new RadioButton { Content = "docker-compose", IsChecked = true, GroupName = "fmt" };
        var runToggle = new RadioButton { Content = "docker run", GroupName = "fmt" };
        var format = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { composeToggle, runToggle } };

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
        var output = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, IsVisible = false,
            FontFamily = new FontFamily("Cascadia Code,Consolas,Menlo,monospace"), FontSize = 11, MinHeight = 150
        };

        // Progress checklist shown during "Create & run" (the mockup's step list). Fixed status colours read
        // in both themes; the steps are driven off ContainerService's progress reports below.
        var doneBrush = new SolidColorBrush(Color.Parse("#3FB950"));
        var runBrush = new SolidColorBrush(Color.Parse("#2F6FEB"));
        var failBrush = new SolidColorBrush(Color.Parse("#C4362F"));
        var stepIcons = new List<TextBlock>();
        var steps = new StackPanel { Margin = new Thickness(16, 4, 16, 0), Spacing = 4, IsVisible = false };
        foreach (var label in new[] { "Write compose & start the container", "Wait until it's ready", "Add the host connection" })
        {
            var icon = new TextBlock { Text = "○", Width = 16, Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center };
            stepIcons.Add(icon);
            steps.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children = { icon, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center } }
            });
        }

        void SetStep(int i, string glyph, IBrush? brush, double opacity)
        {
            if (i < 0 || i >= stepIcons.Count) return;
            stepIcons[i].Text = glyph;
            stepIcons[i].Foreground = brush;
            stepIcons[i].Opacity = opacity;
        }
        void ResetSteps() { for (var i = 0; i < stepIcons.Count; i++) SetStep(i, "○", null, 0.5); }
        void StepRunning(int i) => SetStep(i, "●", runBrush, 1);
        void StepDone(int i) => SetStep(i, "✓", doneBrush, 1);
        void StepFailed(int i) => SetStep(i, "✕", failBrush, 1);

        var grid = new Grid
        {
            Margin = new Thickness(16, 14, 16, 8),
            ColumnDefinitions = new ColumnDefinitions("Auto,12,300"),
            RowDefinitions = new RowDefinitions("Auto,10,Auto,8,Auto,8,Auto,8,Auto,8,Auto,8,Auto,12,Auto,8,Auto")
        };

        void Row(int r, string label, Control input)
        {
            var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(t, r); Grid.SetColumn(t, 0);
            Grid.SetRow(input, r); Grid.SetColumn(input, 2);
            grid.Children.Add(t); grid.Children.Add(input);
        }

        Row(0, "Engine", engineControl);
        Row(2, "Container name", nameBox);
        Row(4, "Image tag", tagBox);
        Row(6, "Host port", portBox);
        Row(8, "Database", databaseBox);
        Row(10, "Username", userBox);
        Row(12, "Password", passBox);
        Row(14, "Output format", format);

        Grid.SetRow(output, 16); Grid.SetColumnSpan(output, 3);
        grid.Children.Add(output);

        var generateButton = new Button { Content = "Generate only" };
        var createButton = new Button { Content = "Create & run", IsDefault = true };
        var closeButton = new Button { Content = "Close" };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16, 0, 16, 4), Children = { generateButton, createButton, closeButton }
        };

        var body = new StackPanel { Children = { grid, buttons, steps, new Border { Margin = new Thickness(16, 8, 16, 12), Child = status } } };
        var root = new ScrollViewer { Content = body, MaxHeight = 640 };

        // Initial prefill.
        if (fromConnection is not null)
        {
            PrefillFrom(fromConnection);
        }
        else if (engineControl is ComboBox box && builder.SupportedProviderIds.Count > 0)
        {
            box.SelectedIndex = 0; // fires SelectionChanged → ApplyEngineDefaults
        }

        CreateContainerRequest? BuildRequest()
        {
            if (selectedProviderId() is not { } providerId)
            {
                status.Text = "Pick an engine first.";
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
                providerId,
                new Dictionary<string, string?> { ["username"] = userBox.Text, ["password"] = passBox.Text },
                ContainerName: name,
                HostPort: port,
                Tag: string.IsNullOrWhiteSpace(tagBox.Text) ? (builder.DefaultTag(providerId) ?? "latest") : tagBox.Text!.Trim(),
                Database: database.Length == 0 ? null : database);
        }

        generateButton.Click += (_, _) =>
        {
            if (BuildRequest() is not { } request)
            {
                return;
            }

            output.Text = service.BuildSnippet(request, runToggle.IsChecked == true ? SnippetFormat.Run : SnippetFormat.Compose);
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
            status.Text = "";
            ResetSteps();
            steps.IsVisible = true;
            createButton.IsEnabled = false;
            generateButton.IsEnabled = false;

            // Map ContainerService's coarse progress reports onto the checklist. `current` tracks the running
            // step so a failure marks the right one.
            var current = 0;
            try
            {
                StepRunning(0);
                var progress = new Progress<string>(msg =>
                {
                    if (msg.Contains("Waiting", StringComparison.OrdinalIgnoreCase)) { StepDone(0); StepRunning(1); current = 1; }
                    else if (msg.Contains("ready", StringComparison.OrdinalIgnoreCase)) { StepDone(1); current = 2; }
                    else if (msg.Contains("Starting", StringComparison.OrdinalIgnoreCase)) { StepRunning(0); current = 0; }
                });

                await service.CreateAndRunAsync(request, progress, CancellationToken.None);
                StepDone(0);
                StepDone(1);
                current = 2;
                StepRunning(2);
                onCreated();
                StepDone(2);

                log($"Local Containers: created container '{request.ContainerName}'.");
                status.Text = $"Created '{request.ContainerName}'. It's in your connection list under Local Containers.";
                createButton.Content = "Create another";
            }
            catch (Exception ex)
            {
                StepFailed(current);
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

    private static string? Get(IReadOnlyDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
}
