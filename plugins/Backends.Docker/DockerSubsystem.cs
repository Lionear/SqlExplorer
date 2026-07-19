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
/// It dogfoods every SE-164 seam: storage, connections, panel, menu (create flow) and background (live status).
/// </summary>
public sealed class DockerSubsystem : ISubsystemPlugin, IPanelPlugin, IMenuPlugin, IBackgroundPlugin, IConnectionMenuPlugin
{
    private IPluginRuntimeContext? _context;
    private IContainerRegistryStore? _registry;
    private DockerComposeBuilder? _builder;
    private ContainerService? _service;
    private ContainersPanelView? _panel;

    public void Initialize(IPluginRuntimeContext context)
    {
        _context = context;

        if (context.Storage is not { } storage)
        {
            context.Log("Local Containers: the 'storage' capability was not granted — the container registry is unavailable.");
            return;
        }

        // The registry now persists the real ManagedContainer set through the storage seam (replacing the
        // old host-owned JSON store), and drives the container lifecycle service the create flow runs.
        _registry = new PluginStorageContainerRegistry(storage);
        _builder = new DockerComposeBuilder();
        _service = new ContainerService(_builder, new DockerCli(), _registry);

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
        _builder = null;
        _service = null;
        _panel = null;
    }

    // --- IBackgroundPlugin (SE-164 background seam) ------------------------------------------------------

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (_service is null || _registry is null)
        {
            return;
        }

        // Poll every managed container's run-state on an interval and push it to the panel. Resilient: a poll
        // failure (Docker went away mid-run) is logged once per streak — not every tick — and the loop keeps
        // going, so it recovers if Docker comes back. An empty registry makes no Docker calls at all.
        try
        {
            var loggedError = false;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var statuses = await ContainerMonitor.PollAsync(_registry, _service, cancellationToken);
                    _panel?.SetStatuses(statuses);
                    loggedError = false;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (!loggedError)
                    {
                        _context?.Log($"Local Containers: status polling paused ({ex.Message}).");
                        loggedError = true;
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    // --- IMenuPlugin (SE-164 menu seam) -----------------------------------------------------------------

    public IReadOnlyList<MenuContribution> MenuItems =>
        [new MenuContribution("new-container", "New Local Container…", ShowCreateDialogAsync)];

    // --- IConnectionMenuPlugin (SE-164 connection-menu seam) --------------------------------------------

    public IReadOnlyList<ConnectionMenuContribution> ConnectionMenuItems =>
        [new ConnectionMenuContribution(
            "create-container",
            "Create local Docker instance…",
            conn => _builder?.Supports(conn.ProviderId) ?? false,
            (conn, hostUi) => ShowCreateDialogAsync(hostUi, conn))];

    private Task ShowCreateDialogAsync(IHostUi hostUi) => ShowCreateDialogAsync(hostUi, fromConnection: null);

    private Task ShowCreateDialogAsync(IHostUi hostUi, ManagedConnectionInfo? fromConnection)
    {
        if (_context is null || _builder is null || _service is null || _registry is null)
        {
            return Task.CompletedTask; // storage wasn't granted — nothing to build against
        }

        // Standalone (fromConnection null) → pick the engine; from a right-clicked connection → engine fixed +
        // prefilled from it. On success reconcile links the new container's host connection (and stamps its
        // ConnectionId so a later restart doesn't link it twice).
        var content = CreateContainerView.Build(
            _builder, _service, fromConnection,
            onCreated: () => ReconcileConnections(_context, _registry),
            log: _context.Log);

        return hostUi.ShowDialogAsync("New local Docker instance", content);
    }

    // --- IPanelPlugin (SE-164 panel seam) ---------------------------------------------------------------

    public string PanelId => "containers";

    public string Title => "Containers";

    /// <summary>Build the Containers panel: a live table of the managed containers with their run-state and
    /// lifecycle actions. Rebuilds on registry changes and on the background poll's status pushes (see
    /// <see cref="RunAsync"/>); <paramref name="hostUi"/> lets it open the logs dialog.</summary>
    public Control CreatePanel(IHostUi hostUi)
    {
        if (_context is null || _service is null || _registry is null)
        {
            return new TextBlock { Text = "Local Containers: storage unavailable.", Margin = new Thickness(12) };
        }

        _panel = new ContainersPanelView(
            _registry, _service, hostUi, _context.Log,
            onNewFromConnection: () => ShowCreateDialogAsync(hostUi));
        return _panel.Root;
    }
}
