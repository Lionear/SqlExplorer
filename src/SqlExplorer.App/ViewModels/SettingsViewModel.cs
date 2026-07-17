using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia.Media;
using SqlExplorer.App.Theming;
using SqlExplorer.Core.Localization;
using SqlExplorer.Core.Providers;
using SqlExplorer.Core.Settings;
using SqlExplorer.Core.Shortcuts;
using SqlExplorer.Core.Store;
using SqlExplorer.Core.Tools;
using SqlExplorer.Sdk;
using SqlExplorer.Sdk.Settings;
using SqlExplorer.Sdk.Tools;
using SqlExplorer.Sdk.Ui;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SqlExplorer.App.ViewModels;

/// <summary>One entry in the Settings category rail: a stable key, a localized label and a vector icon.</summary>
public sealed record SettingsCategory(string Key, string Label, Geometry Icon);

/// <summary>
/// Backs the Preferences window: General/Appearance/Editor/Query plus a Plugins category that lists
/// plugins declaring their own settings (Route A fields or a Route B view). Works on a copy of only the
/// fields shown here — <see cref="ApplyInternal"/> load-patch-saves so a concurrent window-geometry save
/// (<c>MainWindow.PersistLayout</c>) is never clobbered; plugin values go to their own store. Theme/language
/// take effect immediately (no restart) via <see cref="ThemeApplier"/>/<see cref="ILocalizer.SetCulture"/>.
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly IAppSettingsStore _store;
    private readonly IPluginSettingsStore _pluginStore;
    private readonly IDbProviderRegistry _providers;
    private readonly IToolRegistry _tools;
    private readonly KeymapService _keymap;
    private readonly Mcp.Hosting.McpService _mcp;
    private readonly Core.Logging.IQueryLog _queryLog;
    private readonly Core.Security.MasterPasswordService _masterPassword;
    private readonly IStoreSourcesStore _sources;
    private readonly IStoreCatalog _catalog;
    private readonly System.Net.Http.HttpClient _http;

    // Idle auto-lock options (minutes; 0 = Never), index-matched to the Security-page dropdown.
    private static readonly int[] LockMinuteOptions = [0, 15, 30, 60];
    private readonly List<ShortcutItem> _allShortcuts = [];

    [ObservableProperty]
    private SettingsCategory? _selectedCategory;

    [ObservableProperty]
    private string? _language;

    /// <summary>A selectable UI language for the General-page dropdown. Names are endonyms (shown in the
    /// language itself), so they are deliberately not localized.</summary>
    public sealed record LanguageOption(string Code, string Name);

    public IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new("nl", "Nederlands"),
        new("en", "English")
    ];

    /// <summary>Two-way bridge between the language dropdown (<see cref="LanguageOption"/> items) and the
    /// stored <see cref="Language"/> code — kept in sync both ways so Restore-defaults/Load also move it.</summary>
    public LanguageOption? SelectedLanguage
    {
        get => Languages.FirstOrDefault(l => l.Code == Language);
        set
        {
            if (value is not null && value.Code != Language)
            {
                Language = value.Code;
            }
        }
    }

    partial void OnLanguageChanged(string? value) => OnPropertyChanged(nameof(SelectedLanguage));

    [ObservableProperty]
    private AppTheme _theme;

    [ObservableProperty]
    private double? _editorFontSize;

    [ObservableProperty]
    private bool _editorWordWrap;

    [ObservableProperty]
    private bool _confirmBeforeSave;

    [ObservableProperty]
    private int _queryTimeoutSeconds;

    [ObservableProperty]
    private int _browsePageSize;

    [ObservableProperty]
    private bool _restoreTabsOnStartup;

    [ObservableProperty]
    private bool _showSystemDatabases;

    [ObservableProperty]
    private bool _confirmOnExit;

    [ObservableProperty]
    private bool _closeToTray;

    // ── Query log ────────────────────────────────────────────────────────────────────────────────────
    [ObservableProperty]
    private bool _queryLogEnabled;

    [ObservableProperty]
    private bool _queryLogApp;

    [ObservableProperty]
    private bool _queryLogMcp;

    // ── Master password ──────────────────────────────────────────────────────────────────────────────
    [ObservableProperty]
    private bool _masterPasswordEnabled;

    /// <summary>Index into <see cref="LockMinuteOptions"/> for the idle auto-lock dropdown.</summary>
    [ObservableProperty]
    private int _masterPasswordLockIndex;

    [ObservableProperty]
    private string? _masterPasswordMessage;

    /// <summary>Set by the view: show the master-password dialog in the given mode, return the input.</summary>
    public Func<Views.MasterPasswordMode, Task<Views.MasterPasswordDialogResult?>>? PromptMasterPassword { get; set; }

    [RelayCommand]
    private async Task SetMasterPasswordAsync()
    {
        MasterPasswordMessage = null;
        if (PromptMasterPassword is null || await PromptMasterPassword(Views.MasterPasswordMode.Set) is not { NewPassword: { Length: > 0 } pw })
        {
            return;
        }

        _masterPassword.Enable(pw);
        MasterPasswordEnabled = true;
        MasterPasswordMessage = Loc["MasterPwEnabledMsg"];
    }

    [RelayCommand]
    private async Task ChangeMasterPasswordAsync()
    {
        MasterPasswordMessage = null;
        if (PromptMasterPassword is null || await PromptMasterPassword(Views.MasterPasswordMode.Change) is not { Current: { } oldPw, NewPassword: { Length: > 0 } newPw })
        {
            return;
        }

        MasterPasswordMessage = _masterPassword.Change(oldPw, newPw) ? Loc["MasterPwChangedMsg"] : Loc["MasterPwWrong"];
    }

    [RelayCommand]
    private async Task DisableMasterPasswordAsync()
    {
        MasterPasswordMessage = null;
        // Reuse the unlock dialog (single field) with no inline validator, so the service verifies it.
        if (PromptMasterPassword is null || await PromptMasterPassword(Views.MasterPasswordMode.Unlock) is not { Current: { } pw })
        {
            return;
        }

        if (_masterPassword.Disable(pw))
        {
            MasterPasswordEnabled = false;
            MasterPasswordMessage = Loc["MasterPwDisabledMsg"];
        }
        else
        {
            MasterPasswordMessage = Loc["MasterPwWrong"];
        }
    }

    [ObservableProperty]
    private PluginSettingsItem? _selectedPlugin;

    // ── MCP server (top-level) ───────────────────────────────────────────────────────────────────────
    [ObservableProperty]
    private bool _mcpEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(McpUrl))]
    private int _mcpPort;

    /// <summary>The loopback URL the server listens on — mirrors the hardcoded 127.0.0.1 bind in <see cref="Mcp.Hosting.McpServer"/>.</summary>
    public string McpUrl => $"http://127.0.0.1:{McpPort}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoAuthWarning))]
    private bool _mcpRequireAuth;

    [ObservableProperty]
    private string? _mcpToken;

    [ObservableProperty]
    private int _mcpMaxRows;

    [ObservableProperty]
    private int _mcpTimeoutSeconds;

    /// <summary>Show the "any local process can query" warning when auth is turned off (plan §6 / CRIT-3).</summary>
    public bool ShowNoAuthWarning => !McpRequireAuth;

    [RelayCommand]
    private void RegenerateMcpToken() =>
        McpToken = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    public SettingsViewModel(
        IAppSettingsStore store,
        IPluginSettingsStore pluginStore,
        IDbProviderRegistry providers,
        IToolRegistry tools,
        KeymapService keymap,
        Mcp.Hosting.McpService mcp,
        Core.Logging.IQueryLog queryLog,
        Core.Security.MasterPasswordService masterPassword,
        IStoreSourcesStore sources,
        IStoreCatalog catalog,
        System.Net.Http.HttpClient http,
        ILocalizer localizer)
    {
        _store = store;
        _pluginStore = pluginStore;
        _providers = providers;
        _tools = tools;
        _keymap = keymap;
        _mcp = mcp;
        _queryLog = queryLog;
        _masterPassword = masterPassword;
        _sources = sources;
        _catalog = catalog;
        _http = http;
        Loc = localizer;

        Categories =
        [
            new SettingsCategory("General", localizer["SettingsGeneralCat"], NodeIcons.SettingsGeneral),
            new SettingsCategory("Appearance", localizer["SettingsAppearance"], NodeIcons.SettingsAppearance),
            new SettingsCategory("Editor", localizer["SettingsEditor"], NodeIcons.SettingsEditor),
            new SettingsCategory("Query", localizer["SettingsQuery"], NodeIcons.SettingsQuery),
            new SettingsCategory("QueryLog", localizer["SettingsQueryLog"], NodeIcons.SettingsQuery),
            new SettingsCategory("Keyboard", localizer["SettingsKeyboard"], NodeIcons.SettingsKeyboard),
            new SettingsCategory("Mcp", localizer["SettingsMcp"], NodeIcons.SettingsPlugins),
            new SettingsCategory("Security", localizer["SettingsSecurity"], NodeIcons.SettingsGeneral),
            new SettingsCategory("Plugins", localizer["SettingsPlugins"], NodeIcons.SettingsPlugins),
            new SettingsCategory("PluginSources", localizer["SettingsPluginSources"], NodeIcons.SettingsPlugins),
        ];
        _selectedCategory = Categories[0];

        LoadFromStore();
        BuildPluginCatalog();
        BuildShortcutCatalog();
        LoadManualSources();
        _ = RefreshDiscoverySourcesAsync();
    }

    /// <summary>Open on a specific category (deep-link from another window, e.g. Plugin Store's
    /// "Manage sources…" button lands on <c>PluginSources</c>). Falls back to the first category if
    /// the key is unknown.</summary>
    public void SelectCategoryByKey(string key)
    {
        var match = Categories.FirstOrDefault(c => c.Key == key);
        if (match is not null)
        {
            SelectedCategory = match;
        }
    }

    // --- Plugin Sources (relocated from PluginStoreViewModel, SE-122). Bindings hit IStoreSourcesStore
    // directly so the Plugin Store's next Refresh picks up any change without extra plumbing. -----------

    public ObservableCollection<SourceRow> DiscoverySources { get; } = [];
    public ObservableCollection<SourceRow> ManualSources { get; } = [];

    [ObservableProperty]
    private string? _newSourceUrl;

    private void LoadManualSources()
    {
        ManualSources.Clear();
        foreach (var url in _sources.GetManualSources())
        {
            ManualSources.Add(new SourceRow(url, name: null, isDiscovery: false, ok: true, error: null, iconUrl: null));
        }
    }

    // Best-effort discovery refresh: any failure just leaves the source list empty, matching the
    // Plugin Store's own tolerance for offline catalogs.
    private async Task RefreshDiscoverySourcesAsync()
    {
        try
        {
            var catalog = await _catalog.FetchAsync(System.Threading.CancellationToken.None);
            DiscoverySources.Clear();
            foreach (var source in catalog.Sources.Where(s => s.IsDiscovery))
            {
                var row = new SourceRow(source.Url, source.Name, isDiscovery: true, source.Ok, source.Error, source.IconUrl);
                DiscoverySources.Add(row);
                _ = LoadIconAsync(row);
            }

            // Update the manual-source statuses with what the catalog observed (ok/error).
            var byUrl = catalog.Sources.ToDictionary(s => s.Url, s => s, System.StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < ManualSources.Count; i++)
            {
                var row = ManualSources[i];
                if (byUrl.TryGetValue(row.Url, out var status))
                {
                    ManualSources[i] = new SourceRow(row.Url, name: null, isDiscovery: false, status.Ok, status.Error, iconUrl: null);
                }
            }
        }
        catch
        {
            // ignored — offline / DNS fail leaves the last known list intact
        }
    }

    // Same downsample-at-load as the Plugin Store — remote source icons render just as small.
    private async Task LoadIconAsync(SourceRow row)
    {
        if (string.IsNullOrEmpty(row.IconUrl))
        {
            return;
        }

        try
        {
            var bytes = await _http.GetByteArrayAsync(row.IconUrl);
            using var stream = new System.IO.MemoryStream(bytes);
            row.Icon = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(
                stream, PluginIconRenderer.IconDecodeWidth, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
        }
        catch
        {
            // ignored — missing icon just leaves the slot empty
        }
    }

    [RelayCommand]
    private async Task AddManualSourceAsync()
    {
        if (string.IsNullOrWhiteSpace(NewSourceUrl))
        {
            return;
        }

        _sources.AddManualSource(NewSourceUrl);
        NewSourceUrl = null;
        LoadManualSources();
        await RefreshDiscoverySourcesAsync();
    }

    [RelayCommand]
    private async Task RemoveManualSourceAsync(SourceRow row)
    {
        _sources.RemoveManualSource(row.Url);
        LoadManualSources();
        await RefreshDiscoverySourcesAsync();
    }

    public ILocalizer Loc { get; }

    public ObservableCollection<SettingsCategory> Categories { get; }

    public IReadOnlyList<AppTheme> Themes { get; } = [AppTheme.System, AppTheme.Light, AppTheme.Dark];

    /// <summary>Plugins that declare settings (Route A or B); empty when none do.</summary>
    public ObservableCollection<PluginSettingsItem> Plugins { get; } = [];

    public bool HasPlugins => Plugins.Count > 0;

    /// <summary>Shortcut rows grouped by section (Tabs/Query/Editor/Search) for the Keyboard category.</summary>
    public ObservableCollection<ShortcutGroup> ShortcutGroups { get; } = [];

    /// <summary>True while two commands share the same gesture; blocks saving until resolved.</summary>
    [ObservableProperty]
    private bool _hasShortcutConflicts;

    /// <summary>Set by the view; called to close the window (Apply and Close, or Cancel).</summary>
    public Action? CloseRequested { get; set; }

    private void LoadFromStore()
    {
        var settings = _store.Load();
        Language = settings.Language;
        Theme = settings.Theme;
        EditorFontSize = settings.EditorFontSize;
        EditorWordWrap = settings.EditorWordWrap;
        ConfirmBeforeSave = settings.ConfirmBeforeSave;
        QueryTimeoutSeconds = settings.QueryTimeoutSeconds;
        BrowsePageSize = settings.BrowsePageSize;
        RestoreTabsOnStartup = settings.RestoreTabsOnStartup;
        ShowSystemDatabases = settings.ShowSystemDatabases;
        ConfirmOnExit = settings.ConfirmOnExit;
        CloseToTray = settings.CloseToTray;
        QueryLogEnabled = settings.QueryLogEnabled;
        QueryLogApp = settings.QueryLogApp;
        QueryLogMcp = settings.QueryLogMcp;
        MasterPasswordEnabled = settings.MasterPasswordEnabled;
        MasterPasswordLockIndex = Math.Max(0, Array.IndexOf(LockMinuteOptions, settings.MasterPasswordLockMinutes));
        MasterPasswordMessage = null;
        McpEnabled = settings.McpEnabled;
        McpPort = settings.McpPort;
        McpRequireAuth = settings.McpRequireAuth;
        McpToken = settings.McpToken;
        McpMaxRows = settings.McpMaxRows;
        McpTimeoutSeconds = settings.McpTimeoutSeconds;
    }

    // A plugin (provider or tool) gets a tree entry only if it declares fields (Route A) or a custom
    // view (Route B). Both plugin kinds contribute, keyed by their id.
    private void BuildPluginCatalog()
    {
        foreach (var registration in _providers.All)
        {
            TryAddPlugin(registration.Id, registration.Provider.DisplayName, registration.Provider.Icon, registration.Provider);
        }

        foreach (var tool in _tools.All)
        {
            TryAddPlugin(tool.Id, tool.Title, tool.Icon, tool);
        }

        SelectedPlugin = Plugins.FirstOrDefault();
        OnPropertyChanged(nameof(HasPlugins));
    }

    private void TryAddPlugin(string id, string displayName, ProviderIcon? icon, object plugin)
    {
        var declared = plugin as IPluginSettings;
        var customUi = plugin as ICustomPluginSettingsUi;
        if (declared is not { SettingsFields.Count: > 0 } && customUi is null)
        {
            return;
        }

        Plugins.Add(new PluginSettingsItem(id, displayName, icon, _pluginStore.Get(id), declared, customUi));
    }

    // Build the Keyboard category from the catalog, grouped by section, seeded with the live effective
    // gestures. Each row is watched so any edit re-runs conflict detection across the whole list.
    private void BuildShortcutCatalog()
    {
        foreach (var group in _keymap.Commands.GroupBy(c => c.GroupKey))
        {
            var items = new List<ShortcutItem>();
            foreach (var command in group)
            {
                var item = new ShortcutItem(command.Id, Loc[command.LabelKey], _keymap.DefaultGesture(command.Id) ?? string.Empty, _keymap.Resolve(command.Id));
                item.PropertyChanged += OnShortcutChanged;
                items.Add(item);
                _allShortcuts.Add(item);
            }

            ShortcutGroups.Add(new ShortcutGroup(Loc[group.Key], items));
        }

        // Plugin-contributed shortcuts get one group per owning plugin, after the built-ins. They share the
        // same conflict detection and persistence — a plugin key can clash with a built-in and vice versa.
        foreach (var group in _keymap.PluginShortcuts.GroupBy(p => p.PluginTitle))
        {
            var items = new List<ShortcutItem>();
            foreach (var plugin in group)
            {
                var item = new ShortcutItem(plugin.Id, plugin.Title, _keymap.DefaultGesture(plugin.Id) ?? string.Empty, _keymap.Resolve(plugin.Id));
                item.PropertyChanged += OnShortcutChanged;
                items.Add(item);
                _allShortcuts.Add(item);
            }

            ShortcutGroups.Add(new ShortcutGroup(group.Key, items));
        }

        RecomputeShortcutConflicts();
    }

    private void OnShortcutChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShortcutItem.Gesture))
        {
            RecomputeShortcutConflicts();
        }
    }

    // A conflict = two bound commands sharing the same gesture. Both offending rows are flagged with the
    // other's label so the UI can explain the clash; saving is blocked while any conflict stands.
    private void RecomputeShortcutConflicts()
    {
        foreach (var item in _allShortcuts)
        {
            item.HasConflict = false;
            item.ConflictWith = null;
        }

        var byGesture = _allShortcuts
            .Where(i => !string.IsNullOrWhiteSpace(i.Gesture))
            .GroupBy(i => i.Gesture, StringComparer.Ordinal);

        foreach (var clash in byGesture.Where(g => g.Count() > 1))
        {
            var members = clash.ToList();
            foreach (var item in members)
            {
                item.HasConflict = true;
                item.ConflictWith = members.First(o => o != item).Label;
            }
        }

        HasShortcutConflicts = _allShortcuts.Any(i => i.HasConflict);
        ApplyCommand.NotifyCanExecuteChanged();
        ApplyAndCloseCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void SetTheme(AppTheme theme) => Theme = theme;

    [RelayCommand]
    private void RestoreDefaults()
    {
        // Scoped to the app-preference fields; plugin settings keep their own values.
        var defaults = new AppSettings();
        Language = defaults.Language;
        Theme = defaults.Theme;
        EditorFontSize = defaults.EditorFontSize;
        EditorWordWrap = defaults.EditorWordWrap;
        ConfirmBeforeSave = defaults.ConfirmBeforeSave;
        QueryTimeoutSeconds = defaults.QueryTimeoutSeconds;
        BrowsePageSize = defaults.BrowsePageSize;
        RestoreTabsOnStartup = defaults.RestoreTabsOnStartup;
        ShowSystemDatabases = defaults.ShowSystemDatabases;
        ConfirmOnExit = defaults.ConfirmOnExit;
        CloseToTray = defaults.CloseToTray;
        QueryLogEnabled = defaults.QueryLogEnabled;
        QueryLogApp = defaults.QueryLogApp;
        QueryLogMcp = defaults.QueryLogMcp;
        // MCP: reset the tunables but keep an existing token (regenerate is an explicit action).
        McpEnabled = defaults.McpEnabled;
        McpPort = defaults.McpPort;
        McpRequireAuth = defaults.McpRequireAuth;
        McpMaxRows = defaults.McpMaxRows;
        McpTimeoutSeconds = defaults.McpTimeoutSeconds;

        // Keyboard shortcuts reset to their factory bindings too.
        foreach (var shortcut in _allShortcuts)
        {
            shortcut.Gesture = shortcut.DefaultGesture;
        }
    }

    private bool CanSave() => !HasShortcutConflicts;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Apply() => ApplyInternal();

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void ApplyAndClose()
    {
        ApplyInternal();
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();

    private void ApplyInternal()
    {
        // Load-patch-save: never overwrite fields this window doesn't own (window geometry, sidebar).
        var settings = _store.Load();
        settings.Language = Language;
        settings.Theme = Theme;
        settings.EditorFontSize = EditorFontSize;
        settings.EditorWordWrap = EditorWordWrap;
        settings.ConfirmBeforeSave = ConfirmBeforeSave;
        settings.QueryTimeoutSeconds = QueryTimeoutSeconds;
        settings.BrowsePageSize = BrowsePageSize;
        settings.RestoreTabsOnStartup = RestoreTabsOnStartup;
        settings.ShowSystemDatabases = ShowSystemDatabases;
        settings.ConfirmOnExit = ConfirmOnExit;
        settings.CloseToTray = CloseToTray;
        settings.QueryLogEnabled = QueryLogEnabled;
        settings.QueryLogApp = QueryLogApp;
        settings.QueryLogMcp = QueryLogMcp;
        // Master-password enable/change/disable persist themselves immediately; here we only carry the
        // idle-lock interval (a plain preference) and re-apply it.
        settings.MasterPasswordLockMinutes = LockMinuteOptions[Math.Clamp(MasterPasswordLockIndex, 0, LockMinuteOptions.Length - 1)];
        // Generate a token on enabling auth if none is set yet, so the field is never empty when required.
        if (McpEnabled && McpRequireAuth && string.IsNullOrEmpty(McpToken))
        {
            RegenerateMcpToken();
        }

        settings.McpEnabled = McpEnabled;
        settings.McpPort = McpPort;
        settings.McpRequireAuth = McpRequireAuth;
        settings.McpToken = McpToken;
        settings.McpMaxRows = McpMaxRows;
        settings.McpTimeoutSeconds = McpTimeoutSeconds;
        _store.Save(settings);

        // Apply MCP changes immediately (start/stop/restart the server with the new settings).
        _ = _mcp.ApplyAsync();

        // Apply the query-log policy immediately so toggling it takes effect without a restart.
        _queryLog.Configure(QueryLogEnabled, QueryLogApp, QueryLogMcp);

        // Re-apply the idle auto-lock interval (no-op when master password is off).
        _masterPassword.ApplyIdleTimeout();

        // Plugin settings live in their own file, keyed by plugin id.
        foreach (var plugin in Plugins)
        {
            _pluginStore.Save(plugin.PluginId, plugin.CollectValues());
        }

        // Keyboard shortcuts: hand the whole edited map to the keymap service (persists diffs vs. default
        // and raises Changed so the main window rebinds live).
        _keymap.Apply(_allShortcuts.ToDictionary(s => s.Id, s => s.Gesture));

        ThemeApplier.Apply(Theme);
        if (Language is { Length: > 0 } language)
        {
            Loc.SetCulture(CultureInfo.GetCultureInfo(language));
        }
    }
}
