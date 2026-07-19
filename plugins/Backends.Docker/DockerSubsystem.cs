using Avalonia;
using Avalonia.Controls;

namespace SqlExplorer.Backends.Docker;

/// <summary>
/// The Local Containers (Docker) subsystem plugin — the first consumer of the SE-164 extensibility platform,
/// now carrying the migrated SE-113 container regie (<see cref="DockerComposeBuilder"/> / <see cref="IDockerCli"/>
/// / <see cref="ContainerService"/>). It dogfoods three seams: <c>storage</c> (its persisted container registry
/// via <see cref="PluginStorageContainerRegistry"/> over <see cref="IPluginRuntimeContext.Storage"/>),
/// <c>connections</c> (each managed container surfaces as a real host connection tagged with this plugin as
/// origin, via <see cref="IPluginRuntimeContext.Connections"/>), and <c>panel</c> (<see cref="IPanelPlugin"/> —
/// a docked "Containers" panel). It also declares <c>process</c> (it can shell out to <c>docker</c>).
/// The create-container flow and live status polling wire in with the menu and background seams next; the
/// engine is migrated and tested now so those slices are pure wiring.
/// </summary>
public sealed class DockerSubsystem : ISubsystemPlugin, IPanelPlugin
{
    private IPluginRuntimeContext? _context;
    private IContainerRegistryStore? _registry;

    public void Initialize(IPluginRuntimeContext context)
    {
        _context = context;

        if (context.Storage is not { } storage)
        {
            context.Log("Local Containers: the 'storage' capability was not granted — the container registry is unavailable.");
            return;
        }

        // The registry now persists the real ManagedContainer set through the storage seam (replacing the
        // old host-owned JSON store). ContainerService/DockerCli are constructed on demand by the create flow.
        _registry = new PluginStorageContainerRegistry(storage);
        var containers = _registry.GetAll();
        context.Log($"Local Containers: {containers.Count} managed container(s) restored from storage.");

        ReconcileConnections(context, _registry);
    }

    // Connections seam: every managed container gets one real host connection, tagged with this plugin as
    // origin (so the tree can badge it "managed by Local Containers"). Idempotent via the container's
    // ConnectionId — link once, then persist the link so a restart doesn't create a duplicate. An empty
    // registry links nothing, so a fresh install adds no connections until a container actually exists.
    private static void ReconcileConnections(IPluginRuntimeContext context, IContainerRegistryStore registry)
    {
        if (context.Connections is not { } connections)
        {
            return;
        }

        var linked = 0;
        foreach (var container in registry.GetAll())
        {
            if (container.ConnectionId is not null)
            {
                continue; // already linked
            }

            var connectionId = connections.Create(new NewConnectionSpec(
                container.Name,
                container.ProviderId,
                new Dictionary<string, string?> { ["host"] = "localhost", ["port"] = container.HostPort.ToString() },
                Folder: "Local Containers"));

            registry.Save(container with { ConnectionId = connectionId });
            linked++;
        }

        context.Log($"Local Containers: linked {linked} new managed connection(s).");
    }

    public void Deactivate()
    {
        _context = null;
        _registry = null;
    }

    // --- IPanelPlugin (SE-164 panel seam) ---------------------------------------------------------------

    public string PanelId => "containers";

    public string Title => "Containers";

    /// <summary>Build the Containers panel: a snapshot of the managed container registry. No hardcoded colours
    /// — text inherits the host theme, so it reads in light and dark. Live <c>docker ps</c> status and a
    /// richer table arrive with the background seam.</summary>
    public Control CreatePanel()
    {
        var body = new StackPanel { Margin = new Thickness(12, 8, 12, 12), Spacing = 4 };

        var containers = _registry?.GetAll() ?? [];
        if (containers.Count == 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = "No managed containers yet.",
                Opacity = 0.7
            });
        }
        else
        {
            foreach (var container in containers)
            {
                body.Children.Add(new TextBlock
                {
                    Text = $"{container.Name}   ·   {container.Image}:{container.Tag}   ·   localhost:{container.HostPort}"
                });
            }
        }

        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = body
        };
    }
}
