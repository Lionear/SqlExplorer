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
/// The shared contract assembly (<c>Lionear.SqlExplorer.Sdk</c>, whose Ui/ capabilities carry the
/// Route-B <c>ICustomConnectionUi</c>) and Avalonia are intentionally NOT resolved here: <see cref="Load"/>
/// returns <c>null</c> for them so the default context provides the host's single copy, keeping one type
/// identity across the boundary — without that, the cast to <c>IDbProvider</c> (or an
/// <c>ICustomConnectionUi</c> returning an Avalonia <c>Control</c>) would fail at runtime. Everything else
/// (Npgsql, the driver, …) still loads privately from the plugin folder so two plugins can pin different
/// versions. Non-collectible: providers load once at startup and live for the process (matches the
/// Notes §4.2 assumption — "no real unload", desktop-only, not a security boundary).
/// </remarks>
public sealed class ProviderLoadContext : AssemblyLoadContext
{
    // The one SDK assembly — derived from the type so it can't drift from the real assembly name.
    private static readonly string SdkAssembly = typeof(IDbProvider).Assembly.GetName().Name!;

    /// <summary>
    /// Whether an assembly must resolve to the host's copy (single type identity) rather than load
    /// privately in the plugin's context: the SDK contract and all of Avalonia. A Route-B provider's
    /// custom view is an Avalonia <c>Control</c>, so host and plugin must share those exact types.
    /// </summary>
    public static bool ShouldShareWithHost(string? assemblyName) =>
        assemblyName is not null
        && (assemblyName == SdkAssembly
            || assemblyName.StartsWith("Avalonia", StringComparison.Ordinal));

    private readonly AssemblyDependencyResolver _resolver;

    public ProviderLoadContext(string entryAssemblyPath)
        : base(name: Path.GetFileNameWithoutExtension(entryAssemblyPath), isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(entryAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Hand shared assemblies to the default context (returning null falls back to it),
        // so the SDK contracts and Avalonia keep one type identity across the boundary.
        if (ShouldShareWithHost(assemblyName.Name))
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
