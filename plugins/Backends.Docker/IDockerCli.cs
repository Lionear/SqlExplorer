namespace SqlExplorer.Backends.Docker;

/// <summary>The outcome of one <c>docker</c> invocation: exit code plus captured output.</summary>
public sealed record DockerResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;

    /// <summary>The most useful line to surface on failure — stderr, falling back to stdout.</summary>
    public string Error => string.IsNullOrWhiteSpace(StdErr) ? StdOut.Trim() : StdErr.Trim();
}

/// <summary>A container's run-state as Docker reports it (<c>docker inspect</c> <c>.State.Status</c>).</summary>
public enum ContainerState
{
    /// <summary>No container by that name exists (never created, or already removed).</summary>
    Absent,
    Created,
    Running,
    Restarting,
    Paused,
    Exited,
    Dead
}

/// <summary>A container's state plus its health, when it declares a healthcheck (<c>null</c> = none).</summary>
public sealed record ContainerStatus(ContainerState State, bool? Healthy)
{
    public static readonly ContainerStatus Absent = new(ContainerState.Absent, null);

    /// <summary>Ready to connect to: running, and — if it has a healthcheck — reporting healthy.</summary>
    public bool IsReady => State == ContainerState.Running && Healthy != false;
}

/// <summary>
/// Thin async wrapper over the <c>docker</c> / <c>docker compose</c> CLI (compose has no library API). The
/// only Docker seam <see cref="ContainerService"/> sees, so it is testable against a fake. The real
/// implementation (<see cref="DockerCli"/>) shells out; every method is fault-tolerant (a missing Docker
/// surfaces as a failed <see cref="DockerResult"/> / <see cref="ContainerStatus.Absent"/>, never a throw).
/// </summary>
public interface IDockerCli
{
    /// <summary>Is the Docker CLI installed and its daemon reachable?</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct);

    /// <summary><c>docker compose up -d</c> in <paramref name="projectDir"/> (holding a docker-compose file).</summary>
    Task<DockerResult> ComposeUpAsync(string projectDir, CancellationToken ct);

    /// <summary><c>docker compose down</c> (optionally <c>-v</c> to drop the named volume too).</summary>
    Task<DockerResult> ComposeDownAsync(string projectDir, bool removeVolumes, CancellationToken ct);

    Task<DockerResult> StartAsync(string containerName, CancellationToken ct);

    Task<DockerResult> StopAsync(string containerName, CancellationToken ct);

    /// <summary>Current state + health of a container by name; <see cref="ContainerStatus.Absent"/> if none.</summary>
    Task<ContainerStatus> InspectAsync(string containerName, CancellationToken ct);

    /// <summary>The last <paramref name="tailLines"/> lines of a container's combined stdout/stderr.</summary>
    Task<string> LogsAsync(string containerName, int tailLines, CancellationToken ct);
}
