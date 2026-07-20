using SqlExplorer.Core.Connections;
using SqlExplorer.Core.Providers;
using SqlExplorer.Core.Tests.Mcp;

namespace SqlExplorer.Core.Tests.Connections;

// Guards the SE-174 data-loss fixes: metadata-only AI-access updates and Save never wiping a secret the
// caller didn't supply. Reuses the shared Mcp test doubles (FieldsProvider has host/port/password).
public class ConnectionServiceSaveTests
{
    private static (ConnectionService Service, RecordingSecretStore Secrets) NewService()
    {
        var providers = new DbProviderRegistry([new ProviderRegistration("fake", new FieldsProvider())]);
        var secrets = new RecordingSecretStore();
        return (new ConnectionService(new FakeConnectionStore(), secrets, providers), secrets);
    }

    private static Dictionary<string, string?> Values(string host, string? password = null)
    {
        var values = new Dictionary<string, string?> { ["host"] = host, ["port"] = "1234" };
        if (password is not null)
        {
            values["password"] = password;
        }

        return values;
    }

    [Fact] // RANK 3: toggling AI access keeps the field values and the keychain secret intact.
    public void SetAiAccess_updates_metadata_without_touching_values_or_secret()
    {
        var (service, secrets) = NewService();
        var saved = service.Save("c1", "Conn", "fake", Values("myhost", password: "pw"));

        var updated = service.SetAiAccess(saved, AiAccessMode.ReadOnly, excludeFromMcp: true);

        Assert.Equal(AiAccessMode.ReadOnly, updated.AiAccess);
        Assert.True(updated.ExcludeFromMcp);
        Assert.Equal("myhost", updated.Values["host"]);
        Assert.Equal("pw", secrets.Secrets["conn:c1:password"]);   // secret preserved
    }

    [Fact] // RANK 2: a value map that omits the secret key leaves the stored secret untouched.
    public void Save_leaves_a_secret_untouched_when_its_key_is_absent()
    {
        var (service, secrets) = NewService();
        service.Save("c1", "Conn", "fake", Values("h1", password: "pw"));

        var reSaved = service.Save("c1", "Conn", "fake", Values("h2")); // no "password" key

        Assert.Equal("pw", secrets.Secrets["conn:c1:password"]);       // not deleted
        Assert.Equal("h2", reSaved.Values["host"]);                    // non-secret value still updates
    }

    [Fact] // But an explicitly-supplied empty secret still clears it — the user cleared the field.
    public void Save_clears_a_secret_when_its_key_is_present_but_empty()
    {
        var (service, secrets) = NewService();
        service.Save("c1", "Conn", "fake", Values("h1", password: "pw"));

        service.Save("c1", "Conn", "fake", Values("h1", password: ""));

        Assert.False(secrets.Secrets.ContainsKey("conn:c1:password"));
    }
}
