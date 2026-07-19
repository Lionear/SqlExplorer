using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SqlExplorer.Backends.Docker;

namespace SqlExplorer.Backends.Docker.Tests;

public class ContainerServiceTests
{
    // ---- fakes ----

    private sealed class FakeCli : IDockerCli
    {
        public bool Available = true;
        public DockerResult UpResult = new(0, "", "");
        public DockerResult DownResult = new(0, "", "");
        public ContainerStatus DefaultStatus = new(ContainerState.Running, null);
        public readonly Queue<ContainerStatus> StatusSequence = new();
        public readonly List<string> Calls = new();

        public Task<bool> IsAvailableAsync(CancellationToken ct) { Calls.Add("available"); return Task.FromResult(Available); }
        public Task<DockerResult> ComposeUpAsync(string dir, CancellationToken ct) { Calls.Add($"up:{dir}"); return Task.FromResult(UpResult); }
        public Task<DockerResult> ComposeDownAsync(string dir, bool rmVol, CancellationToken ct) { Calls.Add($"down:{dir}:{rmVol}"); return Task.FromResult(DownResult); }
        public Task<DockerResult> StartAsync(string n, CancellationToken ct) { Calls.Add($"start:{n}"); return Task.FromResult(new DockerResult(0, "", "")); }
        public Task<DockerResult> StopAsync(string n, CancellationToken ct) { Calls.Add($"stop:{n}"); return Task.FromResult(new DockerResult(0, "", "")); }
        public Task<ContainerStatus> InspectAsync(string n, CancellationToken ct) { Calls.Add($"inspect:{n}"); return Task.FromResult(StatusSequence.Count > 0 ? StatusSequence.Dequeue() : DefaultStatus); }
        public Task<string> LogsAsync(string n, int tail, CancellationToken ct) { Calls.Add($"logs:{n}"); return Task.FromResult("log output"); }
    }

    private sealed class FakeRegistry : IContainerRegistryStore
    {
        private readonly List<ManagedContainer> _items = new();
        public event Action? Changed;
        public IReadOnlyList<ManagedContainer> GetAll() => _items.ToList();
        public ManagedContainer? Get(string id) => _items.FirstOrDefault(c => c.Id == id);
        public void Save(ManagedContainer c) { _items.RemoveAll(x => x.Id == c.Id); _items.Add(c); Changed?.Invoke(); }
        public void Remove(string id) { _items.RemoveAll(x => x.Id == id); Changed?.Invoke(); }
    }

    private static CreateContainerRequest Request() =>
        new("postgres",
            new Dictionary<string, string?> { ["username"] = "postgres", ["password"] = "pw" },
            ContainerName: "sales-pg-local", HostPort: 5432, Tag: "16", Database: "sales");

    private static (ContainerService Svc, FakeCli Cli, FakeRegistry Reg, string Dir) New(FakeCli? cli = null, TimeSpan? timeout = null)
    {
        cli ??= new FakeCli();
        var reg = new FakeRegistry();
        var dir = Path.Combine(Path.GetTempPath(), "se113-" + Guid.NewGuid().ToString("N"));
        var svc = new ContainerService(new DockerComposeBuilder(), cli, reg, containersDir: dir,
            pollInterval: TimeSpan.FromMilliseconds(1), readyTimeout: timeout ?? TimeSpan.FromSeconds(5));
        return (svc, cli, reg, dir);
    }

    // ---- tests ----

    [Fact]
    public async Task CreateAndRun_writes_compose_brings_up_and_registers()
    {
        var (svc, cli, reg, dir) = New();
        try
        {
            var container = await svc.CreateAndRunAsync(Request(), null, CancellationToken.None);

            var composePath = Path.Combine(dir, "sales-pg-local", "docker-compose.yaml");
            Assert.True(File.Exists(composePath));
            Assert.Contains("image: postgres:16", await File.ReadAllTextAsync(composePath));

            Assert.Contains(cli.Calls, c => c.StartsWith("up:") && c.Contains("sales-pg-local"));
            Assert.Contains(cli.Calls, c => c == "inspect:sales-pg-local");

            var saved = reg.Get("sales-pg-local");
            Assert.NotNull(saved);
            Assert.Equal("postgres", saved!.ProviderId);
            Assert.Equal("postgres", saved.Image);
            Assert.Equal("16", saved.Tag);
            Assert.Equal(5432, saved.HostPort);
            Assert.Equal(container, saved);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CreateAndRun_throws_and_does_not_register_when_up_fails()
    {
        var cli = new FakeCli { UpResult = new DockerResult(1, "", "port already allocated") };
        var (svc, _, reg, dir) = New(cli);
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.CreateAndRunAsync(Request(), null, CancellationToken.None));
            Assert.Contains("port already allocated", ex.Message);
            Assert.Empty(reg.GetAll());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CreateAndRun_polls_until_the_container_is_ready()
    {
        var cli = new FakeCli();
        cli.StatusSequence.Enqueue(new ContainerStatus(ContainerState.Created, null));
        cli.StatusSequence.Enqueue(new ContainerStatus(ContainerState.Running, false)); // still starting
        cli.StatusSequence.Enqueue(new ContainerStatus(ContainerState.Running, true));  // healthy
        var (svc, _, reg, dir) = New(cli);
        try
        {
            await svc.CreateAndRunAsync(Request(), null, CancellationToken.None);
            Assert.Equal(3, cli.Calls.Count(c => c == "inspect:sales-pg-local"));
            Assert.NotNull(reg.Get("sales-pg-local"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CreateAndRun_throws_when_the_container_exits_before_ready()
    {
        var cli = new FakeCli { DefaultStatus = new ContainerStatus(ContainerState.Exited, null) };
        var (svc, _, reg, dir) = New(cli);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.CreateAndRunAsync(Request(), null, CancellationToken.None));
            Assert.Empty(reg.GetAll());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CreateAndRun_times_out_when_never_ready()
    {
        var cli = new FakeCli { DefaultStatus = new ContainerStatus(ContainerState.Created, null) };
        var (svc, _, _, dir) = New(cli, timeout: TimeSpan.FromMilliseconds(30));
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(
                () => svc.CreateAndRunAsync(Request(), null, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Remove_brings_down_with_volumes_and_deregisters()
    {
        var (svc, cli, reg, dir) = New();
        try
        {
            await svc.CreateAndRunAsync(Request(), null, CancellationToken.None);
            await svc.RemoveAsync("sales-pg-local", removeVolumes: true, CancellationToken.None);

            Assert.Contains(cli.Calls, c => c.StartsWith("down:") && c.EndsWith(":True"));
            Assert.Null(reg.Get("sales-pg-local"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BuildSnippet_never_touches_docker()
    {
        var (svc, cli, _, _) = New();
        var snippet = svc.BuildSnippet(Request(), SnippetFormat.Run);

        Assert.Contains("docker run -d", snippet);
        Assert.Contains("postgres:16", snippet);
        Assert.Empty(cli.Calls);
    }
}
