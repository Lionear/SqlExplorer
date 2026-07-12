using System.Reflection;
using System.Runtime.Loader;
using Lionear.SqlExplorer.Sdk;

namespace Lionear.SqlExplorer.Core.Plugins;

/// <summary>
/// Isolated load context for one provider plugin. Private dependencies (Npgsql,
/// Microsoft.Data.Sqlite, the native SQLite, …) load from the plugin folder via the
/// plugin's own <c>.deps.json</c>, so two plugins can pin different driver versions.
/// </summary>
/// <remarks>
/// The shared contract assembly (<c>Provider.Sdk</c>) is intentionally NOT resolved
/// here: <see cref="Load"/> returns <c>null</c> for anything the default context already
/// provides, so <c>IDbProvider</c> keeps a single type identity across the boundary —
/// without that, the cast to <c>IDbProvider</c> in the loader would fail at runtime.
/// Non-collectible: providers load once at startup and live for the process (matches the
/// Notes §4.2 assumption — "no real unload", desktop-only, not a security boundary).
/// </remarks>
public sealed class ProviderLoadContext : AssemblyLoadContext
{
    // The shared contract must resolve to the host's single copy, whatever the plugin's
    // deps.json claims — derived from the type so it can't drift from the real assembly name.
    private static readonly string? SharedContractAssembly = typeof(IDbProvider).Assembly.GetName().Name;

    private readonly AssemblyDependencyResolver _resolver;

    public ProviderLoadContext(string entryAssemblyPath)
        : base(name: Path.GetFileNameWithoutExtension(entryAssemblyPath), isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(entryAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Hand the shared contract to the default context (returning null falls back to it),
        // so IDbProvider keeps one type identity across the boundary.
        if (assemblyName.Name == SharedContractAssembly)
        {
            return null;
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
