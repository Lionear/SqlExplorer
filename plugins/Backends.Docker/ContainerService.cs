namespace SqlExplorer.Backends.Docker;

/// <summary>Everything needed to create one local container from a connection's engine + values.</summary>
public sealed record CreateContainerRequest(
    string ProviderId,
    IReadOnlyDictionary<string, string?> Values,
    string ContainerName,
    int HostPort,
    string Tag,
    string? Database = null);

/// <summary>
/// Orchestrates the managed-container lifecycle on top of the pure <see cref="DockerComposeBuilder"/> and
/// the <see cref="IDockerCli"/> seam: writes each container's <c>docker-compose.yaml</c> under its own
/// folder, brings it up, waits for it to become ready, and records it in the <see cref="IContainerRegistryStore"/>.
/// Also exposes generate-only snippet building (works with no Docker) and start/stop/remove/status/logs.
/// The auto-created connection link (<see cref="ManagedContainer.ConnectionId"/>) is stamped by the plugin's
/// reconcile step, not here.
/// </summary>
public sealed class ContainerService
{
    private const string ComposeFileName = "docker-compose.yaml";

    private readonly DockerComposeBuilder _builder;
    private readonly IDockerCli _cli;
    private readonly IContainerRegistryStore _registry;
    private readonly string _containersDir;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _readyTimeout;

    public ContainerService(
        DockerComposeBuilder builder,
        IDockerCli cli,
        IContainerRegistryStore registry,
        string? containersDir = null,
        TimeSpan? pollInterval = null,
        TimeSpan? readyTimeout = null)
    {
        _builder = builder;
        _cli = cli;
        _registry = registry;
        _containersDir = containersDir ?? DefaultContainersDir();
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
        _readyTimeout = readyTimeout ?? TimeSpan.FromSeconds(90);
    }

    public Task<bool> IsDockerAvailableAsync(CancellationToken ct) => _cli.IsAvailableAsync(ct);

    public IReadOnlyList<ManagedContainer> All() => _registry.GetAll();

    /// <summary>Generate-only: the compose/run snippet, no Docker touched — the "Generate only" dialog path.</summary>
    public string BuildSnippet(CreateContainerRequest request, SnippetFormat format) =>
        _builder.Build(ToSpec(request), format);

    /// <summary>Write the compose file, bring the container up, wait until it's ready, and register it.</summary>
    public async Task<ManagedContainer> CreateAndRunAsync(
        CreateContainerRequest request, IProgress<string>? progress, CancellationToken ct)
    {
        var dir = Path.Combine(_containersDir, Sanitize(request.ContainerName));
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, ComposeFileName), _builder.Build(ToSpec(request), SnippetFormat.Compose), ct);

        progress?.Report($"Starting {request.ContainerName}…");
        var up = await _cli.ComposeUpAsync(dir, ct);
        if (!up.Success)
        {
            throw new InvalidOperationException($"docker compose up failed: {up.Error}");
        }

        progress?.Report("Waiting for the container to become ready…");
        await WaitUntilReadyAsync(request.ContainerName, ct);

        var container = new ManagedContainer(
            Id: request.ContainerName,
            Name: request.ContainerName,
            ProviderId: request.ProviderId,
            Image: _builder.ImageName(request.ProviderId) ?? request.ProviderId,
            Tag: request.Tag,
            HostPort: request.HostPort,
            ComposeDir: dir,
            ConnectionId: null,
            CreatedAtUtc: DateTime.UtcNow.ToString("o"));

        _registry.Save(container);
        progress?.Report($"{request.ContainerName} is ready.");
        return container;
    }

    public Task<DockerResult> StartAsync(string id, CancellationToken ct) => _cli.StartAsync(id, ct);

    public Task<DockerResult> StopAsync(string id, CancellationToken ct) => _cli.StopAsync(id, ct);

    public Task<ContainerStatus> StatusAsync(string id, CancellationToken ct) => _cli.InspectAsync(id, ct);

    public Task<string> LogsAsync(string id, int tailLines, CancellationToken ct) => _cli.LogsAsync(id, tailLines, ct);

    /// <summary>Tear a container down (compose down, optionally dropping its volume) and drop the registry
    /// entry. Best-effort on the Docker side: an already-gone container still gets removed from the registry.</summary>
    public async Task RemoveAsync(string id, bool removeVolumes, CancellationToken ct)
    {
        if (_registry.Get(id) is { } container)
        {
            await _cli.ComposeDownAsync(container.ComposeDir, removeVolumes, ct);
        }

        _registry.Remove(id);
    }

    // Poll until the container reports ready (running, and healthy if it declares a healthcheck). A container
    // that exits before then is a hard failure; exceeding the timeout is a TimeoutException.
    private async Task WaitUntilReadyAsync(string name, CancellationToken ct)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < _readyTimeout; elapsed += _pollInterval)
        {
            ct.ThrowIfCancellationRequested();
            var status = await _cli.InspectAsync(name, ct);
            if (status.IsReady)
            {
                return;
            }

            if (status.State is ContainerState.Exited or ContainerState.Dead)
            {
                throw new InvalidOperationException($"Container '{name}' exited before it became ready.");
            }

            await Task.Delay(_pollInterval, ct);
        }

        throw new TimeoutException($"Container '{name}' did not become ready within {_readyTimeout.TotalSeconds:0}s.");
    }

    private ContainerSpec ToSpec(CreateContainerRequest r) =>
        new(r.ProviderId, r.Values, r.Database, r.Tag, r.ContainerName, r.HostPort);

    private static string Sanitize(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private static string DefaultContainersDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Lionear", "SqlExplorer", "containers");
}
