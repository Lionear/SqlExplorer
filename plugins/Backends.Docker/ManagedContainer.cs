namespace SqlExplorer.Backends.Docker;

/// <summary>
/// One container the app created and now manages, as persisted in the registry (<c>containers.json</c>, via
/// the plugin's storage seam). Its <see cref="Name"/> doubles as the stable id (it is the docker
/// <c>container_name</c>). <see cref="ComposeDir"/> is where its <c>docker-compose.yaml</c> lives so
/// start/stop/remove can find it. <see cref="ConnectionId"/> links the host connection this plugin
/// auto-creates for the container (null until it has been linked).
/// </summary>
public sealed record ManagedContainer(
    string Id,
    string Name,
    string ProviderId,
    string Image,
    string Tag,
    int HostPort,
    string ComposeDir,
    string? ConnectionId,
    string CreatedAtUtc);
