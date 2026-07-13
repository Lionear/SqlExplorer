using CommunityToolkit.Mvvm.ComponentModel;

namespace Lionear.SqlExplorer.App.ViewModels;

/// <summary>Editable state for one <see cref="PluginSettingField"/> in the plugin-settings pane (Route A).
/// Mirrors <see cref="ConnectionFieldInput"/> — same binding shape, so it reuses the same field template.</summary>
public partial class PluginSettingFieldInput : ObservableObject
{
    public PluginSettingFieldInput(PluginSettingField field)
    {
        Field = field;
        _value = field.Default;
    }

    public PluginSettingField Field { get; }

    [ObservableProperty]
    private string? _value;

    public string Label => Field.Label;
    public string? Watermark => Field.Placeholder;
    public bool IsFile => Field.Type == PluginSettingFieldType.File;
    public bool IsFolder => Field.Type == PluginSettingFieldType.Folder;
    public bool IsBool => Field.Type == PluginSettingFieldType.Bool;
    public bool IsChoice => Field.Type == PluginSettingFieldType.Choice;

    /// <summary>File and Folder fields both show a Browse button beside the text box.</summary>
    public bool HasBrowse => IsFile || IsFolder;

    /// <summary>Options for a <see cref="PluginSettingFieldType.Choice"/> field; empty otherwise.</summary>
    public IReadOnlyList<string> Choices => Field.Choices ?? [];

    // Text/File/Folder all show the free-text TextBox (File/Folder add a Browse button beside it).
    public bool IsText => Field.Type is PluginSettingFieldType.Text or PluginSettingFieldType.File or PluginSettingFieldType.Folder;

    public bool BoolValue
    {
        get => bool.TryParse(Value, out var b) && b;
        set => Value = value ? "true" : "false";
    }
}
