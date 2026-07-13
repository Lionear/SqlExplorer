namespace Lionear.SqlExplorer.Sdk.Ui;

/// <summary>
/// Read/write access to a tool's input values, handed to a tool's custom view
/// (<see cref="ICustomToolUi"/>). Mirrors <see cref="IConnectionUiContext"/>: the view reads/writes by
/// <c>ToolField.Key</c> so the host collects exactly what the plugin's own UI gathered.
/// </summary>
public interface IToolUiContext
{
    string? GetValue(string key);

    void SetValue(string key, string? value);
}
