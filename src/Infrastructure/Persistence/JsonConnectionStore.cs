using System.Text.Json;
using Lionear.SqlExplorer.Core.Connections;

namespace Lionear.SqlExplorer.Infrastructure.Persistence;

/// <summary>
/// Stores the non-secret part of connections as a single JSON file under the user's config dir.
/// Writes are atomic (temp file + replace) so a crash mid-write can't corrupt the list.
/// Reads migrate the pre-v10 <c>Kind</c> enum field to the new provider-manifest <c>ProviderId</c>.
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

    public IReadOnlyList<SavedConnection> GetAll()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        using var stream = File.OpenRead(_path);
        var dtos = JsonSerializer.Deserialize<List<ConnectionDto>>(stream, Options) ?? [];
        return dtos.Select(ToSavedConnection).ToList();
    }

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

            List<ConnectionDto> dtos;
            using (var stream = File.OpenRead(_path))
            {
                dtos = JsonSerializer.Deserialize<List<ConnectionDto>>(stream, Options) ?? [];
            }

            if (dtos.All(d => d.ProviderId is not null))
            {
                return; // nothing legacy to migrate
            }

            Write(dtos.Select(ToSavedConnection).ToList());
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
        }
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

    private static SavedConnection ToSavedConnection(ConnectionDto dto) => new()
    {
        Id = dto.Id,
        Name = dto.Name,
        ProviderId = dto.ProviderId ?? MigrateLegacyKind(dto.Kind),
        Color = dto.Color,
        ReadOnly = dto.ReadOnly,
        Values = dto.Values ?? new Dictionary<string, string?>()
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

    /// <summary>Read shape tolerant of both the new <c>ProviderId</c> and the legacy <c>Kind</c> field.</summary>
    private sealed record ConnectionDto
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string? ProviderId { get; init; }
        public string? Kind { get; init; }
        public string? Color { get; init; }
        public bool ReadOnly { get; init; }
        public Dictionary<string, string?>? Values { get; init; }
    }
}
