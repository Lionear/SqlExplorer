using System.Diagnostics;
using System.Text;

namespace SqlExplorer.Backends.Docker;

/// <summary>
/// The real <see cref="IDockerCli"/>: shells out to the <c>docker</c> / <c>docker compose</c> CLI (compose
/// has no managed API). Uses <see cref="ProcessStartInfo.ArgumentList"/> — never string-spliced argv (no
/// shell, no injection) — and reads asynchronously. Requires Docker installed and on PATH; when it isn't,
/// calls surface as failed results rather than throwing. (This is why the plugin declares the
/// <c>process</c> capability — disclosure that it starts external processes.)
/// </summary>
public sealed class DockerCli : IDockerCli
{
    private const string Exe = "docker";

    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        try
        {
            // `docker version` fails (non-zero) when the client can't reach the daemon — exactly the
            // "Docker not usable" signal we want, not just "the binary exists".
            var result = await RunAsync(null, ct, "version", "--format", "{{.Server.Version}}");
            return result.Success;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public Task<DockerResult> ComposeUpAsync(string projectDir, CancellationToken ct) =>
        RunAsync(projectDir, ct, "compose", "up", "-d");

    public Task<DockerResult> ComposeDownAsync(string projectDir, bool removeVolumes, CancellationToken ct) =>
        removeVolumes
            ? RunAsync(projectDir, ct, "compose", "down", "-v")
            : RunAsync(projectDir, ct, "compose", "down");

    public Task<DockerResult> StartAsync(string containerName, CancellationToken ct) =>
        RunAsync(null, ct, "start", containerName);

    public Task<DockerResult> StopAsync(string containerName, CancellationToken ct) =>
        RunAsync(null, ct, "stop", containerName);

    public async Task<ContainerStatus> InspectAsync(string containerName, CancellationToken ct)
    {
        // One inspect emits "status;health" (health = "none" when the image declares no healthcheck).
        var result = await RunAsync(null, ct,
            "inspect", "-f",
            "{{.State.Status}};{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}",
            containerName);

        if (!result.Success)
        {
            return ContainerStatus.Absent; // no such container
        }

        var parts = result.StdOut.Trim().Split(';');
        var health = parts.ElementAtOrDefault(1) switch
        {
            "healthy" => (bool?)true,
            "unhealthy" or "starting" => false,
            _ => null
        };

        return new ContainerStatus(ParseState(parts.ElementAtOrDefault(0)), health);
    }

    public async Task<string> LogsAsync(string containerName, int tailLines, CancellationToken ct)
    {
        var result = await RunAsync(null, ct, "logs", "--tail", tailLines.ToString(), containerName);
        // Docker splits container output across stdout and stderr; present both in order.
        return string.Join('\n',
            new[] { result.StdOut.TrimEnd(), result.StdErr.TrimEnd() }.Where(s => s.Length > 0));
    }

    private static ContainerState ParseState(string? status) => status switch
    {
        "created" => ContainerState.Created,
        "running" => ContainerState.Running,
        "restarting" => ContainerState.Restarting,
        "paused" => ContainerState.Paused,
        "exited" => ContainerState.Exited,
        "dead" => ContainerState.Dead,
        _ => ContainerState.Absent
    };

    private static async Task<DockerResult> RunAsync(string? workingDir, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo(Exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (workingDir is not null)
        {
            psi.WorkingDirectory = workingDir;
        }

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start the docker process.");

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return new DockerResult(process.ExitCode, await stdout, await stderr);
    }
}
