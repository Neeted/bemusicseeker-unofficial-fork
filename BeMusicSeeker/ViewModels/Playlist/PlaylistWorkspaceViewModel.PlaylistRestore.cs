using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    /// <summary>
    /// L/Pを副作用前に非待機取得し、読込み・DB・必要UI公開と出力の終端まで保持します。
    /// 拒否や失敗は終了を許可せず、確定後の失敗でも復元済みDBを巻き戻しません。結果通知はL/P解放後に一回試みます。
    /// </summary>
    /// <param name="canApplyBeforeExit">実L/P受付後、終了で失う設定修復入力がないかを最終検査する窓口。拒否なら読込み・DB変更を行いません。</param>
    /// <returns>実復元と必要公開・出力が全て完了した場合だけtrue。任意通知の失敗はこの結果を変えません。</returns>
    internal async Task<bool> RestorePlaylistBackupAsync(string fileName, Func<Task<bool>> canApplyBeforeExit = null)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        BMSLibrary library = getPlaylistLibrary();
        BMSPlaylist tables = GetPlaylistStore();
        using PlaylistOperationNotificationOwner.OperationNotificationSession session =
            tables.OperationNotificationOwner.BeginSession();
        // SQL復元は、出力を止めた表（出力先が他の表と重なる表）を一件の警告で案内します。
        session.WarnStoppedOutputs();
        bool restored = false;
        bool refused = false;
        // 受付の競合だけを案内します。ライブラリの未接続、受付の閉鎖、終了の要求中の不成立は案内しません。
        bool announceBusy = false;
        try
        {
            if (library == null)
            {
                refused = true;
            }
            else if (!library.OperationAdmission.TryEnter(out IDisposable libraryLease))
            {
                refused = true;
                announceBusy = !library.OperationAdmission.IsAdmissionClosed && tables.CanAnnouncePlaylistMutationBusy;
            }
            else
            {
                using (libraryLease)
                {
                    if (!TryEnterPlaylistMutationAdmission(tables, out IDisposable playlistLease))
                    {
                        refused = true;
                        announceBusy = tables.CanAnnouncePlaylistMutationBusy;
                    }
                    else
                    {
                        using (playlistLease)
                        using (LibraryFileMutationCapability authority = tables.CreatePlaylistMutationCapability(playlistLease))
                        {
                            if (canApplyBeforeExit != null && !await canApplyBeforeExit()) { return false; }
                            string playlistDump = await Task.Run(() => File.ReadAllText(fileName, Encoding.UTF8));
                            await tables.RestorePlaylistDumpAsync(playlistDump, authority);
                        }
                        restored = true;
                    }
                }
            }
        }
        catch (Exception exception)
        {
            restored = false;
            TryLogBackupRestoreFailure(exception, "playlist restore path=" + fileName);
            // 必要出力が既に作ったreceiptを使い、表示のためだけの再送出・重複通知を作りません。
            PlaylistOperationNotificationOwner.OperationNotificationReceipt receipt = session.TakeReceipt();
            foreach (PlaylistOperationNotificationOwner.OperationNotification notification in receipt.Notifications)
            {
                session.Add(notification);
            }
            // BMT 出力の失敗と出力を止めた表の警告は別の事実なので、復元の失敗の通知を補う判定では数えません。
            if (!receipt.Notifications.Any(notification => !notification.IsBmtOutputFailure && !notification.IsStoppedOutputsWarning
                && notification.Severity is PlaylistOperationNotificationOwner.OperationNotificationSeverity.Error
                    or PlaylistOperationNotificationOwner.OperationNotificationSeverity.Warning))
            {
                tables.OperationNotificationOwner.QueueError(
                    PlaylistOperationNotificationOwner.OperationNotificationText.FromResourceWithDetail(
                        nameof(BeMusicSeeker.Properties.Resources.Msg_failed_playlist_restore), exception.Message, detailSeparatorLineBreaks: 2),
                    nameof(BeMusicSeeker.Properties.Resources.Error));
            }
        }

        if (refused)
        {
            if (announceBusy) { session.Add(PlaylistOperationNotices.Rejection(PlaylistWorkspaceMutationKind.Restore, isBusy: true)); }
        }
        else if (restored)
        {
            tables.OperationNotificationOwner.QueueInformation(
                PlaylistOperationNotificationOwner.OperationNotificationText.FromResource(nameof(BeMusicSeeker.Properties.Resources.Msg_success_playlist_restore)),
                nameof(BeMusicSeeker.Properties.Resources.Success));
        }
        // L/Pはここまでに解放済みです。表示の完了は待たず、依頼までを操作の終端に含めます。
        await PublishBackupRestoreNotificationBestEffortAsync(session, "playlist restore notification").ConfigureAwait(false);
        return restored;
    }
}
