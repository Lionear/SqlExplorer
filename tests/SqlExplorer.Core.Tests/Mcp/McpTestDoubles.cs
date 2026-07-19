using System.Collections.Generic;
using SqlExplorer.Core.Connections;
using SqlExplorer.Core.History;
using SqlExplorer.Core.Logging;
using SqlExplorer.Core.Mcp;
using SqlExplorer.Core.Providers;
using SqlExplorer.Core.Security;
using SqlExplorer.Core.Settings;
using SqlExplorer.Sdk;
using SqlExplorer.Sdk.Branding;
using SqlExplorer.Sdk.Connections;
using SqlExplorer.Sdk.Ddl;
using SqlExplorer.Sdk.Query;
using SqlExplorer.Sdk.Schema;

namespace SqlExplorer.Core.Tests.Mcp;

// Shared fakes for the ConnectionService/McpHost tests. Kept deliberately minimal: only the surface these
// tests touch is implemented; every other IDbProvider member throws, since the create/transient paths never
// reach a driver.
internal sealed class FakeConnectionStore : IConnectionStore
{
    private readonly List<SavedConnection> _items = new();
    public IReadOnlyList<SavedConnection> GetAll() => _items.ToList();
    public IReadOnlyDictionary<string, int> GetFolderOrder() => new Dictionary<string, int>();
    public void Save(SavedConnection c) { _items.RemoveAll(x => x.Id == c.Id); _items.Add(c); }
    public void Delete(string id) => _items.RemoveAll(x => x.Id == id);
    public void SaveAll(IReadOnlyList<SavedConnection> connections, IReadOnlyDictionary<string, int> folderOrder)
    { _items.Clear(); _items.AddRange(connections); }
}

internal sealed class RecordingSecretStore : ISecretStore
{
    public Dictionary<string, string> Secrets { get; } = new();
    public void Set(string key, string secret) => Secrets[key] = secret;
    public string? Get(string key) => Secrets.TryGetValue(key, out var v) ? v : null;
    public void Delete(string key) => Secrets.Remove(key);
}

// A provider with a host (required), port (optional) and password (secret) field — enough to exercise the
// required-field, secret-split and host-allowlist logic.
internal sealed class FieldsProvider(string display = "Fake DB") : IDbProvider
{
    public string DisplayName => display;
    public ProviderIcon? Icon => null;
    public ISqlDialect Dialect => throw new NotSupportedException();
    public bool IsSqlBased => true;

    public IReadOnlyList<ConnectionField> ConnectionFields =>
    [
        new ConnectionField("host", "Host", ConnectionFieldType.Text, Required: true),
        new ConnectionField("port", "Port", ConnectionFieldType.Number),
        new ConnectionField("password", "Password", ConnectionFieldType.Password),
    ];

    public string BuildConnectionString(IReadOnlyDictionary<string, string?> values) => "fake";
    public Task<bool> TestConnectionAsync(ConnectionProfile profile, CancellationToken ct) => Task.FromResult(true);
    public Task<IReadOnlyList<DbTreeNode>> GetChildNodesAsync(ConnectionProfile profile, IReadOnlyList<DbNodeRef> ancestors, CancellationToken ct) => throw new NotSupportedException();
    public Task<QueryResult> ExecuteQueryAsync(ConnectionProfile profile, string sql, CancellationToken ct) => throw new NotSupportedException();
    public Task<int> ExecuteBatchAsync(ConnectionProfile profile, IReadOnlyList<SqlStatement> statements, CancellationToken ct) => throw new NotSupportedException();
    public IReadOnlyList<CreateCapability> CreateCapabilities => [];
    public IReadOnlyList<string> ColumnTypes => [];
    public SqlStatement BuildCreateStatement(CreateObjectSpec spec) => throw new NotSupportedException();
    public Task ExecuteDdlAsync(ConnectionProfile profile, string sql, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<string>> GetDatabasesAsync(ConnectionProfile profile, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<QueryResult>> ExecuteScriptAsync(ConnectionProfile profile, string sql, CancellationToken ct) => throw new NotSupportedException();
    public Task<QueryResult> ExplainAsync(ConnectionProfile profile, string sql, CancellationToken ct) => throw new NotSupportedException();
}

internal sealed class FakeQueryHistoryStore : IQueryHistoryStore
{
    public event Action? Changed { add { } remove { } }
    public void Append(QueryHistoryEntry entry) { }
    public IReadOnlyList<QueryHistoryEntry> GetRecent(int limit) => [];
    public IReadOnlyList<QueryHistoryEntry> Search(string text) => [];
    public void Clear() { }
}

internal sealed class FakeQueryLog : IQueryLog
{
    public event Action? Changed { add { } remove { } }
    public void Configure(bool enabled, bool logApp, bool logMcp) { }
    public void Record(QueryHistoryEntry entry) { }
    public IReadOnlyList<QueryHistoryEntry> Read(QueryLogFilter filter) => [];
    public void Clear() { }
}

internal sealed class FakeSettingsStore : IAppSettingsStore
{
    private AppSettings _settings = new();
    public AppSettings Load() => _settings;
    public void Save(AppSettings settings) => _settings = settings;
}

internal sealed class FakeMasterKeyProvider : IMasterKeyProvider
{
    public bool IsUnlocked => true;
    public byte[]? Key => null;
    public void Unlock(byte[] key) { }
    public void Lock() { }
    public void SetIdleTimeout(TimeSpan? timeout) { }
    public event Action? Locked { add { } remove { } }
}

internal static class McpTestHost
{
    // Build a ConnectionService + McpHost over the fakes, with a given create policy. masterPassword is
    // effectively disabled (default settings), so create/reachability gates run without the lock branch.
    public static (McpHost Host, ConnectionService Connections, McpActivityLog Activity) Build(McpConnectionPolicy policy)
    {
        var providers = new DbProviderRegistry([new ProviderRegistration("fake", new FieldsProvider())]);
        var connections = new ConnectionService(new FakeConnectionStore(), new RecordingSecretStore(), providers);
        var settings = new FakeSettingsStore();
        var master = new MasterPasswordService(settings, new FakeMasterKeyProvider(), connections);
        var activity = new McpActivityLog();
        var host = new McpHost(
            connections, providers, new FakeQueryHistoryStore(), new FakeQueryLog(), master,
            _ => null, () => policy, activity, _ => { });
        return (host, connections, activity);
    }
}
