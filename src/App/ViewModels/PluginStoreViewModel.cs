using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lionear.SqlExplorer.Core.Localization;
using Lionear.SqlExplorer.Core.Plugins;
using Lionear.SqlExplorer.Core.Store;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Lionear.SqlExplorer.App.ViewModels;

/// <summary>
/// Backs the Plugin Store window: Browse (catalog), Installed (enable/disable/uninstall/update/rollback)
/// and Sources (Discovery read-only + manual index URLs). Every mutating action stages a change that the
/// installer/state store apply on the next restart, so the window surfaces a "restart needed" banner
/// rather than reloading live. Install shows a capability-consent overlay first when the plugin declares
/// any. Fetching is fault-tolerant — an offline catalog shows what it can with the source errors listed.
/// </summary>
public sealed partial class PluginStoreViewModel : ViewModelBase
{
    private readonly IStoreCatalog _catalog;
    private readonly IPluginInstaller _installer;
    private readonly PluginCatalogService _installed;
    private readonly PluginUpdateService _updates;
    private readonly IStoreSourcesStore _sources;
    private readonly Progress<InstallProgress> _progress;

    private readonly List<StoreListItem> _allBrowse = [];
    private StoreCatalog? _lastCatalog;
    private TaskCompletionSource<bool>? _consent;

    [ObservableProperty]
    private string _selectedTab = "Browse";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _busyText;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _progressIndeterminate = true;

    [ObservableProperty]
    private bool _restartRequired;

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private string? _searchText;

    [ObservableProperty]
    private StoreListItem? _selectedBrowseItem;

    [ObservableProperty]
    private bool _isConsentVisible;

    [ObservableProperty]
    private string? _consentPluginName;

    [ObservableProperty]
    private string? _newSourceUrl;

    public PluginStoreViewModel(
        IStoreCatalog catalog,
        IPluginInstaller installer,
        PluginCatalogService installed,
        PluginUpdateService updates,
        IStoreSourcesStore sources,
        ILocalizer localizer)
    {
        _catalog = catalog;
        _installer = installer;
        _installed = installed;
        _updates = updates;
        _sources = sources;
        Loc = localizer;
        _progress = new Progress<InstallProgress>(OnProgress);
    }

    public ILocalizer Loc { get; }

    public ObservableCollection<StoreListItem> BrowseItems { get; } = [];
    public ObservableCollection<InstalledListItem> BundledPlugins { get; } = [];
    public ObservableCollection<InstalledListItem> UserPlugins { get; } = [];
    public ObservableCollection<SourceRow> DiscoverySources { get; } = [];
    public ObservableCollection<SourceRow> ManualSources { get; } = [];
    public ObservableCollection<string> ConsentCapabilities { get; } = [];

    public bool HasBrowseItems => BrowseItems.Count > 0;
    public bool HasUserPlugins => UserPlugins.Count > 0;
    public int UpdateCount => UserPlugins.Count(p => p.UpdateAvailable);
    public bool HasUpdates => UpdateCount > 0;

    /// <summary>Set by the view: pick a local .zip to install; returns null if cancelled.</summary>
    public Func<Task<string?>>? InstallFromFileRequested { get; set; }

    /// <summary>Set by the view to close the window.</summary>
    public Action? CloseRequested { get; set; }

    [RelayCommand]
    private void SelectTab(string tab) => SelectedTab = tab;

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    /// <summary>Full (re)load: fetch the catalog, then rebuild all three tabs. Called on open and refresh.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsLoading = true;
        ErrorText = null;
        try
        {
            _lastCatalog = await _catalog.FetchAsync(CancellationToken.None);
            BuildInstalled(_lastCatalog);
            BuildBrowse(_lastCatalog);
            BuildSources(_lastCatalog);
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void BuildInstalled(StoreCatalog catalog)
    {
        BundledPlugins.Clear();
        UserPlugins.Clear();

        var updates = _updates.DetectUpdates(_installed.Installed, catalog)
            .ToDictionary(u => u.Id, u => u, StringComparer.Ordinal);
        var entriesById = catalog.Entries.ToDictionary(e => e.Entry.Id, e => e.Entry, StringComparer.Ordinal);

        foreach (var plugin in _installed.Installed.OrderBy(p => p.Name ?? p.Id, StringComparer.OrdinalIgnoreCase))
        {
            var hasRollback = plugin.CanManage
                && Directory.Exists(PluginPaths.UserPluginDir(plugin.Id) + ".prev");
            var row = new InstalledListItem(plugin, hasRollback);

            if (updates.TryGetValue(plugin.Id, out var update))
            {
                row.UpdateAvailable = true;
                row.UpdateTargetVersion = update.Target.Version;
                row.UpdateTarget = update.Target;
                row.CatalogEntry = update.Entry;
            }
            else if (entriesById.TryGetValue(plugin.Id, out var entry))
            {
                row.CatalogEntry = entry;
            }

            (plugin.Origin == PluginOrigin.Bundled ? BundledPlugins : UserPlugins).Add(row);
        }

        OnPropertyChanged(nameof(HasUserPlugins));
        OnPropertyChanged(nameof(HasUpdates));
        OnPropertyChanged(nameof(UpdateCount));
    }

    private void BuildBrowse(StoreCatalog catalog)
    {
        _allBrowse.Clear();

        var installedById = _installed.Installed.ToDictionary(p => p.Id, p => p.Version, StringComparer.Ordinal);

        foreach (var bundle in catalog.Bundles)
        {
            _allBrowse.Add(new StoreListItem(bundle.Bundle, bundle.SourceName, Loc));
        }

        foreach (var entry in catalog.Entries)
        {
            var installedVersion = installedById.GetValueOrDefault(entry.Entry.Id);
            _allBrowse.Add(new StoreListItem(entry.Entry, entry.SourceName, installedVersion,
                HostApiVersions.For(entry.Entry.Type), Loc));
        }

        ApplyBrowseFilter();
    }

    partial void OnSearchTextChanged(string? value) => ApplyBrowseFilter();

    private void ApplyBrowseFilter()
    {
        BrowseItems.Clear();
        var query = SearchText?.Trim();
        IEnumerable<StoreListItem> items = _allBrowse;
        if (!string.IsNullOrEmpty(query))
        {
            items = items.Where(i =>
                i.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (i.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        foreach (var item in items)
        {
            BrowseItems.Add(item);
        }

        SelectedBrowseItem ??= BrowseItems.FirstOrDefault();
        OnPropertyChanged(nameof(HasBrowseItems));
    }

    private void BuildSources(StoreCatalog catalog)
    {
        DiscoverySources.Clear();
        ManualSources.Clear();

        var statusByUrl = catalog.Sources.ToDictionary(s => s.Url, s => s, StringComparer.OrdinalIgnoreCase);

        foreach (var source in catalog.Sources.Where(s => s.IsDiscovery))
        {
            DiscoverySources.Add(new SourceRow(source.Url, source.Name, isDiscovery: true, source.Ok, source.Error));
        }

        foreach (var url in _sources.GetManualSources())
        {
            var ok = statusByUrl.TryGetValue(url, out var status) && status.Ok;
            var error = status?.Error;
            ManualSources.Add(new SourceRow(url, name: null, isDiscovery: false, ok, error));
        }
    }

    // --- Install / update / rollback / uninstall / enable-disable ---------------------------------

    [RelayCommand]
    private async Task InstallAsync(StoreListItem item)
    {
        if (item.IsBundle)
        {
            await InstallBundleAsync(item);
            return;
        }

        if (item.Entry is not { } entry || item.SelectedVersion is not { } version || !item.CanInstall)
        {
            return;
        }

        if (!await ConfirmConsentAsync(entry.Name, entry.Capabilities))
        {
            return;
        }

        var outcome = await RunInstallAsync(() => _installer.InstallAsync(entry, version, _progress, CancellationToken.None));
        if (outcome.Success)
        {
            item.MarkStaged(version.Version);
        }
    }

    private async Task InstallBundleAsync(StoreListItem bundleItem)
    {
        var children = bundleItem.BundlePluginIds
            .Select(id => _lastCatalog?.Entries.FirstOrDefault(e => e.Entry.Id == id)?.Entry)
            .OfType<StoreEntry>()
            .Select(e => (Entry: e, Version: e.HighestCompatibleVersion(HostApiVersions.For(e.Type))))
            .Where(x => x.Version is not null)
            .ToList();

        if (children.Count == 0)
        {
            return;
        }

        var caps = children.SelectMany(c => c.Entry.Capabilities).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!await ConfirmConsentAsync(bundleItem.Name, caps))
        {
            return;
        }

        IsBusy = true;
        ErrorText = null;
        try
        {
            foreach (var (entry, version) in children)
            {
                BusyText = entry.Name;
                var outcome = await _installer.InstallAsync(entry, version!, _progress, CancellationToken.None);
                if (outcome.Success)
                {
                    RestartRequired = true;
                }
                else
                {
                    ErrorText = outcome.Error;
                }
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task InstallFromFileAsync()
    {
        if (InstallFromFileRequested is null || await InstallFromFileRequested() is not { } path)
        {
            return;
        }

        await RunInstallAsync(() => _installer.InstallFromFileAsync(path, _progress, CancellationToken.None));
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task UpdateAsync(InstalledListItem item)
    {
        if (item.CatalogEntry is not { } entry || item.UpdateTarget is not { } version)
        {
            return;
        }

        var outcome = await RunInstallAsync(() => _installer.InstallAsync(entry, version, _progress, CancellationToken.None));
        if (outcome.Success)
        {
            item.UpdateAvailable = false;
            item.Pending = PluginPendingAction.Install;
            OnPropertyChanged(nameof(HasUpdates));
            OnPropertyChanged(nameof(UpdateCount));
        }
    }

    [RelayCommand]
    private async Task UpdateAllAsync()
    {
        if (_lastCatalog is null)
        {
            return;
        }

        var updates = _updates.DetectUpdates(_installed.Installed, _lastCatalog);
        if (updates.Count == 0)
        {
            return;
        }

        IsBusy = true;
        ErrorText = null;
        try
        {
            var outcomes = await _updates.UpdateAllAsync(updates, _progress, CancellationToken.None);
            if (outcomes.Any(o => o.Success))
            {
                RestartRequired = true;
            }

            var failed = outcomes.Where(o => !o.Success).ToList();
            if (failed.Count > 0)
            {
                ErrorText = string.Join("; ", failed.Select(f => $"{f.PluginId}: {f.Error}"));
            }
        }
        finally
        {
            IsBusy = false;
        }

        BuildInstalled(_lastCatalog);
    }

    [RelayCommand]
    private void Rollback(InstalledListItem item)
    {
        var outcome = _installer.RequestRollback(item.Id);
        if (outcome.Success)
        {
            RestartRequired = true;
            item.HasRollback = false;
            item.Pending = PluginPendingAction.Install;
        }
        else
        {
            ErrorText = outcome.Error;
        }
    }

    [RelayCommand]
    private void ToggleEnabled(InstalledListItem item)
    {
        if (!item.CanManage)
        {
            return;
        }

        if (item.Enabled)
        {
            _installed.RequestDisable(item.Id);
            item.Enabled = false;
        }
        else
        {
            _installed.RequestEnable(item.Id);
            item.Enabled = true;
        }

        RestartRequired = true;
    }

    [RelayCommand]
    private void Uninstall(InstalledListItem item)
    {
        if (!item.CanManage)
        {
            return;
        }

        _installed.RequestUninstall(item.Id);
        item.Pending = PluginPendingAction.Remove;
        RestartRequired = true;
    }

    // --- Sources ------------------------------------------------------------------------------------

    [RelayCommand]
    private async Task AddSourceAsync()
    {
        if (string.IsNullOrWhiteSpace(NewSourceUrl))
        {
            return;
        }

        _sources.AddManualSource(NewSourceUrl);
        NewSourceUrl = null;
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RemoveSourceAsync(SourceRow row)
    {
        _sources.RemoveManualSource(row.Url);
        await RefreshAsync();
    }

    // --- Capability consent overlay ----------------------------------------------------------------

    private Task<bool> ConfirmConsentAsync(string pluginName, IReadOnlyList<string> capabilities)
    {
        if (capabilities.Count == 0)
        {
            return Task.FromResult(true);
        }

        ConsentPluginName = pluginName;
        ConsentCapabilities.Clear();
        foreach (var capability in capabilities)
        {
            ConsentCapabilities.Add(capability);
        }

        IsConsentVisible = true;
        _consent = new TaskCompletionSource<bool>();
        return _consent.Task;
    }

    [RelayCommand]
    private void ConfirmConsent()
    {
        IsConsentVisible = false;
        _consent?.TrySetResult(true);
    }

    [RelayCommand]
    private void CancelConsent()
    {
        IsConsentVisible = false;
        _consent?.TrySetResult(false);
    }

    // --- shared install runner ---------------------------------------------------------------------

    private async Task<InstallOutcome> RunInstallAsync(Func<Task<InstallOutcome>> install)
    {
        IsBusy = true;
        ErrorText = null;
        ProgressIndeterminate = true;
        try
        {
            var outcome = await install();
            if (outcome.Success)
            {
                RestartRequired = true;
            }
            else
            {
                ErrorText = outcome.Error;
            }

            return outcome;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnProgress(InstallProgress progress)
    {
        BusyText = progress.Phase switch
        {
            InstallPhase.Downloading => Loc["StoreDownloading"],
            InstallPhase.Verifying => Loc["StoreVerifying"],
            InstallPhase.Extracting => Loc["StoreExtracting"],
            InstallPhase.Staging => Loc["StoreStaging"],
            _ => Loc["StoreInstalling"]
        };

        if (progress is { Phase: InstallPhase.Downloading, TotalBytes: > 0 })
        {
            ProgressIndeterminate = false;
            ProgressValue = (double)progress.BytesDownloaded / progress.TotalBytes.Value;
        }
        else
        {
            ProgressIndeterminate = true;
        }
    }
}
