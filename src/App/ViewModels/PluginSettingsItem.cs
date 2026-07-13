using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Lionear.SqlExplorer.Sdk;
using Lionear.SqlExplorer.Sdk.Ui;

namespace Lionear.SqlExplorer.App.ViewModels;

/// <summary>
/// One settings-bearing plugin in the Settings ▸ Plugins tree. Detects which route the plugin uses:
/// Route B (its own <see cref="ICustomPluginSettingsUi"/> view) wins when present, otherwise Route A
/// (a host-rendered form from its declared <see cref="PluginSettingField"/>s). Acts as the
/// <see cref="IPluginSettingsContext"/> for a Route B view, backing its reads/writes with the values the
/// host will persist.
/// </summary>
public sealed class PluginSettingsItem : IPluginSettingsContext
{
    // Route B backing store, seeded from the settings file and mutated by the plugin's own view.
    private readonly Dictionary<string, string?> _customValues;

    public PluginSettingsItem(string pluginId, IDbProvider provider, IReadOnlyDictionary<string, string?> stored)
    {
        PluginId = pluginId;
        DisplayName = provider.DisplayName;
        Icon = ResolveIcon(provider.Icon);
        _customValues = new Dictionary<string, string?>(stored);

        if (provider is ICustomPluginSettingsUi customUi)
        {
            HasCustomView = true;
            CustomView = customUi.CreateSettingsView(this);
        }
        else if (provider is IPluginSettings declared)
        {
            foreach (var field in declared.SettingsFields)
            {
                Fields.Add(new PluginSettingFieldInput(field)
                {
                    Value = stored.TryGetValue(field.Key, out var value) ? value : field.Default
                });
            }
        }
    }

    public string PluginId { get; }

    public string DisplayName { get; }

    public IImage? Icon { get; }

    public bool HasImageIcon => Icon is not null;

    /// <summary>Line-icon fallback when the plugin has no brand image (a generic connection glyph).</summary>
    public Geometry FallbackIcon => NodeIcons.Connection;

    /// <summary>Route B: the plugin supplies its own settings view.</summary>
    public bool HasCustomView { get; }

    public Control? CustomView { get; }

    /// <summary>Route A: the host renders a form from declared fields (only when there's no custom view).</summary>
    public ObservableCollection<PluginSettingFieldInput> Fields { get; } = [];

    public bool HasFields => !HasCustomView && Fields.Count > 0;

    public string RouteLabel => HasCustomView ? "Route B · custom view" : "Route A · declarative";

    // IPluginSettingsContext — only exercised by a Route B view.
    public string? GetValue(string key) => _customValues.TryGetValue(key, out var value) ? value : null;

    public void SetValue(string key, string? value) => _customValues[key] = value;

    /// <summary>The values to persist for this plugin: the custom view's backing store (Route B) or the
    /// current field values (Route A).</summary>
    public IReadOnlyDictionary<string, string?> CollectValues() =>
        HasCustomView ? _customValues : Fields.ToDictionary(f => f.Field.Key, f => f.Value);

    // ProviderIcon → renderable bitmap; same rules as the sidebar (raster only, no SVG/emoji).
    private static IImage? ResolveIcon(ProviderIcon? icon)
    {
        if (icon?.ImageData is not { Length: > 0 } bytes
            || icon.ImageMediaType is not { } mediaType
            || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("svg", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return new Bitmap(new MemoryStream(bytes));
        }
        catch
        {
            return null;
        }
    }
}
