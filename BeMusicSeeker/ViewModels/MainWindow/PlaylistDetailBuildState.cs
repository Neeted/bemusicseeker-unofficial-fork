using System.Threading;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Holds playlist detail build worker queue and cancellation state.
/// </summary>
internal sealed class PlaylistDetailBuildState
{
    /// <summary>
    /// Serializes request queue and build cancellation updates.
    /// </summary>
    internal readonly object SyncRoot = new();

    /// <summary>
    /// Limits playlist source build execution to one worker iteration.
    /// </summary>
    internal readonly SemaphoreSlim BuildGate = new(1, 1);

    /// <summary>
    /// Latest playlist build request version.
    /// </summary>
    internal int RequestVersion;

    /// <summary>
    /// Token source for the active or most recent playlist build lifecycle.
    /// </summary>
    internal CancellationTokenSource Cancellation = new();

    /// <summary>
    /// Token source for the request currently being built.
    /// </summary>
    internal CancellationTokenSource CurrentBuildCancellation;

    /// <summary>
    /// Request currently held by the worker, including quiet-window candidates.
    /// </summary>
    internal PlaylistBuildRequest CurrentBuildRequest;

    /// <summary>
    /// Latest pending playlist build request.
    /// </summary>
    internal PlaylistBuildRequest PendingRequest;

    /// <summary>
    /// Whether the playlist build worker is currently running.
    /// </summary>
    internal bool WorkerRunning;

    /// <summary>
    /// Whether shutdown cancellation has been requested for the current worker lifecycle.
    /// </summary>
    internal bool ShutdownCancellationRequested;
}
