namespace SqlExplorer.Core.Update;

/// <summary>
/// Fetches the <c>update.json</c> manifest for a channel. The transport (https-only, size-capped) lives in
/// the infrastructure implementation; the service depends only on this so it stays testable offline.
/// </summary>
public interface IUpdateManifestSource
{
    /// <summary>
    /// Returns the channel's manifest, or <c>null</c> when it can't be fetched or parsed (offline, 404,
    /// malformed). Never throws for the expected failure modes — a check that can't reach the network is a
    /// silent no-op, not an error the user sees.
    /// </summary>
    Task<UpdateManifest?> FetchAsync(UpdateChannel channel, CancellationToken ct);
}
