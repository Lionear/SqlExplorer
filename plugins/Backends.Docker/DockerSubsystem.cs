using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

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
        // The Docker CLI is resolved through the host container (the 'services' capability, SE-171) — the
        // plugin dogfoods its own DI wiring — and falls back to a direct instance when it wasn't granted.
        _registry = new PluginStorageContainerRegistry(storage);
        _builder = new DockerComposeBuilder();
        _service = new ContainerService(_builder, ResolveDockerCli(context), _registry);

        var containers = _registry.GetAll();
        context.Log($"Local Containers: {containers.Count} managed container(s) restored from storage.");

        ReconcileConnections(context, _registry);
    }

    // Resolve the Docker CLI from the plugin's own services (the 'services' capability auto-registered
    // DockerCli under IDockerCli). context.Services is scoped to this plugin's types, so this can only ever
    // return the plugin's own registration. Falls back to a direct instance when the capability wasn't
    // granted (Services null) or nothing was registered — the plugin keeps working either way.
    internal static IDockerCli ResolveDockerCli(IPluginRuntimeContext context) =>
        context.Services?.GetService(typeof(IDockerCli)) as IDockerCli ?? new DockerCli();

    // Connections seam, startup-restore path: link any managed container that isn't linked yet (idempotent via
    // ConnectionId, so a restart never duplicates). Newly created containers are linked at create-time with
    // their full credentials by LinkNewContainer — this only backfills legacy/unlinked entries and so can only
    // offer host+port (the password isn't kept in the registry). An empty registry links nothing.
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

    // Unlink side of ReconcileConnections: when a managed container is torn down, drop the host connection it
    // created so no orphan is left behind in the tree. The origin-scoped IManagedConnections.Remove only ever
    // touches this plugin's own connections. No-op when the container was never linked (ConnectionId null) or
    // the connections seam isn't granted.
    private void ReleaseConnection(ManagedContainer container)
    {
        if (_context?.Connections is { } connections)
        {
            ReleaseContainerConnection(connections, container, _context.Log);
        }
    }

    // Drop the host connection a container created, if any. Origin-scoped Remove only touches this plugin's
    // own connections; a container that was never linked (ConnectionId null) is a no-op.
    internal static void ReleaseContainerConnection(IManagedConnections connections, ManagedContainer container, Action<string>? log = null)
    {
        if (container.ConnectionId is { } connectionId)
        {
            connections.Remove(connectionId);
            log?.Invoke($"Local Containers: released the managed connection for '{container.Name}'.");
        }
    }

    // Create-time linking: the container's full field values — including the password the user entered or the
    // engine default — are only in hand right after creation, so build the host connection now instead of from
    // the credential-less registry. ConnectionService routes the password to the OS keychain (per the provider's
    // secret fields), so the secret never lands in the container registry. Stamps ConnectionId so a later
    // restart's ReconcileConnections skips it. No-op if the connections seam wasn't granted or it's already linked.
    private void LinkNewContainer(CreateContainerRequest request)
    {
        if (_context?.Connections is { } connections && _registry is { } registry)
        {
            LinkContainer(connections, registry, request, _context.Log);
        }
    }

    // Link the just-created container to a host connection with its full field values (credentials included)
    // and stamp the ConnectionId so a later restart's ReconcileConnections skips it. Idempotent: a container
    // that's already linked (ConnectionId set) or missing from the registry is a no-op.
    internal static void LinkContainer(IManagedConnections connections, IContainerRegistryStore registry, CreateContainerRequest request, Action<string>? log = null)
    {
        if (registry.Get(request.ContainerName) is not { } container || container.ConnectionId is not null)
        {
            return;
        }

        var connectionId = connections.Create(new NewConnectionSpec(
            container.Name, container.ProviderId, BuildConnectionValues(request), Folder: "Local Containers"));

        registry.Save(container with { ConnectionId = connectionId });
        log?.Invoke($"Local Containers: linked the managed connection for '{container.Name}'.");
    }

    // The host-connection field values for a container: host/port plus the engine credentials it was created
    // with (username/password, and database when set), so the connection actually connects rather than just
    // pointing at the endpoint. Keys match the provider ConnectionField keys the create dialog prefills from.
    internal static Dictionary<string, string?> BuildConnectionValues(CreateContainerRequest request)
    {
        var values = new Dictionary<string, string?>(request.Values)
        {
            ["host"] = "localhost",
            ["port"] = request.HostPort.ToString(CultureInfo.InvariantCulture)
        };

        if (!string.IsNullOrWhiteSpace(request.Database))
        {
            values["database"] = request.Database;
        }

        return values;
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
        // prefilled from it. On success LinkNewContainer creates the new container's host connection with its
        // full credentials and stamps the ConnectionId so a later restart doesn't link it twice.
        var content = CreateContainerView.Build(
            _builder, _service, fromConnection,
            onCreated: LinkNewContainer,
            log: _context.Log);

        return hostUi.ShowDialogAsync("New local Docker instance", content);
    }

    // --- IPanelPlugin (SE-164 panel seam) ---------------------------------------------------------------

    public string PanelId => "containers";

    public string Title => "Containers";

    /// <summary>The Lucide "container" glyph for the panel's bottom-bar toggle (drawn Stretch="Uniform").</summary>
    public Geometry Icon => Geometry.Parse(DockerIcons.Container);

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
            onNewFromConnection: () => ShowCreateDialogAsync(hostUi),
            onRemoved: ReleaseConnection);
        return _panel.Root;
    }
}
