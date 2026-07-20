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
using SqlExplorer.Sdk.Formatting;
using SqlExplorer.Sdk.Settings;
using SqlExplorer.Sdk.Tools;
using SqlExplorer.Sdk.Ui;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SqlExplorer.App.ViewModels;

/// <summary>One entry in the Settings category rail: a stable key, a localized label, a vector icon, and
/// optional space-separated search <paramref name="Keywords"/> (EN/NL terms for settings inside it) so the
/// search box (SE-161) can surface a category by the setting you're looking for, not just its label.</summary>
public sealed record SettingsCategory(string Key, string Label, Geometry Icon, string Keywords = "");

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
    private readonly AppUpdateViewModel _update;
    private readonly Core.Update.IUpdateApplier _updateApplier;

    // Idle auto-lock options (minutes; 0 = Never), index-matched to the Security-page dropdown.
    private static readonly int[] LockMinuteOptions = [0, 15, 30, 60];
    private readonly List<ShortcutItem> _allShortcuts = [];

    [ObservableProperty]
    private SettingsCategory? _selectedCategory;

    // The full category list; Categories is the (search-)filtered view bound by the rail (SE-161).
    private IReadOnlyList<SettingsCategory> _allCategories = [];

    /// <summary>The key of the category whose content pane is shown. Driven by the rail selection but kept
    /// separate so a search that filters the rail (and momentarily nulls the ListBox selection) can never
    /// leave the content area blank — or, worse, show every pane at once (SE-161).</summary>
    [ObservableProperty]
    private string _activeCategoryKey = "General";

    partial void OnSelectedCategoryChanged(SettingsCategory? value)
    {
        if (value is not null)
        {
            ActiveCategoryKey = value.Key;
        }
    }

    /// <summary>Search text for the category rail (SE-161): filters categories by label or keywords.</summary>
    [ObservableProperty]
    private string? _settingsSearch;

    partial void OnSettingsSearchChanged(string? value) => ApplyCategoryFilter();

    // Filter the rail to categories whose label or keywords contain the query; empty shows all. The content
    // pane follows ActiveCategoryKey (not the rail selection), so a no-match query just empties the rail and
    // leaves the last pane showing — the rail selection can safely go null without blanking the content.
    private void ApplyCategoryFilter()
    {
        var query = SettingsSearch?.Trim();
        var visible = string.IsNullOrEmpty(query)
            ? _allCategories
            : _allCategories
                .Where(c => c.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
                            || c.Keywords.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

        var previous = SelectedCategory;
        Categories.Clear();
        foreach (var category in visible)
        {
            Categories.Add(category);
        }

        // Select the previously-shown category if it's still visible, else the first match; on no match leave
        // the selection null (empty rail) — ActiveCategoryKey keeps the content on the last-shown pane.
        if (visible.Count > 0)
        {
            SelectedCategory = previous is not null && visible.Contains(previous) ? previous : visible[0];
        }
    }

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

    // ── App updates (SE-137) ─────────────────────────────────────────────────────────────────────────

    /// <summary>A selectable release channel card in the Updates pane: label, a badge (recommended / the
    /// branch it tracks), a one-line description and the accent dot colour.</summary>
    public sealed record UpdateChannelOption(
        Core.Update.UpdateChannel Channel, string Label, string Badge, string Description,
        IBrush DotBrush, IBrush BadgeBg);

    public IReadOnlyList<UpdateChannelOption> UpdateChannels { get; }

    private static IBrush ChannelDot(string hex) => new SolidColorBrush(Color.Parse(hex));

    // A translucent tint of the channel colour, for the badge background behind the coloured label.
    private static IBrush ChannelTint(string hex)
    {
        var c = Color.Parse(hex);
        return new SolidColorBrush(new Color(0x33, c.R, c.G, c.B));
    }

    [ObservableProperty]
    private Core.Update.UpdateChannel _selectedUpdateChannel;

    /// <summary>Two-way bridge between the channel dropdown and <see cref="SelectedUpdateChannel"/>.</summary>
    public UpdateChannelOption? SelectedUpdateChannelOption
    {
        get => UpdateChannels.FirstOrDefault(o => o.Channel == SelectedUpdateChannel);
        set
        {
            if (value is not null)
            {
                SelectedUpdateChannel = value.Channel;
            }
        }
    }

    partial void OnSelectedUpdateChannelChanged(Core.Update.UpdateChannel value)
    {
        OnPropertyChanged(nameof(SelectedUpdateChannelOption));
        UpdateCheckStatus = null;
    }

    /// <summary>A background re-check interval preset in the Updates pane. <see cref="Minutes"/> 0 means
    /// "only on startup" (no periodic loop).</summary>
    public sealed record UpdateIntervalOption(int Minutes, string Label);

    public IReadOnlyList<UpdateIntervalOption> UpdateIntervals { get; }

    [ObservableProperty]
    private int _selectedUpdateIntervalMinutes;

    /// <summary>Two-way bridge between the interval dropdown and <see cref="SelectedUpdateIntervalMinutes"/>.</summary>
    public UpdateIntervalOption? SelectedUpdateIntervalOption
    {
        get => UpdateIntervals.FirstOrDefault(o => o.Minutes == SelectedUpdateIntervalMinutes)
               ?? UpdateIntervals.FirstOrDefault();
        set
        {
            if (value is not null)
            {
                SelectedUpdateIntervalMinutes = value.Minutes;
            }
        }
    }

    partial void OnSelectedUpdateIntervalMinutesChanged(int value) =>
        OnPropertyChanged(nameof(SelectedUpdateIntervalOption));

    // ── SQL formatter (SE-148) ───────────────────────────────────────────────────────────────────────
    public sealed record KeywordCasingOption(KeywordCasing Casing, string Label);

    public IReadOnlyList<KeywordCasingOption> KeywordCasings { get; }

    [ObservableProperty]
    private KeywordCasing _formatKeywordCasing;

    /// <summary>Two-way bridge between the casing dropdown and <see cref="FormatKeywordCasing"/>.</summary>
    public KeywordCasingOption? SelectedKeywordCasingOption
    {
        get => KeywordCasings.FirstOrDefault(o => o.Casing == FormatKeywordCasing) ?? KeywordCasings.FirstOrDefault();
        set
        {
            if (value is not null)
            {
                FormatKeywordCasing = value.Casing;
            }
        }
    }

    partial void OnFormatKeywordCasingChanged(KeywordCasing value) =>
        OnPropertyChanged(nameof(SelectedKeywordCasingOption));

    /// <summary>Indent-width presets (spaces) for the formatter dropdown.</summary>
    public IReadOnlyList<int> IndentSizes { get; } = [2, 4, 8];

    [ObservableProperty]
    private int _formatIndentSize;

    // ── Proactive plugin updates (SE-138) ────────────────────────────────────────────────────────────
    public sealed record PluginUpdatePolicyOption(PluginUpdatePolicy Policy, string Label);

    public IReadOnlyList<PluginUpdatePolicyOption> PluginUpdatePolicies { get; }

    [ObservableProperty]
    private PluginUpdatePolicy _pluginUpdatePolicy;

    /// <summary>Two-way bridge between the policy dropdown and <see cref="PluginUpdatePolicy"/>.</summary>
    public PluginUpdatePolicyOption? SelectedPluginUpdatePolicyOption
    {
        get => PluginUpdatePolicies.FirstOrDefault(o => o.Policy == PluginUpdatePolicy) ?? PluginUpdatePolicies.FirstOrDefault();
        set
        {
            if (value is not null)
            {
                PluginUpdatePolicy = value.Policy;
            }
        }
    }

    partial void OnPluginUpdatePolicyChanged(PluginUpdatePolicy value) =>
        OnPropertyChanged(nameof(SelectedPluginUpdatePolicyOption));

    [ObservableProperty]
    private bool _checkForUpdatesOnStartup;

    [ObservableProperty]
    private string? _updateCheckStatus;

    [ObservableProperty]
    private bool _isCheckingUpdate;

    /// <summary>True when a previous build is staged to roll back to (Linux AppImage only, Fase 2).</summary>
    public bool CanRollback => _updateApplier.CanRollback;

    /// <summary>Set by the view: carries out the rollback result (relaunch + exit) on the desktop lifetime.</summary>
    public Func<Core.Update.ApplyResult, System.Threading.Tasks.Task>? RollbackRequested { get; set; }

    /// <summary>Roll back to the previous app version from the Updates pane.</summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task RollBackUpdate()
    {
        var result = _updateApplier.Rollback();
        if (result.Action == Core.Update.ApplyAction.Failed)
        {
            UpdateCheckStatus = result.Message;
            return;
        }

        if (RollbackRequested is not null)
        {
            await RollbackRequested(result);
        }
    }

    /// <summary>The shared updater VM — Settings binds its "What's new" button to the same banner state, so a
    /// manual check here lights the main-window banner too.</summary>
    public AppUpdateViewModel Update => _update;

    /// <summary>Set by the view: shows the changelog dialog owned by the Settings window.</summary>
    public Func<UpdateAvailableViewModel, System.Threading.Tasks.Task>? ChangelogRequested { get; set; }

    /// <summary>Manual "Check for updates": routes through the shared VM so the banner + "What's new" light up.</summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task CheckForUpdatesNow()
    {
        IsCheckingUpdate = true;
        UpdateCheckStatus = Loc["UpdateCheckChecking"];
        try
        {
            var status = await _update.RunCheckAsync(SelectedUpdateChannel, System.Threading.CancellationToken.None);
            UpdateCheckStatus = status switch
            {
                Core.Update.UpdateStatus.Available => Loc.Get("UpdateCheckAvailable", _update.OfferedVersion ?? string.Empty),
                Core.Update.UpdateStatus.UpToDate => Loc["UpdateCheckUpToDate"],
                _ => Loc["UpdateCheckFailed"]
            };
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    /// <summary>Open the changelog dialog (with Install/Download) for the found update, from Settings.</summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task ShowWhatsNew()
    {
        var dialog = _update.BuildDialog();
        if (dialog is not null && ChangelogRequested is not null)
        {
            await ChangelogRequested(dialog);
        }
    }

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
    private bool _pageQueryResults;

    [ObservableProperty]
    private int _queryPageSize;

    [ObservableProperty]
    private bool _restoreTabsOnStartup;

    [ObservableProperty]
    private bool _promptSaveQueryOnClose;

    [ObservableProperty]
    private bool _showSystemDatabases;

    [ObservableProperty]
    private bool _confirmOnExit;

    [ObservableProperty]
    private bool _closeToTray;

    /// <summary>Show only one bottom-docked tool panel (Output/Containers/plugin panels) at a time (SE-165).</summary>
    [ObservableProperty]
    private bool _singleBottomPanel;

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoScrubWarning))]
    private bool _mcpScrubSecrets;

    /// <summary>Show the "any local process can query" warning when auth is turned off (plan §6 / CRIT-3).</summary>
    public bool ShowNoAuthWarning => !McpRequireAuth;

    /// <summary>Warn that live secrets in results may reach the AI when scrubbing is turned off (SE-145).</summary>
    public bool ShowNoScrubWarning => !McpScrubSecrets;

    // ── MCP connection creation (SE-155) ─────────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowConnectionCreateWarning))]
    private bool _mcpAllowConnectionCreate;

    [ObservableProperty]
    private string _mcpConnectionFolder = "MCP";

    /// <summary>Warn when AI connection-creation is enabled — any local MCP client can then create (and, for a
    /// transient loopback connection, DDL against) connections. Off by default; the UI makes the change loud.</summary>
    public bool ShowConnectionCreateWarning => McpAllowConnectionCreate;

    /// <summary>Extra hosts (beyond loopback, which is always allowed) an AI-created connection may target.</summary>
    public ObservableCollection<string> McpAllowedHosts { get; } = [];

    [ObservableProperty]
    private string? _newAllowedHost;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHostError))]
    private string? _hostError;

    public bool HasHostError => HostError is not null;

    [RelayCommand]
    private void AddAllowedHost()
    {
        var host = NewAllowedHost?.Trim();
        if (string.IsNullOrEmpty(host))
        {
            return;
        }

        if (!IsValidHost(host))
        {
            HostError = Loc["McpAllowedHostInvalid"];
            return;
        }

        if (!McpAllowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
        {
            McpAllowedHosts.Add(host);
        }

        NewAllowedHost = null;
        HostError = null;
    }

    [RelayCommand]
    private void RemoveAllowedHost(string host) => McpAllowedHosts.Remove(host);

    // A hostname or IP literal: letters/digits/dot/hyphen/underscore/colon (IPv6). Deliberately permissive —
    // the real gate is the exact-match allowlist check at create time; this only rejects obvious junk.
    private static bool IsValidHost(string host) =>
        host.Length <= 253 && host.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or ':' or '_');

    [RelayCommand]
    private void RegenerateMcpToken() =>
        McpToken = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    /// <summary>Live running-state of the MCP server, mirrored from <see cref="Mcp.Hosting.McpService"/> so the
    /// Settings pane reflects startup auto-start, Save-restarts and the manual toggle (SE-147).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(McpStatusText))]
    [NotifyPropertyChangedFor(nameof(McpToggleLabel))]
    private bool _mcpRunning;

    /// <summary>Status line for the MCP pane: "running — &lt;url&gt;" or "stopped".</summary>
    public string McpStatusText => McpRunning ? $"{Loc["McpServerRunning"]} — {McpUrl}" : Loc["McpServerStopped"];

    /// <summary>Label for the Start/Stop button, following the live state.</summary>
    public string McpToggleLabel => McpRunning ? Loc["McpStop"] : Loc["McpStart"];

    /// <summary>Start the server (persisting the current MCP settings first so a just-changed port/auth/token
    /// takes effect) or stop it, depending on the live state. The <c>StateChanged</c> handler updates
    /// <see cref="McpRunning"/>, so the button/status follow automatically.</summary>
    [RelayCommand]
    private async Task ToggleMcpServerAsync()
    {
        if (McpRunning)
        {
            await _mcp.StopAsync();
            return;
        }

        var settings = _store.Load();
        PersistMcpSettings(settings);
        _store.Save(settings);
        await _mcp.ApplyAsync();
    }

    private void OnMcpStateChanged() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => McpRunning = _mcp.IsRunning);

    /// <summary>Unsubscribe from the long-lived <see cref="Mcp.Hosting.McpService"/> singleton so the transient
    /// settings VM doesn't leak a handler each time the window opens. Called from the window's Closed event.</summary>
    public void Cleanup() => _mcp.StateChanged -= OnMcpStateChanged;

    /// <summary>Copy the edited MCP fields onto <paramref name="settings"/>, generating a bearer token first if
    /// auth is on and none is set. Shared by full Save and the Start-half of the Start/Stop toggle so both
    /// routes persist an identical, valid MCP configuration.</summary>
    private void PersistMcpSettings(AppSettings settings)
    {
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
        settings.McpScrubSecrets = McpScrubSecrets;
        settings.McpAllowConnectionCreate = McpAllowConnectionCreate;
        settings.McpAllowedHosts = McpAllowedHosts.Count > 0 ? McpAllowedHosts.ToList() : null;
        settings.McpConnectionFolder = string.IsNullOrWhiteSpace(McpConnectionFolder) ? "MCP" : McpConnectionFolder.Trim();
    }

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
        AppUpdateViewModel appUpdate,
        Core.Update.IUpdateApplier updateApplier,
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
        _update = appUpdate;
        _updateApplier = updateApplier;
        Loc = localizer;

        UpdateChannels =
        [
            new(Core.Update.UpdateChannel.Stable, localizer["UpdateChannelStable"],
                localizer["UpdateChannelStableBadge"], localizer["UpdateChannelStableDesc"],
                ChannelDot("#3FB950"), ChannelTint("#3FB950")),
            new(Core.Update.UpdateChannel.Preview, localizer["UpdateChannelPreview"],
                "main", localizer["UpdateChannelPreviewDesc"],
                ChannelDot("#3B82F6"), ChannelTint("#3B82F6")),
            new(Core.Update.UpdateChannel.Nightly, localizer["UpdateChannelNightly"],
                "develop", localizer["UpdateChannelNightlyDesc"],
                ChannelDot("#E0A33E"), ChannelTint("#E0A33E")),
        ];

        UpdateIntervals =
        [
            new(60, localizer["UpdateIntervalHourly"]),
            new(240, localizer["UpdateInterval4Hours"]),
            new(720, localizer["UpdateInterval12Hours"]),
            new(1440, localizer["UpdateIntervalDaily"]),
            new(10080, localizer["UpdateIntervalWeekly"]),
            new(0, localizer["UpdateIntervalManual"]),
        ];

        KeywordCasings =
        [
            new(KeywordCasing.Upper, localizer["FormatCasingUpper"]),
            new(KeywordCasing.Lower, localizer["FormatCasingLower"]),
            new(KeywordCasing.Preserve, localizer["FormatCasingPreserve"]),
        ];

        PluginUpdatePolicies =
        [
            new(PluginUpdatePolicy.Off, localizer["PluginUpdatePolicyOff"]),
            new(PluginUpdatePolicy.Notify, localizer["PluginUpdatePolicyNotify"]),
            new(PluginUpdatePolicy.Auto, localizer["PluginUpdatePolicyAuto"]),
        ];

        // Keywords are EN/NL terms for the settings inside each category, so the search box (SE-161) can
        // surface a category by the setting you're after, not only its label. Kept language-agnostic here.
        _allCategories =
        [
            new SettingsCategory("General", localizer["SettingsGeneralCat"], NodeIcons.SettingsGeneral,
                "language taal startup opstarten restore tabs herstel tray exit afsluiten system databases updates channel kanaal interval"),
            new SettingsCategory("Appearance", localizer["SettingsAppearance"], NodeIcons.SettingsAppearance,
                "theme thema dark donker light licht panel paneel bottom onder"),
            new SettingsCategory("Editor", localizer["SettingsEditor"], NodeIcons.SettingsEditor,
                "font lettergrootte size word wrap terugloop format opmaak keyword casing indent inspringen"),
            new SettingsCategory("Query", localizer["SettingsQuery"], NodeIcons.SettingsQuery,
                "timeout page pagina rows rijen results resultaten browse confirm bevestig paging pagineren next prev volgende vorige"),
            new SettingsCategory("QueryLog", localizer["SettingsQueryLog"], NodeIcons.SettingsQuery,
                "query log audit logging"),
            new SettingsCategory("Keyboard", localizer["SettingsKeyboard"], NodeIcons.SettingsKeyboard,
                "keyboard toetsenbord shortcuts sneltoetsen keybindings gestures"),
            new SettingsCategory("Mcp", localizer["SettingsMcp"], NodeIcons.SettingsPlugins,
                "mcp ai server token port poort auth connection connectie create aanmaken host scrub secrets redact rows"),
            new SettingsCategory("Security", localizer["SettingsSecurity"], NodeIcons.SettingsGeneral,
                "security beveiliging master password wachtwoord lock vergrendel idle"),
            new SettingsCategory("Plugins", localizer["SettingsPlugins"], NodeIcons.SettingsPlugins,
                "plugins update policy beleid auto notify"),
            new SettingsCategory("PluginSources", localizer["SettingsPluginSources"], NodeIcons.SettingsPlugins,
                "plugin sources bronnen bron source url discovery manual"),
        ];
        Categories = [.._allCategories];
        _selectedCategory = _allCategories[0];

        LoadFromStore();
        BuildPluginCatalog();
        BuildShortcutCatalog();
        LoadManualSources();
        _ = RefreshDiscoverySourcesAsync();

        // Reflect the MCP server's live state (SE-147). Seed from the current state, then track changes;
        // Cleanup() unsubscribes when the window closes so this transient VM doesn't leak on the singleton.
        McpRunning = _mcp.IsRunning;
        _mcp.StateChanged += OnMcpStateChanged;
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

    /// <summary>Why the last Add was refused; null when there is nothing to report.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSourceError))]
    private string? _sourceError;

    public bool HasSourceError => SourceError is not null;

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
        SourceError = null;
        if (string.IsNullOrWhiteSpace(NewSourceUrl))
        {
            return;
        }

        // Rejected here so the user finds out while typing rather than through a failed install later; the
        // fetch paths enforce the same rule themselves, since a URL can also reach the file by hand-editing.
        var url = NewSourceUrl.Trim();
        if (!StoreUrl.IsAllowed(url))
        {
            SourceError = Loc["StoreSourceInsecure"];
            return;
        }

        _sources.AddManualSource(url);
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
        FormatKeywordCasing = settings.FormatKeywordCasing;
        FormatIndentSize = settings.FormatIndentSize;
        PluginUpdatePolicy = settings.PluginUpdatePolicy;
        ConfirmBeforeSave = settings.ConfirmBeforeSave;
        QueryTimeoutSeconds = settings.QueryTimeoutSeconds;
        BrowsePageSize = settings.BrowsePageSize;
        PageQueryResults = settings.PageQueryResults;
        QueryPageSize = settings.QueryPageSize;
        RestoreTabsOnStartup = settings.RestoreTabsOnStartup;
        PromptSaveQueryOnClose = settings.PromptSaveQueryOnClose;
        SelectedUpdateChannel = settings.UpdateChannel ?? _update.RunningChannel;
        CheckForUpdatesOnStartup = settings.CheckForUpdatesOnStartup;
        SelectedUpdateIntervalMinutes = settings.UpdateCheckIntervalMinutes;
        UpdateCheckStatus = null;
        ShowSystemDatabases = settings.ShowSystemDatabases;
        SingleBottomPanel = settings.SingleBottomPanel;
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
        McpScrubSecrets = settings.McpScrubSecrets;
        McpAllowConnectionCreate = settings.McpAllowConnectionCreate;
        McpConnectionFolder = string.IsNullOrWhiteSpace(settings.McpConnectionFolder) ? "MCP" : settings.McpConnectionFolder;
        McpAllowedHosts.Clear();
        foreach (var host in settings.McpAllowedHosts ?? [])
        {
            if (!string.IsNullOrWhiteSpace(host))
            {
                McpAllowedHosts.Add(host.Trim());
            }
        }

        HostError = null;
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
        FormatKeywordCasing = defaults.FormatKeywordCasing;
        FormatIndentSize = defaults.FormatIndentSize;
        PluginUpdatePolicy = defaults.PluginUpdatePolicy;
        ConfirmBeforeSave = defaults.ConfirmBeforeSave;
        QueryTimeoutSeconds = defaults.QueryTimeoutSeconds;
        BrowsePageSize = defaults.BrowsePageSize;
        PageQueryResults = defaults.PageQueryResults;
        QueryPageSize = defaults.QueryPageSize;
        RestoreTabsOnStartup = defaults.RestoreTabsOnStartup;
        PromptSaveQueryOnClose = defaults.PromptSaveQueryOnClose;
        // No explicit default channel: fall back to the running build's channel, same as a fresh install.
        SelectedUpdateChannel = defaults.UpdateChannel ?? _update.RunningChannel;
        CheckForUpdatesOnStartup = defaults.CheckForUpdatesOnStartup;
        SelectedUpdateIntervalMinutes = defaults.UpdateCheckIntervalMinutes;
        ShowSystemDatabases = defaults.ShowSystemDatabases;
        SingleBottomPanel = defaults.SingleBottomPanel;
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
        McpScrubSecrets = defaults.McpScrubSecrets;
        McpAllowConnectionCreate = defaults.McpAllowConnectionCreate;
        McpConnectionFolder = defaults.McpConnectionFolder;
        McpAllowedHosts.Clear();
        HostError = null;

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
        settings.FormatKeywordCasing = FormatKeywordCasing;
        settings.FormatIndentSize = FormatIndentSize;
        settings.PluginUpdatePolicy = PluginUpdatePolicy;
        settings.ConfirmBeforeSave = ConfirmBeforeSave;
        settings.QueryTimeoutSeconds = QueryTimeoutSeconds;
        settings.BrowsePageSize = BrowsePageSize;
        settings.PageQueryResults = PageQueryResults;
        settings.QueryPageSize = QueryPageSize;
        settings.RestoreTabsOnStartup = RestoreTabsOnStartup;
        settings.PromptSaveQueryOnClose = PromptSaveQueryOnClose;
        // Switching channels clears the "Later" dismissal so the new channel's build can notify afresh.
        if (settings.UpdateChannel != SelectedUpdateChannel)
        {
            settings.DismissedUpdateVersion = null;
        }
        settings.UpdateChannel = SelectedUpdateChannel;
        settings.CheckForUpdatesOnStartup = CheckForUpdatesOnStartup;
        settings.UpdateCheckIntervalMinutes = SelectedUpdateIntervalMinutes;
        settings.ShowSystemDatabases = ShowSystemDatabases;
        settings.SingleBottomPanel = SingleBottomPanel;
        settings.ConfirmOnExit = ConfirmOnExit;
        settings.CloseToTray = CloseToTray;
        settings.QueryLogEnabled = QueryLogEnabled;
        settings.QueryLogApp = QueryLogApp;
        settings.QueryLogMcp = QueryLogMcp;
        // Master-password enable/change/disable persist themselves immediately; here we only carry the
        // idle-lock interval (a plain preference) and re-apply it.
        settings.MasterPasswordLockMinutes = LockMinuteOptions[Math.Clamp(MasterPasswordLockIndex, 0, LockMinuteOptions.Length - 1)];
        PersistMcpSettings(settings);
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
