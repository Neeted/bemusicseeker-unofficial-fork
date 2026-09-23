using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    private readonly object deferredExternalSyncLock = new();

    private int deferredExternalSyncRequestedVersion;

    private bool deferredExternalSyncRunning;

    private string deferredExternalSyncReason;

    private bool deferredExternalSyncFromReloadTables;

    private bool deferredExternalSyncPublishesReferenceReceipt;

    private long deferredExternalSyncOperationToken;

    private readonly Func<string, Func<Task>, bool> playlistExternalSyncScheduler;

    private readonly object playlistExternalSyncReceiptSyncRoot = new();

    private PlaylistExternalSyncOwner subscribedPlaylistExternalSyncOwner;

    private BMSPlaylist subscribedPlaylistExternalSyncStore;

    private BMSLibrary subscribedPlaylistExternalSyncLibrary;

    internal event EventHandler<PlaylistExternalSyncRequestEventArgs> PlaylistExternalSyncQueued;

    internal event EventHandler<PlaylistExternalSyncCompletionEventArgs> PlaylistExternalSyncCompleted;

    internal event EventHandler<PlaylistExternalSyncReferenceAppliedEventArgs> PlaylistExternalSyncReferenceApplied;

    internal void QueueExternalPlaylistSync(
        string reason,
        bool fromReloadTables,
        bool publishReferenceReceipt,
        long operationToken)
    {
        BMSPlaylist playlists = getPlaylistStore();
        if (playlists == null)
        {
            return;
        }
        if (playlistReloadCleanupShutdownRequestedProvider())
        {
            WritePlaylistReloadLog(
                "deferred_external_sync skipped reason=shutdown_requested requestReason="
                + (reason ?? string.Empty));
            return;
        }

        int version;
        bool shouldStartWorker = false;
        string queuedReason;
        bool queuedFromReloadTables;
        bool queuedPublishesReferenceReceipt;
        long queuedOperationToken;
        lock (deferredExternalSyncLock)
        {
            version = ++deferredExternalSyncRequestedVersion;
            deferredExternalSyncReason = reason ?? string.Empty;
            deferredExternalSyncFromReloadTables = fromReloadTables;
            deferredExternalSyncPublishesReferenceReceipt = publishReferenceReceipt;
            deferredExternalSyncOperationToken = operationToken;
            queuedReason = deferredExternalSyncReason;
            queuedFromReloadTables = deferredExternalSyncFromReloadTables;
            queuedPublishesReferenceReceipt = deferredExternalSyncPublishesReferenceReceipt;
            queuedOperationToken = deferredExternalSyncOperationToken;
            if (!deferredExternalSyncRunning)
            {
                deferredExternalSyncRunning = true;
                shouldStartWorker = true;
            }
        }

        PlaylistExternalSyncQueued?.Invoke(
            this,
            new PlaylistExternalSyncRequestEventArgs(
                queuedReason,
                version,
                queuedFromReloadTables,
                queuedPublishesReferenceReceipt,
                queuedOperationToken));
        if (!shouldStartWorker)
        {
            return;
        }

        async Task Work()
        {
            while (true)
            {
                ExternalPlaylistSyncRequestSnapshot request = CaptureDeferredExternalSyncRequest();
                DateTime startedAt = DateTime.UtcNow;
                BMSPlaylist currentPlaylists = getPlaylistStore() ?? playlists;
                // deferred request は既存 coalescing worker が受理済みなので、store admission を非同期に待機できます。
                // manual command は非待機 Try 経路を使います。
                using IDisposable admission = await WaitForPlaylistMutationAdmissionAsync(currentPlaylists).ConfigureAwait(false);
                if (playlistReloadCleanupShutdownRequestedProvider())
                {
                    return;
                }
                using PlaylistOperationNotificationOwner.OperationNotificationSession notificationSession = playlists.OperationNotificationOwner.BeginSession();
                bool succeeded = false;
                int updatedCount = 0;
                try
                {
                    BeginPlaylistSyncProgressOperation();
                    string operationKind = GetPlaylistReloadOperationKindText(
                        request.Reason,
                        request.FromReloadTables);
                    WritePlaylistReloadLog(
                        "playlist_reload_operation started operationKind="
                        + operationKind
                        + " reason="
                        + request.Reason
                        + " tableCount=0 version="
                        + request.Version);
                    WritePlaylistReloadLog(
                        "deferred_external_sync run reason="
                        + request.Reason
                        + " fromReloadTables="
                        + request.FromReloadTables.ToString().ToLowerInvariant()
                        + " version="
                        + request.Version);
                    if (getPlaylistStore() == null)
                    {
                        throw new InvalidOperationException("Playlist persistence is not available.");
                    }
                    List<BMSTable> tables = await currentPlaylists.ExternalSyncOwner.UpdateBMSTablesInternalAsync(
                        reloadExtPlaylist: true,
                        result =>
                        {
                            RecordPlaylistSyncResult(result);
                        },
                        ReportPlaylistSyncProgress,
                        publishReferenceReceipts: request.PublishesReferenceReceipt).ConfigureAwait(false);
                    updatedCount = tables?.Count ?? 0;
                    currentPlaylists.BmtOutput.QueueBeatorajaBmtExportAll("DeferredExternalSync:" + request.Reason);
                    if (request.PublishesReferenceReceipt)
                    {
                        PlaylistExternalSyncReferenceApplied?.Invoke(
                            this,
                            new PlaylistExternalSyncReferenceAppliedEventArgs(
                                request.Reason,
                                request.Version,
                                request.OperationToken));
                    }
                    RequestPlaylistSummaryDataRefresh(
                        "deferred_external_sync");
                    bool cleanupQueued = QueuePlaylistReloadCleanup(
                        request.Reason,
                        request.FromReloadTables,
                        updatedCount);
                    WritePlaylistReloadLog(
                        "playlist_reload_operation completed operationKind="
                        + operationKind
                        + " reason="
                        + request.Reason
                        + " tableCount="
                        + updatedCount
                        + " summaryRebuildMs="
                        + LastPlaylistSummaryBuildElapsedMs
                        + " detailRefreshMs="
                        + LastDetailBuildElapsedMs
                        + " cleanupQueued="
                        + cleanupQueued.ToString().ToLowerInvariant()
                        + " elapsedMs="
                        + (long)(DateTime.UtcNow - startedAt).TotalMilliseconds);
                    WritePlaylistReloadLog(
                        "deferred_external_sync done reason="
                        + request.Reason
                        + " fromReloadTables="
                        + request.FromReloadTables.ToString().ToLowerInvariant()
                        + " version="
                        + request.Version
                        + " elapsedMs="
                        + (long)(DateTime.UtcNow - startedAt).TotalMilliseconds
                        + " updatedCount="
                        + updatedCount);
                    succeeded = true;
                }
                catch (Exception ex)
                {
                    WritePlaylistReloadLog(
                        "playlist_reload_operation failed operationKind="
                        + GetPlaylistReloadOperationKindText(request.Reason, request.FromReloadTables)
                        + " reason="
                        + request.Reason
                        + " version="
                        + request.Version
                        + " elapsedMs="
                        + (long)(DateTime.UtcNow - startedAt).TotalMilliseconds
                        + " message="
                        + ex.Message);
                    WritePlaylistReloadLog(
                        "deferred_external_sync failed reason="
                        + request.Reason
                        + " fromReloadTables="
                        + request.FromReloadTables.ToString().ToLowerInvariant()
                        + " version="
                        + request.Version
                        + " elapsedMs="
                        + (long)(DateTime.UtcNow - startedAt).TotalMilliseconds
                        + " message="
                        + ex.Message);
                }
                finally
                {
                    EndPlaylistSyncProgressOperation();
                    RaisePlaylistOperationNotificationPresentationRequested(
                        notificationSession.TakeReceipt(),
                        "external playlist sync notification");
                }

                PlaylistExternalSyncCompleted?.Invoke(
                    this,
                    new PlaylistExternalSyncCompletionEventArgs(
                        request.Reason,
                        request.Version,
                        request.FromReloadTables,
                        request.PublishesReferenceReceipt,
                        request.OperationToken,
                        succeeded,
                        wasSkipped: false));
                if (TryCompleteDeferredExternalSyncWorkerCycle(request.Version))
                {
                    break;
                }
            }
        }

        int rejectedVersion = version;
        string rejectedReason = queuedReason;
        bool rejectedFromReloadTables = queuedFromReloadTables;
        bool rejectedPublishesReferenceReceipt = queuedPublishesReferenceReceipt;
        long rejectedOperationToken = queuedOperationToken;
        while (!playlistExternalSyncScheduler(rejectedReason, Work))
        {
            if (TryCompleteDeferredExternalSyncWorkerCycle(rejectedVersion))
            {
                PublishDeferredExternalSyncSkipped(
                    rejectedVersion,
                    rejectedReason,
                    rejectedFromReloadTables,
                    rejectedPublishesReferenceReceipt,
                    rejectedOperationToken,
                    "startup_scheduler_rejected");
                return;
            }

            ExternalPlaylistSyncRequestSnapshot latest = CaptureDeferredExternalSyncRequest();
            rejectedVersion = latest.Version;
            rejectedReason = latest.Reason;
            rejectedFromReloadTables = latest.FromReloadTables;
            rejectedPublishesReferenceReceipt = latest.PublishesReferenceReceipt;
            rejectedOperationToken = latest.OperationToken;
        }
    }

    internal bool IsDeferredExternalPlaylistSyncIdle
    {
        get
        {
            lock (deferredExternalSyncLock)
            {
                return !deferredExternalSyncRunning;
            }
        }
    }

    internal string DescribeDeferredExternalPlaylistSyncWaitState()
    {
        lock (deferredExternalSyncLock)
        {
            return "deferredExternalSyncRunning="
                + deferredExternalSyncRunning.ToString().ToLowerInvariant()
                + " requestedVersion="
                + deferredExternalSyncRequestedVersion;
        }
    }

    internal void DiscardDeferredExternalPlaylistSyncForShutdown(string reason)
    {
        ExternalPlaylistSyncRequestSnapshot request;
        lock (deferredExternalSyncLock)
        {
            if (!deferredExternalSyncRunning)
            {
                return;
            }
            deferredExternalSyncRunning = false;
            request = new ExternalPlaylistSyncRequestSnapshot(
                deferredExternalSyncRequestedVersion,
                deferredExternalSyncReason ?? string.Empty,
                deferredExternalSyncFromReloadTables,
                deferredExternalSyncPublishesReferenceReceipt,
                deferredExternalSyncOperationToken);
        }
        PlaylistExternalSyncCompleted?.Invoke(
            this,
            new PlaylistExternalSyncCompletionEventArgs(
                request.Reason,
                request.Version,
                request.FromReloadTables,
                request.PublishesReferenceReceipt,
                request.OperationToken,
                succeeded: false,
                wasSkipped: true));
        WritePlaylistReloadLog(
            "deferred_external_sync discarded reason=shutdown_requested requestReason="
            + (reason ?? string.Empty)
            + " version="
            + request.Version);
    }

    private void PublishDeferredExternalSyncSkipped(
        int version,
        string reason,
        bool fromReloadTables,
        bool publishesReferenceReceipt,
        long operationToken,
        string shutdownReason)
    {
        PlaylistExternalSyncCompleted?.Invoke(
            this,
            new PlaylistExternalSyncCompletionEventArgs(
                reason ?? string.Empty,
                version,
                fromReloadTables,
                publishesReferenceReceipt,
                operationToken,
                succeeded: false,
                wasSkipped: true));
        WritePlaylistReloadLog(
            "deferred_external_sync skipped reason="
            + (shutdownReason ?? "shutdown_requested")
            + " requestReason="
            + (reason ?? string.Empty)
            + " version="
            + version);
    }

    private ExternalPlaylistSyncRequestSnapshot CaptureDeferredExternalSyncRequest()
    {
        lock (deferredExternalSyncLock)
        {
            return new ExternalPlaylistSyncRequestSnapshot(
                deferredExternalSyncRequestedVersion,
                deferredExternalSyncReason ?? string.Empty,
                deferredExternalSyncFromReloadTables,
                deferredExternalSyncPublishesReferenceReceipt,
                deferredExternalSyncOperationToken);
        }
    }

    private bool TryCompleteDeferredExternalSyncWorkerCycle(int version)
    {
        lock (deferredExternalSyncLock)
        {
            if (deferredExternalSyncRequestedVersion != version)
            {
                return false;
            }
            deferredExternalSyncRunning = false;
            return true;
        }
    }

    private void RaisePlaylistOperationNotificationPresentationRequested(
        PlaylistOperationNotificationOwner.OperationNotificationReceipt receipt,
        string routeName)
    {
        PlaylistOperationNotificationPresentationRequested?.Invoke(
            this,
            new PlaylistOperationNotificationPresentationRequestedEventArgs(receipt, routeName));
    }

    private void SetPlaylistExternalSyncReceiptSubscription(
        BMSPlaylist playlists,
        BMSLibrary library)
    {
        PlaylistExternalSyncOwner owner = playlists?.ExternalSyncOwner;
        lock (playlistExternalSyncReceiptSyncRoot)
        {
            SetPlaylistExternalSyncReceiptSubscriptionUnsafe(playlists, owner, library);
        }
    }

    private void SetPlaylistExternalSyncReceiptSubscriptionUnsafe(
        BMSPlaylist playlists,
        PlaylistExternalSyncOwner owner,
        BMSLibrary library)
    {
        if (subscribedPlaylistExternalSyncOwner != null)
        {
            subscribedPlaylistExternalSyncOwner.PlaylistTableUpdateReceiptPublished -=
                PlaylistTableUpdateReceiptPublished;
        }
        subscribedPlaylistExternalSyncStore = playlists;
        subscribedPlaylistExternalSyncLibrary = library;
        subscribedPlaylistExternalSyncOwner = owner;
        if (subscribedPlaylistExternalSyncOwner != null)
        {
            subscribedPlaylistExternalSyncOwner.PlaylistTableUpdateReceiptPublished +=
                PlaylistTableUpdateReceiptPublished;
        }
    }

    private void PlaylistTableUpdateReceiptPublished(
        object sender,
        PlaylistExternalSyncOwner.PlaylistTableUpdateReceiptPublishedEventArgs eventArgs)
    {
        var owner = sender as PlaylistExternalSyncOwner;
        lock (playlistExternalSyncReceiptSyncRoot)
        {
            BMSPlaylist currentPlaylist = subscribedPlaylistExternalSyncStore;
            BMSLibrary currentLibrary = subscribedPlaylistExternalSyncLibrary;
            if (owner == null
                || !ReferenceEquals(owner, subscribedPlaylistExternalSyncOwner)
                || !ReferenceEquals(owner, currentPlaylist?.ExternalSyncOwner))
            {
                return;
            }
            ApplyReferenceReplaceReceipt(eventArgs?.Receipt, currentPlaylist, currentLibrary);
        }
    }

    private readonly struct ExternalPlaylistSyncRequestSnapshot
    {
        internal ExternalPlaylistSyncRequestSnapshot(
            int version,
            string reason,
            bool fromReloadTables,
            bool publishesReferenceReceipt,
            long operationToken)
        {
            Version = version;
            Reason = reason;
            FromReloadTables = fromReloadTables;
            PublishesReferenceReceipt = publishesReferenceReceipt;
            OperationToken = operationToken;
        }

        internal int Version { get; }

        internal string Reason { get; }

        internal bool FromReloadTables { get; }

        internal bool PublishesReferenceReceipt { get; }

        internal long OperationToken { get; }
    }
}

internal sealed class PlaylistExternalSyncRequestEventArgs : EventArgs
{
    internal PlaylistExternalSyncRequestEventArgs(
        string reason,
        int version,
        bool fromReloadTables,
        bool publishesReferenceReceipt,
        long operationToken)
    {
        Reason = reason;
        Version = version;
        FromReloadTables = fromReloadTables;
        PublishesReferenceReceipt = publishesReferenceReceipt;
        OperationToken = operationToken;
    }

    internal string Reason { get; }

    internal int Version { get; }

    internal bool FromReloadTables { get; }

    internal bool PublishesReferenceReceipt { get; }

    internal long OperationToken { get; }
}

internal sealed class PlaylistExternalSyncCompletionEventArgs : EventArgs
{
    internal PlaylistExternalSyncCompletionEventArgs(
        string reason,
        int version,
        bool fromReloadTables,
        bool publishesReferenceReceipt,
        long operationToken,
        bool succeeded,
        bool wasSkipped)
    {
        Reason = reason;
        Version = version;
        FromReloadTables = fromReloadTables;
        PublishesReferenceReceipt = publishesReferenceReceipt;
        OperationToken = operationToken;
        Succeeded = succeeded;
        WasSkipped = wasSkipped;
    }

    internal string Reason { get; }

    internal int Version { get; }

    internal bool FromReloadTables { get; }

    internal bool PublishesReferenceReceipt { get; }

    internal long OperationToken { get; }

    internal bool Succeeded { get; }

    internal bool WasSkipped { get; }
}

internal sealed class PlaylistExternalSyncReferenceAppliedEventArgs : EventArgs
{
    internal PlaylistExternalSyncReferenceAppliedEventArgs(
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
