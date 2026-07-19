using System.Text.Json;
using SqlExplorer.Core.Connections;

namespace SqlExplorer.Infrastructure.Persistence;

/// <summary>
/// Stores the non-secret part of connections as a single JSON file under the user's config dir.
/// Writes are atomic (temp file + replace) so a crash mid-write can't corrupt the list.
/// Reads migrate the pre-v10 <c>Kind</c> enum field to the new provider-manifest <c>ProviderId</c>,
/// and tolerate both the legacy array-root shape and the current envelope shape (with a folder-order map).
/// </summary>
public sealed class JsonConnectionStore : IConnectionStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true
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

    public IReadOnlyList<SavedConnection> GetAll() => Read().Connections;

    public IReadOnlyDictionary<string, int> GetFolderOrder() => Read().FolderOrder;

    /// <summary>
    /// One-time startup migration: if the file still stores connections with the pre-v10 <c>Kind</c>
    /// enum (no <c>ProviderId</c>), rewrite it so the mapped <c>ProviderId</c> is persisted and the
    /// legacy field is dropped for good. Idempotent — an already-migrated (or absent) file is left
    /// untouched — and best-effort: a read/write failure never blocks startup (the on-read mapping in
    /// <see cref="GetAll"/> still covers it).
    /// </summary>
    public void MigrateLegacyProviderIds()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var (connections, folderOrder, dtos) = ReadWithDtos();
            if (dtos.All(d => d.ProviderId is not null))
            {
                return; // nothing legacy to migrate
            }

            Write(connections.ToList(), folderOrder);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
        }
    }

    public void Save(SavedConnection connection)
    {
        var current = Read();
        var all = current.Connections.Where(c => c.Id != connection.Id).ToList();
        all.Add(connection);
        Write(all, current.FolderOrder);
    }

    public void Delete(string id)
    {
        var current = Read();
        var all = current.Connections.Where(c => c.Id != id).ToList();
        Write(all, current.FolderOrder);
    }

    public void SaveAll(IReadOnlyList<SavedConnection> connections, IReadOnlyDictionary<string, int> folderOrder)
    {
        Write(connections.ToList(), folderOrder);
    }

    private (IReadOnlyList<SavedConnection> Connections, IReadOnlyDictionary<string, int> FolderOrder) Read()
    {
        var (connections, folderOrder, _) = ReadWithDtos();
        return (connections, folderOrder);
    }

    // Reads the file and returns both the mapped connections and the raw DTOs (so the legacy-Kind
    // migration can decide whether a rewrite is needed without re-parsing).
    private (IReadOnlyList<SavedConnection> Connections, IReadOnlyDictionary<string, int> FolderOrder, IReadOnlyList<ConnectionDto> Dtos) ReadWithDtos()
    {
        if (!File.Exists(_path))
        {
            return ([], new Dictionary<string, int>(), []);
        }

        var text = File.ReadAllText(_path);
        var trimmed = text.AsSpan().TrimStart();
        if (trimmed.Length == 0)
        {
            return ([], new Dictionary<string, int>(), []);
        }

        // Legacy shape: root is a bare array of connection DTOs (no folder order). Current shape:
        // envelope object { "connections": [...], "folderOrder": {...} }.
        if (trimmed[0] == '[')
        {
            var legacy = JsonSerializer.Deserialize<List<ConnectionDto>>(text, Options) ?? [];
            var mapped = legacy.Select(ToSavedConnection).ToList();
            return (mapped, new Dictionary<string, int>(), legacy);
        }

        var envelope = JsonSerializer.Deserialize<StoreEnvelope>(text, Options) ?? new StoreEnvelope();
        var dtos = envelope.Connections ?? [];
        var connections = dtos.Select(ToSavedConnection).ToList();
        var folderOrder = envelope.FolderOrder is null
            ? new Dictionary<string, int>()
            : new Dictionary<string, int>(envelope.FolderOrder);
        return (connections, folderOrder, dtos);
    }

    private void Write(List<SavedConnection> connections, IReadOnlyDictionary<string, int> folderOrder)
    {
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);

        var envelope = new StoreEnvelope
        {
            Connections = connections.Select(ToDto).ToList(),
            FolderOrder = folderOrder.Count == 0 ? null : new Dictionary<string, int>(folderOrder)
        };

        var temp = Path.Combine(dir, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(envelope, Options));
        File.Move(temp, _path, overwrite: true);
    }

    private static SavedConnection ToSavedConnection(ConnectionDto dto) => new()
    {
        Id = dto.Id,
        Name = dto.Name,
        ProviderId = dto.ProviderId ?? MigrateLegacyKind(dto.Kind),
        Color = dto.Color,
        ReadOnly = dto.ReadOnly,
        Folder = dto.Folder,
        AiAccess = dto.AiAccess,
        ExcludeFromMcp = dto.ExcludeFromMcp,
        Values = dto.Values ?? new Dictionary<string, string?>(),
        SortOrder = dto.SortOrder,
        Origin = dto.Origin
    };

    private static ConnectionDto ToDto(SavedConnection connection) => new()
    {
        Id = connection.Id,
        Name = connection.Name,
        ProviderId = connection.ProviderId,
        Color = connection.Color,
        ReadOnly = connection.ReadOnly,
        Folder = connection.Folder,
        AiAccess = connection.AiAccess,
        ExcludeFromMcp = connection.ExcludeFromMcp,
        Values = connection.Values.ToDictionary(kv => kv.Key, kv => kv.Value),
        SortOrder = connection.SortOrder,
        Origin = connection.Origin
    };

    // Files written before host-API v10 carry a "Kind" enum name instead of a "ProviderId".
    // Map the built-in engines onto their manifest ids; new files already have ProviderId.
    private static string MigrateLegacyKind(string? legacyKind) => legacyKind switch
    {
        "PostgreSql" => "postgres",
        "MySql" => "mysql",
        "Sqlite" => "sqlite",
        "SqlServer" => "sqlserver",
        "Oracle" => "oracle",
        not null => legacyKind, // unknown/third-party legacy value: keep it verbatim
        null => throw new InvalidDataException("Stored connection has neither 'ProviderId' nor legacy 'Kind'.")
    };

    private sealed record StoreEnvelope
    {
        public List<ConnectionDto>? Connections { get; init; }
        public Dictionary<string, int>? FolderOrder { get; init; }
    }

    /// <summary>Read shape tolerant of both the new <c>ProviderId</c> and the legacy <c>Kind</c> field.</summary>
    private sealed record ConnectionDto
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string? ProviderId { get; init; }
        public string? Kind { get; init; }
        public string? Color { get; init; }
        public bool ReadOnly { get; init; }
        public string? Folder { get; init; }
        public AiAccessMode AiAccess { get; init; } = AiAccessMode.None;
        public bool ExcludeFromMcp { get; init; }
        public Dictionary<string, string?>? Values { get; init; }
        public int SortOrder { get; init; }

        /// <summary>The plugin that created this connection (SE-164), or null for a user connection.
        /// Persisted so the "Managed" badge and origin-scoped IManagedConnections survive a restart.</summary>
        public string? Origin { get; init; }
    }
}
