using System.Text.Json;
using Lionear.SqlExplorer.Core.Settings;

namespace Lionear.SqlExplorer.Infrastructure.Persistence;

/// <summary>
/// Stores plugin settings as a single JSON file next to connections.json, shaped
/// <c>{ "&lt;pluginId&gt;": { "&lt;key&gt;": "&lt;value&gt;" } }</c>. Writes are atomic (temp file + replace)
/// and a corrupt/unreadable file degrades to empty rather than crashing — mirrors
/// <see cref="JsonAppSettingsStore"/> / <c>JsonConnectionStore</c>.
/// </summary>
public sealed class JsonPluginSettingsStore : IPluginSettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true
    };

    private readonly string _path;

    public JsonPluginSettingsStore(string? path = null)
    {
        _path = path ?? DefaultPath();
    }

    private static string DefaultPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Lionear", "SqlExplorer");
        return Path.Combine(dir, "plugin-settings.json");
    }

    public IReadOnlyDictionary<string, string?> Get(string pluginId) =>
        ReadAll().TryGetValue(pluginId, out var values) ? values : new Dictionary<string, string?>();

    public void Save(string pluginId, IReadOnlyDictionary<string, string?> values)
    {
        var all = ReadAll();
        all[pluginId] = new Dictionary<string, string?>(values);
        Write(all);
    }

    private Dictionary<string, Dictionary<string, string?>> ReadAll()
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, Dictionary<string, string?>>();
        }

        try
        {
            using var stream = File.OpenRead(_path);
            return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string?>>>(stream, Options)
                   ?? new Dictionary<string, Dictionary<string, string?>>();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new Dictionary<string, Dictionary<string, string?>>();
        }
    }

    private void Write(Dictionary<string, Dictionary<string, string?>> all)
    {
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);

        var temp = Path.Combine(dir, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(all, Options));
        File.Move(temp, _path, overwrite: true);
    }
}
