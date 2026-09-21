using System;
using System.Diagnostics;
using System.Threading.Tasks;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    private const int PlaylistReloadCleanupBuildWaitTimeoutMs = 10000;

    private const int PlaylistReloadCleanupStartupOperableWaitTimeoutMs = 30000;

    private readonly object playlistReloadCleanupSync = new();

    private PlaylistReloadCleanupRequest pendingPlaylistReloadCleanup;

    private bool playlistReloadCleanupRunning;

    private TaskCompletionSource<bool> playlistReloadCleanupIdleCompletion =
        CreateCompletedLifecycleCompletion();

    private long playlistReloadCleanupSeed;

    private long playlistReloadCleanupWorkerSeed;

    private long playlistReloadCleanupWorkerId;

    private readonly Func<bool> playlistReloadCleanupStartupOperableProvider;

    private readonly Func<MainViewUpdateMode> playlistReloadCleanupCurrentTreeModeProvider;

    private readonly Func<Task> playlistReloadCleanupDispatcherIdleWaiter;

    private readonly Func<bool> playlistReloadCleanupShutdownRequestedProvider;

    private readonly Action playlistReloadCleanupGarbageCollector;

    private readonly Action<string> playlistReloadLog;

    internal static string GetPlaylistReloadOperationKindText(string reason, bool fromReloadTables)
    {
        return GetPlaylistReloadOperationKindText(DeterminePlaylistReloadOperationKind(reason, fromReloadTables));
    }

    internal static string GetPlaylistReloadOperationKindText(bool isFullReload)
    {
        return GetPlaylistReloadOperationKindText(
            isFullReload
                ? PlaylistReloadCleanupOperationKind.ManualFullReload
                : PlaylistReloadCleanupOperationKind.SinglePlaylistReload);
    }

    internal bool QueuePlaylistReloadCleanup(string reason, bool fromReloadTables, int tableCount)
    {
        return QueuePlaylistReloadCleanup(
            DeterminePlaylistReloadOperationKind(reason, fromReloadTables),
            tableCount);
    }

    internal bool QueuePlaylistReloadCleanup(bool isFullReload, int tableCount)
    {
        return QueuePlaylistReloadCleanup(
            isFullReload
                ? PlaylistReloadCleanupOperationKind.ManualFullReload
                : PlaylistReloadCleanupOperationKind.SinglePlaylistReload,
            tableCount);
    }

    private bool QueuePlaylistReloadCleanup(
        PlaylistReloadCleanupOperationKind operationKind,
        int tableCount)
    {
        if (operationKind != PlaylistReloadCleanupOperationKind.StartupFullReload
            && operationKind != PlaylistReloadCleanupOperationKind.ManualFullReload)
        {
            WritePlaylistReloadCleanupLog(
                "playlist_reload_cleanup skipped operationKind="
                + GetPlaylistReloadOperationKindText(operationKind)
                + " tableCount="
                + tableCount
                + " reason=not_full_reload");
            return false;
        }

        var request = new PlaylistReloadCleanupRequest
        {
            CleanupId = System.Threading.Interlocked.Increment(ref playlistReloadCleanupSeed),
            OperationKind = operationKind,
            TableCount = tableCount,
            WaitForStartupOperable = operationKind == PlaylistReloadCleanupOperationKind.StartupFullReload,
            WaitForSummaryRefresh = IsPlaylistSummaryMode,
            WaitForDetailRefresh = ShouldRefreshPlaylistDetailAfterReload(playlistReloadCleanupCurrentTreeModeProvider()),
            GcAllowed = true,
            RequestedAtTimestamp = Stopwatch.GetTimestamp()
        };
        long workerId = 0L;
        lock (playlistReloadCleanupSync)
        {
            pendingPlaylistReloadCleanup = request;
            if (!playlistReloadCleanupRunning)
            {
                playlistReloadCleanupIdleCompletion = CreatePendingLifecycleCompletion();
                playlistReloadCleanupRunning = true;
                workerId = System.Threading.Interlocked.Increment(ref playlistReloadCleanupWorkerSeed);
                playlistReloadCleanupWorkerId = workerId;
            }
        }
        WritePlaylistReloadCleanupLog(
            "playlist_reload_cleanup queued cleanupId="
            + request.CleanupId
            + " operationKind="
            + GetPlaylistReloadOperationKindText(operationKind)
            + " tableCount="
            + tableCount
            + " waitForStartupOperable="
            + request.WaitForStartupOperable.ToString().ToLowerInvariant()
            + " waitForSummaryRefresh="
            + request.WaitForSummaryRefresh.ToString().ToLowerInvariant()
            + " waitForDetailRefresh="
            + request.WaitForDetailRefresh.ToString().ToLowerInvariant()
            + " gcAllowed="
            + request.GcAllowed.ToString().ToLowerInvariant());
        if (workerId != 0L)
        {
            Task.Run(() => ProcessPendingPlaylistReloadCleanupAsync(workerId)).ObserveFault("ProcessPendingPlaylistReloadCleanupAsync");
        }
        return true;
    }

    internal void TrySchedulePlaylistReloadCleanup()
    {
        long workerId = 0L;
        lock (playlistReloadCleanupSync)
        {
            if (pendingPlaylistReloadCleanup != null && !playlistReloadCleanupRunning)
            {
                playlistReloadCleanupRunning = true;
                workerId = System.Threading.Interlocked.Increment(ref playlistReloadCleanupWorkerSeed);
                playlistReloadCleanupWorkerId = workerId;
            }
        }
        if (workerId != 0L)
        {
            Task.Run(() => ProcessPendingPlaylistReloadCleanupAsync(workerId)).ObserveFault("ProcessPendingPlaylistReloadCleanup");
        }
    }

    internal async Task WaitForPlaylistReloadCleanupReadinessAsync(
        bool waitForSummaryRefresh,
        bool waitForDetailRefresh,
        long requestedAtTimestamp)
    {
        await WaitForPlaylistReloadCleanupBuildReadinessAsync(
            waitForSummaryRefresh,
            waitForDetailRefresh,
            requestedAtTimestamp).ConfigureAwait(false);
    }

    internal bool IsPlaylistReloadCleanupIdle
    {
        get
        {
            lock (playlistReloadCleanupSync)
            {
                return pendingPlaylistReloadCleanup == null && !playlistReloadCleanupRunning;
            }
        }
    }

    /// <summary>
    /// Returns a task for the captured reload-cleanup lifecycle. A replacement queued while that lifecycle
    /// is running is included, so the task cannot complete between coalesced cleanup requests.
    /// </summary>
    internal Task WaitForPlaylistReloadCleanupIdleAsync()
    {
        lock (playlistReloadCleanupSync)
        {
            return pendingPlaylistReloadCleanup == null && !playlistReloadCleanupRunning
                ? Task.CompletedTask
                : playlistReloadCleanupIdleCompletion.Task;
        }
    }

    internal string DescribePlaylistReloadCleanupWaitState()
    {
        lock (playlistReloadCleanupSync)
        {
            return "pending="
                + (pendingPlaylistReloadCleanup != null).ToString().ToLowerInvariant()
                + " running="
                + playlistReloadCleanupRunning.ToString().ToLowerInvariant();
        }
    }

    internal void CancelPlaylistReloadCleanupForShutdown()
    {
        lock (playlistReloadCleanupSync)
        {
            pendingPlaylistReloadCleanup = null;
        }
    }

    internal PlaylistReloadCleanupSnapshot CapturePlaylistReloadCleanupSnapshot()
    {
        PlaylistPreviousDetailRowsSnapshot previousDetailRows = CapturePreviousDetailRowsSnapshot();
        return new PlaylistReloadCleanupSnapshot(
            summaryAlive: false,
            summaryRowCount: 0,
            previousDetailRows);
    }

    private async Task ProcessPendingPlaylistReloadCleanupAsync(long workerId)
    {
        TaskCompletionSource<bool> idleCompletion = null;
        try
        {
            while (true)
            {
                PlaylistReloadCleanupRequest request;
                lock (playlistReloadCleanupSync)
                {
                    request = pendingPlaylistReloadCleanup;
                    pendingPlaylistReloadCleanup = null;
                    if (request == null)
                    {
                        if (playlistReloadCleanupWorkerId == workerId)
                        {
                            playlistReloadCleanupRunning = false;
                            idleCompletion = playlistReloadCleanupIdleCompletion;
                        }
                    }
                }
                if (request == null)
                {
                    idleCompletion?.TrySetResult(true);
                    return;
                }

                await WaitForPlaylistReloadCleanupReadinessAsync(request).ConfigureAwait(false);
                PlaylistReloadCleanupSnapshot cleanupSnapshot = CapturePlaylistReloadCleanupSnapshot();
                long managedMemoryBeforeBytes = GC.GetTotalMemory(forceFullCollection: false);
                bool gcInvoked = request.GcAllowed && !playlistReloadCleanupShutdownRequestedProvider();
                if (gcInvoked)
                {
                    playlistReloadCleanupGarbageCollector();
                }
                long managedMemoryAfterBytes = GC.GetTotalMemory(forceFullCollection: false);
                WritePlaylistReloadCleanupLog(
                    "playlist_reload_cleanup completed cleanupId="
                    + request.CleanupId
                    + " operationKind="
                    + GetPlaylistReloadOperationKindText(request.OperationKind)
                    + " tableCount="
                    + request.TableCount
                    + " oldSummaryAlive="
                    + cleanupSnapshot.SummaryAlive.ToString().ToLowerInvariant()
                    + " oldSummaryRowCount="
                    + cleanupSnapshot.SummaryRowCount
                    + " oldDetailSourceAlive="
                    + cleanupSnapshot.PreviousDetailRows.SourceAlive.ToString().ToLowerInvariant()
                    + " oldDetailSourceRowCount="
                    + cleanupSnapshot.PreviousDetailRows.SourceRowCount
                    + " oldDetailViewAlive="
                    + cleanupSnapshot.PreviousDetailRows.ViewAlive.ToString().ToLowerInvariant()
                    + " oldDetailViewRowCount="
                    + cleanupSnapshot.PreviousDetailRows.ViewRowCount
                    + " managedMemoryBeforeMb="
                    + (managedMemoryBeforeBytes / 1024L / 1024L)
                    + " managedMemoryAfterMb="
                    + (managedMemoryAfterBytes / 1024L / 1024L)
                    + " gcInvoked="
                    + gcInvoked.ToString().ToLowerInvariant());
            }
        }
        finally
        {
            long restartWorkerId = 0L;
            idleCompletion = null;
            lock (playlistReloadCleanupSync)
            {
                if (playlistReloadCleanupWorkerId == workerId)
                {
                    playlistReloadCleanupRunning = false;
                    if (pendingPlaylistReloadCleanup != null)
                    {
                        playlistReloadCleanupRunning = true;
                        restartWorkerId = System.Threading.Interlocked.Increment(ref playlistReloadCleanupWorkerSeed);
                        playlistReloadCleanupWorkerId = restartWorkerId;
                    }
                    else
                    {
                        idleCompletion = playlistReloadCleanupIdleCompletion;
                    }
                }
            }
            idleCompletion?.TrySetResult(true);
            if (restartWorkerId != 0L)
            {
                Task.Run(() => ProcessPendingPlaylistReloadCleanupAsync(restartWorkerId)).ObserveFault("ProcessPendingPlaylistReloadCleanupAfterFailure");
            }
        }
    }

    private async Task WaitForPlaylistReloadCleanupReadinessAsync(PlaylistReloadCleanupRequest request)
    {
        if (request.WaitForStartupOperable)
        {
            var operableWaitStopwatch = Stopwatch.StartNew();
            while (!playlistReloadCleanupShutdownRequestedProvider()
                && !playlistReloadCleanupStartupOperableProvider()
                && operableWaitStopwatch.ElapsedMilliseconds < PlaylistReloadCleanupStartupOperableWaitTimeoutMs)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }
        }
        if (playlistReloadCleanupShutdownRequestedProvider())
        {
            return;
        }
        await WaitForPlaylistReloadCleanupBuildReadinessAsync(
            request.WaitForSummaryRefresh,
            request.WaitForDetailRefresh,
            request.RequestedAtTimestamp).ConfigureAwait(false);
        if (!playlistReloadCleanupShutdownRequestedProvider())
        {
            await playlistReloadCleanupDispatcherIdleWaiter().ConfigureAwait(false);
        }
    }

    private async Task WaitForPlaylistReloadCleanupBuildReadinessAsync(
        bool waitForSummaryRefresh,
        bool waitForDetailRefresh,
        long requestedAtTimestamp)
    {
        if (waitForSummaryRefresh)
        {
            await WaitForBuildCompletionAsync(
                () => LastPlaylistSummaryBuildCompletedTimestamp,
                requestedAtTimestamp,
                playlistReloadCleanupShutdownRequestedProvider).ConfigureAwait(false);
        }
        if (waitForDetailRefresh)
        {
            await WaitForBuildCompletionAsync(
                () => LastDetailBuildCompletedTimestamp,
                requestedAtTimestamp,
                playlistReloadCleanupShutdownRequestedProvider).ConfigureAwait(false);
        }
    }

    private void WritePlaylistReloadCleanupLog(string message)
    {
        WritePlaylistReloadLog(message);
    }

    private void WritePlaylistReloadLog(string message)
    {
        playlistReloadLog(message);
    }

    private static PlaylistReloadCleanupOperationKind DeterminePlaylistReloadOperationKind(
        string reason,
        bool fromReloadTables)
    {
        if (string.Equals(reason, "Initialize", StringComparison.Ordinal))
        {
            return PlaylistReloadCleanupOperationKind.StartupFullReload;
        }
        if (fromReloadTables || string.Equals(reason, "ReloadTables", StringComparison.Ordinal))
        {
            return PlaylistReloadCleanupOperationKind.ManualFullReload;
        }
        return PlaylistReloadCleanupOperationKind.None;
    }

    private static string GetPlaylistReloadOperationKindText(PlaylistReloadCleanupOperationKind operationKind)
    {
        return operationKind switch
        {
            PlaylistReloadCleanupOperationKind.StartupFullReload => "startup_full",
            PlaylistReloadCleanupOperationKind.ManualFullReload => "manual_full",
            PlaylistReloadCleanupOperationKind.SinglePlaylistReload => "single",
            _ => "none",
        };
    }

    private static async Task WaitForBuildCompletionAsync(
        Func<long> completedTimestampProvider,
        long requestedAtTimestamp,
        Func<bool> stopRequestedProvider = null)
    {
        var waitStopwatch = Stopwatch.StartNew();
        while ((stopRequestedProvider == null || !stopRequestedProvider())
            && completedTimestampProvider() < requestedAtTimestamp
            && waitStopwatch.ElapsedMilliseconds < PlaylistReloadCleanupBuildWaitTimeoutMs)
        {
            await Task.Delay(50).ConfigureAwait(false);
        }
    }
}

internal enum PlaylistReloadCleanupOperationKind
{
    None,
    StartupFullReload,
    ManualFullReload,
    SinglePlaylistReload
}

internal sealed class PlaylistReloadCleanupRequest
{
    internal long CleanupId;

    internal PlaylistReloadCleanupOperationKind OperationKind;

    internal int TableCount;

    internal bool WaitForStartupOperable;

    internal bool WaitForSummaryRefresh;

    internal bool WaitForDetailRefresh;

    internal bool GcAllowed;

    internal long RequestedAtTimestamp;
}

internal readonly struct PlaylistReloadCleanupSnapshot
{
    internal PlaylistReloadCleanupSnapshot(
        bool summaryAlive,
        int summaryRowCount,
        PlaylistPreviousDetailRowsSnapshot previousDetailRows)
    {
        SummaryAlive = summaryAlive;
        SummaryRowCount = summaryRowCount;
        PreviousDetailRows = previousDetailRows;
    }

    internal bool SummaryAlive { get; }

    internal int SummaryRowCount { get; }

    internal PlaylistPreviousDetailRowsSnapshot PreviousDetailRows { get; }
}
