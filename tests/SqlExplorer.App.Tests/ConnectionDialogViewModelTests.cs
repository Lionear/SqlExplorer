using System.ComponentModel;
using System.Globalization;
using SqlExplorer.App.ViewModels;
using SqlExplorer.Core.Connections;
using SqlExplorer.Core.Localization;
using SqlExplorer.Core.Providers;
using SqlExplorer.Sdk;
using SqlExplorer.Sdk.Branding;
using SqlExplorer.Sdk.Connections;
using SqlExplorer.Sdk.Ddl;
using SqlExplorer.Sdk.Query;
using SqlExplorer.Sdk.Schema;

namespace SqlExplorer.App.Tests;

public class ConnectionDialogViewModelTests
{
    [Fact] // SE-174: editing a connection then a provider re-select (the ComboBox's transient null->same-value
           // round-trip when the detail view re-attaches) must NOT reset the fields to the provider defaults.
    public void Editing_keeps_field_values_across_a_provider_reselect()
    {
        var providers = new DbProviderRegistry([new ProviderRegistration("fake", new FakeFieldsProvider())]);
        var connections = new ConnectionService(new FakeConnectionStore(), new FakeSecretStore(), providers);
        var saved = connections.Save("c1", "Prod", "fake", new Dictionary<string, string?>
        {
            ["host"] = "db.internal", ["port"] = "5555", ["password"] = "secret",
        });

        var vm = new ConnectionDialogViewModel(connections, providers, new FakeLocalizer());
        vm.LoadForEdit(saved);
        Assert.Equal("db.internal", FieldValue(vm, "host"));

        // Reproduce the spurious re-fire: SelectedProvider goes null then back to the same provider.
        var provider = vm.SelectedProvider;
        vm.SelectedProvider = null;
        vm.SelectedProvider = provider;

        Assert.Equal("db.internal", FieldValue(vm, "host"));   // was reset to the "localhost" default before the fix
        Assert.Equal("5555", FieldValue(vm, "port"));

        var reSaved = vm.Save();
        Assert.Equal("db.internal", reSaved.Values["host"]);   // and the reset defaults are not persisted
    }

    private static string? FieldValue(ConnectionDialogViewModel vm, string key) =>
        vm.Fields.First(f => f.Field.Key == key).Value;

    private sealed class FakeLocalizer : ILocalizer
    {
        public CultureInfo Culture => CultureInfo.InvariantCulture;
        public string this[string key] => key;
        public string Get(string key, params object[] args) => key;
        public void SetCulture(CultureInfo culture) { }
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    }

    private sealed class FakeConnectionStore : IConnectionStore
    {
        private readonly List<SavedConnection> _items = [];
        public IReadOnlyList<SavedConnection> GetAll() => _items.ToList();
        public IReadOnlyDictionary<string, int> GetFolderOrder() => new Dictionary<string, int>();
        public void Save(SavedConnection c) { _items.RemoveAll(x => x.Id == c.Id); _items.Add(c); }
        public void Delete(string id) => _items.RemoveAll(x => x.Id == id);
        public void SaveAll(IReadOnlyList<SavedConnection> connections, IReadOnlyDictionary<string, int> folderOrder)
        { _items.Clear(); _items.AddRange(connections); }
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _secrets = [];
        public void Set(string key, string secret) => _secrets[key] = secret;
        public string? Get(string key) => _secrets.TryGetValue(key, out var v) ? v : null;
        public void Delete(string key) => _secrets.Remove(key);
    }

    // A minimal SQL provider with host/port/password fields — the create/edit paths only read ConnectionFields.
    private sealed class FakeFieldsProvider : IDbProvider
    {
        public string DisplayName => "Fake DB";
        public ProviderIcon? Icon => null;
        public ISqlDialect Dialect => throw new NotSupportedException();
        public bool IsSqlBased => true;

        public IReadOnlyList<ConnectionField> ConnectionFields =>
        [
            new ConnectionField("host", "Host", ConnectionFieldType.Text, Required: true, Default: "localhost"),
            new ConnectionField("port", "Port", ConnectionFieldType.Number, Default: "1234"),
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
}
