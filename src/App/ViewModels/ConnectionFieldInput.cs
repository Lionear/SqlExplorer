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
    public bool IsChoice => Field.Type == ConnectionFieldType.Choice;

    /// <summary>Options for a <see cref="ConnectionFieldType.Choice"/> field; empty otherwise.</summary>
    public IReadOnlyList<string> Choices => Field.Choices ?? [];

    // A plain text/number/password field: the only kind that shows the free-text TextBox.
    public bool IsText => Field.Type is ConnectionFieldType.Text or ConnectionFieldType.Password
        or ConnectionFieldType.Number or ConnectionFieldType.File;

    // (char)0 tells the TextBox to show plaintext; a bullet masks secrets.
    public char PasswordChar => Field.Type == ConnectionFieldType.Password ? '•' : '\0';

    public bool BoolValue
    {
        get => bool.TryParse(Value, out var b) && b;
        set => Value = value ? "true" : "false";
    }
}
