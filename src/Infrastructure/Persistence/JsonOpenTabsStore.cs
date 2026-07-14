using System.Text.Json;
using Lionear.SqlExplorer.Core.Session;

namespace Lionear.SqlExplorer.Infrastructure.Persistence;

/// <summary>
/// Persists the open query tabs as open-tabs.json under the user's config dir, beside settings.json.
/// Same atomic write + degrade-to-empty idiom as the other JSON stores: a missing or malformed file
/// just means "no session to restore".
/// </summary>
public sealed class JsonOpenTabsStore : IOpenTabsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;

    public JsonOpenTabsStore(string? path = null)
    {
        _path = path ?? DefaultPath();
    }

    private static string DefaultPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Lionear", "SqlExplorer");
        return Path.Combine(dir, "open-tabs.json");
    }

    public IReadOnlyList<OpenTabState> Load()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            using var stream = File.OpenRead(_path);
            return JsonSerializer.Deserialize<List<OpenTabState>>(stream, Options) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A malformed or briefly-locked session file must never block startup.
            return [];
        }
    }

    public void Save(IReadOnlyList<OpenTabState> tabs)
    {
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);

        var temp = Path.Combine(dir, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(tabs, Options));
        File.Move(temp, _path, overwrite: true);
    }
}
