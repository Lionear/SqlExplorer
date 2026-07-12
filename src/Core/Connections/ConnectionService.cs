using Lionear.SqlExplorer.Core.Providers;
using Lionear.SqlExplorer.Sdk;

namespace Lionear.SqlExplorer.Core.Connections;

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

    public ConnectionService(IConnectionStore store, ISecretStore secrets, IDbProviderRegistry providers)
    {
        _store = store;
        _secrets = secrets;
        _providers = providers;
    }

    public IReadOnlyList<SavedConnection> List() => _store.GetAll();

    /// <summary>Persist a connection: secrets to the keychain, the rest to the config file.</summary>
    public SavedConnection Save(string id, string name, DatabaseKind kind, IReadOnlyDictionary<string, string?> values)
    {
        var fields = _providers.Get(kind).ConnectionFields;
        var secretKeys = fields.Where(f => f.IsSecret).Select(f => f.Key).ToHashSet();

        foreach (var field in fields.Where(f => f.IsSecret))
        {
            var secretKey = SecretKey(id, field.Key);
            var value = values.TryGetValue(field.Key, out var v) ? v : null;
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

        var connection = new SavedConnection { Id = id, Name = name, Kind = kind, Values = nonSecret };
        _store.Save(connection);
        return connection;
    }

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
        return Save(Guid.NewGuid().ToString("N"), newName, original.Kind, values);
    }

    public void Delete(string id)
    {
        var connection = _store.GetAll().FirstOrDefault(c => c.Id == id);
        if (connection is not null)
        {
            foreach (var field in _providers.Get(connection.Kind).ConnectionFields.Where(f => f.IsSecret))
            {
                _secrets.Delete(SecretKey(id, field.Key));
            }
        }

        _store.Delete(id);
    }

    /// <summary>Merge stored non-secret values with keychain secrets into a runnable profile.</summary>
    public ConnectionProfile Resolve(SavedConnection connection) =>
        BuildProfile(connection.Name, connection.Kind, WithSecrets(connection));

    /// <summary>All field values (non-secret + secrets from the keychain) to prefill the edit dialog.</summary>
    public IReadOnlyDictionary<string, string?> GetEditableValues(SavedConnection connection) =>
        WithSecrets(connection);

    /// <summary>Build a profile from raw dialog values (used by Test, before anything is persisted).</summary>
    public ConnectionProfile BuildProfile(string name, DatabaseKind kind, IReadOnlyDictionary<string, string?> values) =>
        new()
        {
            Name = name,
            Kind = kind,
            ConnectionString = _providers.Get(kind).BuildConnectionString(values)
        };

    private Dictionary<string, string?> WithSecrets(SavedConnection connection)
    {
        var values = new Dictionary<string, string?>(connection.Values);
        foreach (var field in _providers.Get(connection.Kind).ConnectionFields.Where(f => f.IsSecret))
        {
            values[field.Key] = _secrets.Get(SecretKey(connection.Id, field.Key));
        }

        return values;
    }

    private static string SecretKey(string id, string field) => $"conn:{id}:{field}";
}
