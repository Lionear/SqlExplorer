using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Lionear.SqlExplorer.Core.Localization;
using Lionear.SqlExplorer.Core.Settings;
using Lionear.SqlExplorer.Sdk;
using Lionear.SqlExplorer.Sdk.Connections;
using Lionear.SqlExplorer.Sdk.Schema;
using Lionear.SqlExplorer.Sdk.Tools;
using Lionear.SqlExplorer.Sdk.Ui;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Lionear.SqlExplorer.App.ViewModels;

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

    private IToolPlugin _tool = null!;
    private ConnectionProfile _profile = null!;
    private DbNodeRef? _node;
    private IDbProvider _provider = null!;
    private string _providerId = string.Empty;
    private CancellationTokenSource? _cts;

    public ToolDialogViewModel(ILocalizer localizer, IPluginSettingsStore pluginStore)
    {
        Loc = localizer;
        _pluginStore = pluginStore;
    }

    public ILocalizer Loc { get; }

    /// <summary>Set by the view: shows a save-file picker (suggestedName, extensions) → path or null.</summary>
    public Func<string, string[], Task<string?>>? SaveFilePicker { get; set; }

    /// <summary>Set by the view: shows an open-file picker (extensions) → path or null.</summary>
    public Func<string[], Task<string?>>? OpenFilePicker { get; set; }

    // --- IToolHost: the host services handed to the running tool ---
    Task<string?> IToolHost.PickSaveFileAsync(string suggestedName, params string[] extensions) =>
        SaveFilePicker?.Invoke(suggestedName, extensions) ?? Task.FromResult<string?>(null);

    Task<string?> IToolHost.PickOpenFileAsync(params string[] extensions) =>
        OpenFilePicker?.Invoke(extensions) ?? Task.FromResult<string?>(null);

    string? IToolHost.GetPluginSetting(string key) =>
        _pluginStore.Get(_tool.Id).TryGetValue(key, out var value) ? value : null;

    /// <summary>Set by the view so the VM can ask a yes/no question (destructive confirm).</summary>
    public Func<string, string, Task<bool>>? ConfirmRequested { get; set; }

    /// <summary>Set by the view; called to close the window.</summary>
    public Action? CloseRequested { get; set; }

    public ObservableCollection<ToolFieldInput> Fields { get; } = [];

    public ObservableCollection<string> Log { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExecute))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    private string? _preview;

    public bool HasPreview => !string.IsNullOrEmpty(Preview);

    [ObservableProperty]
    private string _title = string.Empty;

    /// <summary>Route B: a tool-supplied view drives the form instead of the generated fields.</summary>
    [ObservableProperty]
    private Control? _customView;

    public bool HasCustomView => CustomView is not null;

    public bool HasFields => CustomView is null && Fields.Count > 0;

    public bool CanExecute => !IsRunning && Fields.All(f => f.IsFilled);

    /// <summary>Prepare the dialog for one tool run against the given connection/node.</summary>
    public void Configure(IToolPlugin tool, ConnectionProfile profile, DbNodeRef? node, IDbProvider provider, string providerId)
    {
        _tool = tool;
        _profile = profile;
        _node = node;
        _provider = provider;
        _providerId = providerId;
        Title = tool.Title;

        if (tool is ICustomToolUi customUi)
        {
            CustomView = customUi.CreateView(this);
        }
        else
        {
            foreach (var field in tool.Fields)
            {
                var input = new ToolFieldInput(field);
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
        IsRunning = true;
        Log.Clear();

        var inputs = HasCustomView
            ? _customValues
            : Fields.ToDictionary(f => f.Field.Key, f => f.Value);
        var context = new ToolExecutionContext(_profile, _node, _provider, _providerId, this);
        var progress = new Progress<ToolProgress>(p => Log.Add(p.Message));

        try
        {
            await _tool.ExecuteAsync(context, inputs, progress, _cts.Token);
            Log.Add(Loc["ToolDone"]);
        }
        catch (OperationCanceledException)
        {
            Log.Add(Loc["ToolCancelled"]);
        }
        catch (Exception ex)
        {
            Log.Add($"⚠ {ex.Message}");
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
}
