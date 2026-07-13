using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Lionear.SqlExplorer.App.Theming;
using Lionear.SqlExplorer.Core.Localization;
using Lionear.SqlExplorer.Core.Providers;
using Lionear.SqlExplorer.Core.Settings;
using Lionear.SqlExplorer.Sdk;
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
        ILocalizer localizer)
    {
        _store = store;
        _pluginStore = pluginStore;
        _providers = providers;
        Loc = localizer;

        Categories =
        [
            new SettingsCategory("General", localizer["SettingsGeneralCat"], NodeIcons.SettingsGeneral),
            new SettingsCategory("Appearance", localizer["SettingsAppearance"], NodeIcons.SettingsAppearance),
            new SettingsCategory("Editor", localizer["SettingsEditor"], NodeIcons.SettingsEditor),
            new SettingsCategory("Query", localizer["SettingsQuery"], NodeIcons.SettingsQuery),
            new SettingsCategory("Plugins", localizer["SettingsPlugins"], NodeIcons.SettingsPlugins),
        ];
        _selectedCategory = Categories[0];

        LoadFromStore();
        BuildPluginCatalog();
    }

    public ILocalizer Loc { get; }

    public ObservableCollection<SettingsCategory> Categories { get; }

    public IReadOnlyList<AppTheme> Themes { get; } = [AppTheme.System, AppTheme.Light, AppTheme.Dark];

    /// <summary>Plugins that declare settings (Route A or B); empty when none do.</summary>
    public ObservableCollection<PluginSettingsItem> Plugins { get; } = [];

    public bool HasPlugins => Plugins.Count > 0;

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

    // A plugin gets a tree entry only if it declares fields (Route A) or a custom view (Route B).
    // Today only providers load, so the list is providers-only; grouping by type comes when tools exist.
    private void BuildPluginCatalog()
    {
        foreach (var registration in _providers.All)
        {
            var provider = registration.Provider;
            var hasCustom = provider is ICustomPluginSettingsUi;
            var hasFields = provider is IPluginSettings { SettingsFields.Count: > 0 };
            if (!hasCustom && !hasFields)
            {
                continue;
            }

            Plugins.Add(new PluginSettingsItem(registration.Id, provider, _pluginStore.Get(registration.Id)));
        }

        SelectedPlugin = Plugins.FirstOrDefault();
        OnPropertyChanged(nameof(HasPlugins));
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
    }

    [RelayCommand]
    private void Apply() => ApplyInternal();

    [RelayCommand]
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

        ThemeApplier.Apply(Theme);
        if (Language is { Length: > 0 } language)
        {
            Loc.SetCulture(CultureInfo.GetCultureInfo(language));
        }
    }
}
