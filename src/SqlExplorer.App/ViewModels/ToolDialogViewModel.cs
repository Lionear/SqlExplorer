using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using SqlExplorer.Core.Connections;
using SqlExplorer.Core.Localization;
using SqlExplorer.Core.Providers;
using SqlExplorer.Core.Settings;
using SqlExplorer.Sdk;
using SqlExplorer.Sdk.Connections;
using SqlExplorer.Sdk.Schema;
using SqlExplorer.Core.Tools;
using SqlExplorer.Sdk.Localization;
using SqlExplorer.Sdk.Tools;
using SqlExplorer.Sdk.Ui;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SqlExplorer.App.ViewModels;

/// <summary>
/// Backs the one generic tool dialog: renders a tool's declared <see cref="ToolField"/>s (Route A) or its
/// own view (Route B), runs <see cref="IToolPlugin.ExecuteAsync"/> with a live progress log, and closes
/// on completion/cancel. Reconfigured per open via <see cref="Configure"/> (same factory-delegate pattern
/// as the other dialogs).
/// </summary>
public partial class ToolDialogViewModel : ViewModelBase, IToolUiContext, IToolHost
{
    // Route B backing store, mutated by the plugin's own view via IToolUiContext.
    private readonly Dictionary<string, string?> _customValues = new();

    private readonly IPluginSettingsStore _pluginStore;
    private readonly IToolRegistry _tools;
    private readonly ConnectionService _connections;
    private readonly IDbProviderRegistry _providers;

    private IToolPlugin _tool = null!;
    private IPluginLocalizer _pluginLoc = EmptyPluginLocalizer.Instance;
    private ConnectionProfile _profile = null!;
    private DbNodeRef? _node;
    private IDbProvider _provider = null!;
    private string _providerId = string.Empty;
    private CancellationTokenSource? _cts;

    public ToolDialogViewModel(
        ILocalizer localizer,
        IPluginSettingsStore pluginStore,
        IToolRegistry tools,
        ConnectionService connections,
        IDbProviderRegistry providers)
    {
        Loc = localizer;
        _pluginStore = pluginStore;
        _tools = tools;
        _connections = connections;
        _providers = providers;
        Log.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasLogArea));
    }

    public ILocalizer Loc { get; }

    /// <summary>The tool's target — the selected node (database/table), else the connected database.
    /// The view uses it to default a save-file name (e.g. "MyDatabase.lbak") instead of "backup.lbak".</summary>
    public string? TargetName => _node?.Name ?? _profile?.Database;

    /// <summary>Set by the view: shows a save-file picker (suggestedName, extensions) → path or null.</summary>
    public Func<string, string[], Task<string?>>? SaveFilePicker { get; set; }

    /// <summary>Set by the view: shows an open-file picker (extensions) → path or null.</summary>
    public Func<string[], Task<string?>>? OpenFilePicker { get; set; }

    /// <summary>Set by <c>MainViewModel</c> per open: opens a query tab on the launched connection/database
    /// with the given SQL (used by <see cref="IToolHost.OpenQueryEditor"/>).</summary>
    public Action<string>? OpenQueryRequested { get; set; }

    /// <summary>Set by <c>MainViewModel</c> per open: opens a query tab on a picked <i>secondary</i> connection
    /// and database with the given SQL (used by <see cref="IToolHost.OpenQueryEditorOn"/>).</summary>
    public Action<SavedConnection, string?, string>? OpenQueryOnConnectionRequested { get; set; }

    // --- IToolHost: the host services handed to the running tool ---
    Task<string?> IToolHost.PickSaveFileAsync(string suggestedName, params string[] extensions) =>
        SaveFilePicker?.Invoke(suggestedName, extensions) ?? Task.FromResult<string?>(null);

    Task<string?> IToolHost.PickOpenFileAsync(params string[] extensions) =>
        OpenFilePicker?.Invoke(extensions) ?? Task.FromResult<string?>(null);

    string? IToolHost.GetPluginSetting(string key) =>
        _pluginStore.Get(_tool.Id).TryGetValue(key, out var value) ? value : null;

    void IToolHost.SetPluginSetting(string key, string? value)
    {
        var values = new Dictionary<string, string?>(_pluginStore.Get(_tool.Id)) { [key] = value };
        _pluginStore.Save(_tool.Id, values);
    }

    IReadOnlyList<ToolConnectionInfo> IToolHost.ListConnections() => PickableConnections();

    ToolConnection? IToolHost.OpenConnection(string connectionId, string? database)
    {
        var saved = _connections.List().FirstOrDefault(c => c.Id == connectionId);
        if (saved is null || !_providers.TryGet(saved.ProviderId, out var provider))
        {
            return null;
        }

        return new ToolConnection(_connections.Resolve(saved, database), provider, saved.ProviderId);
    }

    async Task<IReadOnlyList<string>> IToolHost.ListDatabasesAsync(string connectionId, CancellationToken ct)
    {
        var saved = _connections.List().FirstOrDefault(c => c.Id == connectionId);
        if (saved is null || !_providers.TryGet(saved.ProviderId, out var provider))
        {
            return [];
        }

        return await provider.GetDatabasesAsync(_connections.Resolve(saved), ct);
    }

    void IToolHost.OpenQueryEditor(string sql) => OpenQueryRequested?.Invoke(sql);

    void IToolHost.OpenQueryEditorOn(string connectionId, string? database, string sql)
    {
        if (_connections.List().FirstOrDefault(c => c.Id == connectionId) is { } saved)
        {
            OpenQueryOnConnectionRequested?.Invoke(saved, database, sql);
        }
    }

    // The picker offers same-provider connections only (a cross-provider schema diff would need type-mapping
    // we don't do yet) and never the launched connection itself (comparing it to itself is a no-op). The
    // primary connection isn't a SavedConnection here — we only hold its ConnectionProfile — so it's matched
    // out by name, which is unique enough for this UX.
    private IReadOnlyList<ToolConnectionInfo> PickableConnections() =>
        _connections.List()
            .Where(c => c.ProviderId == _providerId && c.Name != _profile.Name)
            .Select(c => new ToolConnectionInfo(c.Id, c.Name, c.ProviderId))
            .ToList();

    // Refill every database picker with the chosen connection's databases (cleared when nothing/failing).
    private async Task PopulateDatabasePickersAsync(string? connectionId)
    {
        var dbFields = Fields.Where(f => f.IsDatabasePicker).ToList();
        if (dbFields.Count == 0)
        {
            return;
        }

        IReadOnlyList<ToolPickerOption> options = [];
        if (!string.IsNullOrWhiteSpace(connectionId))
        {
            try
            {
                var databases = await ((IToolHost)this).ListDatabasesAsync(connectionId, CancellationToken.None);
                options = databases.Select(d => new ToolPickerOption(d, d)).ToList();
            }
            catch
            {
                // A connection that can't be reached just leaves the database picker empty.
                options = [];
            }
        }

        foreach (var field in dbFields)
        {
            field.SetPickerOptions(options);
        }
    }

    /// <summary>Set by the view so the VM can ask a yes/no question (destructive confirm).</summary>
    public Func<string, string, Task<bool>>? ConfirmRequested { get; set; }

    /// <summary>Set by the view; called to close the window.</summary>
    public Action? CloseRequested { get; set; }

    public ObservableCollection<ToolFieldInput> Fields { get; } = [];

    public ObservableCollection<string> Log { get; } = [];

    /// <summary>Live per-item checklist, populated from <see cref="ToolProgress.ItemKey"/>/ItemStatus as the
    /// tool runs. Empty for tools that don't report keyed items — the log panel is then the only feedback.</summary>
    public ObservableCollection<ToolChecklistRow> Checklist { get; } = [];

    public bool HasChecklist => Checklist.Count > 0 && !CustomOwnsLifecycle;

    /// <summary>True when the Route-B view implements <see cref="IToolDialogLifecycle"/>: it renders the
    /// progress and completion states itself, so the host's generic checklist, log, progress bar and action
    /// bar all step aside and the view keeps the full dialog for the whole run.</summary>
    public bool CustomOwnsLifecycle => CustomView is IToolDialogLifecycle;

    /// <summary>A lifecycle-owning view draws its own header/footer edge-to-edge, so it gets no host padding.</summary>
    public Thickness CustomViewMargin => CustomOwnsLifecycle ? new Thickness(0) : new Thickness(18, 16);

    // Hide the plain log once there's a checklist: a keyed-item tool's checklist already shows per-item
    // status, so a second, line-by-line log underneath is mostly the same information twice (e.g. the
    // Backup dialog showed "Table 7/9: Traders — 0 row(s)" in both panels at once). Otherwise, hide the
    // panel until there is something to show, so form-only tools (e.g. the Shrink dialogs) aren't dominated
    // by a big empty box before the first run.
    public bool HasLogArea => !HasChecklist && !CustomOwnsLifecycle && (IsRunning || IsCompleted || Log.Count > 0);

    /// <summary>True once a run has started (or finished): the Route-A/B fields area collapses so the
    /// checklist/log can expand into that space instead — the object-selection tree (or form) has done its
    /// job by the time there's something to report.</summary>
    public bool ShowingResults => IsRunning || IsCompleted;

    /// <summary>Row-0 (fields/custom view) height: fills the dialog while editing, collapses to nothing once
    /// a run starts so <see cref="ResultsAreaHeight"/> can expand into the freed space.</summary>
    public GridLength ObjectAreaHeight =>
        ShowingResults && !CustomOwnsLifecycle ? new GridLength(0) : new GridLength(1, GridUnitType.Star);

    /// <summary>Row-1 (checklist/log) height: content-sized while editing (usually empty), fills the space
    /// <see cref="ObjectAreaHeight"/> just freed up once a run starts.</summary>
    public GridLength ResultsAreaHeight =>
        ShowingResults && !CustomOwnsLifecycle ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExecute))]
    [NotifyPropertyChangedFor(nameof(InputsEnabled))]
    [NotifyPropertyChangedFor(nameof(CustomViewEnabled))]
    [NotifyPropertyChangedFor(nameof(HostChromeRunning))]
    [NotifyPropertyChangedFor(nameof(HasLogArea))]
    [NotifyPropertyChangedFor(nameof(ShowingResults))]
    [NotifyPropertyChangedFor(nameof(ObjectAreaHeight))]
    [NotifyPropertyChangedFor(nameof(ResultsAreaHeight))]
    private bool _isRunning;

    /// <summary>Set once the tool has finished successfully: the dialog switches to a done state (success
    /// banner + Finish button) instead of letting the same backup/restore be run again.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExecute))]
    [NotifyPropertyChangedFor(nameof(InputsEnabled))]
    [NotifyPropertyChangedFor(nameof(CustomViewEnabled))]
    [NotifyPropertyChangedFor(nameof(HostChromeRunning))]
    [NotifyPropertyChangedFor(nameof(HasLogArea))]
    [NotifyPropertyChangedFor(nameof(ShowingResults))]
    [NotifyPropertyChangedFor(nameof(ObjectAreaHeight))]
    [NotifyPropertyChangedFor(nameof(ResultsAreaHeight))]
    private bool _isCompleted;

    /// <summary>Inputs are editable only before a run and while not finished.</summary>
    public bool InputsEnabled => !IsRunning && !IsCompleted;

    /// <summary>The host's own progress bar: only while running, and only when the view didn't take over.</summary>
    public bool HostChromeRunning => IsRunning && !CustomOwnsLifecycle;

    /// <summary>A lifecycle-owning view stays interactive during the run (it draws the Cancel button and its
    /// own progress); a plain Route-B input view is greyed out like the Route-A fields are.</summary>
    public bool CustomViewEnabled => CustomOwnsLifecycle || InputsEnabled;

    /// <summary>0..1 determinate progress; ignored while <see cref="IsProgressIndeterminate"/> is true.</summary>
    [ObservableProperty]
    private double _progress;

    /// <summary>True until a tool first reports a fraction — the bar spins ("busy") rather than filling.</summary>
    [ObservableProperty]
    private bool _isProgressIndeterminate = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    private string? _preview;

    public bool HasPreview => !string.IsNullOrEmpty(Preview);

    [ObservableProperty]
    private string _title = string.Empty;

    /// <summary>Optional explanatory text a tool shows above its fields (<see cref="IToolPlugin.Description"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDescription))]
    private string? _description;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    /// <summary>The connection/database the tool was launched on — the object it acts on. Shown in the dialog
    /// so it's unambiguous which database a tool targets (e.g. Schema Diff changes this one).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTargetSummary))]
    private string? _targetSummary;

    public bool HasTargetSummary => !string.IsNullOrWhiteSpace(TargetSummary);

    /// <summary>Route B: a tool-supplied view drives the form instead of the generated fields.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCustomView))]
    [NotifyPropertyChangedFor(nameof(HasFields))]
    [NotifyPropertyChangedFor(nameof(CustomOwnsLifecycle))]
    [NotifyPropertyChangedFor(nameof(CustomViewEnabled))]
    [NotifyPropertyChangedFor(nameof(HostChromeRunning))]
    [NotifyPropertyChangedFor(nameof(CustomViewMargin))]
    [NotifyPropertyChangedFor(nameof(ObjectAreaHeight))]
    [NotifyPropertyChangedFor(nameof(ResultsAreaHeight))]
    private Control? _customView;

    private IToolDialogLifecycle? Lifecycle => CustomView as IToolDialogLifecycle;

    public bool HasCustomView => CustomView is not null;

    public bool HasFields => CustomView is null && Fields.Count > 0;

    public bool CanExecute => !IsRunning && !IsCompleted && Fields.All(f => f.IsFilled);

    /// <summary>Prepare the dialog for one tool run against the given connection/node.</summary>
    public void Configure(IToolPlugin tool, ConnectionProfile profile, DbNodeRef? node, IDbProvider provider, string providerId)
    {
        _tool = tool;
        _pluginLoc = _tools.LocalizerFor(tool.Id);
        _profile = profile;
        _node = node;
        _provider = provider;
        _providerId = providerId;
        Title = _pluginLoc.Resolve(tool.DialogTitleKey, tool.DialogTitle);

        var description = _pluginLoc.Resolve(tool.DescriptionKey, tool.Description ?? string.Empty);
        Description = string.IsNullOrWhiteSpace(description) ? null : description;
        TargetSummary = string.IsNullOrWhiteSpace(profile.Database)
            ? profile.Name
            : $"{profile.Name} / {profile.Database}";

        if (tool is ICustomToolUi customUi)
        {
            CustomView = customUi.CreateView(this);
        }
        else
        {
            // Resolve the connection dropdown once per open, not per field, so every ConnectionPicker on a
            // tool shares one list.
            IReadOnlyList<ToolPickerOption> connectionOptions =
                tool.Fields.Any(f => f.Type == ToolFieldType.ConnectionPicker)
                    ? PickableConnections().Select(c => new ToolPickerOption(c.Id, c.Name)).ToList()
                    : [];

            foreach (var field in tool.Fields)
            {
                // A database picker starts empty; it's filled once its companion connection is chosen.
                IReadOnlyList<ToolPickerOption> options =
                    field.Type == ToolFieldType.ConnectionPicker ? connectionOptions : [];
                var input = new ToolFieldInput(field, _pluginLoc, options);
                input.PropertyChanged += OnFieldChanged;
                Fields.Add(input);
            }
        }
    }

    private async void OnFieldChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ToolFieldInput.Value) || sender is not ToolFieldInput input)
        {
            return;
        }

        OnPropertyChanged(nameof(CanExecute));

        // A connection-picker just changed → refill any database-picker with that server's databases.
        if (input.IsConnectionPicker)
        {
            await PopulateDatabasePickersAsync(input.Value);
        }

        // A file field just changed → offer a preview (e.g. a backup's plaintext header) under it.
        if (input.IsFile && !string.IsNullOrWhiteSpace(input.Value))
        {
            try
            {
                Preview = await _tool.PreviewAsync(input.Value, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Preview = ex.Message;
            }
        }
    }

    [RelayCommand]
    private async Task ExecuteAsync()
    {
        if (IsRunning)
        {
            return;
        }

        if (_tool.IsDestructive && ConfirmRequested is not null
            && !await ConfirmRequested(_tool.Title, Loc["ToolDestructiveConfirm"]))
        {
            return;
        }

        _cts = new CancellationTokenSource();
        // A lifecycle-owning view can offer "run it again" from its done state, so a new run clears the
        // completed flag rather than being blocked by it.
        IsCompleted = false;
        IsRunning = true;
        Progress = 0;
        IsProgressIndeterminate = true;
        Log.Clear();
        Checklist.Clear();
        OnPropertyChanged(nameof(HasChecklist));
        OnPropertyChanged(nameof(HasLogArea));

        var inputs = HasCustomView
            ? _customValues
            : Fields.ToDictionary(f => f.Field.Key, f => f.Value);
        var context = new ToolExecutionContext(_profile, _node, _provider, _providerId, this, _pluginLoc);
        Lifecycle?.OnRunStarted();
        var progress = new Progress<ToolProgress>(p =>
        {
            Lifecycle?.OnProgress(p);
            Log.Add(p.Message);
            if (p.Fraction is { } fraction)
            {
                IsProgressIndeterminate = false;
                Progress = Math.Clamp(fraction, 0, 1);
            }

            // Keyed item → live checklist row (first sighting adds it, later reports flip its status).
            if (p is { ItemKey: { } key, ItemStatus: { } status })
            {
                var row = Checklist.FirstOrDefault(r => r.Key == key);
                if (row is null)
                {
                    Checklist.Add(new ToolChecklistRow(key, p.Message) { Status = status });
                    OnPropertyChanged(nameof(HasChecklist));
                    OnPropertyChanged(nameof(HasLogArea));
                }
                else
                {
                    row.Label = p.Message;
                    row.Status = status;
                }
            }
        });

        try
        {
            await _tool.ExecuteAsync(context, inputs, progress, _cts.Token);
            Log.Add(Loc["ToolDone"]);
            IsCompleted = true; // success → switch the dialog to its done state
            Lifecycle?.OnRunFinished(ToolRunOutcome.Succeeded, null);
        }
        catch (OperationCanceledException)
        {
            Log.Add(Loc["ToolCancelled"]);
            Lifecycle?.OnRunFinished(ToolRunOutcome.Cancelled, Loc["ToolCancelled"]);
        }
        catch (Exception ex)
        {
            Log.Add($"⚠ {ex.Message}");
            Lifecycle?.OnRunFinished(ToolRunOutcome.Failed, ex.Message);
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsRunning)
        {
            _cts?.Cancel();
        }
        else
        {
            CloseRequested?.Invoke();
        }
    }

    // IToolUiContext (Route B only).
    public string? GetValue(string key) => _customValues.TryGetValue(key, out var v) ? v : null;

    public void SetValue(string key, string? value)
    {
        _customValues[key] = value;
        OnPropertyChanged(nameof(CanExecute));
    }

    // Route B live-data hook: run a read-only query through the same provider/profile the tool will use.
    public Task<QueryResult> QueryAsync(string sql, CancellationToken ct) =>
        _provider.ExecuteQueryAsync(_profile, sql, ct);

    IDbProvider IToolUiContext.Provider => _provider;
    ConnectionProfile IToolUiContext.Profile => _profile;
    DbNodeRef? IToolUiContext.Node => _node;

    Task<string?> IToolUiContext.PickSaveFileAsync(string suggestedName, params string[] extensions) =>
        SaveFilePicker?.Invoke(suggestedName, extensions) ?? Task.FromResult<string?>(null);

    Task<string?> IToolUiContext.PickOpenFileAsync(params string[] extensions) =>
        OpenFilePicker?.Invoke(extensions) ?? Task.FromResult<string?>(null);

    // A Route B view's destination pickers reuse the same host lookups the Route A ConnectionPicker /
    // DatabasePicker fields do.
    IReadOnlyList<ToolConnectionInfo> IToolUiContext.ListConnections() => ((IToolHost)this).ListConnections();

    Task<IReadOnlyList<string>> IToolUiContext.ListDatabasesAsync(string connectionId, CancellationToken ct) =>
        ((IToolHost)this).ListDatabasesAsync(connectionId, ct);

    // A lifecycle-owning view (IToolDialogLifecycle) renders its own action bar, so it drives the run
    // through these three instead of the host's buttons.
    IPluginLocalizer IToolUiContext.Localizer => _pluginLoc;

    Task IToolUiContext.RunAsync() => ExecuteAsync();

    void IToolUiContext.CancelRun() => _cts?.Cancel();

    void IToolUiContext.CloseDialog() => CloseRequested?.Invoke();
}
