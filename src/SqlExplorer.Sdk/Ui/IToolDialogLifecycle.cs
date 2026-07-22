using SqlExplorer.Sdk.Tools;

namespace SqlExplorer.Sdk.Ui;

/// <summary>How a tool run ended, handed to <see cref="IToolDialogLifecycle.OnRunFinished"/>.</summary>
public enum ToolRunOutcome
{
    Succeeded,
    Cancelled,
    Failed
}

/// <summary>
/// Optional capability a Route-B view (<see cref="ICustomToolUi.CreateView"/>) may implement to own the
/// <i>whole</i> dialog lifecycle — input, progress and completion — instead of only the input area. A view
/// that implements it tells the host: hide your generic checklist, log, progress bar and action bar; I
/// render those myself. The host then only feeds it the run's events and lets it drive the run through
/// <see cref="IToolUiContext.RunAsync"/> / <see cref="IToolUiContext.CancelRun"/> /
/// <see cref="IToolUiContext.CloseDialog"/>.
///
/// <para>Purely additive: a view that doesn't implement it keeps the host-rendered chrome, which is still
/// the right default for tools without a designed progress story.</para>
/// </summary>
public interface IToolDialogLifecycle
{
    /// <summary>A run is starting: switch to the progress state and build the step list.</summary>
    void OnRunStarted();

    /// <summary>One progress report from the running tool — the same value the host would log.</summary>
    void OnProgress(ToolProgress progress);

    /// <summary>The run ended. <paramref name="message"/> carries the cancellation/error text when the
    /// outcome isn't <see cref="ToolRunOutcome.Succeeded"/>.</summary>
    void OnRunFinished(ToolRunOutcome outcome, string? message);
}
