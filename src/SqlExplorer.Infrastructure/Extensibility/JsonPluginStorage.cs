using System.Text.Json;
using SqlExplorer.Sdk.Extensibility;

namespace SqlExplorer.Infrastructure.Extensibility;

/// <summary>
/// JSON-backed <see cref="IPluginStorage"/>: one file per key under a plugin-scoped folder that lives
/// <em>outside</em> the plugin's install dir (so it survives updates and is removed on uninstall). Atomic
/// temp-file + <see cref="File.Move(string,string,bool)"/> writes and degrade-to-default reads, matching the
/// host's own stores. Each plugin gets its own instance (id fixes the folder), so calls are serialised per
/// plugin by a lock.
/// </summary>
public sealed class JsonPluginStorage : IPluginStorage
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _dir;
    private readonly object _gate = new();

    public JsonPluginStorage(string pluginId, string? rootDir = null)
    {
        _dir = Path.Combine(rootDir ?? DefaultRoot(), Sanitize(pluginId));
    }

    public T? Load<T>(string key)
    {
        lock (_gate)
        {
            try
            {
                var path = PathFor(key);
                return File.Exists(path)
                    ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
                    : default;
            }
            catch (Exception)
            {
                return default;
            }
        }
    }

    public void Save<T>(string key, T value)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_dir);
            var path = PathFor(key);
            var temp = Path.Combine(_dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
            File.Move(temp, path, overwrite: true);
        }
    }

    public void Delete(string key)
    {
        lock (_gate)
        {
            var path = PathFor(key);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private string PathFor(string key) => Path.Combine(_dir, Sanitize(key) + ".json");

    private static string Sanitize(string value) =>
        string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    // Deliberately NOT under the install dir (…/plugins/<id>): that folder is replaced wholesale on update.
    private static string DefaultRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Lionear", "SqlExplorer", "plugin-data");
}
