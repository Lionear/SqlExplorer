using Lionear.SqlExplorer.Sdk;

namespace Lionear.SqlExplorer.Core.Plugins;

/// <summary>Outcome of scanning one plugin folder — a loaded provider or a reason it was skipped.</summary>
public sealed record ProviderLoadResult(string PluginDirectory, IDbProvider? Provider, string? Error)
{
    public bool Succeeded => Provider is not null;
}

/// <summary>
/// Discovers plugins under a root folder (one subfolder per plugin, each with a <c>plugin.json</c>),
/// and loads those of type <see cref="PluginManifest.Types.Provider"/> in their own
/// <see cref="ProviderLoadContext"/>, returning the <see cref="IDbProvider"/> instances. Other plugin
/// types are skipped here — the <c>type</c> discriminator is the seam where a future generic loader
/// dispatches to other handlers. First-party providers load through this exact path too, with no
/// privileged access over third-party ones.
/// </summary>
public sealed class ProviderPluginLoader
{
    public IReadOnlyList<ProviderLoadResult> Load(string pluginsRoot)
    {
        if (!Directory.Exists(pluginsRoot))
        {
            return [];
        }

        var results = new List<ProviderLoadResult>();
        foreach (var dir in Directory.EnumerateDirectories(pluginsRoot).OrderBy(d => d))
        {
            var manifestPath = Path.Combine(dir, "plugin.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            results.Add(LoadOne(dir, manifestPath));
        }

        return results;
    }

    private static ProviderLoadResult LoadOne(string dir, string manifestPath)
    {
        try
        {
            var manifest = PluginManifest.Load(manifestPath);

            if (manifest.Type != PluginManifest.Types.Provider)
            {
                return new ProviderLoadResult(dir, null,
                    $"Plugin '{manifest.Id}' is type '{manifest.Type}', not a provider — skipped.");
            }

            if (!ProviderHostApi.IsCompatible(manifest.HostApiVersion))
            {
                return new ProviderLoadResult(dir, null,
                    $"Plugin '{manifest.Id}' targets host API v{manifest.HostApiVersion}, " +
                    $"this host is v{ProviderHostApi.Version}.");
            }

            var assemblyPath = Path.Combine(dir, manifest.EntryAssembly);
            if (!File.Exists(assemblyPath))
            {
                return new ProviderLoadResult(dir, null,
                    $"Entry assembly '{manifest.EntryAssembly}' not found in '{dir}'.");
            }

            var context = new ProviderLoadContext(assemblyPath);
            var assembly = context.LoadFromAssemblyPath(assemblyPath);

            var providerType = assembly.GetTypes()
                .FirstOrDefault(t => typeof(IDbProvider).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false });

            if (providerType is null)
            {
                return new ProviderLoadResult(dir, null,
                    $"Assembly '{manifest.EntryAssembly}' has no public IDbProvider implementation.");
            }

            if (Activator.CreateInstance(providerType) is not IDbProvider provider)
            {
                return new ProviderLoadResult(dir, null,
                    $"Could not instantiate '{providerType.FullName}' as IDbProvider.");
            }

            return new ProviderLoadResult(dir, provider, null);
        }
        catch (Exception ex)
        {
            return new ProviderLoadResult(dir, null, ex.Message);
        }
    }
}
