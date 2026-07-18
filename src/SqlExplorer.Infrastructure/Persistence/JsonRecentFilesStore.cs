using System.Text.Json;
using SqlExplorer.Core.Session;

namespace SqlExplorer.Infrastructure.Persistence;

/// <summary>
/// Recently opened/saved <c>.sql</c> paths in a single <c>recent-files.json</c> beside settings.json.
/// Newest-first, capped at <see cref="Capacity"/>, de-duplicated case-sensitively by path. Same atomic
/// write + degrade-to-empty idiom as the other JSON stores; entries are cached in memory after first load.
/// Mirrors <see cref="JsonQueryHistoryStore"/>.
/// </summary>
public sealed class JsonRecentFilesStore : IRecentFilesStore
{
    private const int Capacity = 12;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();
    private List<string>? _entries; // newest first; null until loaded

    public event Action? Changed;

    public JsonRecentFilesStore(string? path = null)
    {
        _path = path ?? DefaultPath();
    }

    private static string DefaultPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Lionear", "SqlExplorer");
        return Path.Combine(dir, "recent-files.json");
    }

    public IReadOnlyList<string> GetRecent()
    {
        lock (_gate)
        {
            return Load().ToList();
        }
    }

    public void Add(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        lock (_gate)
        {
            var entries = Load();
            entries.RemoveAll(p => string.Equals(p, path, StringComparison.Ordinal));
            entries.Insert(0, path);
            if (entries.Count > Capacity)
            {
                entries.RemoveRange(Capacity, entries.Count - Capacity);
            }

            Write(entries);
        }

        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries = [];
            Write(_entries);
        }

        Changed?.Invoke();
    }

    private List<string> Load()
    {
        if (_entries is not null)
        {
            return _entries;
        }

        if (!File.Exists(_path))
        {
            return _entries = [];
        }

        try
        {
            using var stream = File.OpenRead(_path);
            return _entries = JsonSerializer.Deserialize<List<string>>(stream, Options) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return _entries = [];
        }
    }

    private void Write(List<string> entries)
    {
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);

        var temp = Path.Combine(dir, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(entries, Options));
        File.Move(temp, _path, overwrite: true);
    }
}
