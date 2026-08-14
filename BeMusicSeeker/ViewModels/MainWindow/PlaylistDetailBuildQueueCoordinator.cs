using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace BeMusicSeeker.ViewModels;

internal readonly struct PlaylistBuildQueueRegisterResult
{
    internal PlaylistBuildQueueRegisterResult(
        CancellationTokenSource previousCancellation,
        bool startWorker,
        string deduplicatedTarget,
        string ignoredReason,
        int lastBuiltScoreSnapshotVersion)
    {
        PreviousCancellation = previousCancellation;
        StartWorker = startWorker;
        DeduplicatedTarget = deduplicatedTarget;
        IgnoredReason = ignoredReason;
        LastBuiltScoreSnapshotVersion = lastBuiltScoreSnapshotVersion;
    }

    internal CancellationTokenSource PreviousCancellation { get; }

    internal bool StartWorker { get; }

    internal string DeduplicatedTarget { get; }

    internal string IgnoredReason { get; }

    internal int LastBuiltScoreSnapshotVersion { get; }

    internal bool Enqueued => DeduplicatedTarget == null;
}

internal readonly struct PlaylistBuildQueueCoalesceResult
{
    internal PlaylistBuildQueueCoalesceResult(PlaylistBuildRequest request, int coalescedCount, long elapsedMs, bool cancelledForShutdown)
    {
        Request = request;
        CoalescedCount = coalescedCount;
        ElapsedMs = elapsedMs;
        CancelledForShutdown = cancelledForShutdown;
    }

    internal PlaylistBuildRequest Request { get; }

    internal int CoalescedCount { get; }

    internal long ElapsedMs { get; }

    internal bool CancelledForShutdown { get; }
}

internal static class PlaylistDetailBuildQueueCoordinator
{
    internal static PlaylistBuildQueueRegisterResult RegisterRequest(
        PlaylistDetailBuildState state,
        PlaylistBuildRequest request,
        PlaylistRequestIdentity? currentViewIdentity,
        int lastBuiltScoreSnapshotVersion,
        bool isShutdownRequested)
    {
        TaskCompletionSource<bool> completion = null;
        PlaylistBuildQueueRegisterResult result;
        lock (state.SyncRoot)
        {
            request.RequestVersion = state.RequestVersion + 1;
            if (state.PendingRequest != null && state.PendingRequest.Identity == request.Identity)
            {
                request.RequestVersion = state.PendingRequest.RequestVersion;
                return new PlaylistBuildQueueRegisterResult(null, startWorker: false, "pending", ignoredReason: null, lastBuiltScoreSnapshotVersion);
            }

            if (state.PendingRequest == null
                && state.CurrentBuildRequest != null
                && state.CurrentBuildRequest.Identity == request.Identity)
            {
                request.RequestVersion = state.CurrentBuildRequest.RequestVersion;
                return new PlaylistBuildQueueRegisterResult(null, startWorker: false, "running", ignoredReason: null, lastBuiltScoreSnapshotVersion);
            }

            if (state.PendingRequest == null
                && state.CurrentBuildRequest == null
                && currentViewIdentity.HasValue
                && currentViewIdentity.Value == request.Identity)
            {
                request.RequestVersion = state.RequestVersion;
                return new PlaylistBuildQueueRegisterResult(null, startWorker: false, "current_view", "noop_same_view", lastBuiltScoreSnapshotVersion);
            }

            if (state.ShutdownCancellationRequested || isShutdownRequested)
            {
                request.RequestVersion = state.RequestVersion;
                return new PlaylistBuildQueueRegisterResult(null, startWorker: false, "shutdown", "shutdown_requested", lastBuiltScoreSnapshotVersion);
            }

            state.RequestVersion = request.RequestVersion;
            completion = state.AdvanceCompletedRequestVersionUnsafe(request.RequestVersion - 1);
            CancellationTokenSource previousCancellation = state.CurrentBuildCancellation;
            state.PendingRequest = request;
            state.ShutdownCancellationRequested = false;
            bool startWorker = !state.WorkerRunning;
            if (startWorker)
            {
                state.IdleCompletion = PlaylistDetailBuildState.CreatePendingCompletion();
                state.WorkerRunning = true;
            }

            Monitor.PulseAll(state.SyncRoot);
            result = new PlaylistBuildQueueRegisterResult(previousCancellation, startWorker, deduplicatedTarget: null, ignoredReason: null, lastBuiltScoreSnapshotVersion);
        }
        completion?.TrySetResult(true);
        return result;
    }

    internal static bool IsLatestRequest(PlaylistDetailBuildState state, int requestVersion)
    {
        lock (state.SyncRoot)
        {
            return requestVersion == state.RequestVersion;
        }
    }

    internal static bool TryTakeNextRequestOrStopWorker(
        PlaylistDetailBuildState state,
        out PlaylistBuildRequest request)
    {
        TaskCompletionSource<bool> idleCompletion = null;
        bool requestAvailable;
        lock (state.SyncRoot)
        {
            if (state.ShutdownCancellationRequested || state.PendingRequest == null)
            {
                idleCompletion = StopWorkerUnsafe(state);
                request = null;
                requestAvailable = false;
            }
            else
            {
                request = state.PendingRequest;
                state.PendingRequest = null;
                state.CurrentBuildRequest = request;
                requestAvailable = true;
            }
        }
        idleCompletion?.TrySetResult(true);
        return requestAvailable;
    }

    internal static bool FinishWorkerAfterFailure(PlaylistDetailBuildState state)
    {
        TaskCompletionSource<bool> idleCompletion = null;
        bool restartWorker;
        lock (state.SyncRoot)
        {
            idleCompletion = StopWorkerUnsafe(state);
            if (state.ShutdownCancellationRequested || state.PendingRequest == null)
            {
                restartWorker = false;
            }
            else
            {
                state.WorkerRunning = true;
                restartWorker = true;
                idleCompletion = null;
            }
        }
        idleCompletion?.TrySetResult(true);
        return restartWorker;
    }

    private static TaskCompletionSource<bool> StopWorkerUnsafe(PlaylistDetailBuildState state)
    {
        state.WorkerRunning = false;
        state.CurrentBuildCancellation?.Dispose();
        state.CurrentBuildCancellation = null;
        state.CurrentBuildRequest = null;
        state.Cancellation = new CancellationTokenSource();
        return state.IdleCompletion;
    }

    internal static PlaylistBuildQueueCoalesceResult CoalescePendingRequest(PlaylistDetailBuildState state, PlaylistBuildRequest request, int coalescingWindowMs)
    {
        if (request == null || !request.UseCoalescingWindow || coalescingWindowMs <= 0)
        {
            return new PlaylistBuildQueueCoalesceResult(request, coalescedCount: 0, elapsedMs: 0L, cancelledForShutdown: false);
        }

        var stopwatch = Stopwatch.StartNew();
        int coalescedCount = 0;
        bool cancelledForShutdown = false;
        lock (state.SyncRoot)
        {
            while (true)
            {
                int remainingMs = coalescingWindowMs - (int)stopwatch.ElapsedMilliseconds;
                if (remainingMs <= 0)
                {
                    break;
                }

                Monitor.Wait(state.SyncRoot, remainingMs);
                if (state.ShutdownCancellationRequested)
                {
                    request = null;
                    cancelledForShutdown = true;
                    break;
                }

                if (state.PendingRequest == null)
                {
                    continue;
                }

                request = state.PendingRequest;
                state.PendingRequest = null;
                state.CurrentBuildRequest = request;
                coalescedCount++;
            }
        }

        return new PlaylistBuildQueueCoalesceResult(request, coalescedCount, stopwatch.ElapsedMilliseconds, cancelledForShutdown);
    }

    internal static bool TryBeginIteration(PlaylistDetailBuildState state, PlaylistBuildRequest request, CancellationTokenSource buildCancellation, bool isShutdownRequested)
    {
        lock (state.SyncRoot)
        {
            if (state.ShutdownCancellationRequested || isShutdownRequested)
            {
                return false;
            }

            state.CurrentBuildCancellation?.Dispose();
            state.CurrentBuildCancellation = buildCancellation;
            state.Cancellation = buildCancellation;
            state.CurrentBuildRequest = request;
            return true;
        }
    }

    internal static void CompleteIteration(PlaylistDetailBuildState state, CancellationTokenSource buildCancellation, PlaylistBuildRequest request)
    {
        TaskCompletionSource<bool> completion;
        lock (state.SyncRoot)
        {
            if (ReferenceEquals(state.CurrentBuildCancellation, buildCancellation))
            {
                state.CurrentBuildCancellation = null;
            }

            if (ReferenceEquals(state.CurrentBuildRequest, request))
            {
                state.CurrentBuildRequest = null;
            }
            completion = state.AdvanceCompletedRequestVersionUnsafe(request.RequestVersion);
        }
        completion?.TrySetResult(true);
    }

    internal static void CancelForShutdown(PlaylistDetailBuildState state)
    {
        TaskCompletionSource<bool> completion;
        lock (state.SyncRoot)
        {
            state.PendingRequest = null;
            state.ShutdownCancellationRequested = true;
            TryCancel(state.CurrentBuildCancellation);
            TryCancel(state.Cancellation);
            completion = state.AdvanceCompletedRequestVersionUnsafe(state.RequestVersion);
            Monitor.PulseAll(state.SyncRoot);
        }
        completion?.TrySetResult(true);
    }

    /// <summary>
    /// Returns a task that completes when the specified existing request version is applied, superseded,
    /// cancelled, or failed. Versions outside the current lifecycle do not create persistent wait state.
    /// </summary>
    internal static Task WaitForRequestCompletionAsync(PlaylistDetailBuildState state, int requestVersion)
    {
        lock (state.SyncRoot)
        {
            return requestVersion <= state.CompletedRequestVersion || requestVersion > state.RequestVersion
                ? Task.CompletedTask
                : state.RequestCompletionPulse.Task;
        }
    }

    /// <summary>
    /// Returns a task that completes when the captured detail worker lifecycle has fully stopped.
    /// </summary>
    internal static Task WaitForIdleAsync(PlaylistDetailBuildState state)
    {
        lock (state.SyncRoot)
        {
            return !state.WorkerRunning && state.PendingRequest == null && state.CurrentBuildRequest == null
                ? Task.CompletedTask
                : state.IdleCompletion.Task;
        }
    }

    private static void TryCancel(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal static bool IsIdle(PlaylistDetailBuildState state)
    {
        lock (state.SyncRoot)
        {
            return !state.WorkerRunning
                && state.PendingRequest == null
                && state.CurrentBuildRequest == null;
        }
    }

    internal static string FormatDiagnostics(PlaylistDetailBuildState state, Func<bool, string> formatBool)
    {
        lock (state.SyncRoot)
        {
            return "workerRunning=" + formatBool(state.WorkerRunning)
                + " pendingRequest=" + formatBool(state.PendingRequest != null)
                + " currentBuildRequest=" + formatBool(state.CurrentBuildRequest != null);
        }
    }
}
