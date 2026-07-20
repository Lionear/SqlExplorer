namespace SqlExplorer.Sdk.Tools;

/// <summary>How a <see cref="ToolField"/> is captured — the host renders one control per type,
/// mirroring the connection dialog's field rendering.</summary>
public enum ToolFieldType
{
    Text,
    Password,
    Choice,

    /// <summary>A filesystem path; the host shows a Browse button that opens a save/open picker via
    /// <see cref="IToolHost"/>.</summary>
    File,
    Bool,

    /// <summary>A second saved connection, chosen from a host-rendered dropdown of the user's connections
    /// (filtered to the launched connection's provider). The collected value is the chosen connection's id;
    /// the tool turns it into a runnable connection via <see cref="IToolHost.OpenConnection"/>. The picker's
    /// options are host state, not knowable by the plugin, so <see cref="ToolField.Choices"/> is ignored here.
    /// </summary>
    ConnectionPicker
}

/// <summary>
/// One input in a tool's generated form (Route A). A tool declares these; the host renders a generic
/// dialog from them and collects the values into the dictionary passed to
/// <see cref="IToolPlugin.ExecuteAsync"/>. Purely declarative so it crosses the plugin ALC boundary
/// cleanly — the same shape as <c>ConnectionField</c>.
/// </summary>
/// <param name="Choices">Allowed values for a <see cref="ToolFieldType.Choice"/> field; ignored otherwise.</param>
/// <param name="FileExtensions">For a <see cref="ToolFieldType.File"/> field: the file extensions the
/// picker filters on (e.g. "lbak"); empty = any file.</param>
/// <param name="SaveFile">For a <see cref="ToolFieldType.File"/> field: true opens a save-file picker
/// (a target path to write), false an open-file picker (an existing file to read).</param>
/// <param name="LabelKey">Optional localization key for <paramref name="Label"/>; when set and the plugin
/// ships a matching translation the host shows it, otherwise <paramref name="Label"/> stays the fallback.</param>
/// <param name="PlaceholderKey">Optional localization key for <paramref name="Placeholder"/> (same rule).</param>
public sealed record ToolField(
    string Key,
    string Label,
    ToolFieldType Type = ToolFieldType.Text,
    bool Required = false,
    string? Default = null,
    string? Placeholder = null,
    IReadOnlyList<string>? Choices = null,
    IReadOnlyList<string>? FileExtensions = null,
    bool SaveFile = false,
    string? LabelKey = null,
    string? PlaceholderKey = null);
