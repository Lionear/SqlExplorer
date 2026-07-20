using SqlExplorer.Sdk.Localization;
using SqlExplorer.Sdk.Tools;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SqlExplorer.App.ViewModels;

/// <summary>Editable state for one <see cref="ToolField"/> in the generic tool dialog (Route A).
/// Mirrors <see cref="ConnectionFieldInput"/>, plus a per-field password reveal toggle (§7).</summary>
public partial class ToolFieldInput : ObservableObject
{
    private readonly IPluginLocalizer _localizer;

    public ToolFieldInput(ToolField field, IPluginLocalizer localizer)
        : this(field, localizer, [])
    {
    }

    public ToolFieldInput(ToolField field, IPluginLocalizer localizer, IReadOnlyList<ToolConnectionOption> connections)
    {
        Field = field;
        _localizer = localizer;
        _value = field.Default;
        ConnectionOptions = connections;
    }

    public ToolField Field { get; }

    [ObservableProperty]
    private string? _value;

    /// <summary>Password field: when true the value shows as plain text (the 👁 toggle).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordChar))]
    private bool _revealPassword;

    private string LabelText => _localizer.Resolve(Field.LabelKey, Field.Label);
    public string Label => Field.Required ? $"{LabelText} *" : LabelText;
    public string? Watermark =>
        _localizer.Resolve(Field.PlaceholderKey, Field.Placeholder ?? string.Empty) is { Length: > 0 } text ? text : null;
    public bool IsFile => Field.Type == ToolFieldType.File;
    public bool IsBool => Field.Type == ToolFieldType.Bool;
    public bool IsChoice => Field.Type == ToolFieldType.Choice;
    public bool IsPassword => Field.Type == ToolFieldType.Password;
    public bool IsConnectionPicker => Field.Type == ToolFieldType.ConnectionPicker;

    /// <summary>Text/Password/File all show the free-text box (File adds a Browse button beside it).</summary>
    public bool IsText => Field.Type is ToolFieldType.Text or ToolFieldType.Password or ToolFieldType.File;

    public IReadOnlyList<string> Choices => Field.Choices ?? [];

    /// <summary>Candidate connections for a <see cref="ToolFieldType.ConnectionPicker"/> field, supplied by
    /// the host (empty for every other field type).</summary>
    public IReadOnlyList<ToolConnectionOption> ConnectionOptions { get; }

    /// <summary>The picked connection; its id is stored in <see cref="Value"/> so it flows through the same
    /// inputs dictionary as every other field.</summary>
    public ToolConnectionOption? SelectedConnection
    {
        get => ConnectionOptions.FirstOrDefault(c => c.Id == Value);
        set => Value = value?.Id;
    }

    // Masked bullet for a password unless revealed; (char)0 shows plaintext.
    public char PasswordChar => IsPassword && !RevealPassword ? '•' : '\0';

    public bool BoolValue
    {
        get => bool.TryParse(Value, out var b) && b;
        set => Value = value ? "true" : "false";
    }

    public bool IsFilled => !Field.Required || !string.IsNullOrWhiteSpace(Value);
}
