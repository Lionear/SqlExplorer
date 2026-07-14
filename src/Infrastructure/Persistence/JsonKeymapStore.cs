using System.Text.Json;
using Lionear.SqlExplorer.Core.Shortcuts;

namespace Lionear.SqlExplorer.Infrastructure.Persistence;

/// <summary>
/// Persists keyboard-shortcut overrides as keymap.json under the user's config dir, beside
/// settings.json / connections.json. Same atomic write (temp file + replace) and degrade-to-empty
/// idiom as <see cref="JsonAppSettingsStore"/>: only user-changed bindings are written, so a fresh
/// install has no file at all. A <c>null</c> value marks a deliberately unbound command.
/// </summary>
public sealed class JsonKeymapStore : IKeymapStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;

    public JsonKeymapStore(string? path = null)
    {
        _path = path ?? DefaultPath();
    }

    private static string DefaultPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Lionear", "SqlExplorer");
        return Path.Combine(dir, "keymap.json");
    }

    public IReadOnlyDictionary<string, string?> Load()
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, string?>();
        }

        try
        {
            using var stream = File.OpenRead(_path);
            return JsonSerializer.Deserialize<Dictionary<string, string?>>(stream, Options)
                   ?? new Dictionary<string, string?>();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A malformed or briefly-locked keymap file must never block startup — fall back to defaults.
            return new Dictionary<string, string?>();
        }
    }

    public void Save(IReadOnlyDictionary<string, string?> overrides)
    {
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);

        var temp = Path.Combine(dir, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(overrides, Options));
        File.Move(temp, _path, overwrite: true);
    }
}
