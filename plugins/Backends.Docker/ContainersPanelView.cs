using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace SqlExplorer.Backends.Docker;

/// <summary>
/// The Containers panel content (SE-164 panel + background seams): a live list of the managed containers with
/// their run-state. Rebuilds itself when the registry changes (a container created/removed) and when the
/// background poll pushes fresh statuses — always marshalled onto the UI thread. No hardcoded colours, so it
/// reads in light and dark.
/// </summary>
internal sealed class ContainersPanelView
{
    private readonly IContainerRegistryStore _registry;
    private readonly StackPanel _list;
    private IReadOnlyDictionary<string, ContainerStatus> _statuses = new Dictionary<string, ContainerStatus>();

    public ContainersPanelView(IContainerRegistryStore registry)
    {
        _registry = registry;
        _list = new StackPanel { Margin = new Thickness(12, 8, 12, 12), Spacing = 4 };
        Root = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = _list
        };

        registry.Changed += () => Post(Rebuild);
        Rebuild();
    }

    public Control Root { get; }

    /// <summary>Push a fresh status map from the background poll; rebuilds the list on the UI thread.</summary>
    public void SetStatuses(IReadOnlyDictionary<string, ContainerStatus> statuses)
    {
        _statuses = statuses;
        Post(Rebuild);
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
        _list.Children.Clear();

        var containers = _registry.GetAll();
        if (containers.Count == 0)
        {
            _list.Children.Add(new TextBlock { Text = "No managed containers yet.", Opacity = 0.7 });
            return;
        }

        foreach (var container in containers)
        {
            var state = _statuses.TryGetValue(container.Id, out var status) ? Describe(status) : "—";
            _list.Children.Add(new TextBlock
            {
                Text = $"{container.Name}   ·   {container.Image}:{container.Tag}   ·   localhost:{container.HostPort}   ·   {state}"
            });
        }
    }

    private static string Describe(ContainerStatus status) => status.State switch
    {
        ContainerState.Absent => "not created",
        ContainerState.Running => status.Healthy == false ? "starting" : "running",
        _ => status.State.ToString().ToLowerInvariant()
    };
}
