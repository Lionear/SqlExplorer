namespace SqlExplorer.App.ViewModels;

/// <summary>One selectable connection in a <see cref="SqlExplorer.Sdk.Tools.ToolFieldType.ConnectionPicker"/>
/// dropdown: the id stored as the field value, and the name shown in the list.</summary>
public sealed record ToolConnectionOption(string Id, string Name)
{
    // Shown by the ComboBox when no ItemTemplate is applied.
    public override string ToString() => Name;
}
