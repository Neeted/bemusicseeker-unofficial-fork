using System;
using System.Diagnostics;
using System.Threading;

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
        lock (state.SyncRoot)
        {
            request.RequestVersion = state.RequestVersion + 1;
            if (state.PendingRequest != null && state.PendingRequest.Identity == request.Identity)
            {
                return new PlaylistBuildQueueRegisterResult(null, startWorker: false, "pending", ignoredReason: null, lastBuiltScoreSnapshotVersion);
            }

            if (state.PendingRequest == null
                && state.CurrentBuildRequest != null
                && state.CurrentBuildRequest.Identity == request.Identity)
            {
                return new PlaylistBuildQueueRegisterResult(null, startWorker: false, "running", ignoredReason: null, lastBuiltScoreSnapshotVersion);
            }

            if (state.PendingRequest == null
                && state.CurrentBuildRequest == null
                && currentViewIdentity.HasValue
                && currentViewIdentity.Value == request.Identity)
            {
                return new PlaylistBuildQueueRegisterResult(null, startWorker: false, "current_view", "noop_same_view", lastBuiltScoreSnapshotVersion);
            }

            if (state.ShutdownCancellationRequested || isShutdownRequested)
            {
                return new PlaylistBuildQueueRegisterResult(null, startWorker: false, "shutdown", "shutdown_requested", lastBuiltScoreSnapshotVersion);
            }

            state.RequestVersion = request.RequestVersion;
            CancellationTokenSource previousCancellation = state.CurrentBuildCancellation;
            state.PendingRequest = request;
            state.ShutdownCancellationRequested = false;
            bool startWorker = !state.WorkerRunning;
            if (startWorker)
            {
                state.WorkerRunning = true;
            }

            Monitor.PulseAll(state.SyncRoot);
            return new PlaylistBuildQueueRegisterResult(previousCancellation, startWorker, deduplicatedTarget: null, ignoredReason: null, lastBuiltScoreSnapshotVersion);
        }
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
        lock (state.SyncRoot)
        {
            if (state.ShutdownCancellationRequested || state.PendingRequest == null)
            {
                StopWorkerUnsafe(state);
                request = null;
                return false;
            }

            request = state.PendingRequest;
            state.PendingRequest = null;
            state.CurrentBuildRequest = request;
            return true;
        }
    }

    internal static bool FinishWorkerAfterFailure(PlaylistDetailBuildState state)
    {
        lock (state.SyncRoot)
        {
            StopWorkerUnsafe(state);
            if (state.ShutdownCancellationRequested || state.PendingRequest == null)
            {
                return false;
            }

            state.WorkerRunning = true;
            return true;
        }
    }

    private static void StopWorkerUnsafe(PlaylistDetailBuildState state)
    {
        state.WorkerRunning = false;
        state.CurrentBuildCancellation?.Dispose();
        state.CurrentBuildCancellation = null;
        state.CurrentBuildRequest = null;
        state.Cancellation = new CancellationTokenSource();
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
        }
    }

    internal static void CancelForShutdown(PlaylistDetailBuildState state)
    {
        lock (state.SyncRoot)
        {
            state.PendingRequest = null;
            state.ShutdownCancellationRequested = true;
            TryCancel(state.CurrentBuildCancellation);
            TryCancel(state.Cancellation);
            Monitor.PulseAll(state.SyncRoot);
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
