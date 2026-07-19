namespace SqlExplorer.Sdk.Extensibility;

/// <summary>
/// Optional contribution a standing-subsystem plugin (<see cref="ISubsystemPlugin"/>) may implement to run a
/// long-lived background loop while the app is open — e.g. polling <c>docker ps</c> for live container status.
/// Gated by the <see cref="PluginCapabilities.Background"/> capability. The host starts <see cref="RunAsync"/>
/// once at startup with a cancellation token tied to app shutdown, and expects the loop to exit cleanly when
/// it's cancelled. The task runs unobserved (fire-and-forget), so the plugin must not let exceptions escape —
/// swallow/log them and keep going, or return.
/// </summary>
public interface IBackgroundPlugin
{
    /// <summary>Run until <paramref name="cancellationToken"/> is cancelled (app shutdown). Must return
    /// promptly on cancellation and never throw out of the returned task.</summary>
    Task RunAsync(CancellationToken cancellationToken);
}
