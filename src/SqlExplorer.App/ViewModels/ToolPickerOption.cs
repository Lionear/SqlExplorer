namespace SqlExplorer.App.ViewModels;

/// <summary>One selectable entry in a <see cref="SqlExplorer.Sdk.Tools.ToolFieldType.ConnectionPicker"/> or
/// <see cref="SqlExplorer.Sdk.Tools.ToolFieldType.DatabasePicker"/> dropdown: the value stored on the field
/// (a connection id, or a database name) and the label shown in the list.</summary>
public sealed record ToolPickerOption(string Id, string Name)
{
    // Shown by the ComboBox when no ItemTemplate is applied.
    public override string ToString() => Name;
}
