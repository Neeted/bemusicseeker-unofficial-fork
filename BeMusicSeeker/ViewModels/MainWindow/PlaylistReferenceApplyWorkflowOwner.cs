using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Immutable queue input for one deferred playlist-reference apply operation.
/// </summary>
internal sealed class PlaylistReferenceApplyQueueRequest
{
    /// <summary>
    /// Initializes a queue request with the reason and shell operation token to preserve.
    /// </summary>
    /// <param name="reason">The stable operation reason.</param>
    /// <param name="operationToken">The shell startup-progress operation token.</param>
    internal PlaylistReferenceApplyQueueRequest(string reason, long operationToken)
    {
        Reason = reason ?? string.Empty;
        OperationToken = operationToken;
    }

    /// <summary>
    /// Gets the stable operation reason.
    /// </summary>
    internal string Reason { get; }

    /// <summary>
    /// Gets the shell startup-progress operation token.
    /// </summary>
    internal long OperationToken { get; }
}

/// <summary>
/// Owns deferred playlist-reference hydration, snapshotting, and live-index application.
/// </summary>
internal sealed class PlaylistReferenceApplyWorkflowOwner
{
    private readonly Func<string, Func<Task>, bool> scheduler;

    private readonly Action<Action> dispatchPresentation;

    private readonly Func<bool> shutdownRequestedProvider;

    private readonly Action<string> playlistReloadLog;

    private readonly object stateSyncRoot = new();

    private readonly object contextSyncRoot = new();

    private int requestedVersion;

    private bool running;

    private string requestedReason;

    private long requestedOperationToken;

    private int lastCompletedVersion;

    private BMSPlaylist attachedStore;

    private BMSLibrary attachedLibrary;

    private long attachedContextGeneration;

    internal event EventHandler<PlaylistReferenceApplyQueuedEventArgs> Queued;

    internal event EventHandler<PlaylistReferenceApplyCompletedEventArgs> Completed;

    internal event EventHandler<PlaylistReferenceApplyPresentationRequestedEventArgs> PresentationRequested;

    internal event EventHandler<PlaylistReferenceAppliedEventArgs> ReferenceApplied;

    internal PlaylistReferenceApplyWorkflowOwner(
        Func<string, Func<Task>, bool> scheduler,
        Action<Action> dispatchPresentation,
        Func<bool> shutdownRequestedProvider,
        Action<string> playlistReloadLog)
    {
        this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        this.dispatchPresentation = dispatchPresentation ?? throw new ArgumentNullException(nameof(dispatchPresentation));
        this.shutdownRequestedProvider = shutdownRequestedProvider ?? throw new ArgumentNullException(nameof(shutdownRequestedProvider));
        this.playlistReloadLog = playlistReloadLog ?? throw new ArgumentNullException(nameof(playlistReloadLog));
    }

    internal void AttachContext(BMSPlaylist store, BMSLibrary library, long contextGeneration)
    {
        lock (contextSyncRoot)
        {
            attachedStore = store;
            attachedLibrary = library;
            attachedContextGeneration = contextGeneration;
        }
    }

    internal void Queue(string reason, long operationToken)
    {
        if (shutdownRequestedProvider())
        {
            playlistReloadLog(
                "playlist_ref_deferred skipped reason=shutdown_requested requestReason="
                + (reason ?? string.Empty));
            return;
        }

        int version;
        bool shouldStartWorker = false;
        string queuedReason;
        long queuedOperationToken;
        lock (stateSyncRoot)
        {
            version = ++requestedVersion;
            requestedReason = reason ?? string.Empty;
            requestedOperationToken = operationToken;
            queuedReason = requestedReason;
            queuedOperationToken = requestedOperationToken;
            if (!running)
            {
                running = true;
                shouldStartWorker = true;
            }
        }

        Queued?.Invoke(
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
        while (!scheduler(rejectedReason, Work))
        {
            if (TryCompleteWorkerCycle(rejectedVersion))
            {
                PublishSkipped(
                    rejectedVersion,
                    rejectedReason,
                    rejectedOperationToken,
                    "startup_scheduler_rejected");
                return;
            }

            PlaylistReferenceApplyRequestSnapshot latest = CaptureRequest();
            rejectedVersion = latest.Version;
            rejectedReason = latest.Reason;
            rejectedOperationToken = latest.OperationToken;
        }

        void WorkBody()
        {
            while (true)
            {
                PlaylistReferenceApplyRequestSnapshot request = CaptureRequest();
                DateTime startedAt = DateTime.UtcNow;
                bool succeeded = false;
                try
                {
                    PlaylistReferenceApplyContext context = CaptureContext();
                    context.Store.EnsureAllPlaylistEntriesLoadedAsync("playlist_ref_deferred")
                        .GetAwaiter()
                        .GetResult();
                    while (true)
                    {
                        List<BMSTable> tables;
                        context.Store.AcquireReaderLockBMSTables();
                        try
                        {
                            tables = [.. (context.Store.BMSTables ?? Enumerable.Empty<BMSTable>())
                                .Where(table => table != null)];
                        }
                        finally
                        {
                            context.Store.FreeReaderLockBMSTables();
                        }
                        BmsLibraryPlaylistReferenceOwner.PlaylistReferenceSynchronizationPlan synchronizationPlan =
                            context.Library.PrepareReferenceBMSTableSynchronization(tables);
                        lock (contextSyncRoot)
                        {
                            EnsureCurrentContext(context);
                            if (context.Library.TryCommitReferenceBMSTableSynchronization(synchronizationPlan))
                            {
                                break;
                            }
                        }
                    }

                    PublishReferenceApplied(request.Reason, request.Version, request.OperationToken);
                    int presentationRequestVersion = request.Version;
                    dispatchPresentation(() =>
                    {
                        if (!IsCurrentContext(context))
                        {
                            return;
                        }
                        PresentationRequested?.Invoke(
                            this,
                            new PlaylistReferenceApplyPresentationRequestedEventArgs(
                                request.Reason,
                                presentationRequestVersion,
                                request.OperationToken));
                    });
                    playlistReloadLog(
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
                    playlistReloadLog(
                        "playlist_ref_deferred failed reason="
                        + request.Reason
                        + " version="
                        + request.Version
                        + " elapsedMs="
                        + (long)(DateTime.UtcNow - startedAt).TotalMilliseconds
                        + " message="
                        + ex.Message);
                }

                lock (stateSyncRoot)
                {
                    lastCompletedVersion = Math.Max(lastCompletedVersion, request.Version);
                }

                Completed?.Invoke(
                    this,
                    new PlaylistReferenceApplyCompletedEventArgs(
                        request.Reason,
                        request.Version,
                        request.OperationToken,
                        succeeded,
                        wasSkipped: false));
                if (TryCompleteWorkerCycle(request.Version))
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Queues a deferred playlist-reference apply from one immutable typed request.
    /// </summary>
    /// <param name="request">The reason and operation token to preserve.</param>
    internal void Queue(PlaylistReferenceApplyQueueRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        Queue(request.Reason, request.OperationToken);
    }

    internal void ApplyHydrationReceipt(
        BMSPlaylist sourceStore,
        long sourceContextGeneration,
        PlaylistEntriesHydrationOwner.PlaylistEntriesHydrationReceipt receipt)
    {
        if (receipt == null)
        {
            return;
        }

        lock (contextSyncRoot)
        {
            if (!ReferenceEquals(attachedStore, sourceStore)
                || attachedContextGeneration != sourceContextGeneration
                || attachedLibrary == null)
            {
                return;
            }
        }
        if (!sourceStore.IsPlaylistEntriesHydrationReceiptCurrent(receipt))
        {
            return;
        }

        // The hydration receipt only wakes the canonical reference-apply worker. That worker
        // snapshots the current store after it starts, so a reload/edit that races this
        // notification cannot apply an older receipt after the newer playlist state.
        Queue(receipt.Reason, operationToken: 0L);
    }

    internal bool IsIdle
    {
        get
        {
            lock (stateSyncRoot)
            {
                return !running;
            }
        }
    }

    internal string DescribeWaitState()
    {
        lock (stateSyncRoot)
        {
            return "playlistReferenceApplyRunning="
                + running.ToString().ToLowerInvariant()
                + " requestedVersion="
                + requestedVersion;
        }
    }

    internal int LastCompletedVersion
    {
        get
        {
            lock (stateSyncRoot)
            {
                return lastCompletedVersion;
            }
        }
    }

    internal void DiscardForShutdown(string reason)
    {
        PlaylistReferenceApplyRequestSnapshot request;
        lock (stateSyncRoot)
        {
            if (!running)
            {
                return;
            }
            running = false;
            request = CaptureRequestUnsafe();
            lastCompletedVersion = Math.Max(lastCompletedVersion, request.Version);
        }
        Completed?.Invoke(
            this,
            new PlaylistReferenceApplyCompletedEventArgs(
                request.Reason,
                request.Version,
                request.OperationToken,
                succeeded: false,
                wasSkipped: true));
        playlistReloadLog(
            "playlist_ref_deferred discarded reason=shutdown_requested requestReason="
            + (reason ?? string.Empty)
            + " version="
            + request.Version);
    }

    private void PublishSkipped(
        int version,
        string reason,
        long operationToken,
        string shutdownReason)
    {
        lock (stateSyncRoot)
        {
            lastCompletedVersion = Math.Max(lastCompletedVersion, version);
        }
        Completed?.Invoke(
            this,
            new PlaylistReferenceApplyCompletedEventArgs(
                reason ?? string.Empty,
                version,
                operationToken,
                succeeded: false,
                wasSkipped: true));
        playlistReloadLog(
            "playlist_ref_deferred skipped reason="
            + (shutdownReason ?? "shutdown_requested")
            + " requestReason="
            + (reason ?? string.Empty)
            + " version="
            + version);
    }

    private void PublishReferenceApplied(string reason, int version, long operationToken)
    {
        ReferenceApplied?.Invoke(
            this,
            new PlaylistReferenceAppliedEventArgs(
                reason,
                version,
                operationToken));
    }

    private PlaylistReferenceApplyRequestSnapshot CaptureRequest()
    {
        lock (stateSyncRoot)
        {
            return CaptureRequestUnsafe();
        }
    }

    private PlaylistReferenceApplyRequestSnapshot CaptureRequestUnsafe()
    {
        return new PlaylistReferenceApplyRequestSnapshot(
            requestedVersion,
            requestedReason ?? string.Empty,
            requestedOperationToken);
    }

    private bool TryCompleteWorkerCycle(int version)
    {
        lock (stateSyncRoot)
        {
            if (requestedVersion != version)
            {
                return false;
            }
            running = false;
            return true;
        }
    }

    private PlaylistReferenceApplyContext CaptureContext()
    {
        lock (contextSyncRoot)
        {
            if (attachedStore == null || attachedLibrary == null)
            {
                throw new InvalidOperationException("Playlist reference apply context is not attached.");
            }
            return new PlaylistReferenceApplyContext(
                attachedStore,
                attachedLibrary,
                attachedContextGeneration);
        }
    }

    private void EnsureCurrentContext(PlaylistReferenceApplyContext context)
    {
        if (!ReferenceEquals(attachedStore, context.Store)
            || !ReferenceEquals(attachedLibrary, context.Library)
            || attachedContextGeneration != context.Generation)
        {
            throw new InvalidOperationException("Playlist reference apply context changed while applying.");
        }
    }

    private bool IsCurrentContext(PlaylistReferenceApplyContext context)
    {
        lock (contextSyncRoot)
        {
            return ReferenceEquals(attachedStore, context.Store)
                && ReferenceEquals(attachedLibrary, context.Library)
                && attachedContextGeneration == context.Generation;
        }
    }

    private bool IsCurrentContext(BMSPlaylist store, long generation)
    {
        lock (contextSyncRoot)
        {
            return ReferenceEquals(attachedStore, store)
                && attachedContextGeneration == generation;
        }
    }

    private readonly struct PlaylistReferenceApplyContext
    {
        internal PlaylistReferenceApplyContext(BMSPlaylist store, BMSLibrary library, long generation)
        {
            Store = store;
            Library = library;
            Generation = generation;
        }

        internal BMSPlaylist Store { get; }

        internal BMSLibrary Library { get; }

        internal long Generation { get; }
    }

    private readonly struct PlaylistReferenceApplyRequestSnapshot
    {
        internal PlaylistReferenceApplyRequestSnapshot(int version, string reason, long operationToken)
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
    internal PlaylistReferenceApplyQueuedEventArgs(string reason, int version, long operationToken)
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
    internal PlaylistReferenceApplyPresentationRequestedEventArgs(string reason, int version, long operationToken)
    {
        Reason = reason;
        Version = version;
        OperationToken = operationToken;
    }

    internal string Reason { get; }

    internal int Version { get; }

    internal long OperationToken { get; }
}

internal sealed class PlaylistReferenceAppliedEventArgs : EventArgs
{
    internal PlaylistReferenceAppliedEventArgs(string reason, int version, long operationToken)
    {
        Reason = reason ?? string.Empty;
        Version = version;
        OperationToken = operationToken;
    }

    internal string Reason { get; }

    internal int Version { get; }

    internal long OperationToken { get; }
}
