using SqlExplorer.Core.Providers;
using SqlExplorer.Sdk;

namespace SqlExplorer.Core.Connections;

/// <summary>
/// Ties saved connections, the keychain and the providers together. Uses each provider's
/// declared <see cref="ConnectionField"/>s to decide which values are secret (→ keychain) and
/// which are plain (→ config file), so the split is provider-driven, not hard-coded here.
/// </summary>
public sealed class ConnectionService
{
    private readonly IConnectionStore _store;
    private readonly ISecretStore _secrets;
    private readonly IDbProviderRegistry _providers;

    // In-memory, session-only connections (SE-155): the full value set (incl. secrets) is held here and
    // never touches the config file or keychain; cleared by ClearTransient() at shutdown. Keyed by id.
    private readonly Dictionary<string, TransientConnection> _transient = new();

    private sealed record TransientConnection(SavedConnection Connection, IReadOnlyDictionary<string, string?> Values);

    public ConnectionService(IConnectionStore store, ISecretStore secrets, IDbProviderRegistry providers)
    {
        _store = store;
        _secrets = secrets;
        _providers = providers;
    }

    /// <summary>Raised after a connection is persisted via <see cref="Save"/>. The tree subscribes so a
    /// connection created outside the UI — e.g. by a subsystem plugin through <c>IManagedConnections</c> —
    /// still appears live, without waiting for a restart or a manual refresh.</summary>
    public event Action<SavedConnection>? Saved;

    /// <summary>Raised after a connection is removed via <see cref="Delete"/>, carrying the connection as it
    /// was. Mirror of <see cref="Saved"/> so the tree can drop a node deleted outside the UI — e.g. a
    /// subsystem plugin tearing down its managed connection when the backing container is removed.</summary>
    public event Action<SavedConnection>? Removed;

    public IReadOnlyList<SavedConnection> List() => _store.GetAll();

    /// <summary>Manual folder-order map (full path → index). Absent path = alphabetical fallback.</summary>
    public IReadOnlyDictionary<string, int> ListFolderOrder() => _store.GetFolderOrder();

    /// <summary>One-shot rewrite of both the connection list (with <see cref="SavedConnection.SortOrder"/>
    /// already stamped) and the folder-order map. Used by the Connection Manager's drag-to-reorder so a
    /// mixed-scope reorder (folders and connections interleaved) lands atomically in one file write.</summary>
    public void ApplyReorder(IReadOnlyList<SavedConnection> connections, IReadOnlyDictionary<string, int> folderOrder)
    {
        _store.SaveAll(connections, folderOrder);
    }

    /// <summary>Persist a connection: secrets to the keychain, the rest to the config file.</summary>
    public SavedConnection Save(
        string id, string name, string providerId, IReadOnlyDictionary<string, string?> values,
        string? color = null, bool readOnly = false, string? folder = null,
        AiAccessMode aiAccess = AiAccessMode.None, bool excludeFromMcp = false, string? origin = null)
    {
        var fields = _providers.Get(providerId).ConnectionFields;
        var secretKeys = fields.Where(f => f.IsSecret).Select(f => f.Key).ToHashSet();

        foreach (var field in fields.Where(f => f.IsSecret))
        {
            // A secret the caller didn't mention is left untouched — only an explicitly-supplied empty value
            // clears it. Guards against a partial value map (e.g. a metadata-only re-save) wiping the stored
            // password (SE-174).
            if (!values.TryGetValue(field.Key, out var value))
            {
                continue;
            }

            var secretKey = SecretKey(id, field.Key);
            if (string.IsNullOrEmpty(value))
            {
                _secrets.Delete(secretKey);
            }
            else
            {
                _secrets.Set(secretKey, value);
            }
        }

        var nonSecret = values
            .Where(kv => !secretKeys.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        var connection = new SavedConnection
        {
            Id = id, Name = name, ProviderId = providerId, Color = color, ReadOnly = readOnly,
            Folder = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim(),
            AiAccess = aiAccess, ExcludeFromMcp = excludeFromMcp, Values = nonSecret, Origin = origin
        };
        _store.Save(connection);
        Saved?.Invoke(connection);
        return connection;
    }

    /// <summary>Transient (in-memory, session-only) connections currently held — for the tree and the MCP
    /// host to surface alongside the persisted ones.</summary>
    public IReadOnlyList<SavedConnection> ListTransient() => _transient.Values.Select(t => t.Connection).ToList();

    /// <summary>Create an in-memory, session-only connection (SE-155). Values (including secrets) are kept in
    /// memory only — never written to the config file or keychain — and dropped by <see cref="ClearTransient"/>
    /// at shutdown. Fires <see cref="Saved"/> so the tree shows it live, exactly like a persisted create.</summary>
    public SavedConnection CreateTransient(
        string id, string name, string providerId, IReadOnlyDictionary<string, string?> values,
        string? folder = null, AiAccessMode aiAccess = AiAccessMode.None, string? origin = null)
    {
        var fields = _providers.Get(providerId).ConnectionFields;
        var secretKeys = fields.Where(f => f.IsSecret).Select(f => f.Key).ToHashSet();
        var nonSecret = values.Where(kv => !secretKeys.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);

        var connection = new SavedConnection
        {
            Id = id, Name = name, ProviderId = providerId,
            Folder = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim(),
            AiAccess = aiAccess, Origin = origin, IsTransient = true, Values = nonSecret
        };

        // Keep the full value set (incl. secrets) in memory so Resolve can build a runnable profile without
        // ever reaching the keychain.
        _transient[id] = new TransientConnection(connection, new Dictionary<string, string?>(values));
        Saved?.Invoke(connection);
        return connection;
    }

    /// <summary>Drop a single transient connection (e.g. the AI removing one it created). No-op for an unknown
    /// or persisted id. Fires <see cref="Removed"/> so the tree drops the node.</summary>
    public bool RemoveTransient(string id)
    {
        if (!_transient.Remove(id, out var entry))
        {
            return false;
        }

        Removed?.Invoke(entry.Connection);
        return true;
    }

    /// <summary>Wipe every transient connection — called on shutdown so nothing session-only survives.</summary>
    public void ClearTransient() => _transient.Clear();

    /// <summary>
    /// Copy a saved connection (including its keychain secret) under a fresh id and the given name.
    /// The caller supplies the new name so the label stays localizable in the UI layer.
    /// </summary>
    public SavedConnection Duplicate(string id, string newName)
    {
        var original = _store.GetAll().FirstOrDefault(c => c.Id == id)
            ?? throw new InvalidOperationException($"Connection '{id}' not found.");

        // WithSecrets pulls the password back from the keychain so the copy is fully usable.
        var values = WithSecrets(original);
        return Save(Guid.NewGuid().ToString("N"), newName, original.ProviderId, values, original.Color, original.ReadOnly, original.Folder);
    }

    /// <summary>
    /// Move a connection to another folder (or ungroup it) without touching its secrets. Only the
    /// non-secret <see cref="SavedConnection.Folder"/> path changes, so the keychain is left alone —
    /// used by the Connection Manager's drag &amp; drop and folder rename/delete.
    /// </summary>
    public SavedConnection SetFolder(SavedConnection connection, string? folder)
    {
        var updated = connection with { Folder = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim() };
        _store.Save(updated);
        return updated;
    }

    /// <summary>
    /// Update only the AI-access metadata (the tree's "AI access" submenu, SE-158) without rewriting field
    /// values or touching the keychain. The full <see cref="Save"/> path re-derives secrets from the passed
    /// values and deletes any that come back empty — so toggling AI access through it could wipe the password
    /// when the vault is locked (SE-174). This edits the stored record in place, like <see cref="SetFolder"/>.
    /// </summary>
    public SavedConnection SetAiAccess(SavedConnection connection, AiAccessMode aiAccess, bool excludeFromMcp)
    {
        var updated = connection with { AiAccess = aiAccess, ExcludeFromMcp = excludeFromMcp };
        _store.Save(updated);
        Saved?.Invoke(updated);
        return updated;
    }

    public void Delete(string id)
    {
        var connection = _store.GetAll().FirstOrDefault(c => c.Id == id);
        if (connection is not null)
        {
            foreach (var field in _providers.Get(connection.ProviderId).ConnectionFields.Where(f => f.IsSecret))
            {
                _secrets.Delete(SecretKey(id, field.Key));
            }
        }

        _store.Delete(id);
        if (connection is not null)
        {
            Removed?.Invoke(connection);
        }
    }

    /// <summary>
    /// Merge stored non-secret values with keychain secrets into a runnable profile. <paramref name="database"/>
    /// overrides the target catalog when the caller knows it from the schema tree (null = connection default).
    /// </summary>
    public ConnectionProfile Resolve(SavedConnection connection, string? database = null) =>
        BuildProfile(connection.Name, connection.ProviderId, WithSecrets(connection), database);

    /// <summary>All field values (non-secret + secrets from the keychain) to prefill the edit dialog.</summary>
    public IReadOnlyDictionary<string, string?> GetEditableValues(SavedConnection connection) =>
        WithSecrets(connection);

    /// <summary>Build a profile from raw dialog values (used by Test, before anything is persisted).</summary>
    public ConnectionProfile BuildProfile(
        string name, string providerId, IReadOnlyDictionary<string, string?> values, string? database = null) =>
        new()
        {
            Name = name,
            ConnectionString = _providers.Get(providerId).BuildConnectionString(values),
            Database = database
        };

    /// <summary>Every stored connection secret (connection id, field key, current value), read through the
    /// secret store. Used by the master-password flow to re-encrypt/decrypt all secrets in one pass; the
    /// value comes back plaintext or decrypted depending on the store's current key state.</summary>
    public IReadOnlyList<ConnectionSecret> ExportSecrets()
    {
        var list = new List<ConnectionSecret>();
        foreach (var connection in _store.GetAll())
        {
            foreach (var field in _providers.Get(connection.ProviderId).ConnectionFields.Where(f => f.IsSecret))
            {
                list.Add(new ConnectionSecret(connection.Id, field.Key, _secrets.Get(SecretKey(connection.Id, field.Key))));
            }
        }

        return list;
    }

    /// <summary>Write one secret back through the store (Set, or Delete when empty). The store decides
    /// whether it lands encrypted, based on its current key state.</summary>
    public void ImportSecret(ConnectionSecret secret)
    {
        var key = SecretKey(secret.ConnectionId, secret.FieldKey);
        if (string.IsNullOrEmpty(secret.Value))
        {
            _secrets.Delete(key);
        }
        else
        {
            _secrets.Set(key, secret.Value);
        }
    }

    private Dictionary<string, string?> WithSecrets(SavedConnection connection)
    {
        // Transient connections hold their secrets in memory, never in the keychain.
        if (_transient.TryGetValue(connection.Id, out var transient))
        {
            return new Dictionary<string, string?>(transient.Values);
        }

        var values = new Dictionary<string, string?>(connection.Values);
        foreach (var field in _providers.Get(connection.ProviderId).ConnectionFields.Where(f => f.IsSecret))
        {
            values[field.Key] = _secrets.Get(SecretKey(connection.Id, field.Key));
        }

        return values;
    }

    private static string SecretKey(string id, string field) => $"conn:{id}:{field}";
}
