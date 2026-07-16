using System.Diagnostics.CodeAnalysis;
using SqlExplorer.Sdk;

namespace SqlExplorer.Core.Providers;

/// <summary>One loaded provider paired with the manifest <c>id</c> that identifies its engine.</summary>
public sealed record ProviderRegistration(string Id, IDbProvider Provider);

public interface IDbProviderRegistry
{
    IReadOnlyList<ProviderRegistration> All { get; }

    IDbProvider Get(string providerId);

    /// <summary>Non-throwing lookup for call sites that must survive a saved connection whose provider
    /// plugin isn't installed (e.g. a non-default provider like MongoDB left out of a build).</summary>
    bool TryGet(string providerId, [NotNullWhen(true)] out IDbProvider? provider);
}

/// <summary>
/// Resolves a provider from its manifest id. Identity lives in the plugin manifest, not in the
/// provider or a host enum, so the set of engines stays open to third-party providers.
/// </summary>
public sealed class DbProviderRegistry : IDbProviderRegistry
{
    private readonly List<ProviderRegistration> _all;
    private readonly Dictionary<string, IDbProvider> _byId;

    public DbProviderRegistry(IEnumerable<ProviderRegistration> registrations)
    {
        _all = registrations.ToList();
        _byId = _all.ToDictionary(r => r.Id, r => r.Provider);
    }

    public IReadOnlyList<ProviderRegistration> All => _all;

    public IDbProvider Get(string providerId) =>
        _byId.TryGetValue(providerId, out var provider)
            ? provider
            : throw new NotSupportedException($"No provider registered with id '{providerId}'.");

    public bool TryGet(string providerId, [NotNullWhen(true)] out IDbProvider? provider) => _byId.TryGetValue(providerId, out provider);
}
