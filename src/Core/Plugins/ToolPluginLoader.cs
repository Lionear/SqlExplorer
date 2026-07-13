using Lionear.SqlExplorer.Sdk.Tools;

namespace Lionear.SqlExplorer.Core.Plugins;

/// <summary>Outcome of loading one tool plugin folder: the tools it contributed (an assembly may ship
/// several), or an error explaining why it was skipped.</summary>
public sealed record ToolLoadResult(string PluginDirectory, string? Id, IReadOnlyList<IToolPlugin> Tools, string? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// Loads <c>type: "tool"</c> plugins. Mirrors <see cref="ProviderPluginLoader"/> and reuses
/// <see cref="ProviderLoadContext"/> (which already shares the SDK + Avalonia with the host, so a tool's
/// declared types and any Route-B view keep one type identity across the boundary). Unlike a provider,
/// one tool assembly may contain several <see cref="IToolPlugin"/> implementations — all are instantiated.
/// </summary>
public sealed class ToolPluginLoader
{
    public IReadOnlyList<ToolLoadResult> Load(string pluginsRoot)
    {
        if (!Directory.Exists(pluginsRoot))
        {
            return [];
        }

        var results = new List<ToolLoadResult>();
        foreach (var dir in Directory.EnumerateDirectories(pluginsRoot).OrderBy(d => d))
        {
            var manifestPath = Path.Combine(dir, "plugin.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            if (LoadOne(dir, manifestPath) is { } result)
            {
                results.Add(result);
            }
        }

        return results;
    }

    // Returns null for a non-tool plugin (the provider loader handles those); otherwise a load result.
    private static ToolLoadResult? LoadOne(string dir, string manifestPath)
    {
        try
        {
            var manifest = PluginManifest.Load(manifestPath);
            if (manifest.Type != PluginManifest.Types.Tool)
            {
                return null;
            }

            if (!ToolHostApi.IsCompatible(manifest.HostApiVersion))
            {
                return new ToolLoadResult(dir, manifest.Id, [],
                    $"Tool '{manifest.Id}' targets tool API v{manifest.HostApiVersion}, this host is v{ToolHostApi.Version}.");
            }

            var assemblyPath = Path.Combine(dir, manifest.EntryAssembly);
            if (!File.Exists(assemblyPath))
            {
                return new ToolLoadResult(dir, manifest.Id, [],
                    $"Entry assembly '{manifest.EntryAssembly}' not found in '{dir}'.");
            }

            var context = new ProviderLoadContext(assemblyPath);
            var assembly = context.LoadFromAssemblyPath(assemblyPath);

            var tools = assembly.GetTypes()
                .Where(t => typeof(IToolPlugin).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
                .Select(Activator.CreateInstance)
                .OfType<IToolPlugin>()
                .ToList();

            return tools.Count == 0
                ? new ToolLoadResult(dir, manifest.Id, [], $"Assembly '{manifest.EntryAssembly}' has no public IToolPlugin implementation.")
                : new ToolLoadResult(dir, manifest.Id, tools, null);
        }
        catch (Exception ex)
        {
            return new ToolLoadResult(dir, null, [], ex.Message);
        }
    }
}
