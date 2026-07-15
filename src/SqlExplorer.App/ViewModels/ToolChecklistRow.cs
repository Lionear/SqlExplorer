using CommunityToolkit.Mvvm.ComponentModel;
using SqlExplorer.Sdk.Tools;

namespace SqlExplorer.App.ViewModels;

/// <summary>One row in a tool's live per-item checklist (§ object-selection backup/restore): keyed by the
/// tool's <see cref="ToolProgress.ItemKey"/>, its label and status flip as the tool reports progress.</summary>
public partial class ToolChecklistRow : ObservableObject
{
    public string Key { get; }

    public ToolChecklistRow(string key, string label)
    {
        Key = key;
        _label = label;
    }

    [ObservableProperty]
    private string _label;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusGlyph))]
    private ToolItemStatus _status = ToolItemStatus.Running;

    /// <summary>A vector-friendly text glyph for the status (no emoji — Linux/Avalonia can't render them).</summary>
    public string StatusGlyph => Status switch
    {
        ToolItemStatus.Done => "✓",
        ToolItemStatus.Error => "✕",
        ToolItemStatus.Skipped => "–",
        _ => "…"
    };
}
