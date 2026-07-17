namespace SqlExplorer.Core.Update;

/// <summary>What the host should do after an apply/rollback stages its files (SE-137, Fase 2).</summary>
public enum ApplyAction
{
    /// <summary>Files are swapped in place; launch <see cref="ApplyResult.RelaunchTarget"/> and exit (Linux AppImage).</summary>
    RelaunchAfterExit,

    /// <summary>An OS installer was launched and will replace + relaunch the app; just exit now (Windows).</summary>
    ExitForInstaller,

    /// <summary>A guided flow was opened (macOS DMG); the app keeps running, the user finishes by hand.</summary>
    Guided,

    /// <summary>Nothing was applied; see <see cref="ApplyResult.Message"/>.</summary>
    Failed
}

/// <summary>Outcome of <see cref="IUpdateApplier.ApplyAsync"/> / <see cref="IUpdateApplier.Rollback"/>.</summary>
public sealed record ApplyResult(ApplyAction Action, string? RelaunchTarget = null, string? Message = null)
{
    public static ApplyResult Relaunch(string target) => new(ApplyAction.RelaunchAfterExit, target);
    public static ApplyResult Installer() => new(ApplyAction.ExitForInstaller);
    public static ApplyResult GuidedFlow(string? message = null) => new(ApplyAction.Guided, null, message);
    public static ApplyResult Fail(string message) => new(ApplyAction.Failed, null, message);
}

/// <summary>
/// Applies a downloaded update in place, per platform (SE-137, Fase 2). Hybrid strategy: Linux swaps the
/// AppImage beside the running one with a <c>.prev</c> for rollback (the <c>PluginMaintenance</c> model at
/// app level); Windows delegates to the silent installer; macOS opens the DMG for a guided drag. Rollback
/// is only offered where a <c>.prev</c> exists (Linux). The actual relaunch/exit is the host's job — this
/// only stages files and reports what the host should do next.
/// </summary>
public interface IUpdateApplier
{
    /// <summary>True when this platform can apply the given asset in place (vs. only hand it to the user).</summary>
    bool CanApplyInPlace(UpdateAsset asset);

    /// <summary>Stages the verified <paramref name="filePath"/> and returns what the host should do next.</summary>
    Task<ApplyResult> ApplyAsync(string filePath, UpdateAsset asset, CancellationToken ct);

    /// <summary>True when a previous version is staged to roll back to (Linux AppImage only).</summary>
    bool CanRollback { get; }

    /// <summary>Swaps the current build back to the <c>.prev</c> one; the swap is itself reversible.</summary>
    ApplyResult Rollback();
}
