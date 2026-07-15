using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    private readonly Func<string, Func<Task>, bool> playlistReferenceApplyScheduler;

    private readonly object playlistReferenceApplyLock = new();

    private int playlistReferenceApplyRequestedVersion;

    private bool playlistReferenceApplyRunning;

    private string playlistReferenceApplyReason;

    private long playlistReferenceApplyOperationToken;

    private int playlistReferenceApplyLastCompletedVersion;

    internal event EventHandler<PlaylistReferenceApplyQueuedEventArgs> PlaylistReferenceApplyQueued;

    internal event EventHandler<PlaylistReferenceApplyCompletedEventArgs> PlaylistReferenceApplyCompleted;

    internal event EventHandler<PlaylistReferenceApplyPresentationRequestedEventArgs> PlaylistReferenceApplyPresentationRequested;

    internal void QueuePlaylistReferenceApply(string reason, long operationToken)
    {
        if (playlistReloadCleanupShutdownRequestedProvider())
        {
            WritePlaylistReloadLog(
                "playlist_ref_deferred skipped reason=shutdown_requested requestReason="
                + (reason ?? string.Empty));
            return;
        }

        int version;
        bool shouldStartWorker = false;
        string queuedReason;
        long queuedOperationToken;
        lock (playlistReferenceApplyLock)
        {
            version = ++playlistReferenceApplyRequestedVersion;
            playlistReferenceApplyReason = reason ?? string.Empty;
            playlistReferenceApplyOperationToken = operationToken;
            queuedReason = playlistReferenceApplyReason;
            queuedOperationToken = playlistReferenceApplyOperationToken;
            if (!playlistReferenceApplyRunning)
            {
                playlistReferenceApplyRunning = true;
                shouldStartWorker = true;
            }
        }

        PlaylistReferenceApplyQueued?.Invoke(
            this,
            new PlaylistReferenceApplyQueuedEventArgs(
                queuedReason,
                version,
                queuedOperationToken));
        if (!shouldStartWorker)
        {
            return;
        }

        Task Work()
        {
            WorkBody();
            return Task.CompletedTask;
        }

        int rejectedVersion = version;
        string rejectedReason = queuedReason;
        long rejectedOperationToken = queuedOperationToken;
        while (!playlistReferenceApplyScheduler(rejectedReason, Work))
        {
            if (TryCompletePlaylistReferenceApplyWorkerCycle(rejectedVersion))
            {
                PublishPlaylistReferenceApplySkipped(
                    rejectedVersion,
                    rejectedReason,
                    rejectedOperationToken,
                    "startup_scheduler_rejected");
                return;
            }

            PlaylistReferenceApplyRequestSnapshot latest = CapturePlaylistReferenceApplyRequest();
            rejectedVersion = latest.Version;
            rejectedReason = latest.Reason;
            rejectedOperationToken = latest.OperationToken;
        }

        void WorkBody()
        {
            while (true)
            {
                PlaylistReferenceApplyRequestSnapshot request = CapturePlaylistReferenceApplyRequest();
                DateTime startedAt = DateTime.UtcNow;
                bool succeeded = false;
                try
                {
                    BMSPlaylist playlists = GetPlaylistStore();
                    playlists.EnsureAllPlaylistEntriesLoadedAsync("playlist_ref_deferred")
                        .GetAwaiter()
                        .GetResult();
                    List<BMSTable> tables = [];
                    playlists.AcquireReaderLockBMSTables();
                    try
                    {
                        IEnumerable<BMSTable> sourceTables = PlaylistTreeTables ?? Enumerable.Empty<BMSTable>();
                        tables = [.. sourceTables.Where(table => table != null)];
                    }
                    finally
                    {
                        playlists.FreeReaderLockBMSTables();
                    }
                    WritePlaylistReloadLog(
                        "playlist_ref_deferred run version="
                        + request.Version
                        + " tableCount="
                        + tables.Count);
                    GetPlaylistLibrary().SynchronizeReferenceBMSTables(tables);
                    PlaylistReferenceSortInvalidationRequested?.Invoke(this, EventArgs.Empty);
                    int presentationRequestVersion = request.Version;
                    dispatchPresentation(() =>
                    {
                        PlaylistReferenceApplyPresentationRequested?.Invoke(
                            this,
                            new PlaylistReferenceApplyPresentationRequestedEventArgs(
                                request.Reason,
                                presentationRequestVersion,
                                request.OperationToken));
                    });
                    WritePlaylistReloadLog(
                        "playlist_ref_deferred done reason="
                        + request.Reason
                        + " version="
                        + request.Version
                        + " elapsedMs="
                        + (long)(DateTime.UtcNow - startedAt).TotalMilliseconds
                        + " presentation=queued");
                    succeeded = true;
                }
                catch (Exception ex)
                {
                    WritePlaylistReloadLog(
                        "playlist_ref_deferred failed reason="
                        + request.Reason
                        + " version="
                        + request.Version
                        + " elapsedMs="
                        + (long)(DateTime.UtcNow - startedAt).TotalMilliseconds
                        + " message="
                        + ex.Message);
                }

                lock (playlistReferenceApplyLock)
                {
                    playlistReferenceApplyLastCompletedVersion = Math.Max(
                        playlistReferenceApplyLastCompletedVersion,
                        request.Version);
                }

                PlaylistReferenceApplyCompleted?.Invoke(
                    this,
                    new PlaylistReferenceApplyCompletedEventArgs(
                        request.Reason,
                        request.Version,
                        request.OperationToken,
                        succeeded,
                        wasSkipped: false));
                if (TryCompletePlaylistReferenceApplyWorkerCycle(request.Version))
                {
                    break;
                }
            }
        }
    }

    internal bool IsPlaylistReferenceApplyIdle
    {
        get
        {
            lock (playlistReferenceApplyLock)
            {
                return !playlistReferenceApplyRunning;
            }
        }
    }

    internal string DescribePlaylistReferenceApplyWaitState()
    {
        lock (playlistReferenceApplyLock)
        {
            return "playlistReferenceApplyRunning="
                + playlistReferenceApplyRunning.ToString().ToLowerInvariant()
                + " requestedVersion="
                + playlistReferenceApplyRequestedVersion;
        }
    }

    internal int PlaylistReferenceApplyLastCompletedVersion
    {
        get
        {
            lock (playlistReferenceApplyLock)
            {
                return playlistReferenceApplyLastCompletedVersion;
            }
        }
    }

    internal void DiscardPlaylistReferenceApplyForShutdown(string reason)
    {
        PlaylistReferenceApplyRequestSnapshot request;
        lock (playlistReferenceApplyLock)
        {
            if (!playlistReferenceApplyRunning)
            {
                return;
            }
            playlistReferenceApplyRunning = false;
            request = CapturePlaylistReferenceApplyRequestUnsafe();
            playlistReferenceApplyLastCompletedVersion = Math.Max(
                playlistReferenceApplyLastCompletedVersion,
                request.Version);
        }
        PlaylistReferenceApplyCompleted?.Invoke(
            this,
            new PlaylistReferenceApplyCompletedEventArgs(
                request.Reason,
                request.Version,
                request.OperationToken,
                succeeded: false,
                wasSkipped: true));
        WritePlaylistReloadLog(
            "playlist_ref_deferred discarded reason=shutdown_requested requestReason="
            + (reason ?? string.Empty)
            + " version="
            + request.Version);
    }

    private void PublishPlaylistReferenceApplySkipped(
        int version,
        string reason,
        long operationToken,
        string shutdownReason)
    {
        lock (playlistReferenceApplyLock)
        {
            playlistReferenceApplyLastCompletedVersion = Math.Max(
                playlistReferenceApplyLastCompletedVersion,
                version);
        }
        PlaylistReferenceApplyCompleted?.Invoke(
            this,
            new PlaylistReferenceApplyCompletedEventArgs(
                reason ?? string.Empty,
                version,
                operationToken,
                succeeded: false,
                wasSkipped: true));
        WritePlaylistReloadLog(
            "playlist_ref_deferred skipped reason="
            + (shutdownReason ?? "shutdown_requested")
            + " requestReason="
            + (reason ?? string.Empty)
            + " version="
            + version);
    }

    private PlaylistReferenceApplyRequestSnapshot CapturePlaylistReferenceApplyRequest()
    {
        lock (playlistReferenceApplyLock)
        {
            return CapturePlaylistReferenceApplyRequestUnsafe();
        }
    }

    private PlaylistReferenceApplyRequestSnapshot CapturePlaylistReferenceApplyRequestUnsafe()
    {
        return new PlaylistReferenceApplyRequestSnapshot(
            playlistReferenceApplyRequestedVersion,
            playlistReferenceApplyReason ?? string.Empty,
            playlistReferenceApplyOperationToken);
    }

    private bool TryCompletePlaylistReferenceApplyWorkerCycle(int version)
    {
        lock (playlistReferenceApplyLock)
        {
            if (playlistReferenceApplyRequestedVersion != version)
            {
                return false;
            }
            playlistReferenceApplyRunning = false;
            return true;
        }
    }

    private readonly struct PlaylistReferenceApplyRequestSnapshot
    {
        internal PlaylistReferenceApplyRequestSnapshot(
            int version,
            string reason,
            long operationToken)
        {
            Version = version;
            Reason = reason;
            OperationToken = operationToken;
        }

        internal int Version { get; }

        internal string Reason { get; }

        internal long OperationToken { get; }
    }
}

internal sealed class PlaylistReferenceApplyQueuedEventArgs : EventArgs
{
    internal PlaylistReferenceApplyQueuedEventArgs(
        string reason,
        int version,
        long operationToken)
    {
        Reason = reason;
        Version = version;
        OperationToken = operationToken;
    }

    internal string Reason { get; }

    internal int Version { get; }

    internal long OperationToken { get; }
}

internal sealed class PlaylistReferenceApplyCompletedEventArgs : EventArgs
{
    internal PlaylistReferenceApplyCompletedEventArgs(
        string reason,
        int version,
        long operationToken,
        bool succeeded,
        bool wasSkipped)
    {
        Reason = reason;
        Version = version;
        OperationToken = operationToken;
        Succeeded = succeeded;
        WasSkipped = wasSkipped;
    }

    internal string Reason { get; }

    internal int Version { get; }

    internal long OperationToken { get; }

    internal bool Succeeded { get; }

    internal bool WasSkipped { get; }
}

internal sealed class PlaylistReferenceApplyPresentationRequestedEventArgs : EventArgs
{
    internal PlaylistReferenceApplyPresentationRequestedEventArgs(
        string reason,
        int version,
        long operationToken)
    {
        Reason = reason;
        Version = version;
        OperationToken = operationToken;
    }

    internal string Reason { get; }

    internal int Version { get; }

    internal long OperationToken { get; }
}
