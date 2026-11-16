using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// playlist open 要求時点の library index readiness を表します。
/// </summary>
internal sealed class PlaylistLibraryIndexReadinessSnapshot
{
    internal string State = "inline";

    internal long BuildElapsedMs;
}

/// <summary>
/// playlist 詳細で使う候補索引の初回準備と取消寿命を所有します。
/// </summary>
public sealed partial class PlaylistWorkspaceViewModel
{
    internal static MainViewUpdateMode ResolvePlaylistColumnSettingMode(PlaylistDetailFilter filterType)
    {
        return filterType == PlaylistDetailFilter.PlaylistNotOwnedFilterSelected
            ? MainViewUpdateMode.PlaylistNotOwnedFilterSelected
            : MainViewUpdateMode.PlaylistFilterSelected;
    }

    private readonly object playlistLibraryIndexSync = new();
    private Task<PlaylistLibraryResolveIndexSnapshot> playlistLibraryIndexPrewarmTask;
    private IPlaylistDetailDataSource playlistLibraryIndexPrewarmSource;
    private CancellationTokenSource playlistLibraryIndexPrewarmCancellation;
    private readonly Func<string, Func<Task>, bool> playlistLibraryIndexPrewarmScheduler;
    private bool playlistLibraryIndexShutdownRequested;

    private void ResetPlaylistLibraryIndexPrewarmForDataSourceChange()
    {
        lock (playlistLibraryIndexSync)
        {
            if (!ReferenceEquals(playlistLibraryIndexPrewarmSource, Volatile.Read(ref detailDataSource)))
            { playlistLibraryIndexPrewarmCancellation?.Cancel(); }
        }
    }

    /// <summary>初回の cold 候補索引を既存schedulerで準備します。予約時のsourceを捕捉し、交換後の古い予約は実行しません。</summary>
    internal void SchedulePlaylistLibraryIndexPrewarm(string reason)
    {
        IPlaylistDetailDataSource source = Volatile.Read(ref detailDataSource);
        if (source == null) { return; }
        if (string.Equals(reason, "initialize_completed", StringComparison.OrdinalIgnoreCase))
        {
            if (!playlistLibraryIndexPrewarmScheduler(reason,
                async () => { await StartPlaylistLibraryIndexPrewarmTask(source, reason).ConfigureAwait(false); }))
            { LogPlaylistLibraryIndex("playlist_library_index_prewarm skipped reason=" + reason + " detail=startup_scheduler_rejected"); }
            return;
        }
        StartPlaylistLibraryIndexPrewarmTask(source, reason);
    }

    private Task<PlaylistLibraryResolveIndexSnapshot> StartPlaylistLibraryIndexPrewarmTask(IPlaylistDetailDataSource source, string reason)
    {
        lock (playlistLibraryIndexSync)
        {
            if (!ReferenceEquals(source, Volatile.Read(ref detailDataSource)) || playlistLibraryIndexShutdownRequested)
            { return Task.FromResult<PlaylistLibraryResolveIndexSnapshot>(null); }
            if (playlistLibraryIndexPrewarmTask != null && !playlistLibraryIndexPrewarmTask.IsCompleted
                && ReferenceEquals(source, playlistLibraryIndexPrewarmSource)
                && playlistLibraryIndexPrewarmCancellation?.IsCancellationRequested != true)
            { return playlistLibraryIndexPrewarmTask; }
            playlistLibraryIndexPrewarmCancellation?.Cancel();
            playlistLibraryIndexPrewarmCancellation = new CancellationTokenSource();
            CancellationToken token = playlistLibraryIndexPrewarmCancellation.Token;
            playlistLibraryIndexPrewarmSource = source;
            playlistLibraryIndexPrewarmTask = Task.Run(() =>
            {
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    PlaylistLibraryResolveIndexSnapshot snapshot = CreatePlaylistLibraryResolveIndexSnapshot(source, token,
                        out bool cacheHit, out int staleRetryCount);
                    LogPlaylistLibraryIndex("playlist_library_index_prewarm completed status=" + (cacheHit ? "cached" : "built")
                        + " chartsByMd5Count=" + snapshot.Md5HashCount + " buildMs=" + snapshot.BuildElapsedMs
                        + " staleRetries=" + staleRetryCount + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
                    return snapshot;
                }
                catch (OperationCanceledException)
                {
                    LogPlaylistLibraryIndex("playlist_library_index_prewarm cancelled reason=" + reason);
                    throw;
                }
                catch (Exception failure)
                {
                    LogPlaylistLibraryIndex("playlist_library_index_prewarm failed reason=" + reason + " exception=" + failure.GetType().Name);
                    throw;
                }
            });
            return playlistLibraryIndexPrewarmTask;
        }
    }

    private static PlaylistLibraryResolveIndexSnapshot CreatePlaylistLibraryResolveIndexSnapshot(
        IPlaylistDetailDataSource dataSource, CancellationToken cancellationToken,
        out bool cacheHit,
        out int staleRetryCount)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PlaylistLibraryResolveIndexSnapshot resolveIndex = dataSource.GetResolveIndexSnapshot(
            cancellationToken,
            out cacheHit,
            out staleRetryCount);
        cancellationToken.ThrowIfCancellationRequested();
        return resolveIndex;
    }

    /// <summary>同じ接続sourceの現在候補版と実prewarm taskだけから表示準備状態を返します。</summary>
    internal PlaylistLibraryIndexReadinessSnapshot CapturePlaylistLibraryIndexReadinessSnapshot()
    {
        IPlaylistDetailDataSource dataSource = Volatile.Read(ref detailDataSource);
        BMSLibrary.PlaylistLibraryResolveIndexRuntimeState modelState = dataSource?.GetResolveIndexRuntimeState();
        Task<PlaylistLibraryResolveIndexSnapshot> prewarmTask = null;
        IPlaylistDetailDataSource prewarmSource;
        lock (playlistLibraryIndexSync)
        {
            prewarmTask = playlistLibraryIndexPrewarmTask;
            prewarmSource = playlistLibraryIndexPrewarmSource;
        }
        if (modelState?.IsCached == true
            && dataSource != null
            && modelState.CatalogRevision == dataSource.CatalogRevision)
        {
            return new PlaylistLibraryIndexReadinessSnapshot
            {
                State = "cached",
                BuildElapsedMs = modelState.BuildElapsedMs
            };
        }
        if (prewarmTask != null && ReferenceEquals(prewarmSource, dataSource))
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
