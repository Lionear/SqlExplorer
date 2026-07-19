using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlExplorer.Core.Plugins;

/// <summary>
/// The <c>plugin.json</c> that sits next to every installed plugin. It is the loader/package
/// envelope shared by all plugin kinds: <see cref="Type"/> discriminates what the plugin is
/// (currently only <see cref="Types.Provider"/>) and type-specific loading is decided from there.
/// This is the on-disk contract a third party ships, so it is deliberately small and versioned via
/// <see cref="SchemaVersion"/>; richer capability/signing fields land with the gallery (Notes.md §4.1).
/// </summary>
public sealed record PluginManifest
{
    /// <summary>Version of this manifest format itself, so it can evolve without guess-parsing.</summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>What kind of plugin this is; the loader dispatches on it. See <see cref="Types"/>.</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>The plugin's own version (SemVer). Used later for gallery listing/updates.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    /// <summary>Host API version the plugin was built against; the loader gates on it.</summary>
    [JsonPropertyName("hostApiVersion")]
    public required int HostApiVersion { get; init; }

    /// <summary>Entry assembly for a compiled (Layer B) plugin, relative to the plugin folder.</summary>
    [JsonPropertyName("entryAssembly")]
    public required string EntryAssembly { get; init; }

    /// <summary>Optional embedded-resource prefix for the plugin's localization files (e.g.
    /// <c>"Lang/strings"</c> for <c>Lang/strings.json</c> + <c>Lang/strings.nl.json</c>). Absent = the
    /// plugin ships no translations and its hardcoded (English) strings are used as-is.</summary>
    [JsonPropertyName("localization")]
    public string? Localization { get; init; }

    /// <summary>The capabilities a standing-subsystem plugin declares it needs (SqlExplorer.Sdk.Extensibility
    /// <c>PluginCapabilities</c>: storage/connections/panel/background/menu/process). The user consents at
    /// install; the runtime gates each host power on this list. Empty for the classic provider/tool/mcp
    /// plugins that don't use the extensibility seams (SE-164).</summary>
    [JsonPropertyName("capabilities")]
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static PluginManifest Load(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<PluginManifest>(stream, Options)
            ?? throw new InvalidDataException($"Manifest '{path}' deserialised to null.");
    }

    /// <summary>Known plugin types (the <see cref="Type"/> discriminator).</summary>
    public static class Types
    {
        public const string Provider = "provider";
        public const string Tool = "tool";

        /// <summary>Contributes MCP tools to the host-owned MCP server (see <c>IMcpToolProvider</c>).</summary>
        public const string Mcp = "mcp";

        /// <summary>A standing-subsystem plugin (SE-164): loaded by <c>SubsystemPluginLoader</c>, drives its
        /// behaviour off declared <see cref="Capabilities"/> rather than this discriminator. The value is a
        /// loader-hint so the provider/tool/mcp loaders skip it; capabilities — not the type — decide what it
        /// may do.</summary>
        public const string Extension = "extension";

        /// <summary>Whether <paramref name="type"/> is a plugin type this host knows how to load.</summary>
        public static bool IsKnown(string? type) =>
            type is Provider or Tool or Mcp or Extension;
    }
}
