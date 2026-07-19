using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SqlExplorer.Backends.Docker;

namespace SqlExplorer.Backends.Docker.Tests;

public class ContainerMonitorTests
{
    private sealed class FakeCli : IDockerCli
    {
        public readonly Dictionary<string, ContainerStatus> ByName = new();
        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<DockerResult> ComposeUpAsync(string dir, CancellationToken ct) => Task.FromResult(new DockerResult(0, "", ""));
        public Task<DockerResult> ComposeDownAsync(string dir, bool rmVol, CancellationToken ct) => Task.FromResult(new DockerResult(0, "", ""));
        public Task<DockerResult> StartAsync(string n, CancellationToken ct) => Task.FromResult(new DockerResult(0, "", ""));
        public Task<DockerResult> StopAsync(string n, CancellationToken ct) => Task.FromResult(new DockerResult(0, "", ""));
        public Task<ContainerStatus> InspectAsync(string n, CancellationToken ct) =>
            Task.FromResult(ByName.TryGetValue(n, out var s) ? s : ContainerStatus.Absent);
        public Task<string> LogsAsync(string n, int tail, CancellationToken ct) => Task.FromResult("");
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

    private static ManagedContainer Container(string id) =>
        new(id, id, "postgres", "postgres", "16", 5432, "/tmp/" + id, null, "2024-01-01T00:00:00Z");

    [Fact]
    public async Task PollAsync_reads_each_managed_container_state()
    {
        var cli = new FakeCli
        {
            ByName =
            {
                ["pg-1"] = new ContainerStatus(ContainerState.Running, true),
                ["redis-1"] = new ContainerStatus(ContainerState.Exited, null)
            }
        };
        var registry = new FakeRegistry();
        registry.Save(Container("pg-1"));
        registry.Save(Container("redis-1"));
        var service = new ContainerService(new DockerComposeBuilder(), cli, registry);

        var map = await ContainerMonitor.PollAsync(registry, service, CancellationToken.None);

        Assert.Equal(2, map.Count);
        Assert.Equal(ContainerState.Running, map["pg-1"].State);
        Assert.Equal(ContainerState.Exited, map["redis-1"].State);
    }

    [Fact]
    public async Task PollAsync_is_empty_for_an_empty_registry()
    {
        var registry = new FakeRegistry();
        var service = new ContainerService(new DockerComposeBuilder(), new FakeCli(), registry);

        Assert.Empty(await ContainerMonitor.PollAsync(registry, service, CancellationToken.None));
    }

    [Fact]
    public async Task PollAsync_honours_cancellation()
    {
        var registry = new FakeRegistry();
        registry.Save(Container("pg-1"));
        var service = new ContainerService(new DockerComposeBuilder(), new FakeCli(), registry);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ContainerMonitor.PollAsync(registry, service, cts.Token));
    }
}
