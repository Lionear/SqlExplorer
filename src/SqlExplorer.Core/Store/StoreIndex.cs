using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlExplorer.Core.Store;

/// <summary>
/// The <c>index.json</c> a store publishes: the plugins it offers plus optional install-time
/// <see cref="StoreBundle"/> groupings. Display metadata lives here (not in the small loader
/// <c>plugin.json</c>) so plugin authors don't have to duplicate it; the Store caches it on install.
/// Versioned via <see cref="SchemaVersion"/> so the format can evolve without guess-parsing.
/// </summary>
public sealed record StoreIndex
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("plugins")]
    public IReadOnlyList<StoreEntry> Plugins { get; init; } = [];

    [JsonPropertyName("bundles")]
    public IReadOnlyList<StoreBundle> Bundles { get; init; } = [];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static StoreIndex Parse(string json) =>
        JsonSerializer.Deserialize<StoreIndex>(json, Options)
        ?? throw new InvalidDataException("Store index deserialised to null.");
}

/// <summary>
/// One plugin as advertised by a store: plugin-level display metadata plus a
/// <see cref="Versions"/> list, so several versions can be installed/chosen side by side. The loader
/// envelope (<c>plugin.json</c>) stays minimal; everything cosmetic lives here.
/// </summary>
public sealed record StoreEntry
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Plugin kind (<c>provider</c>/<c>tool</c>), needed to pick the right host-API version to
    /// gate against and to drive the icon/label — mirrors <c>plugin.json</c>'s <c>type</c>.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("author")]
    public string? Author { get; init; }

    [JsonPropertyName("homepage")]
    public string? Homepage { get; init; }

    [JsonPropertyName("iconUrl")]
    public string? IconUrl { get; init; }

    /// <summary>Capabilities the plugin declares it needs; shown for consent at install (§4.1).</summary>
    [JsonPropertyName("capabilities")]
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    [JsonPropertyName("versions")]
    public IReadOnlyList<StoreVersion> Versions { get; init; } = [];

    /// <summary>The highest SemVer version this host can load, or null if none.</summary>
    public StoreVersion? HighestCompatibleVersion(HostApiCompat host) =>
        Versions
            .Where(v => v.IsCompatible(host))
            .OrderByDescending(v => v.Version, Comparer<string?>.Create(SemVer.Compare))
            .FirstOrDefault();
}

/// <summary>One downloadable version of a <see cref="StoreEntry"/>, with its integrity + compat data.</summary>
public sealed record StoreVersion
{
    [JsonPropertyName("version")]
    public required string Version { get; init; }

    /// <summary>The host API version this build was made against — the single compatibility fact a plugin
    /// declares. The host decides loadability from its own [MinimumSupported, Version] window; a plugin
    /// never declares an upper bound (it can't know which future host breaks it — that's the host's floor).
    /// Defaults to 1.</summary>
    [JsonPropertyName("minHostApiVersion")]
    public int MinHostApiVersion { get; init; } = 1;

    [JsonPropertyName("downloadUrl")]
    public required string DownloadUrl { get; init; }

    /// <summary>Lower-case hex SHA-256 of the <c>.zip</c>; verified after download before install.</summary>
    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }

    /// <summary>Declared download size in bytes; the installer hard-caps the download at this.</summary>
    [JsonPropertyName("size")]
    public long Size { get; init; }

    /// <summary>Optional release notes for this version, markdown (SE-138 phase 2, index schemaVersion 2).
    /// Additive: an older index without it deserialises to null = "no changelog". Never affects loading.</summary>
    [JsonPropertyName("notes")]
    public string? Notes { get; init; }

    /// <summary>True when this host's acceptance window includes this build's <see cref="MinHostApiVersion"/>.</summary>
    public bool IsCompatible(HostApiCompat host) => host.Accepts(MinHostApiVersion);
}

/// <summary>
/// A catalog-level install-time grouping: one "Install all" card that fans out into the normal
/// single-plugin pipeline per <see cref="PluginIds"/>. Purely cosmetic/convenience — it never changes
/// <c>plugin.json</c> or the loader, and the plugins stay individually managed once installed.
/// </summary>
public sealed record StoreBundle
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("pluginIds")]
    public IReadOnlyList<string> PluginIds { get; init; } = [];
}
