using System;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    private readonly Func<BMSPlaylist> getPlaylistStore;

    internal bool CanBeginDetailEdit(MainChartListCellEditContext context)
    {
        return context?.Row is PlaylistDetailRow
            && IsDetailEditableProperty(context.PropertyName)
            && GridRowResolver.CanEditPlaylistCell(context.Row, context.PropertyName);
    }

    internal void BeginDetailEdit(MainChartListCellEditContext context)
    {
        if (!CanBeginDetailEdit(context))
        {
            return;
        }
        lock (DetailViewState.SyncRoot)
        {
            DetailViewState.Source.IsPlaylistCellEditing = true;
        }
    }

    /// <summary>
    /// プレイリスト詳細のセル編集をP内で保存し、関連出力・通知の実試行とsession終端まで待ちます。
    /// 確定値の必要表示と編集解除を終えてからPを解放し、先行失敗を保ちます。
    /// 再同期前の古い項目は保存せず通知し、最新行からの明示編集を受け付けます。
    /// URL の空白だけの入力は未設定への解除として保存し、非空の無効な絶対 URI は保存しません。
    /// </summary>
    /// <param name="request">一覧から受け取った編集結果。</param>
    /// <returns>
    /// 保存・必要出力・必要表示・編集cleanupまでを追跡するTask。確定後の失敗でも保存値を保ち、先行する元原因を返します。
    /// </returns>
    /// <exception cref="PlaylistMutationPostCommitException">保存後の必要処理が失敗または取り消されました。先行の実失敗を保ち、二次receipt・session失敗は局所診断します。</exception>
    internal async Task CompleteDetailEdit(MainChartListCellEditEndedEventArgs request)
    {
        if (request?.Context.Row is not PlaylistDetailRow playlistRow)
        {
            return;
        }
        IDisposable admission = null;
        LibraryFileMutationCapability authority = null;
        ExceptionDispatchInfo primaryFailure = null;
        bool saveAttempted = false;
        try
        {
            if (!request.Commit
                || !IsDetailEditableProperty(request.Context.PropertyName)
                || !GridRowResolver.CanEditPlaylistCell(playlistRow, request.Context.PropertyName))
            {
                return;
            }
            BMSPlaylist store = getPlaylistStore();
            if (store == null) { return; }
            if (!store.TryEnterPlaylistMutation(out admission))
            {
                RaiseMutationRejected(PlaylistWorkspaceMutationKind.PropertySave, isBusy: true, isStale: false);
                return;
            }
            SetDetailEntryCommitStore(store);
            authority = store.CreatePlaylistMutationCapability(admission);
            if (!store.ContainsCurrentDetailEntry(playlistRow.Entry))
            {
                RaiseMutationRejected(PlaylistWorkspaceMutationKind.PropertySave, isBusy: false, isStale: true);
                return;
            }
            string propertyName = request.Context.PropertyName;
            if ((propertyName == nameof(PlaylistDetailRow.Url) || propertyName == nameof(PlaylistDetailRow.Url_diff))
                && !BMSTableEntry.TryParseEditedUrl(request.Text, out _))
            {
                return;
            }
            lock (detailEntryDeliveryLock)
            { if (detailEntryDelivery.IsCompleted) { detailEntryDelivery = Task.CompletedTask; } }
            saveAttempted = true;
            await Task.Run(() => CommitRow(playlistRow, propertyName, request.Text, authority)).ConfigureAwait(true);
        }
        catch (Exception failure) { primaryFailure = ExceptionDispatchInfo.Capture(failure); }
        finally
        {
            try
            {
                if (saveAttempted)
                {
                    await WaitForDetailEntryDeliveryAsync().ConfigureAwait(true);
                }
            }
            catch (Exception failure) { CapturePresentationFailure(failure, "playlist_detail_edit_projection_failed"); }
            try
            {
                if (saveAttempted)
                {
                    PlaylistEntryValue saved;
                    using (playlistRow.Entry.parent.ReaderWriterLock.GetReaderGuard()) { saved = playlistRow.Entry.CaptureValue(); }
                    PlaylistDetailSourceRow current;
                    lock (DetailViewState.SyncRoot)
                    {
                        PlaylistDetailDependencies dependencies = EnsureDetailDependencies();
                        dependencies.TryGet(playlistRow.Entry, out current);
                        // 同値保存ではreceiptを作らないため、直前の表示失敗後もこの明示要求の正本値を必要反映します。
                        if (current != null && !current.HasEditedValues(saved))
                        {
                            PlaylistDetailSourceRow next = current.WithEditedValues(saved);
                            ReplaceDetailProjection(dependencies, current, next); current = next;
                        }
                    }
                    if (current != null) { playlistRow.ApplySource(current); }
                }
            }
            catch (Exception failure) { CapturePresentationFailure(failure, "playlist_detail_edit_row_projection_failed"); }
            try { playlistRow.EndDraft(); }
            catch (Exception failure) { CapturePresentationFailure(failure, "playlist_detail_edit_draft_notification_failed"); }
            try { CompleteDetailEditSession(); }
            catch (Exception failure) { CapturePresentationFailure(failure, "playlist_detail_edit_refresh_notification_failed"); }
            authority?.Dispose();
            admission?.Dispose();
            primaryFailure?.Throw();
        }

        void CapturePresentationFailure(Exception failure, string reason)
        {
            if (primaryFailure == null) { primaryFailure = ExceptionDispatchInfo.Capture(failure); }
            else { TryLogPlaylistDropSecondaryFailure(failure, reason); }
        }
    }

    private async Task CommitRow(PlaylistDetailRow playlistRow, string propertyName, string text, LibraryFileMutationCapability capability)
    {
        BMSPlaylist playlistStore = getPlaylistStore()
            ?? throw new InvalidOperationException("Playlist persistence is not available.");
        bool committed = false;
        ExceptionDispatchInfo primaryFailure = null;
        try
        {
            using PlaylistOperationNotificationOwner.OperationNotificationSession notificationSession = playlistStore.OperationNotificationOwner.BeginSession();
            try
            {
                PlaylistEntryCommitReceipt receipt = playlistStore.CommitBMSTableEntry(
                    playlistRow.Entry, propertyName, text, capability, playlistRow.Chart);
                BMSTable changed = receipt.Table;
                committed = true;
                if (changed != null)
                {
                    await playlistStore.BmtOutput.ExportTablesAsync([changed], "playlist_detail_cell_edit", capability).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                // model内のLR2失敗でもDBは確定済みです。後続BMTの取消も同じ確定後境界へ揃えます。
                committed |= exception is PlaylistMutationPostCommitException;
                primaryFailure = ExceptionDispatchInfo.Capture(committed && exception is not PlaylistMutationPostCommitException
                    ? new PlaylistMutationPostCommitException("playlist_detail_cell_edit", exception)
                    : exception);
            }
            try
            {
                PublishPlaylistOperationNotificationReceipt(notificationSession, "playlist detail cell edit notification");
            }
            catch (Exception receiptFailure)
            {
                // 任意表示はpublisher内で終端済みです。ここでは必須receipt取得の実失敗だけを扱います。
                if (primaryFailure != null)
                {
                    TryLogPlaylistDropSecondaryFailure(receiptFailure, "playlist_detail_cell_edit_receipt_failed");
                }
                else
                {
                    primaryFailure = ExceptionDispatchInfo.Capture(committed
                        ? new PlaylistMutationPostCommitException("playlist_detail_cell_edit", receiptFailure)
                        : receiptFailure);
                }
            }
        }
        catch (Exception sessionFailure)
        {
            // session取得・解放の実失敗も確定状態で分類し、先行の保存・出力失敗を置換しません。
            if (primaryFailure != null)
            {
                TryLogPlaylistDropSecondaryFailure(sessionFailure, "playlist_detail_cell_edit_notification_session_failed");
            }
            else
            {
                primaryFailure = ExceptionDispatchInfo.Capture(committed
                    ? new PlaylistMutationPostCommitException("playlist_detail_cell_edit", sessionFailure)
                    : sessionFailure);
            }
        }
        primaryFailure?.Throw();
    }

    private void CompleteDetailEditSession()
    {
        lock (DetailViewState.SyncRoot)
        {
            DetailViewState.Source.IsPlaylistCellEditing = false;
            if (DetailViewState.View.CurrentIdentity is PlaylistRequestIdentity current)
            { (DetailViewState.View.Rows as PlaylistDetailVirtualView)?.FlushDeferred(current); }
        }
        PublishDetailChanges();
    }

    private static bool IsDetailEditableProperty(string propertyName)
    {
        return string.Equals(propertyName, nameof(PlaylistDetailRow.Level), StringComparison.Ordinal)
            || string.Equals(propertyName, nameof(PlaylistDetailRow.Url), StringComparison.Ordinal)
            || string.Equals(propertyName, nameof(PlaylistDetailRow.Url_diff), StringComparison.Ordinal)
            || string.Equals(propertyName, nameof(PlaylistDetailRow.comment), StringComparison.Ordinal)
            || string.Equals(propertyName, nameof(PlaylistDetailRow.memo), StringComparison.Ordinal);
    }
}
