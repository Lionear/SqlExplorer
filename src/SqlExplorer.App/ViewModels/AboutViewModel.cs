using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using SqlExplorer.Core.Localization;
using SqlExplorer.Core.Plugins;
using SqlExplorer.Core.Store;
using SqlExplorer.Sdk;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SqlExplorer.App.ViewModels;

/// <summary>One plugin row in the About dialog's diagnostics list: what's loaded, from where, and whether
/// it's pinned or disabled. Read-only — installing/enabling/pinning stays in the Store and Settings.</summary>
/// <param name="Source">Raw and English (<see cref="SourceBundled"/>/<see cref="SourceStore"/>). This is what
/// <see cref="AboutViewModel.BuildDiagnostics"/> emits, so a pasted report reads the same whatever language the
/// reporter runs in; <see cref="SourceLabel"/> is the localized counterpart the UI binds to.</param>
public sealed record AboutPluginRow(string Id, string Version, string Source, bool IsEnabled, bool IsPinned, ILocalizer Loc)
{
    public const string SourceBundled = "bundled";
    public const string SourceStore = "store";

    private const string FlagDisabled = "disabled";
    private const string FlagPinned = "pinned";

    public bool IsDisabled => !IsEnabled;

    /// <summary>Only one flag is ever shown; disabled outranks pinned (it explains why it isn't running).
    /// Raw and English, same contract as <see cref="Source"/> — the UI binds <see cref="FlagLabel"/>.</summary>
    public string? Flag => IsDisabled ? FlagDisabled : IsPinned ? FlagPinned : null;

    public bool HasFlag => Flag is not null;

    public string SourceLabel => Loc[Source == SourceBundled ? "AboutSourceBundled" : "AboutSourceStore"];

    public string? FlagLabel => Flag switch
    {
        FlagDisabled => Loc["AboutFlagDisabled"],
        FlagPinned => Loc["AboutFlagPinned"],
        _ => null
    };
}

/// <summary>
/// Backs the About dialog (SE-126). Answers the two questions About is actually opened for: "which
/// version am I running?" (host + plugins, whose versions come from three sources since SE-120:
/// bundled / store / pinned) and "what do I paste into a YouTrack issue?" — see
/// <see cref="BuildDiagnostics"/>, which renders the whole picture as markdown.
/// </summary>
public sealed partial class AboutViewModel : ViewModelBase
{
    private readonly PluginCatalogService _plugins;
    private readonly IPluginPinStore _pins;

    public AboutViewModel(PluginCatalogService plugins, IPluginPinStore pins, ILocalizer localizer)
    {
        _plugins = plugins;
        _pins = pins;
        Loc = localizer;

        AppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "—";
        BuildPluginRows();
    }

    public ILocalizer Loc { get; }

    // --- Hero ---------------------------------------------------------------------------------------

    public string AppVersion { get; }

    public string VersionLine => Loc.Get("AboutVersion", AppVersion);

    /// <summary>"Host API 24 · min 23" — decides which Store plugins this build is even offered.</summary>
    public string HostApiChip => Loc.Get("AboutHostApiChip", ProviderHostApi.Version, ProviderHostApi.MinimumSupported);

    // --- System card --------------------------------------------------------------------------------

    public string OsInfo => $"{RuntimeInformation.OSDescription.Trim()} ({RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()})";

    public string RuntimeInfo => RuntimeInformation.FrameworkDescription;

    /// <summary>Avalonia's assembly version — read off a type we already reference rather than hardcoding
    /// the package version, so it can't drift from what actually loaded.</summary>
    public string AvaloniaVersion =>
        typeof(Avalonia.Application).Assembly.GetName().Version?.ToString(3) ?? "—";

    public string LanguageInfo => CultureInfo.CurrentUICulture.Name;

    /// <summary>The config directory — the one path worth showing (and the only one in the diagnostics).</summary>
    public string ConfigPath => Path.GetDirectoryName(PluginPaths.UserRoot) ?? PluginPaths.UserRoot;

    // --- Plugins card -------------------------------------------------------------------------------

    public ObservableCollection<AboutPluginRow> Plugins { get; } = [];

    public string PluginCountLine => Loc.Get("AboutPluginCount", LoadedCount, DisabledCount);

    private int LoadedCount => Plugins.Count(p => p.IsEnabled);
    private int DisabledCount => Plugins.Count(p => p.IsDisabled);

    private void BuildPluginRows()
    {
        var pins = _pins.GetAll();
        foreach (var plugin in _plugins.Installed.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
        {
            Plugins.Add(new AboutPluginRow(
                plugin.Id,
                plugin.Version ?? "—",
                plugin.Origin == PluginOrigin.Bundled ? AboutPluginRow.SourceBundled : AboutPluginRow.SourceStore,
                plugin.Enabled,
                pins.ContainsKey(plugin.Id),
                Loc));
        }
    }

    // --- Copy diagnostics ---------------------------------------------------------------------------

    /// <summary>Set by the view; copies text to the clipboard (the view owns the TopLevel).</summary>
    public Func<string, Task>? ClipboardRequested { get; set; }

    /// <summary>Flips to the "Copied" label for a moment after a successful copy — an in-place
    /// confirmation, no toast and no dialog.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CopyLabel))]
    private bool _justCopied;

    public string CopyLabel => JustCopied ? Loc["AboutCopied"] : Loc["AboutCopyDiagnostics"];

    [RelayCommand]
    private async Task CopyDiagnostics()
    {
        if (ClipboardRequested is null)
        {
            return;
        }

        await ClipboardRequested(BuildDiagnostics());

        JustCopied = true;
        await Task.Delay(1600);
        JustCopied = false;
    }

    /// <summary>
    /// The whole picture as markdown, so it pastes straight into a YouTrack issue and renders as a table.
    /// Deliberately carries no secrets: no connection strings, no hostnames, no paths beyond the config
    /// directory (which is itself omitted here — it's a local path and adds nothing to a bug report).
    /// </summary>
    public string BuildDiagnostics()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**SQL Explorer** {AppVersion}");
        sb.AppendLine();
        sb.AppendLine("| | |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Host API | {ProviderHostApi.Version} (min {ProviderHostApi.MinimumSupported}) |");
        sb.AppendLine($"| OS | {OsInfo} |");
        sb.AppendLine($"| Runtime | {RuntimeInfo} |");
        sb.AppendLine($"| Avalonia | {AvaloniaVersion} |");
        sb.AppendLine($"| Language | {LanguageInfo} |");
        sb.AppendLine();
        sb.AppendLine($"**Plugins** ({LoadedCount} loaded, {DisabledCount} disabled)");
        sb.AppendLine();
        sb.AppendLine("| Plugin | Version | Source | |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var plugin in Plugins)
        {
            sb.AppendLine($"| {plugin.Id} | {plugin.Version} | {plugin.Source} | {plugin.Flag} |");
        }

        return sb.ToString().TrimEnd();
    }
}
