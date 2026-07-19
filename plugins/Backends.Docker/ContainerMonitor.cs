namespace SqlExplorer.Backends.Docker;

/// <summary>Reads the live run-state of every managed container in one pass (the background seam's poll step,
/// factored out so it's testable without the loop/UI). Fault-tolerant: <see cref="ContainerService.StatusAsync"/>
/// yields <see cref="ContainerStatus.Absent"/> for an unknown container or an unavailable Docker, never a throw.</summary>
internal static class ContainerMonitor
{
    public static async Task<IReadOnlyDictionary<string, ContainerStatus>> PollAsync(
        IContainerRegistryStore registry, ContainerService service, CancellationToken ct)
    {
        var result = new Dictionary<string, ContainerStatus>();
        foreach (var container in registry.GetAll())
        {
            ct.ThrowIfCancellationRequested();
            result[container.Id] = await service.StatusAsync(container.Id, ct);
        }

        return result;
    }
}
