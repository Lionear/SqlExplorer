using System.IO.Pipes;
using System.Threading;
using SqlExplorer.Infrastructure.Persistence;

namespace SqlExplorer.App;

/// <summary>
/// Single-instance coordination over a per-user named pipe. Primary detection is done by probing the pipe:
/// if an instance is already listening, this launch is a "secondary" — it signals the primary to surface
/// its window and then exits; otherwise it is the "primary" and owns the UI. Using a live listener (rather
/// than a lock file or named mutex) as the source of truth is self-healing: after a crash there is nothing
/// to answer the probe, so the next launch cleanly becomes the new primary — no stale lock to clear.
///
/// This is the fallback for close-to-tray on desktops without a visible tray icon (e.g. GNOME without an
/// AppIndicator extension): re-launching the app brings the hidden window back instead of doing nothing.
/// </summary>
public static class SingleInstance
{
    // Per-user so different logged-in users don't collide; a named pipe maps to a user-owned file on Unix.
    private static string PipeName => $"SqlExplorer.SingleInstance.{Environment.UserName}";

    /// <summary>Reads the "allow multiple instances" preference straight from disk — before Avalonia and DI
    /// exist — so <c>Program</c> can decide whether to run the single-instance probe at all (SE-124). A
    /// missing/corrupt settings file degrades to the default (false = single instance).</summary>
    public static bool MultipleInstancesAllowed() => new JsonAppSettingsStore().Load().AllowMultipleInstances;

    /// <summary>
    /// Returns true if this process should own the UI (no other instance answered). Returns false if an
    /// existing instance was found and signalled — the caller must then exit without starting the UI.
    /// </summary>
    public static bool TryBecomePrimary()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(400); // Short: with no server listening this fails fast and we become primary.
            client.WriteByte(1);
            client.Flush();
            return false;
        }
        catch
        {
            // Nobody listening (or unreachable) → we are the primary instance.
            return true;
        }
    }

    /// <summary>
    /// Primary-instance listener: invokes <paramref name="onSignal"/> each time a later launch asks the
    /// running app to surface its window. Runs on a background task until <paramref name="token"/> is
    /// cancelled. The callback is responsible for marshalling to the UI thread.
    /// </summary>
    public static void StartServer(Action onSignal, CancellationToken token)
    {
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(token);
                    _ = server.ReadByte();
                    onSignal();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Keep listening across transient pipe errors.
                }
            }
        }, token);
    }
}
