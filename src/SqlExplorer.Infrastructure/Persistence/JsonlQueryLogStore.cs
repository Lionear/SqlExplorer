using System.Text.Json;
using System.Text.Json.Serialization;
using SqlExplorer.Core.History;
using SqlExplorer.Core.Logging;

namespace SqlExplorer.Infrastructure.Persistence;

/// <summary>
/// Append-only query log as <c>query-log.jsonl</c> (one JSON object per line) in the app data dir. Appends
/// a single line per query — no whole-file rewrite — and rotates to a single <c>.1</c> backup once the file
/// passes the configured size. The policy (enabled + per-source scope) is held in memory and set via
/// <see cref="Configure"/>, so recording a query never reads settings from disk. All I/O is best-effort:
/// a failure to write or rotate is swallowed rather than allowed to break query execution.
/// </summary>
public sealed class JsonlQueryLogStore : IQueryLog
{
    // Compact (not indented) so each entry is exactly one line — the whole point of JSONL.
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly long _maxBytes;
    private readonly object _gate = new();

    private bool _enabled;
    private bool _logApp = true;
    private bool _logMcp = true;

    public event Action? Changed;

    public JsonlQueryLogStore(string? path = null, int maxSizeMb = 10)
    {
        _path = path ?? DefaultPath();
        _maxBytes = Math.Max(1, maxSizeMb) * 1024L * 1024L;
    }

    private static string DefaultPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Lionear", "SqlExplorer");
        return Path.Combine(dir, "query-log.jsonl");
    }

    public void Configure(bool enabled, bool logApp, bool logMcp)
    {
        lock (_gate)
        {
            _enabled = enabled;
            _logApp = logApp;
            _logMcp = logMcp;
        }
    }

    public void Record(QueryHistoryEntry entry)
    {
        lock (_gate)
        {
            if (!_enabled)
            {
                return;
            }

            var inScope = entry.Source switch
            {
                QueryHistorySource.User => _logApp,
                QueryHistorySource.Ai => _logMcp,
                _ => false
            };
            if (!inScope)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                RotateIfNeeded();
                File.AppendAllText(_path, JsonSerializer.Serialize(entry, Options) + "\n");
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return; // Logging must never break a query.
            }
        }

        Changed?.Invoke();
    }

    // Move the current log aside to a single-generation backup once it grows past the cap. Keeping one
    // previous file means the viewer still sees entries written just before a rotation (it reads both).
    private void RotateIfNeeded()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length < _maxBytes)
        {
            return;
        }

        var backup = _path + ".1";
        try
        {
            if (File.Exists(backup))
            {
                File.Delete(backup);
            }
            File.Move(_path, backup);
        }
        catch (IOException)
        {
            // If rotation fails, keep appending to the current file rather than losing the write.
        }
    }

    public IReadOnlyList<QueryHistoryEntry> Read(QueryLogFilter filter)
    {
        lock (_gate)
        {
            var entries = new List<QueryHistoryEntry>();
            ReadFile(_path, entries);
            ReadFile(_path + ".1", entries);

            IEnumerable<QueryHistoryEntry> query = entries;
            if (filter.Source is { } source)
            {
                query = query.Where(e => e.Source == source);
            }
            if (filter.Success is { } success)
            {
                query = query.Where(e => e.Success == success);
            }
            if (filter.SinceUtc is { } since)
            {
                query = query.Where(e => e.TimestampUtc >= since);
            }
            if (!string.IsNullOrWhiteSpace(filter.Text))
            {
                query = query.Where(e => e.Sql.Contains(filter.Text, StringComparison.OrdinalIgnoreCase)
                                      || e.ConnectionName.Contains(filter.Text, StringComparison.OrdinalIgnoreCase));
            }

            return query.OrderByDescending(e => e.TimestampUtc).Take(filter.Limit).ToList();
        }
    }

    private static void ReadFile(string file, List<QueryHistoryEntry> into)
    {
        if (!File.Exists(file))
        {
            return;
        }

        try
        {
            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    if (JsonSerializer.Deserialize<QueryHistoryEntry>(line, Options) is { } entry)
                    {
                        into.Add(entry);
                    }
                }
                catch (JsonException)
                {
                    // Skip a single malformed line rather than discarding the whole log.
                }
            }
        }
        catch (IOException)
        {
            // An unreadable/locked file degrades to "no entries from here".
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                }
                if (File.Exists(_path + ".1"))
                {
                    File.Delete(_path + ".1");
                }
            }
            catch (IOException)
            {
                // Best-effort clear.
            }
        }

        Changed?.Invoke();
    }
}
