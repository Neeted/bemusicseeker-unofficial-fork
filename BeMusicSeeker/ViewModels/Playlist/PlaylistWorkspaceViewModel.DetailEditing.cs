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
    /// 操作後の任意表示の失敗は局所診断し、保存・出力・取消の結果へ加えません。
    /// 再同期前の古い項目は保存せず通知し、最新行からの明示編集を受け付けます。
    /// </summary>
    /// <param name="request">一覧から受け取った編集結果。</param>
    /// <returns>
    /// 保存・必要出力・通知session終端までを追跡するTask。DB未確定の失敗だけを戻し、確定後の出力・取消・receipt取得やsession寿命の失敗は確定値と元原因を保持します。
    /// </returns>
    /// <exception cref="PlaylistMutationPostCommitException">保存後の必要処理が失敗または取り消されました。先行の実失敗を保ち、二次receipt・session失敗は局所診断します。</exception>
    internal async Task CompleteDetailEdit(MainChartListCellEditEndedEventArgs request)
    {
        if (request?.Context.Row is not PlaylistDetailRow playlistRow)
        {
            return;
        }
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
            if (!store.TryEnterPlaylistMutation(out IDisposable admission))
            {
                RaiseMutationRejected(PlaylistWorkspaceMutationKind.PropertySave, isBusy: true, isStale: false);
                return;
            }
            SetDetailEntryCommitStore(store);
            using IDisposable accepted = admission;
            using LibraryFileMutationCapability authority = store.CreatePlaylistMutationCapability(admission);
            if (!store.ContainsCurrentDetailEntry(playlistRow.Entry))
            {
                RaiseMutationRejected(PlaylistWorkspaceMutationKind.PropertySave, isBusy: false, isStale: true);
                return;
            }
            string propertyName = request.Context.PropertyName;
            if ((propertyName == nameof(PlaylistDetailRow.Url) || propertyName == nameof(PlaylistDetailRow.Url_diff))
                && !Uri.TryCreate(request.Text, UriKind.Absolute, out _))
            {
                return;
            }
            await Task.Run(() => CommitRow(playlistRow, propertyName, request.Text, authority)).ConfigureAwait(true);
        }
        finally
        {
            // 任意表示の失敗で先行する DB・必要出力・session の結果を置換しません。
            TryCompleteDetailEditPresentation(() =>
            {
                PlaylistDetailSourceRow current;
                lock (DetailViewState.SyncRoot) { EnsureDetailDependencies().TryGet(playlistRow.Entry, out current); }
                if (current != null) { playlistRow.ApplySource(current); }
            }, "playlist_detail_edit_projection_failed");
            TryCompleteDetailEditPresentation(playlistRow.EndDraft, "playlist_detail_edit_draft_notification_failed");
            TryCompleteDetailEditPresentation(CompleteDetailEditSession, "playlist_detail_edit_refresh_notification_failed");
        }
    }

    private void TryCompleteDetailEditPresentation(Action presentation, string reason)
    {
        try { presentation(); }
        catch (Exception failure) { TryLogPlaylistDropSecondaryFailure(failure, reason); }
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
