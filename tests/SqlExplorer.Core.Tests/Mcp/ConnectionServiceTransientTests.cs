using System.Collections.Generic;
using System.Linq;
using SqlExplorer.Core.Connections;
using SqlExplorer.Core.Providers;

namespace SqlExplorer.Core.Tests.Mcp;

// Transient (session-only) connections (SE-155): held in an in-memory overlay, never written to the config
// file or keychain, resolvable with their in-memory secrets, and wiped on demand.
public class ConnectionServiceTransientTests
{
    private static (ConnectionService Service, FakeConnectionStore Store, RecordingSecretStore Secrets) New()
    {
        var providers = new DbProviderRegistry([new ProviderRegistration("fake", new FieldsProvider())]);
        var store = new FakeConnectionStore();
        var secrets = new RecordingSecretStore();
        return (new ConnectionService(store, secrets, providers), store, secrets);
    }

    private static Dictionary<string, string?> Values() =>
        new() { ["host"] = "127.0.0.1", ["port"] = "5432", ["password"] = "s3cret" };

    [Fact]
    public void CreateTransient_keeps_it_out_of_the_store_and_the_keychain()
    {
        var (svc, store, secrets) = New();

        var c = svc.CreateTransient("t1", "Temp", "fake", Values(), aiAccess: AiAccessMode.Sandbox);

        Assert.True(c.IsTransient);
        Assert.Empty(store.GetAll());                       // never persisted
        Assert.Empty(secrets.Secrets);                      // secret stayed in memory
        Assert.Empty(svc.List());
        Assert.Equal("t1", Assert.Single(svc.ListTransient()).Id);
        // Non-secret values are surfaced; the secret is stripped from the visible value set.
        Assert.False(c.Values.ContainsKey("password"));
        Assert.Equal("127.0.0.1", c.Values["host"]);
    }

    [Fact]
    public void Resolve_uses_the_in_memory_secret_for_a_transient_connection()
    {
        var (svc, _, _) = New();
        var c = svc.CreateTransient("t1", "Temp", "fake", Values());

        // BuildConnectionString on the fake returns a constant, but Resolve must not throw reaching for a
        // keychain entry that was never written — the in-memory value set carries the secret.
        var profile = svc.Resolve(c);
        Assert.Equal("Temp", profile.Name);
    }

    [Fact]
    public void CreateTransient_fires_Saved_and_RemoveTransient_fires_Removed()
    {
        var (svc, _, _) = New();
        SavedConnection? saved = null, removed = null;
        svc.Saved += c => saved = c;
        svc.Removed += c => removed = c;

        svc.CreateTransient("t1", "Temp", "fake", Values());
        Assert.Equal("t1", saved?.Id);

        Assert.True(svc.RemoveTransient("t1"));
        Assert.Equal("t1", removed?.Id);
        Assert.Empty(svc.ListTransient());
        Assert.False(svc.RemoveTransient("t1"));            // already gone
    }

    [Fact]
    public void ClearTransient_drops_all_session_connections()
    {
        var (svc, _, _) = New();
        svc.CreateTransient("t1", "A", "fake", Values());
        svc.CreateTransient("t2", "B", "fake", Values());

        svc.ClearTransient();

        Assert.Empty(svc.ListTransient());
    }
}
