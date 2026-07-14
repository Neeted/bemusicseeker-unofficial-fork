using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    private const int PlaylistReloadCleanupBuildWaitTimeoutMs = 10000;

    internal async Task WaitForPlaylistReloadCleanupReadinessAsync(
        bool waitForSummaryRefresh,
        bool waitForDetailRefresh,
        long requestedAtTimestamp)
    {
        if (waitForSummaryRefresh)
        {
            await WaitForBuildCompletionAsync(
                () => LastPlaylistSummaryBuildCompletedTimestamp,
                requestedAtTimestamp).ConfigureAwait(false);
        }
        if (waitForDetailRefresh)
        {
            await WaitForBuildCompletionAsync(
                () => LastDetailBuildCompletedTimestamp,
                requestedAtTimestamp).ConfigureAwait(false);
        }
    }

    internal PlaylistReloadCleanupSnapshot CapturePlaylistReloadCleanupSnapshot()
    {
        ObservableCollection<PlaylistSummaryRow> previousSummaryRows = null;
        bool summaryAlive;
        lock (playlistSummaryTransitionLock)
        {
            summaryAlive = previousPlaylistSummaryViewWeakReference != null
                && previousPlaylistSummaryViewWeakReference.TryGetTarget(out previousSummaryRows);
        }
        PlaylistPreviousDetailRowsSnapshot previousDetailRows = CapturePreviousDetailRowsSnapshot();
        return new PlaylistReloadCleanupSnapshot(
            summaryAlive,
            summaryAlive ? previousSummaryRows.Count : 0,
            previousDetailRows);
    }

    private static async Task WaitForBuildCompletionAsync(
        Func<long> completedTimestampProvider,
        long requestedAtTimestamp)
    {
        var waitStopwatch = Stopwatch.StartNew();
        while (completedTimestampProvider() < requestedAtTimestamp
            && waitStopwatch.ElapsedMilliseconds < PlaylistReloadCleanupBuildWaitTimeoutMs)
        {
            await Task.Delay(50).ConfigureAwait(false);
        }
    }
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
