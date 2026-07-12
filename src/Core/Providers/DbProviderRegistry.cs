using Lionear.SqlExplorer.Sdk;

namespace Lionear.SqlExplorer.Core.Providers;

public interface IDbProviderRegistry
{
    IReadOnlyCollection<IDbProvider> All { get; }

    IDbProvider Get(DatabaseKind kind);
}

public sealed class DbProviderRegistry : IDbProviderRegistry
{
    private readonly Dictionary<DatabaseKind, IDbProvider> _providers;

    public DbProviderRegistry(IEnumerable<IDbProvider> providers)
    {
        _providers = providers.ToDictionary(p => p.Kind);
    }

    public IReadOnlyCollection<IDbProvider> All => _providers.Values;

    public IDbProvider Get(DatabaseKind kind) =>
        _providers.TryGetValue(kind, out var provider)
            ? provider
            : throw new NotSupportedException($"No provider registered for {kind}.");
}
