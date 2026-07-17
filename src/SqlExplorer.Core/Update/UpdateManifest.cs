using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlExplorer.Core.Update;

/// <summary>
/// The <c>update.json</c> a channel publishes as a release asset — the single source of truth for "is there
/// a newer build?". A rolling tag (<c>nightly</c>/<c>preview</c>) keeps the same name across builds, so the
/// tag says nothing; this manifest, refreshed every build, does. Mirrors the Store's index model
/// (<c>StoreIndex</c>): versioned via <see cref="SchemaVersion"/>, parsed leniently.
/// </summary>
public sealed record UpdateManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    /// <summary>The channel this manifest describes (<c>stable</c>/<c>preview</c>/<c>nightly</c>).</summary>
    [JsonPropertyName("channel")]
    public string? Channel { get; init; }

    /// <summary>The build's full version stamp, e.g. <c>0.2.0-nightly.20260717.42</c> or <c>0.2.0</c>.</summary>
    [JsonPropertyName("version")]
    public required string Version { get; init; }

    /// <summary>Short commit the build was cut from; shown in the changelog for traceability.</summary>
    [JsonPropertyName("commit")]
    public string? Commit { get; init; }

    /// <summary>ISO-8601 publish timestamp.</summary>
    [JsonPropertyName("publishedAt")]
    public string? PublishedAt { get; init; }

    /// <summary>Release notes as markdown (rendered by the changelog dialog).</summary>
    [JsonPropertyName("notes")]
    public string? Notes { get; init; }

    /// <summary>Per-RID download assets, keyed by <c>win-x64</c>, <c>win-x64-setup</c>, <c>linux-x64</c>, ….</summary>
    [JsonPropertyName("assets")]
    public IReadOnlyDictionary<string, UpdateAsset> Assets { get; init; } =
        new Dictionary<string, UpdateAsset>();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static UpdateManifest Parse(string json) =>
        JsonSerializer.Deserialize<UpdateManifest>(json, Options)
        ?? throw new InvalidDataException("Update manifest deserialised to null.");
}

/// <summary>One downloadable artifact in an <see cref="UpdateManifest"/>, with its integrity data.</summary>
public sealed record UpdateAsset
{
    [JsonPropertyName("url")]
    public required string Url { get; init; }

    /// <summary>Hex SHA-256 of the file; verified after download (case-insensitive compare).</summary>
    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }

    /// <summary>Artifact kind: <c>installer</c>, <c>zip</c>, <c>appimage</c>, <c>dmg</c>. Drives how it's applied.</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    /// <summary>Declared size in bytes (0 = unknown); the downloader hard-caps at this.</summary>
    [JsonPropertyName("size")]
    public long Size { get; init; }
}
