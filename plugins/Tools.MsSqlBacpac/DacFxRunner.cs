using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;
using SqlExplorer.Sdk.Localization;

namespace SqlExplorer.Tools.MsSqlBacpac;

/// <summary>
/// The single seam onto <see cref="DacServices"/> shared by all four tools (Export/Import BACPAC,
/// Extract/Publish DACPAC). DacFx runs in-process against the managed <c>Microsoft.Data.SqlClient</c>
/// that ships in this plugin's isolated ALC — verified cross-platform on .NET&#160;10 / Linux (SE-59
/// STAP&#160;0). Every op is synchronous and CPU/IO-bound, so it runs on the thread pool via
/// <see cref="System.Threading.Tasks.Task.Run(Action,CancellationToken)"/> while its
/// <see cref="DacServices.ProgressChanged"/> events feed the tool's <see cref="IProgress{T}"/>.
/// </summary>
internal sealed class DacFxRunner
{
    private readonly ConnectionProfile _profile;
    private readonly IPluginLocalizer _localizer;
    private readonly IProgress<ToolProgress> _progress;

    public DacFxRunner(ConnectionProfile profile, IPluginLocalizer localizer, IProgress<ToolProgress> progress)
    {
        _profile = profile;
        _localizer = localizer;
        _progress = progress;
    }

    // Re-point the connection at a specific catalog, host/credentials intact — mirrors
    // MsSqlProvider.ConnectionStringFor so DacFx talks to exactly the database the tool targets.
    private string ConnectionStringFor(string database) =>
        new SqlConnectionStringBuilder(_profile.ConnectionString) { InitialCatalog = database }.ConnectionString;

    private DacServices ServicesFor(string catalog)
    {
        var services = new DacServices(ConnectionStringFor(catalog));
        // DacFx fires fine-grained coarse-step events ("Running Creating deployment plan", …). Surface the
        // human-readable message; the host throttles/last-writer-wins on the progress line.
        services.ProgressChanged += (_, e) => _progress.Report(new ToolProgress(e.Message));
        return services;
    }

    /// <summary>Export a database (schema always full) to a <c>.bacpac</c>. <paramref name="dataTables"/>
    /// null = every table's data; a list = only those tables' data (empty = schema-only bacpac).</summary>
    public Task ExportBacpacAsync(string database, string filePath, IEnumerable<Tuple<string, string>>? dataTables, CancellationToken ct) =>
        Task.Run(() =>
        {
            _progress.Report(new ToolProgress(_localizer.Get("bacpac.export.progress.start", database)));
            ServicesFor(database).ExportBacpac(filePath, database, new DacExportOptions(), dataTables, ct);
            _progress.Report(new ToolProgress(_localizer.Get("bacpac.export.progress.done", filePath), 1.0));
        }, ct);

    /// <summary>Import a <c>.bacpac</c> as a brand-new database on the same server. Fails if it exists;
    /// never overwrites (the tool is destructive only in that it creates an object).</summary>
    public Task ImportBacpacAsync(string filePath, string newDatabase, CancellationToken ct) =>
        Task.Run(() =>
        {
            _progress.Report(new ToolProgress(_localizer.Get("bacpac.import.progress.start", newDatabase)));
            using var package = BacPackage.Load(filePath);
            ServicesFor("master").ImportBacpac(package, newDatabase, new DacImportOptions(), ct);
            _progress.Report(new ToolProgress(_localizer.Get("bacpac.import.progress.done", newDatabase), 1.0));
        }, ct);

    /// <summary>Extract a database's schema (no data) to a <c>.dacpac</c> for CI/CD or schema compare.</summary>
    public Task ExtractDacpacAsync(string database, string filePath, string appName, Version version, DacExtractOptions options, CancellationToken ct) =>
        Task.Run(() =>
        {
            _progress.Report(new ToolProgress(_localizer.Get("bacpac.extract.progress.start", database)));
            ServicesFor(database).Extract(filePath, database, appName, version, null, null, options, ct);
            _progress.Report(new ToolProgress(_localizer.Get("bacpac.extract.progress.done", filePath), 1.0));
        }, ct);

    /// <summary>Publish a <c>.dacpac</c> against an existing database (schema update). Destructive;
    /// <see cref="DacDeployOptions.BlockOnPossibleDataLoss"/> guards data loss when set.</summary>
    public Task PublishDacpacAsync(string filePath, string targetDatabase, DacDeployOptions options, CancellationToken ct) =>
        Task.Run(() =>
        {
            _progress.Report(new ToolProgress(_localizer.Get("bacpac.publish.progress.start", targetDatabase)));
            using var package = DacPackage.Load(filePath);
            ServicesFor("master").Deploy(package, targetDatabase, upgradeExisting: true, options, ct);
            _progress.Report(new ToolProgress(_localizer.Get("bacpac.publish.progress.done", targetDatabase), 1.0));
        }, ct);
}
