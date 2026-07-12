namespace Lionear.SqlExplorer.Core.Connections;

/// <summary>
/// Stores secrets in the OS credential vault (Windows Credential Manager, macOS Keychain,
/// Linux Secret Service). One implementation per platform; the app never persists a secret
/// itself. Keys are opaque strings composed by <see cref="ConnectionService"/>.
/// </summary>
public interface ISecretStore
{
    void Set(string key, string secret);

    string? Get(string key);

    void Delete(string key);
}
