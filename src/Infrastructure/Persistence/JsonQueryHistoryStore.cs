using System.Text.Json;
using System.Text.Json.Serialization;
using Lionear.SqlExplorer.Core.History;

namespace Lionear.SqlExplorer.Infrastructure.Persistence;

/// <summary>
/// Query history in a single <c>history.json</c> beside connections.json. A ring buffer keeps the
/// newest <see cref="Capacity"/> entries; writes are atomic (temp + replace) and a corrupt/unreadable
/// file degrades to empty rather than crashing. Entries are cached in memory after first load so an
/// append doesn't re-parse the whole file.
/// </summary>
public sealed class JsonQueryHistoryStore : IQueryHistoryStore
{
    private const int Capacity = 1000;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly object _gate = new();
    private List<QueryHistoryEntry>? _entries; // oldest first; null until loaded

    public event Action? Changed;

    public JsonQueryHistoryStore(string? path = null)
    {
        _path = path ?? DefaultPath();
    }

    private static string DefaultPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Lionear", "SqlExplorer");
        return Path.Combine(dir, "history.json");
    }

    public void Append(QueryHistoryEntry entry)
    {
        lock (_gate)
        {
            var entries = Load();
            entries.Add(entry);
            if (entries.Count > Capacity)
            {
                entries.RemoveRange(0, entries.Count - Capacity);
            }

            Write(entries);
        }

        Changed?.Invoke();
    }

    public IReadOnlyList<QueryHistoryEntry> GetRecent(int limit)
    {
        lock (_gate)
        {
            return Newest(Load()).Take(limit).ToList();
        }
    }

    public IReadOnlyList<QueryHistoryEntry> Search(string text)
    {
        lock (_gate)
        {
            var newest = Newest(Load());
            if (string.IsNullOrWhiteSpace(text))
            {
                return newest.ToList();
            }

            return newest
                .Where(e => e.Sql.Contains(text, StringComparison.OrdinalIgnoreCase)
                            || e.ConnectionName.Contains(text, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
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

    private static IEnumerable<QueryHistoryEntry> Newest(List<QueryHistoryEntry> entries) =>
        Enumerable.Reverse(entries);

    private List<QueryHistoryEntry> Load()
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
            return _entries = JsonSerializer.Deserialize<List<QueryHistoryEntry>>(stream, Options) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return _entries = [];
        }
    }

    private void Write(List<QueryHistoryEntry> entries)
    {
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);

        var temp = Path.Combine(dir, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(entries, Options));
        File.Move(temp, _path, overwrite: true);
    }
}
