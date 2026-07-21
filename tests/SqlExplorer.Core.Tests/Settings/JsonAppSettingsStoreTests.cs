using SqlExplorer.Core.Settings;
using SqlExplorer.Infrastructure.Persistence;

namespace SqlExplorer.Core.Tests.Settings;

public class JsonAppSettingsStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "se-settings-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        try { File.Delete(_path); } catch { /* best-effort temp cleanup */ }
    }

    [Fact]
    public void AllowMultipleInstances_defaults_to_single_instance()
    {
        Assert.False(new AppSettings().AllowMultipleInstances);
        // A missing file degrades to defaults, so the single-instance probe stays on until opted out.
        Assert.False(new JsonAppSettingsStore(_path).Load().AllowMultipleInstances);
    }

    [Fact]
    public void AllowMultipleInstances_round_trips()
    {
        var store = new JsonAppSettingsStore(_path);
        store.Save(new AppSettings { AllowMultipleInstances = true });

        Assert.True(store.Load().AllowMultipleInstances);
        // A fresh store over the same file reads the persisted value (this is how Program reads it at startup).
        Assert.True(new JsonAppSettingsStore(_path).Load().AllowMultipleInstances);
    }
}
