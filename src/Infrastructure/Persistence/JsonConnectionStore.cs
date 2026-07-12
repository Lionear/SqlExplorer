using System.Text.Json;
using System.Text.Json.Serialization;
using Lionear.SqlExplorer.Core.Connections;

namespace Lionear.SqlExplorer.Infrastructure.Persistence;

/// <summary>
/// Stores the non-secret part of connections as a single JSON file under the user's config dir.
/// Writes are atomic (temp file + replace) so a crash mid-write can't corrupt the list.
/// </summary>
public sealed class JsonConnectionStore : IConnectionStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;

    public JsonConnectionStore(string? path = null)
    {
        _path = path ?? DefaultPath();
    }

    private static string DefaultPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Lionear", "SqlExplorer");
        return Path.Combine(dir, "connections.json");
    }

    public IReadOnlyList<SavedConnection> GetAll()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        using var stream = File.OpenRead(_path);
        return JsonSerializer.Deserialize<List<SavedConnection>>(stream, Options) ?? [];
    }

    public void Save(SavedConnection connection)
    {
        var all = GetAll().Where(c => c.Id != connection.Id).ToList();
        all.Add(connection);
        Write(all);
    }

    public void Delete(string id)
    {
        var all = GetAll().Where(c => c.Id != id).ToList();
        Write(all);
    }

    private void Write(List<SavedConnection> connections)
    {
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);

        var temp = Path.Combine(dir, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(connections, Options));
        File.Move(temp, _path, overwrite: true);
    }
}
