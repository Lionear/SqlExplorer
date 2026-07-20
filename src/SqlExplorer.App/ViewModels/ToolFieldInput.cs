using System.Collections.ObjectModel;
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

    public ToolFieldInput(ToolField field, IPluginLocalizer localizer, IReadOnlyList<ToolPickerOption> options)
    {
        Field = field;
        _localizer = localizer;
        _value = field.Default;
        PickerOptions = new ObservableCollection<ToolPickerOption>(options);
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
    public bool IsDatabasePicker => Field.Type == ToolFieldType.DatabasePicker;

    /// <summary>Connection- and database-pickers share one dropdown control in the dialog template.</summary>
    public bool IsPicker => IsConnectionPicker || IsDatabasePicker;

    /// <summary>Text/Password/File all show the free-text box (File adds a Browse button beside it).</summary>
    public bool IsText => Field.Type is ToolFieldType.Text or ToolFieldType.Password or ToolFieldType.File;

    public IReadOnlyList<string> Choices => Field.Choices ?? [];

    /// <summary>Options for a <see cref="ToolFieldType.ConnectionPicker"/> / <see cref="ToolFieldType.DatabasePicker"/>
    /// field (empty for every other type). A database-picker's options are refilled by the host whenever its
    /// companion connection changes, so this is observable.</summary>
    public ObservableCollection<ToolPickerOption> PickerOptions { get; }

    /// <summary>The picked option; its id/name is stored in <see cref="Value"/> so it flows through the same
    /// inputs dictionary as every other field.</summary>
    public ToolPickerOption? SelectedOption
    {
        get => PickerOptions.FirstOrDefault(c => c.Id == Value);
        set => Value = value?.Id;
    }

    /// <summary>Replace the picker's options (a connection change repopulating a database picker) and clear the
    /// selection so a stale database name can't linger.</summary>
    public void SetPickerOptions(IEnumerable<ToolPickerOption> options)
    {
        PickerOptions.Clear();
        foreach (var option in options)
        {
            PickerOptions.Add(option);
        }

        Value = null;
        OnPropertyChanged(nameof(SelectedOption));
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
