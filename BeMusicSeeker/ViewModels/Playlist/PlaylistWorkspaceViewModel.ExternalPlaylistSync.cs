using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    /// <summary>要求受付時に表示識別を捕捉する窓口です。</summary>
    internal Func<string, long, OperationProgressRequest> ProgressRequestFactory { get; set; }

    /// <summary>捕捉した要求の実行境界を表示先へ通知します。</summary>
    internal Action<OperationProgressRequest, bool> RequestProgressReporter { get; set; }

    private const string ExternalPlaylistSyncProgressSource = "external_playlist_sync";

    private int externalSyncProgressSeed;

    private readonly Func<string, Func<Task>, bool> playlistExternalSyncScheduler;

    private readonly object playlistExternalSyncReceiptSyncRoot = new();

    private PlaylistExternalSyncOwner subscribedPlaylistExternalSyncOwner;

    private BMSPlaylist subscribedPlaylistExternalSyncStore;

    private BMSLibrary subscribedPlaylistExternalSyncLibrary;

    /// <summary>schedulerが開始した同じ同期Taskを通知します。同期完了receiptはこの通知より先に発生する場合があります。</summary>
    internal event Action<string, Task> ExternalPlaylistSyncStarted;

    internal event EventHandler<PlaylistExternalSyncRequestEventArgs> PlaylistExternalSyncQueued;

    internal event EventHandler<PlaylistExternalSyncCompletionEventArgs> PlaylistExternalSyncCompleted;

    internal event EventHandler<PlaylistExternalSyncReferenceAppliedEventArgs> PlaylistExternalSyncReferenceApplied;

    /// <summary>起動の任意同期を既存schedulerへ登録します。実開始時にPを非待機取得し、Busyでは再予約せず見送ります。</summary>
    internal void QueueExternalPlaylistSync(string reason, bool fromReloadTables, bool publishReferenceReceipt, long operationToken)
    {
        if (!playlistExternalSyncScheduler(reason, () =>
        {
            Task operation = RunExternalPlaylistSyncAsync(reason, fromReloadTables, publishReferenceReceipt, operationToken);
            Action<string, Task> observers = ExternalPlaylistSyncStarted;
            if (observers != null)
            {
                foreach (Action<string, Task> observer in observers.GetInvocationList())
                {
                    try { observer(reason, operation); }
                    catch (Exception failure) { Task.FromException(failure).ObserveFault("playlistSyncObserver"); }
                }
            }
            return operation;
        }))
        {
            PublishPlaylistProgressEvent(PlaylistExternalSyncCompleted, new PlaylistExternalSyncCompletionEventArgs(
                reason, 0, fromReloadTables, publishReferenceReceipt, operationToken, succeeded: false, wasSkipped: true));
        }
    }

    /// <summary>Pを通信・対象確定前に取得し、外部同期の保存・必要出力・公開・通知session終端を直接待ちます。内部継続は同owner権限を借用し、進捗・任意receipt表示の失敗は実同期結果へ加えません。</summary>
    internal async Task RunExternalPlaylistSyncAsync(string reason, bool fromReloadTables, bool publishReferenceReceipt, long operationToken,
        LibraryFileMutationCapability capability = null)
    {
        BMSPlaylist playlists = getPlaylistStore();
        if (playlists == null) { return; }
        // 通知の収集先はPより外側に置き、Pを解放した後に表示を依頼します。借用した権限の呼出し元は、自身の収集先の内側で呼びます。
        await RunWithOperationNoticesAsync(playlists, "external playlist sync notification",
            () => RunExternalPlaylistSyncAdmittedAsync(playlists, reason, fromReloadTables, publishReferenceReceipt, operationToken, capability))
            .ConfigureAwait(false);
    }

    private async Task RunExternalPlaylistSyncAdmittedAsync(BMSPlaylist playlists, string reason, bool fromReloadTables, bool publishReferenceReceipt, long operationToken,
        LibraryFileMutationCapability capability)
    {
        IDisposable admission;
        if (capability != null)
        {
            admission = playlists.AcquirePlaylistMutationLease(reason, capability: capability);
        }
        else if (playlistReloadCleanupShutdownRequestedProvider() || !playlists.TryEnterPlaylistMutation(out admission))
        {
            PublishPlaylistProgressEvent(PlaylistExternalSyncCompleted, new PlaylistExternalSyncCompletionEventArgs(
                reason, 0, fromReloadTables, publishReferenceReceipt, operationToken, succeeded: false, wasSkipped: true));
            return;
        }
        using (admission)
        using (LibraryFileMutationCapability authority = playlists.CreatePlaylistMutationCapability(admission))
        using (PlaylistOperationNotificationOwner.OperationNotificationSession notifications = playlists.OperationNotificationOwner.BeginSession())
        {
            int version = System.Threading.Interlocked.Increment(ref externalSyncProgressSeed);
            OperationProgressRequest request = ProgressRequestFactory?.Invoke(ExternalPlaylistSyncProgressSource, version);
            // 必須同期の業務token=0でも、受付時の起動由来の表示識別は保持します。
            if (request != null && operationToken != 0) { request = request with { OperationToken = operationToken }; }
            PublishPlaylistProgressEvent(PlaylistExternalSyncQueued, new PlaylistExternalSyncRequestEventArgs(reason, version, fromReloadTables, publishReferenceReceipt, operationToken, request));
            bool succeeded = false;
            List<Exception> failures = [];
            var adoptionFailures = new ConcurrentQueue<Exception>();
            try
            {
                ReportRequestProgress(request, true);
                BeginPlaylistSyncProgressOperation(ExternalPlaylistSyncProgressSource, request);
                PlaylistExternalSyncOwner.PlaylistReloadOperationResult result = await playlists.ExternalSyncOwner.UpdateBMSTablesInternalAsync(reloadExtPlaylist: true,
                    target => AdoptPlaylistReloadTarget(target, "external_playlist_sync", adoptionFailures,
                        applyReferences: publishReferenceReceipt),
                    snapshot => ReportPlaylistSyncProgress(snapshot, ExternalPlaylistSyncProgressSource,
                        PlaylistSyncProgressKind.ExternalPlaylistSync, request),
                    cancellationToken: playlists.StartupReadiness.ShutdownToken,
                    publishReferenceReceipts: false, capability: authority).ConfigureAwait(false);
                if (result.Exception is Exception reloadFailure) { failures.Add(reloadFailure); }
                if (publishReferenceReceipt)
                {
                    PublishPlaylistProgressEvent(PlaylistExternalSyncReferenceApplied,
                        new PlaylistExternalSyncReferenceAppliedEventArgs(reason, version, operationToken));
                }
                RequestPlaylistDetailReloadRefresh();
                // 例外を再送出しなくても、対象失敗を全件成功として公開しません。
                succeeded = failures.Count == 0 && adoptionFailures.IsEmpty && result.All(target => target.Succeeded);
            }
            catch (Exception failure) { failures.Add(failure); }
            finally
            {
                failures.AddRange(adoptionFailures);
                CapturePlaylistWorkflowFailure(() => EndPlaylistSyncProgressOperation(ExternalPlaylistSyncProgressSource, request), failures);
                ReportRequestProgress(request, false);
                CapturePlaylistWorkflowFailure(() => PublishPlaylistOperationNotificationReceipt(
                    notifications, "external playlist sync notification"), failures);
                PublishPlaylistProgressEvent(PlaylistExternalSyncCompleted, new PlaylistExternalSyncCompletionEventArgs(
                    reason, version, fromReloadTables, publishReferenceReceipt, operationToken, succeeded && failures.Count == 0, wasSkipped: false));
            }
            ThrowPlaylistWorkflowFailures(failures);
        }
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


}

internal sealed class PlaylistExternalSyncRequestEventArgs : EventArgs
{
    /// <summary>既存の受付・完了情報に、同じ要求の捕捉済み表示識別を添えます。</summary>
    internal PlaylistExternalSyncRequestEventArgs(
        string reason,
        int version,
        bool fromReloadTables,
        bool publishesReferenceReceipt,
        long operationToken, OperationProgressRequest progressRequest = null)
    {
        ProgressRequest = progressRequest;
        Reason = reason;
        Version = version;
        FromReloadTables = fromReloadTables;
        PublishesReferenceReceipt = publishesReferenceReceipt;
        OperationToken = operationToken;
    }

    internal OperationProgressRequest ProgressRequest { get; }

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
