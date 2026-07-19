using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace SqlExplorer.Backends.Docker;

/// <summary>
/// The Containers panel (SE-164 panel + background seams): a live table of the app-managed containers with
/// their run-state and per-row lifecycle actions (Start/Stop, Logs, Remove), plus a "New from connection…"
/// button and a graceful "Docker not found" state. Rebuilds on registry changes and on the background poll's
/// status pushes, always marshalled to the UI thread. Status dots use fixed status colours (readable in both
/// themes); everything else inherits the host theme.
/// </summary>
internal sealed class ContainersPanelView
{
    private static readonly IBrush RunBrush = new SolidColorBrush(Color.Parse("#3FB950"));
    private static readonly IBrush WarnBrush = new SolidColorBrush(Color.Parse("#B9791F"));
    private static readonly IBrush StopBrush = new SolidColorBrush(Color.Parse("#8A909A"));

    private readonly IContainerRegistryStore _registry;
    private readonly ContainerService _service;
    private readonly IHostUi _hostUi;
    private readonly Action<string> _log;
    private readonly Func<Task> _onNewFromConnection;

    private readonly TextBlock _count;
    private readonly Decorator _content = new();
    private IReadOnlyDictionary<string, ContainerStatus> _statuses = new Dictionary<string, ContainerStatus>();
    private bool _dockerAvailable = true;

    public ContainersPanelView(
        IContainerRegistryStore registry, ContainerService service, IHostUi hostUi,
        Action<string> log, Func<Task> onNewFromConnection)
    {
        _registry = registry;
        _service = service;
        _hostUi = hostUi;
        _log = log;
        _onNewFromConnection = onNewFromConnection;

        _count = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6, FontSize = 12, Margin = new Thickness(6, 0, 0, 0) };

        var newButton = new Button { Content = "+ New container…" };
        newButton.Click += async (_, _) => await _onNewFromConnection();
        var refreshButton = new Button { Content = "↻ Refresh", Margin = new Thickness(0, 0, 6, 0) };
        refreshButton.Click += async (_, _) => await RefreshAsync();

        var title = new TextBlock { Text = "Containers", FontWeight = FontWeight.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var bar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto"),
            Margin = new Thickness(10, 7, 8, 7)
        };
        Grid.SetColumn(title, 0);
        Grid.SetColumn(_count, 1);
        Grid.SetColumn(refreshButton, 3);
        Grid.SetColumn(newButton, 4);
        bar.Children.Add(title);
        bar.Children.Add(_count);
        bar.Children.Add(refreshButton);
        bar.Children.Add(newButton);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(bar, 0);
        Grid.SetRow(_content, 1);
        root.Children.Add(bar);
        root.Children.Add(_content);
        Root = root;

        registry.Changed += () => Post(Rebuild);
        Rebuild();
        _ = CheckDockerAsync();
    }

    public Control Root { get; }

    /// <summary>Push a fresh status map from the background poll; rebuilds the list on the UI thread.</summary>
    public void SetStatuses(IReadOnlyDictionary<string, ContainerStatus> statuses)
    {
        _statuses = statuses;
        Post(Rebuild);
    }

    private async Task CheckDockerAsync()
    {
        var available = await _service.IsDockerAvailableAsync(CancellationToken.None);
        Post(() => { _dockerAvailable = available; Rebuild(); });
    }

    private async Task RefreshAsync()
    {
        try
        {
            var available = await _service.IsDockerAvailableAsync(CancellationToken.None);
            var statuses = await ContainerMonitor.PollAsync(_registry, _service, CancellationToken.None);
            Post(() => { _dockerAvailable = available; _statuses = statuses; Rebuild(); });
        }
        catch (Exception ex)
        {
            _log($"Local Containers: refresh failed: {ex.Message}");
        }
    }

    private static void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    private void Rebuild()
    {
        var containers = _registry.GetAll();
        _count.Text = containers.Count == 1 ? "· 1 managed" : $"· {containers.Count} managed";

        if (!_dockerAvailable)
        {
            _content.Child = BuildDockerMissing();
            return;
        }

        if (containers.Count == 0)
        {
            _content.Child = new TextBlock
            {
                Text = "No managed containers yet. Use “New container…” to spin one up.",
                Opacity = 0.7,
                Margin = new Thickness(12)
            };
            return;
        }

        var table = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto"),
            Margin = new Thickness(10, 0, 10, 10)
        };
        table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        AddHeader(table, "Name", "Status", "Endpoint", "Connection", "");

        var row = 1;
        foreach (var container in containers)
        {
            table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            AddRow(table, row++, container);
        }

        _content.Child = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = table
        };
    }

    private static void AddHeader(Grid table, params string[] headers)
    {
        for (var col = 0; col < headers.Length; col++)
        {
            var cell = new TextBlock
            {
                Text = headers[col],
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Opacity = 0.6,
                Margin = new Thickness(0, 4, 12, 6)
            };
            Grid.SetRow(cell, 0);
            Grid.SetColumn(cell, col);
            table.Children.Add(cell);
        }
    }

    private void AddRow(Grid table, int row, ManagedContainer container)
    {
        var status = _statuses.TryGetValue(container.Id, out var s) ? s : ContainerStatus.Absent;

        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 3, 12, 3) };
        name.Children.Add(new TextBlock { Text = container.Name, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        name.Children.Add(new Border
        {
            Background = new SolidColorBrush(Colors.Gray, 0.14),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = $"{container.Image}:{container.Tag}", FontSize = 10.5, Opacity = 0.75, FontFamily = new FontFamily("Cascadia Code,Consolas,Menlo,monospace") }
        });
        Place(table, name, row, 0);

        Place(table, BuildStatus(status), row, 1);
        Place(table, Meta($"localhost:{container.HostPort}"), row, 2);
        Place(table, Meta($"Local Containers ▸ {container.Name}"), row, 3);
        Place(table, BuildActions(container, status), row, 4);
    }

    private Control BuildStatus(ContainerStatus status)
    {
        var (brush, label) = status.State switch
        {
            ContainerState.Running when status.Healthy == false => (WarnBrush, "Starting…"),
            ContainerState.Running => (RunBrush, "Running"),
            ContainerState.Restarting => (WarnBrush, "Restarting"),
            ContainerState.Paused => (StopBrush, "Paused"),
            ContainerState.Exited => (StopBrush, "Stopped"),
            ContainerState.Dead => (StopBrush, "Dead"),
            ContainerState.Created => (StopBrush, "Created"),
            _ => (StopBrush, "Not created")
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 12, 3) };
        panel.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = brush, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = label, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private Control BuildActions(ManagedContainer container, ContainerStatus status)
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 3, 0, 3) };

        var running = status.State == ContainerState.Running;
        var startStop = new Button { Content = running ? "Stop" : "Start", Padding = new Thickness(9, 3, 9, 3), FontSize = 11 };
        startStop.Click += async (_, _) => await RunActionAsync(
            running ? "stop" : "start", container.Name,
            ct => running ? _service.StopAsync(container.Id, ct) : _service.StartAsync(container.Id, ct));

        var logs = new Button { Content = "Logs", Padding = new Thickness(9, 3, 9, 3), FontSize = 11 };
        logs.Click += async (_, _) => await ShowLogsAsync(container);

        var remove = new Button { Content = "Remove", Padding = new Thickness(9, 3, 9, 3), FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#C4362F")) };
        remove.Click += async (_, _) =>
        {
            try
            {
                await _service.RemoveAsync(container.Id, removeVolumes: false, CancellationToken.None);
                _log($"Local Containers: removed '{container.Name}'.");
            }
            catch (Exception ex)
            {
                _log($"Local Containers: remove '{container.Name}' failed: {ex.Message}");
            }
        };

        actions.Children.Add(startStop);
        actions.Children.Add(logs);
        actions.Children.Add(remove);
        return actions;
    }

    private async Task RunActionAsync(string verb, string name, Func<CancellationToken, Task<DockerResult>> action)
    {
        try
        {
            var result = await action(CancellationToken.None);
            _log(result.Success
                ? $"Local Containers: {verb} '{name}' done."
                : $"Local Containers: {verb} '{name}' failed: {result.Error}");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _log($"Local Containers: {verb} '{name}' failed: {ex.Message}");
        }
    }

    private Task ShowLogsAsync(ManagedContainer container) =>
        _hostUi.ShowDialogAsync(
            $"Logs — {container.Name}",
            ContainerLogsView.Build(container.Name, ct => _service.LogsAsync(container.Id, tailLines: 300, ct)));

    private Control BuildDockerMissing()
    {
        var panel = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(20, 22, 20, 22) };
        panel.Children.Add(new TextBlock { Text = "⚠", FontSize = 22, Foreground = WarnBrush, HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = "Docker was not found on this machine", FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock
        {
            Text = "Install Docker and make sure the “docker” command is on your PATH.",
            Opacity = 0.7,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        var recheck = new Button { Content = "Re-check", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        recheck.Click += async (_, _) => await RefreshAsync();
        panel.Children.Add(recheck);
        return panel;
    }

    private static TextBlock Meta(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Opacity = 0.75,
        FontFamily = new FontFamily("Cascadia Code,Consolas,Menlo,monospace"),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 3, 12, 3)
    };

    private static void Place(Grid table, Control control, int row, int col)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, col);
        table.Children.Add(control);
    }
}
