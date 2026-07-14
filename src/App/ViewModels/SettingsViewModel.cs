using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia.Media;
using Lionear.SqlExplorer.App.Theming;
using Lionear.SqlExplorer.Core.Localization;
using Lionear.SqlExplorer.Core.Providers;
using Lionear.SqlExplorer.Core.Settings;
using Lionear.SqlExplorer.Core.Shortcuts;
using Lionear.SqlExplorer.Core.Tools;
using Lionear.SqlExplorer.Sdk;
using Lionear.SqlExplorer.Sdk.Settings;
using Lionear.SqlExplorer.Sdk.Tools;
using Lionear.SqlExplorer.Sdk.Ui;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Lionear.SqlExplorer.App.ViewModels;

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
    private readonly List<ShortcutItem> _allShortcuts = [];

    [ObservableProperty]
    private SettingsCategory? _selectedCategory;

    [ObservableProperty]
    private string? _language;

    [ObservableProperty]
    private AppTheme _theme;

    [ObservableProperty]
    private double? _editorFontSize;

    [ObservableProperty]
    private bool _editorWordWrap;

    [ObservableProperty]
    private bool _confirmBeforeSave;

    [ObservableProperty]
    private PluginSettingsItem? _selectedPlugin;

    public SettingsViewModel(
        IAppSettingsStore store,
        IPluginSettingsStore pluginStore,
        IDbProviderRegistry providers,
        IToolRegistry tools,
        KeymapService keymap,
        ILocalizer localizer)
    {
        _store = store;
        _pluginStore = pluginStore;
        _providers = providers;
        _tools = tools;
        _keymap = keymap;
        Loc = localizer;

        Categories =
        [
            new SettingsCategory("General", localizer["SettingsGeneralCat"], NodeIcons.SettingsGeneral),
            new SettingsCategory("Appearance", localizer["SettingsAppearance"], NodeIcons.SettingsAppearance),
            new SettingsCategory("Editor", localizer["SettingsEditor"], NodeIcons.SettingsEditor),
            new SettingsCategory("Query", localizer["SettingsQuery"], NodeIcons.SettingsQuery),
            new SettingsCategory("Keyboard", localizer["SettingsKeyboard"], NodeIcons.SettingsKeyboard),
            new SettingsCategory("Plugins", localizer["SettingsPlugins"], NodeIcons.SettingsPlugins),
        ];
        _selectedCategory = Categories[0];

        LoadFromStore();
        BuildPluginCatalog();
        BuildShortcutCatalog();
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
    private void SetLanguage(string code) => Language = code;

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
        _store.Save(settings);

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
