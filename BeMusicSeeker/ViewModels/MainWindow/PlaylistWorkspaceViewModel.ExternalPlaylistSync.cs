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

    private Action<PlaylistExternalSyncOwner.PlaylistTableUpdateContext> deferredExternalSyncUpdateCallback;

    private long deferredExternalSyncOperationToken;

    private readonly Func<string, Func<Task>, bool> playlistExternalSyncScheduler;

    internal event EventHandler<PlaylistExternalSyncRequestEventArgs> PlaylistExternalSyncQueued;

    internal event EventHandler<PlaylistExternalSyncCompletionEventArgs> PlaylistExternalSyncCompleted;

    internal event EventHandler<PlaylistExternalSyncReferenceApplyRequestedEventArgs> PlaylistExternalSyncReferenceApplyRequested;

    internal event EventHandler<PlaylistExternalSyncReferenceAppliedEventArgs> PlaylistExternalSyncReferenceApplied;

    internal void QueueExternalPlaylistSync(
        string reason,
        bool fromReloadTables,
        Action<PlaylistExternalSyncOwner.PlaylistTableUpdateContext> updateCallbackAction,
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
        bool queuedHasUpdateCallback;
        long queuedOperationToken;
        lock (deferredExternalSyncLock)
        {
            version = ++deferredExternalSyncRequestedVersion;
            deferredExternalSyncReason = reason ?? string.Empty;
            deferredExternalSyncFromReloadTables = fromReloadTables;
            deferredExternalSyncUpdateCallback = updateCallbackAction;
            deferredExternalSyncOperationToken = operationToken;
            queuedReason = deferredExternalSyncReason;
            queuedFromReloadTables = deferredExternalSyncFromReloadTables;
            queuedHasUpdateCallback = deferredExternalSyncUpdateCallback != null;
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
                queuedHasUpdateCallback,
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
                BMSPlaylist currentPlaylists = null;
                PlaylistOperationNotificationOwner.OperationNotificationScope notificationScope = null;
                bool succeeded = false;
                int updatedCount = 0;
                try
                {
                    BeginPlaylistSyncProgressOperation();
                    currentPlaylists = GetPlaylistStore();
                    notificationScope = currentPlaylists.OperationNotificationOwner.BeginScope();
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
                    List<Action<PlaylistExternalSyncOwner.PlaylistTableUpdateContext>> updateCallbackActions = request.UpdateCallback == null
                        ? null
                        : [request.UpdateCallback];
                    List<BMSTable> tables = await currentPlaylists.ExternalSyncOwner.UpdateBMSTablesInternalAsync(
                        reloadExtPlaylist: true,
                        updateCallbackActions,
                        result =>
                        {
                            RecordPlaylistSyncResult(result);
                        },
                        ReportPlaylistSyncProgress).ConfigureAwait(false);
                    updatedCount = tables?.Count ?? 0;
                    currentPlaylists.BmtOutput.QueueBeatorajaBmtExportAll("DeferredExternalSync:" + request.Reason);
                    if (request.UpdateCallback == null)
                    {
                        PlaylistExternalSyncReferenceApplyRequested?.Invoke(
                            this,
                            new PlaylistExternalSyncReferenceApplyRequestedEventArgs(
                                "DeferredExternalSync:" + request.Reason,
                                request.OperationToken));
                    }
                    else
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
                    try
                    {
                        EndPlaylistSyncProgressOperation();
                        RaisePlaylistOperationNotificationsFlushRequested(
                            notificationScope,
                            "external playlist sync notification");
                    }
                    finally
                    {
                        notificationScope?.Dispose();
                    }
                }

                PlaylistExternalSyncCompleted?.Invoke(
                    this,
                    new PlaylistExternalSyncCompletionEventArgs(
                        request.Reason,
                        request.Version,
                        request.FromReloadTables,
                        request.UpdateCallback != null,
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
        bool rejectedHasUpdateCallback = queuedHasUpdateCallback;
        long rejectedOperationToken = queuedOperationToken;
        while (!playlistExternalSyncScheduler(rejectedReason, Work))
        {
            if (TryCompleteDeferredExternalSyncWorkerCycle(rejectedVersion))
            {
                PublishDeferredExternalSyncSkipped(
                    rejectedVersion,
                    rejectedReason,
                    rejectedFromReloadTables,
                    rejectedHasUpdateCallback,
                    rejectedOperationToken,
                    "startup_scheduler_rejected");
                return;
            }

            ExternalPlaylistSyncRequestSnapshot latest = CaptureDeferredExternalSyncRequest();
            rejectedVersion = latest.Version;
            rejectedReason = latest.Reason;
            rejectedFromReloadTables = latest.FromReloadTables;
            rejectedHasUpdateCallback = latest.UpdateCallback != null;
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
                deferredExternalSyncUpdateCallback,
                deferredExternalSyncOperationToken);
        }
        PlaylistExternalSyncCompleted?.Invoke(
            this,
            new PlaylistExternalSyncCompletionEventArgs(
                request.Reason,
                request.Version,
                request.FromReloadTables,
                request.UpdateCallback != null,
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
        bool hadUpdateCallback,
        long operationToken,
        string shutdownReason)
    {
        PlaylistExternalSyncCompleted?.Invoke(
            this,
            new PlaylistExternalSyncCompletionEventArgs(
                reason ?? string.Empty,
                version,
                fromReloadTables,
                hadUpdateCallback,
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
                deferredExternalSyncUpdateCallback,
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

    private void RaisePlaylistOperationNotificationsFlushRequested(
        PlaylistOperationNotificationOwner.OperationNotificationScope scope,
        string routeName)
    {
        PlaylistOperationNotificationsFlushRequested?.Invoke(
            this,
            new PlaylistOperationNotificationsFlushRequestedEventArgs(scope, routeName));
    }

    private readonly struct ExternalPlaylistSyncRequestSnapshot
    {
        internal ExternalPlaylistSyncRequestSnapshot(
            int version,
            string reason,
            bool fromReloadTables,
            Action<PlaylistExternalSyncOwner.PlaylistTableUpdateContext> updateCallback,
            long operationToken)
        {
            Version = version;
            Reason = reason;
            FromReloadTables = fromReloadTables;
            UpdateCallback = updateCallback;
            OperationToken = operationToken;
        }

        internal int Version { get; }

        internal string Reason { get; }

        internal bool FromReloadTables { get; }

        internal Action<PlaylistExternalSyncOwner.PlaylistTableUpdateContext> UpdateCallback { get; }

        internal long OperationToken { get; }
    }
}

internal sealed class PlaylistExternalSyncRequestEventArgs : EventArgs
{
    internal PlaylistExternalSyncRequestEventArgs(
        string reason,
        int version,
        bool fromReloadTables,
        bool hasUpdateCallback,
        long operationToken)
    {
        Reason = reason;
        Version = version;
        FromReloadTables = fromReloadTables;
        HasUpdateCallback = hasUpdateCallback;
        OperationToken = operationToken;
    }

    internal string Reason { get; }

    internal int Version { get; }

    internal bool FromReloadTables { get; }

    internal bool HasUpdateCallback { get; }

    internal long OperationToken { get; }
}

internal sealed class PlaylistExternalSyncCompletionEventArgs : EventArgs
{
    internal PlaylistExternalSyncCompletionEventArgs(
        string reason,
        int version,
        bool fromReloadTables,
        bool hasUpdateCallback,
        long operationToken,
        bool succeeded,
        bool wasSkipped)
    {
        Reason = reason;
        Version = version;
        FromReloadTables = fromReloadTables;
        HasUpdateCallback = hasUpdateCallback;
        OperationToken = operationToken;
        Succeeded = succeeded;
        WasSkipped = wasSkipped;
    }

    internal string Reason { get; }

    internal int Version { get; }

    internal bool FromReloadTables { get; }

    internal bool HasUpdateCallback { get; }

    internal long OperationToken { get; }

    internal bool Succeeded { get; }

    internal bool WasSkipped { get; }
}

internal sealed class PlaylistExternalSyncReferenceApplyRequestedEventArgs : EventArgs
{
    internal PlaylistExternalSyncReferenceApplyRequestedEventArgs(string reason, long operationToken)
    {
        Reason = reason;
        OperationToken = operationToken;
    }

    internal string Reason { get; }

    internal long OperationToken { get; }
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
