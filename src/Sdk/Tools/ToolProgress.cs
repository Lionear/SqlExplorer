namespace Lionear.SqlExplorer.Sdk.Tools;

/// <summary>A single progress/log line a tool reports while running; the host appends it to the tool
/// dialog's log panel.</summary>
public sealed record ToolProgress(string Message);
