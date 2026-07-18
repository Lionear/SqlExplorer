using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using SqlExplorer.Core.Localization;
using SqlExplorer.Core.Plugins;
using SqlExplorer.Core.Store;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SqlExplorer.App.ViewModels;

/// <summary>
/// A Browse-tab card: one catalog plugin (with a selectable version) or a bundle. Holds the display
/// metadata and, for a plugin, recomputes its action label/enabled-state against the selected version and
/// what's installed (Install / Update / Downgrade / Reinstall / Incompatible).
/// </summary>
public sealed partial class StoreListItem : ObservableObject
{
    private readonly HostApiCompat _host;
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
    public string? SourceUrl { get; }
    public string? IconUrl { get; }
    public string SubLine { get; }
    public bool IsBundle => Bundle is not null;

    /// <summary>Plugin icon, downloaded lazily from <see cref="IconUrl"/>; null falls back to a vector glyph.</summary>
    [ObservableProperty]
    private Avalonia.Media.IImage? _icon;

    /// <summary>True for the card shown in the detail pane — drives the selected-card highlight.</summary>
    [ObservableProperty]
    private bool _isSelected;
    public IReadOnlyList<string> Capabilities { get; }
    public IReadOnlyList<string> BundlePluginIds { get; }
    public IReadOnlyList<BundleChild> BundleChildren { get; }
    public bool HasSource => !string.IsNullOrEmpty(SourceName);
    public ObservableCollection<StoreVersion> Versions { get; } = [];

    /// <summary>"SHA-256 8f3a… · 4.8 MB · source: …" for the selected version (plugin cards only).</summary>
    [ObservableProperty]
    private string? _provenanceLine;

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
    public StoreListItem(StoreEntry entry, string? sourceName, string? sourceUrl, string? installedVersion, HostApiCompat host, ILocalizer loc)
    {
        Entry = entry;
        _host = host;
        _loc = loc;
        Id = entry.Id;
        Name = entry.Name;
        Author = entry.Author;
        Description = entry.Description;
        Homepage = entry.Homepage;
        SourceName = sourceName;
        SourceUrl = sourceUrl;
        IconUrl = entry.IconUrl;
        Capabilities = entry.Capabilities;
        BundlePluginIds = [];
        BundleChildren = [];
        foreach (var v in entry.Versions.OrderByDescending(v => v.Version, Comparer<string?>.Create(SemVer.Compare)))
        {
            Versions.Add(v);
        }

        _installedVersion = installedVersion;
        _selectedVersion = entry.HighestCompatibleVersion(host) ?? Versions.FirstOrDefault();
        var top = Versions.FirstOrDefault()?.Version;
        SubLine = string.IsNullOrEmpty(Author) ? $"v{top}" : $"v{top} · {Author}";
        Recompute();
    }

    // Bundle card
    public StoreListItem(StoreBundle bundle, string? sourceName, string? sourceUrl, IReadOnlyList<BundleChild> children, ILocalizer loc)
    {
        Bundle = bundle;
        _loc = loc;
        Id = bundle.Id;
        Name = bundle.Name;
        Description = bundle.Description;
        SourceName = sourceName;
        SourceUrl = sourceUrl;
        Capabilities = [];
        BundlePluginIds = bundle.PluginIds;
        BundleChildren = children;
        SubLine = loc.Get("StoreBundleSub", bundle.PluginIds.Count);
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

        ProvenanceLine = SelectedVersion is { } v
            ? $"SHA-256 {Shorten(v.Sha256)} · {FormatSize(v.Size)} · {SourceUrl}"
            : null;

        if (SelectedVersion is not { } version)
        {
            CanInstall = false;
            ActionLabel = _loc["StoreInstall"];
            return;
        }

        if (!version.IsCompatible(_host))
        {
            CanInstall = false;
            ActionLabel = _loc["StoreIncompatible"];
            // Either the build needs a newer host (min above our current) or it is too old (min below the
            // floor the host still supports). No upper bound exists any more — the host owns compatibility.
            IncompatibleReason = version.MinHostApiVersion > _host.Current
                ? _loc.Get("StoreIncompatMin", version.MinHostApiVersion, _host.Current)
                : _loc.Get("StoreIncompatOld", version.MinHostApiVersion, _host.MinSupported);
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

    private static string Shorten(string sha) => sha.Length > 8 ? sha[..8] + "…" : sha;

    private static string FormatSize(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / (1024.0 * 1024.0):0.0} MB"
        : $"{bytes / 1024.0:0} KB";
}

/// <summary>One child plugin of a bundle, for the detail pane's "Contains" list.</summary>
public sealed record BundleChild(string Name, string? Version);

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
    [NotifyPropertyChangedFor(nameof(HasChangelog))]
    private bool _updateAvailable;

    [ObservableProperty]
    private string? _updateTargetVersion;

    /// <summary>True when an update is available and its target version carries release notes (SE-138
    /// phase 2) — gates the row's "changelog" link.</summary>
    public bool HasChangelog => UpdateAvailable && !string.IsNullOrWhiteSpace(UpdateTarget?.Notes);

    /// <summary>Localized "Update → x.y.z" label for the row's update button.</summary>
    [ObservableProperty]
    private string? _updateLabel;

    [ObservableProperty]
    private bool _hasRollback;

    /// <summary>True when this plugin is version-pinned via <see cref="IPluginPinStore"/> — updates
    /// are skipped until the pin is cleared, and a badge shows in the Installed row (SE-120).</summary>
    [ObservableProperty]
    private bool _isPinned;

    /// <summary>Localized "Back to x.y.z" (falls back to a generic "Roll back") for the rollback button.</summary>
    [ObservableProperty]
    private string? _rollbackLabel;

    /// <summary>Plugin icon, downloaded lazily from the matched catalog entry; null falls back to a glyph.</summary>
    [ObservableProperty]
    private Avalonia.Media.IImage? _icon;

    public StoreEntry? CatalogEntry { get; set; }
    public StoreVersion? UpdateTarget { get; set; }
    public string? IconUrl { get; set; }

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
    public string? IconUrl { get; }

    [ObservableProperty]
    private bool _ok;

    [ObservableProperty]
    private string? _error;

    /// <summary>The store's icon, downloaded lazily from <see cref="IconUrl"/> (Discovery sources only).</summary>
    [ObservableProperty]
    private Avalonia.Media.IImage? _icon;

    public SourceRow(string url, string? name, bool isDiscovery, bool ok, string? error, string? iconUrl)
    {
        Url = url;
        Name = name;
        IsDiscovery = isDiscovery;
        _ok = ok;
        _error = error;
        IconUrl = iconUrl;
    }
}
