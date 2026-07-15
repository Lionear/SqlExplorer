namespace SqlExplorer.Sdk.Tools;

/// <summary>Live status of one item in a tool's per-item checklist (§ object-selection backup/restore).</summary>
public enum ToolItemStatus
{
    Running,
    Done,
    Error,
    Skipped
}

/// <summary>A single progress/log line a tool reports while running; the host appends it to the tool
/// dialog's log panel. <paramref name="Fraction"/> (0..1), when supplied, also drives the dialog's
/// progress bar as a determinate value; leave it null to keep the bar indeterminate (just "busy").
///
/// <para><paramref name="ItemKey"/>/<paramref name="ItemStatus"/> are optional and additive: a tool that
/// works through a known set of items (e.g. per table/object) can key a line to a checklist row and flip
/// its status live. Tools that don't set them are unaffected — the line is just appended to the log.</para></summary>
public sealed record ToolProgress(
    string Message,
    double? Fraction = null,
    string? ItemKey = null,
    ToolItemStatus? ItemStatus = null);
