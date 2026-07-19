namespace SqlExplorer.Sdk.Extensibility;

/// <summary>What a subsystem plugin needs to create one host-managed connection: identity, provider and the
/// connection-field values (secrets included — the host routes password-type fields to the OS keychain,
/// exactly as the connection dialog does). <see cref="Folder"/> groups it in the tree.</summary>
public sealed record NewConnectionSpec(
    string Name,
    string ProviderId,
    IReadOnlyDictionary<string, string?> Values,
    string? Folder = null);

/// <summary>A read-only view of one host connection the plugin may build on — e.g. to spin up a container
/// matching it. <see cref="Values"/> are the <em>non-secret</em> field values only (host/port/user/database
/// etc.); passwords stay in the OS keychain and are never handed out. <see cref="Folder"/> is its tree group.</summary>
public sealed record ManagedConnectionInfo(
    string Id,
    string Name,
    string ProviderId,
    string? Folder,
    IReadOnlyDictionary<string, string?> Values);

/// <summary>
/// Lets a subsystem plugin manage <em>real</em> connections in the host's connection list, tagged with the
/// plugin as their origin (which drives a "managed by X" tree badge). On <see cref="IPluginRuntimeContext.Connections"/>
/// when the plugin declared the <see cref="PluginCapabilities.Connections"/> capability. Write access is
/// scoped to the plugin's own connections — <see cref="Remove"/>/<see cref="Mine"/> never touch the user's or
/// another plugin's. <see cref="All"/> is read-only across every connection (non-secret values only), so a
/// plugin can offer "create a local container for this connection". A plugin does not inject raw tree nodes;
/// it creates connections the host already knows how to render.
/// </summary>
public interface IManagedConnections
{
    /// <summary>Create a connection tagged with this plugin as origin; returns its new id.</summary>
    string Create(NewConnectionSpec spec);

    /// <summary>Remove a connection — only if this plugin created it (origin match); otherwise a no-op.</summary>
    void Remove(string connectionId);

    /// <summary>The ids of the connections this plugin created.</summary>
    IReadOnlyList<string> Mine();

    /// <summary>Read every host connection (non-secret values only) — for "new container from a connection".</summary>
    IReadOnlyList<ManagedConnectionInfo> All();
}
