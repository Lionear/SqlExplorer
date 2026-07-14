using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Lionear.SqlExplorer.Core.Localization;
using Lionear.SqlExplorer.Core.Plugins;
using Lionear.SqlExplorer.Core.Store;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Lionear.SqlExplorer.App.ViewModels;

/// <summary>
/// A Browse-tab card: one catalog plugin (with a selectable version) or a bundle. Holds the display
/// metadata and, for a plugin, recomputes its action label/enabled-state against the selected version and
/// what's installed (Install / Update / Downgrade / Reinstall / Incompatible).
/// </summary>
public sealed partial class StoreListItem : ObservableObject
{
    private readonly int _hostApiVersion;
    private readonly ILocalizer _loc;

    // Non-null for a plugin card; null for a bundle card.
    public StoreEntry? Entry { get; }

    // Non-null for a bundle card.
    public StoreBundle? Bundle { get; }

    public string Id { get; }
    public string Name { get; }
    public string? Author { get; }
    public string? Description { get; }
    public string? Homepage { get; }
    public string? SourceName { get; }
    public bool IsBundle => Bundle is not null;
    public IReadOnlyList<string> Capabilities { get; }
    public IReadOnlyList<string> BundlePluginIds { get; }
    public ObservableCollection<StoreVersion> Versions { get; } = [];

    [ObservableProperty]
    private StoreVersion? _selectedVersion;

    [ObservableProperty]
    private string? _installedVersion;

    [ObservableProperty]
    private string _actionLabel = string.Empty;

    [ObservableProperty]
    private bool _canInstall;

    [ObservableProperty]
    private string? _incompatibleReason;

    [ObservableProperty]
    private bool _isStaged;

    // Plugin card
    public StoreListItem(StoreEntry entry, string? sourceName, string? installedVersion, int hostApiVersion, ILocalizer loc)
    {
        Entry = entry;
        _hostApiVersion = hostApiVersion;
        _loc = loc;
        Id = entry.Id;
        Name = entry.Name;
        Author = entry.Author;
        Description = entry.Description;
        Homepage = entry.Homepage;
        SourceName = sourceName;
        Capabilities = entry.Capabilities;
        BundlePluginIds = [];
        foreach (var v in entry.Versions.OrderByDescending(v => v.Version, Comparer<string?>.Create(SemVer.Compare)))
        {
            Versions.Add(v);
        }

        _installedVersion = installedVersion;
        _selectedVersion = entry.HighestCompatibleVersion(hostApiVersion) ?? Versions.FirstOrDefault();
        Recompute();
    }

    // Bundle card
    public StoreListItem(StoreBundle bundle, string? sourceName, ILocalizer loc)
    {
        Bundle = bundle;
        _loc = loc;
        Id = bundle.Id;
        Name = bundle.Name;
        Description = bundle.Description;
        SourceName = sourceName;
        Capabilities = [];
        BundlePluginIds = bundle.PluginIds;
        _actionLabel = loc["StoreInstallAll"];
        _canInstall = true;
    }

    partial void OnSelectedVersionChanged(StoreVersion? value) => Recompute();

    public void MarkStaged(string version)
    {
        InstalledVersion = version;
        IsStaged = true;
        Recompute();
    }

    private void Recompute()
    {
        if (IsBundle)
        {
            return;
        }

        if (SelectedVersion is not { } version)
        {
            CanInstall = false;
            ActionLabel = _loc["StoreInstall"];
            return;
        }

        if (!version.IsCompatible(_hostApiVersion))
        {
            CanInstall = false;
            ActionLabel = _loc["StoreIncompatible"];
            IncompatibleReason = version.MaxHostApiVersion is { } max
                ? _loc.Get("StoreIncompatRange", version.MinHostApiVersion, max, _hostApiVersion)
                : _loc.Get("StoreIncompatMin", version.MinHostApiVersion, _hostApiVersion);
            return;
        }

        IncompatibleReason = null;
        CanInstall = true;
        ActionLabel = InstalledVersion is null
            ? _loc["StoreInstall"]
            : SemVer.Compare(version.Version, InstalledVersion) switch
            {
                > 0 => _loc["StoreUpdate"],
                < 0 => _loc["StoreDowngrade"],
                _ => _loc["StoreReinstall"]
            };
    }
}

/// <summary>An Installed-tab row: an <see cref="InstalledPlugin"/> plus store-derived update/rollback state.</summary>
public sealed partial class InstalledListItem : ObservableObject
{
    public string Id { get; }
    public string? Name { get; }
    public string? Type { get; }
    public PluginOrigin Origin { get; }
    public bool CanManage { get; }
    public bool Loaded { get; }
    public string? LoadError { get; }

    [ObservableProperty]
    private string? _version;

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private PluginPendingAction _pending;

    [ObservableProperty]
    private bool _updateAvailable;

    [ObservableProperty]
    private string? _updateTargetVersion;

    [ObservableProperty]
    private bool _hasRollback;

    public StoreEntry? CatalogEntry { get; set; }
    public StoreVersion? UpdateTarget { get; set; }

    public bool HasLoadError => !Loaded && LoadError is not null;

    public InstalledListItem(InstalledPlugin plugin, bool hasRollback)
    {
        Id = plugin.Id;
        Name = plugin.Name;
        Type = plugin.Type;
        Origin = plugin.Origin;
        CanManage = plugin.CanManage;
        Loaded = plugin.Loaded;
        LoadError = plugin.LoadError;
        _version = plugin.Version;
        _enabled = plugin.Enabled;
        _pending = plugin.Pending;
        _hasRollback = hasRollback;
    }
}

/// <summary>A row in the Sources tab: a Discovery-provided store (read-only) or a manual index URL.</summary>
public sealed partial class SourceRow : ObservableObject
{
    public string Url { get; }
    public string? Name { get; }
    public bool IsDiscovery { get; }

    [ObservableProperty]
    private bool _ok;

    [ObservableProperty]
    private string? _error;

    public SourceRow(string url, string? name, bool isDiscovery, bool ok, string? error)
    {
        Url = url;
        Name = name;
        IsDiscovery = isDiscovery;
        _ok = ok;
        _error = error;
    }
}
