using System.Text.Json;
using System.Text.Json.Serialization;
using Lionear.SqlExplorer.Core.Settings;

namespace Lionear.SqlExplorer.Infrastructure.Persistence;

/// <summary>
/// Stores UI preferences as a single JSON file under the user's config dir, next to
/// connections.json. Writes are atomic (temp file + replace) so a crash mid-write can't
/// corrupt the file, and a corrupt/unreadable file degrades to defaults rather than crashing.
/// </summary>
public sealed class JsonAppSettingsStore : IAppSettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;

    public JsonAppSettingsStore(string? path = null)
    {
        _path = path ?? DefaultPath();
    }

    private static string DefaultPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Lionear", "SqlExplorer");
        return Path.Combine(dir, "settings.json");
    }

    public AppSettings Load()
    {
        if (!File.Exists(_path))
        {
            return new AppSettings();
        }

        try
        {
            using var stream = File.OpenRead(_path);
            return JsonSerializer.Deserialize<AppSettings>(stream, Options) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A malformed or briefly-locked settings file must never block startup.
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);

        var temp = Path.Combine(dir, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
        File.Move(temp, _path, overwrite: true);
    }
}
