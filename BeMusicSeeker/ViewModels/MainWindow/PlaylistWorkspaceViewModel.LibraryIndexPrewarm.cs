using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// playlist source build で再利用するライブラリ索引 snapshot です。
/// </summary>
internal sealed class PlaylistLibraryIndexSnapshot
{
    internal long Version;

    internal long BuildElapsedMs;

    internal PlaylistLibraryResolveIndexSnapshot ResolveIndex = PlaylistLibraryResolveIndexSnapshot.Empty;
}

/// <summary>
/// playlist open 要求時点の library index readiness を表します。
/// </summary>
internal sealed class PlaylistLibraryIndexReadinessSnapshot
{
    internal string State = "inline";

    internal long BuildElapsedMs;
}

/// <summary>
/// Owns the playlist library resolve-index prewarm lifecycle used by the playlist detail workflow.
/// </summary>
public sealed partial class PlaylistWorkspaceViewModel
{
    internal static MainViewUpdateMode ResolvePlaylistColumnSettingMode(PlaylistDetailFilter filterType)
    {
        return filterType == PlaylistDetailFilter.PlaylistNotOwnedFilterSelected
            ? MainViewUpdateMode.PlaylistNotOwnedFilterSelected
            : MainViewUpdateMode.PlaylistFilterSelected;
    }

    private const int PlaylistLibraryIndexPrewarmDebounceMs = 500;

    private readonly object playlistLibraryIndexSync = new();

    private long playlistLibraryIndexVersion;

    private Task<PlaylistLibraryIndexSnapshot> playlistLibraryIndexPrewarmTask;

    private long playlistLibraryIndexPrewarmVersion;

    private CancellationTokenSource playlistLibraryIndexPrewarmCancellation;

    private int duplicateRefreshPriorityDepth;

    private bool deferredPlaylistLibraryIndexPrewarmForDuplicateRefresh;

    private long deferredPlaylistLibraryIndexPrewarmVersion;

    private string deferredPlaylistLibraryIndexPrewarmReason;

    private readonly Func<string, Func<Task>, bool> playlistLibraryIndexPrewarmScheduler;

    private bool playlistLibraryIndexShutdownRequested;

    private void ResetPlaylistLibraryIndexPrewarmForDataSourceChange()
    {
        lock (playlistLibraryIndexSync)
        {
            playlistLibraryIndexVersion++;
            playlistLibraryIndexPrewarmCancellation?.Cancel();
            deferredPlaylistLibraryIndexPrewarmForDuplicateRefresh = false;
            deferredPlaylistLibraryIndexPrewarmVersion = 0;
            deferredPlaylistLibraryIndexPrewarmReason = null;
        }
    }

    internal void InvalidatePlaylistLibraryIndexSnapshot(
        string reason,
        bool startupReadyOperable,
        MainViewUpdateMode currentTreeViewMode)
    {
        long nextVersion;
        lock (playlistLibraryIndexSync)
        {
            nextVersion = ++playlistLibraryIndexVersion;
        }
        LogPlaylistLibraryIndex("playlist_library_index invalidated version=" + nextVersion + " reason=" + reason);
        if (!startupReadyOperable)
        {
            LogPlaylistLibraryIndex("playlist_library_index_prewarm deferred_until_operable version=" + nextVersion + " reason=" + reason);
            return;
        }
        if (TryDeferPlaylistLibraryIndexPrewarmForDuplicateRefresh(
            nextVersion,
            reason,
            startupReadyOperable,
            currentTreeViewMode))
        {
            return;
        }
        SchedulePlaylistLibraryIndexPrewarm(nextVersion, reason);
    }

    internal void SchedulePlaylistLibraryIndexPrewarm(string reason)
    {
        long targetVersion;
        lock (playlistLibraryIndexSync)
        {
            targetVersion = playlistLibraryIndexVersion;
        }
        SchedulePlaylistLibraryIndexPrewarm(targetVersion, reason);
    }

    private void SchedulePlaylistLibraryIndexPrewarm(long targetVersion, string reason)
    {
        if (Volatile.Read(ref detailDataSource) == null)
        {
            return;
        }
        bool debounce = ShouldDebouncePlaylistLibraryIndexPrewarm(reason);
        int delayMs = debounce ? PlaylistLibraryIndexPrewarmDebounceMs : 0;
        if (!debounce && string.Equals(reason, "initialize_completed", StringComparison.OrdinalIgnoreCase))
        {
            LogPlaylistLibraryIndex("playlist_library_index_prewarm queued version=" + targetVersion + " reason=" + reason + " debounceMs=" + delayMs + " source=scheduler");
            if (playlistLibraryIndexPrewarmScheduler(
                reason,
                async delegate
                {
                    Task<PlaylistLibraryIndexSnapshot> task = StartPlaylistLibraryIndexPrewarmTask(targetVersion, reason, delayMs, "scheduler");
                    if (task != null)
                    {
                        await task.ConfigureAwait(false);
                    }
                }))
            {
                return;
            }
            LogPlaylistLibraryIndex("playlist_library_index_prewarm skipped version=" + targetVersion + " reason=" + reason + " detail=startup_scheduler_rejected");
            return;
        }
        StartPlaylistLibraryIndexPrewarmTask(targetVersion, reason, delayMs, debounce ? "debounce" : "inline");
    }

    private Task<PlaylistLibraryIndexSnapshot> StartPlaylistLibraryIndexPrewarmTask(
        long targetVersion,
        string reason,
        int delayMs,
        string source)
    {
        lock (playlistLibraryIndexSync)
        {
            if (playlistLibraryIndexVersion != targetVersion)
            {
                LogPlaylistLibraryIndex("playlist_library_index_prewarm stale_skipped version=" + targetVersion + " currentVersion=" + playlistLibraryIndexVersion + " reason=" + reason + " source=" + source);
                return Task.FromResult<PlaylistLibraryIndexSnapshot>(null);
            }
            if (playlistLibraryIndexShutdownRequested)
            {
                LogPlaylistLibraryIndex("playlist_library_index_prewarm skipped version=" + targetVersion + " reason=" + reason + " source=" + source + " detail=shutdown_requested");
                return Task.FromResult<PlaylistLibraryIndexSnapshot>(null);
            }
            if (playlistLibraryIndexPrewarmTask != null
                && !playlistLibraryIndexPrewarmTask.IsCompleted
                && playlistLibraryIndexPrewarmVersion == targetVersion
                && playlistLibraryIndexPrewarmCancellation?.IsCancellationRequested != true)
            {
                return playlistLibraryIndexPrewarmTask;
            }
            if (playlistLibraryIndexPrewarmTask != null && !playlistLibraryIndexPrewarmTask.IsCompleted)
            {
                playlistLibraryIndexPrewarmCancellation?.Cancel();
                LogPlaylistLibraryIndex("playlist_library_index_prewarm debounced oldVersion=" + playlistLibraryIndexPrewarmVersion + " newVersion=" + targetVersion + " reason=" + reason);
            }
            playlistLibraryIndexPrewarmCancellation = new CancellationTokenSource();
            CancellationToken prewarmToken = playlistLibraryIndexPrewarmCancellation.Token;
            playlistLibraryIndexPrewarmVersion = targetVersion;
            LogPlaylistLibraryIndex("playlist_library_index_prewarm scheduled version=" + targetVersion + " reason=" + reason + " debounceMs=" + delayMs + " source=" + source);
            playlistLibraryIndexPrewarmTask = Task.Run(async delegate
            {
                var stopwatch = Stopwatch.StartNew();
                LogPlaylistLibraryIndex("playlist_library_index_prewarm started version=" + targetVersion + " reason=" + reason + " source=" + source);
                try
                {
                    if (delayMs > 0)
                    {
                        await Task.Delay(delayMs, prewarmToken).ConfigureAwait(false);
                    }
                    prewarmToken.ThrowIfCancellationRequested();
                    PlaylistLibraryIndexSnapshot snapshot = CreatePlaylistLibraryIndexSnapshot(prewarmToken, targetVersion, out bool cacheHit, out int staleRetryCount);
                    LogPlaylistLibraryIndex("playlist_library_index_prewarm completed version=" + targetVersion + " status=" + (cacheHit ? "cached" : "built") + " chartsByMd5Count=" + (snapshot.ResolveIndex?.ChartsByMd5.Count ?? 0) + " buildMs=" + snapshot.BuildElapsedMs + " staleRetries=" + staleRetryCount + " elapsedMs=" + stopwatch.ElapsedMilliseconds + " source=" + source);
                    return snapshot;
                }
                catch (OperationCanceledException)
                {
                    LogPlaylistLibraryIndex("playlist_library_index_prewarm cancelled version=" + targetVersion + " elapsedMs=" + stopwatch.ElapsedMilliseconds + " source=" + source);
                    throw;
                }
                catch (Exception ex)
                {
                    LogPlaylistLibraryIndex("playlist_library_index_prewarm failed version=" + targetVersion + " elapsedMs=" + stopwatch.ElapsedMilliseconds + " exception=" + ex.GetType().Name + " source=" + source);
                    throw;
                }
            });
            return playlistLibraryIndexPrewarmTask;
        }
    }

    private static bool ShouldDebouncePlaylistLibraryIndexPrewarm(string reason)
    {
        return string.Equals(reason, "library_charts_changed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(reason, "library_bmsons_changed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(reason, "owned_collection_changed", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldDeferPlaylistLibraryIndexPrewarmForDuplicateRefresh(
        string reason,
        bool startupReadyOperable,
        bool duplicateRefreshPriorityActive,
        MainViewUpdateMode currentTreeViewMode)
    {
        return startupReadyOperable
            && duplicateRefreshPriorityActive
            && currentTreeViewMode == MainViewUpdateMode.DuplicateFilterSelected
            && string.Equals(reason, "owned_collection_changed", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ShouldDeferPlaylistLibraryIndexPrewarmForDuplicateRefresh(
        string reason,
        bool startupReadyOperable,
        bool duplicateRefreshPriorityActive,
        int currentTreeViewMode)
    {
        return ShouldDeferPlaylistLibraryIndexPrewarmForDuplicateRefresh(
            reason,
            startupReadyOperable,
            duplicateRefreshPriorityActive,
            (MainViewUpdateMode)currentTreeViewMode);
    }

    private bool TryDeferPlaylistLibraryIndexPrewarmForDuplicateRefresh(
        long targetVersion,
        string reason,
        bool startupReadyOperable,
        MainViewUpdateMode currentTreeViewMode)
    {
        if (!ShouldDeferPlaylistLibraryIndexPrewarmForDuplicateRefresh(
            reason,
            startupReadyOperable,
            duplicateRefreshPriorityActive: duplicateRefreshPriorityDepth > 0,
            currentTreeViewMode))
        {
            return false;
        }

        lock (playlistLibraryIndexSync)
        {
            if (!startupReadyOperable
                || duplicateRefreshPriorityDepth <= 0
                || currentTreeViewMode != MainViewUpdateMode.DuplicateFilterSelected
                || !string.Equals(reason, "owned_collection_changed", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            deferredPlaylistLibraryIndexPrewarmForDuplicateRefresh = true;
            deferredPlaylistLibraryIndexPrewarmVersion = targetVersion;
            deferredPlaylistLibraryIndexPrewarmReason = reason;
            if (playlistLibraryIndexPrewarmTask != null && !playlistLibraryIndexPrewarmTask.IsCompleted)
            {
                playlistLibraryIndexPrewarmCancellation?.Cancel();
                LogPlaylistLibraryIndex("playlist_library_index_prewarm cancelled_for_duplicate_refresh oldVersion="
                    + playlistLibraryIndexPrewarmVersion
                    + " newVersion=" + targetVersion
                    + " reason=" + reason);
            }
        }

        LogPlaylistLibraryIndex("playlist_library_index_prewarm deferred_for_duplicate_refresh version="
            + targetVersion
            + " reason=" + reason);
        return true;
    }

    internal void BeginDuplicateRefreshPriorityWindow(string reason)
    {
        int depth;
        lock (playlistLibraryIndexSync)
        {
            duplicateRefreshPriorityDepth++;
            depth = duplicateRefreshPriorityDepth;
        }
        LogPlaylistLibraryIndex("duplicate_refresh_priority begin depth=" + depth + " reason=" + (reason ?? string.Empty));
    }

    internal void ReleaseDuplicateRefreshPriorityWindow(string reason)
    {
        long targetVersion = 0;
        string prewarmReason = null;
        int depth;
        bool releasePrewarm = false;
        bool hadActiveWindow = false;
        lock (playlistLibraryIndexSync)
        {
            if (duplicateRefreshPriorityDepth > 0)
            {
                hadActiveWindow = true;
                duplicateRefreshPriorityDepth--;
            }
            depth = duplicateRefreshPriorityDepth;
            if (duplicateRefreshPriorityDepth == 0 && deferredPlaylistLibraryIndexPrewarmForDuplicateRefresh)
            {
                releasePrewarm = true;
                targetVersion = deferredPlaylistLibraryIndexPrewarmVersion;
                prewarmReason = deferredPlaylistLibraryIndexPrewarmReason;
                deferredPlaylistLibraryIndexPrewarmForDuplicateRefresh = false;
                deferredPlaylistLibraryIndexPrewarmVersion = 0;
                deferredPlaylistLibraryIndexPrewarmReason = null;
            }
        }

        if (!hadActiveWindow && !releasePrewarm)
        {
            return;
        }

        LogPlaylistLibraryIndex("duplicate_refresh_priority end depth=" + depth + " reason=" + (reason ?? string.Empty));
        if (!releasePrewarm)
        {
            return;
        }

        LogPlaylistLibraryIndex("playlist_library_index_prewarm released_after_duplicate_refresh version="
            + targetVersion
            + " reason=" + prewarmReason
            + " releaseReason=" + (reason ?? string.Empty));
        SchedulePlaylistLibraryIndexPrewarm(targetVersion, prewarmReason);
    }

    private PlaylistLibraryIndexSnapshot CreatePlaylistLibraryIndexSnapshot(
        CancellationToken cancellationToken,
        long targetVersion,
        out bool cacheHit,
        out int staleRetryCount)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IPlaylistDetailDataSource dataSource = Volatile.Read(ref detailDataSource)
            ?? throw new InvalidOperationException("Playlist detail data source is not attached.");
        PlaylistLibraryResolveIndexSnapshot resolveIndex = dataSource.GetResolveIndexSnapshot(
            cancellationToken,
            out cacheHit,
            out staleRetryCount);
        cancellationToken.ThrowIfCancellationRequested();
        return new PlaylistLibraryIndexSnapshot
        {
            Version = targetVersion,
            BuildElapsedMs = resolveIndex.BuildElapsedMs,
            ResolveIndex = resolveIndex
        };
    }

    internal PlaylistLibraryIndexReadinessSnapshot CapturePlaylistLibraryIndexReadinessSnapshot()
    {
        IPlaylistDetailDataSource dataSource = Volatile.Read(ref detailDataSource);
        BMSLibrary.PlaylistLibraryResolveIndexRuntimeState modelState = dataSource?.GetResolveIndexRuntimeState();
        Task<PlaylistLibraryIndexSnapshot> prewarmTask = null;
        long currentVersion = 0L;
        long prewarmVersion = 0L;
        lock (playlistLibraryIndexSync)
        {
            prewarmTask = playlistLibraryIndexPrewarmTask;
            currentVersion = playlistLibraryIndexVersion;
            prewarmVersion = playlistLibraryIndexPrewarmVersion;
        }
        if (modelState?.IsCached == true
            && dataSource != null
            && modelState.OwnedCollectionVersion == dataSource.OwnedChartCollectionVersion)
        {
            return new PlaylistLibraryIndexReadinessSnapshot
            {
                State = "cached",
                BuildElapsedMs = modelState.BuildElapsedMs
            };
        }
        if (prewarmTask != null && prewarmVersion == currentVersion)
        {
            if (prewarmTask.IsCompleted)
            {
                return new PlaylistLibraryIndexReadinessSnapshot
                {
                    State = "inline",
                    BuildElapsedMs = 0L
                };
            }
            return new PlaylistLibraryIndexReadinessSnapshot
            {
                State = "prewarmed",
                BuildElapsedMs = 0L
            };
        }
        return new PlaylistLibraryIndexReadinessSnapshot
        {
            State = "inline",
            BuildElapsedMs = 0L
        };
    }

    internal Task GetPlaylistLibraryIndexPrewarmTask()
    {
        lock (playlistLibraryIndexSync)
        {
            return playlistLibraryIndexPrewarmTask;
        }
    }

    internal void CancelPlaylistLibraryIndexPrewarmForShutdown()
    {
        lock (playlistLibraryIndexSync)
        {
            playlistLibraryIndexPrewarmCancellation?.Cancel();
        }
    }

    internal void MarkPlaylistLibraryIndexShutdownRequested()
    {
        lock (playlistLibraryIndexSync)
        {
            playlistLibraryIndexShutdownRequested = true;
            playlistLibraryIndexPrewarmCancellation?.Cancel();
        }
    }

    private void LogPlaylistLibraryIndex(string message)
    {
        detailRetentionLog(message);
    }
}
