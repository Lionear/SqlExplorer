using Lionear.SqlExplorer.Sdk;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Lionear.SqlExplorer.App.ViewModels;

/// <summary>Editable state for one <see cref="ConnectionField"/> in the connection dialog.</summary>
public partial class ConnectionFieldInput : ObservableObject
{
    public ConnectionFieldInput(ConnectionField field)
    {
        Field = field;
        _value = field.Default;
    }

    public ConnectionField Field { get; }

    [ObservableProperty]
    private string? _value;

    public string Label => Field.Required ? $"{Field.Label} *" : Field.Label;
    public string? Watermark => Field.Placeholder;
    public bool IsFile => Field.Type == ConnectionFieldType.File;
    public bool IsBool => Field.Type == ConnectionFieldType.Bool;

    // (char)0 tells the TextBox to show plaintext; a bullet masks secrets.
    public char PasswordChar => Field.Type == ConnectionFieldType.Password ? '•' : '\0';

    public bool BoolValue
    {
        get => bool.TryParse(Value, out var b) && b;
        set => Value = value ? "true" : "false";
    }
}
