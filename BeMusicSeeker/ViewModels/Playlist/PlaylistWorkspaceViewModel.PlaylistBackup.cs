using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    /// <summary>バックアップの実書込みと後片付けを待ち、失敗は元原因・保存先を診断して一回の結果通知へ渡します。任意通知の失敗を保存結果へ加えません。</summary>
    internal Task BackupPlaylistAsync(string fileName)
    {
        BMSPlaylist tables = getPlaylistStore();
        if (tables == null)
        {
            if (PlaylistTreeTables == null || PlaylistTreeTables.Count() == 0)
            {
                return PublishBackupNotification(new PlaylistOperationNotificationOwner.OperationNotification(
                    PlaylistOperationNotificationOwner.OperationNotificationText.FromResource(nameof(BeMusicSeeker.Properties.Resources.Msg_warn_playlist_backup)),
                    nameof(BeMusicSeeker.Properties.Resources.Warning),
                    PlaylistOperationNotificationOwner.OperationNotificationSeverity.Warning));
            }
            TryLogBackupRestoreFailure(new InvalidOperationException("Playlist persistence is not available."),
                "playlist backup path=" + fileName);
            return PublishBackupNotification(new PlaylistOperationNotificationOwner.OperationNotification(
                PlaylistOperationNotificationOwner.OperationNotificationText.FromResource(nameof(BeMusicSeeker.Properties.Resources.Msg_failed_playlist_backup)),
                nameof(BeMusicSeeker.Properties.Resources.Failure),
                PlaylistOperationNotificationOwner.OperationNotificationSeverity.Error));
        }

        return BackupPlaylistAsync(tables, fileName);
    }

    private async Task BackupPlaylistAsync(BMSPlaylist tables, string fileName)
    {
        using PlaylistOperationNotificationOwner.OperationNotificationSession session = tables.OperationNotificationOwner.BeginSession();
        try
        {
            await Task.Run(() => BackupPlaylist(tables, fileName)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            TryLogBackupRestoreFailure(ex, "playlist backup path=" + fileName);
            tables.OperationNotificationOwner.QueueError(
                PlaylistOperationNotificationOwner.OperationNotificationText.FromResourceWithDetail(
                    nameof(BeMusicSeeker.Properties.Resources.Msg_failed_playlist_backup), ex.Message, detailSeparatorLineBreaks: 2),
                nameof(BeMusicSeeker.Properties.Resources.Failure));
        }
        await PublishBackupRestoreNotificationBestEffortAsync(session, "playlist backup notification").ConfigureAwait(false);
    }

    private void BackupPlaylist(BMSPlaylist tables, string fileName)
    {
        if (PlaylistTreeTables == null)
        {
            return;
        }
        if (PlaylistTreeTables.Count() == 0)
        {
            tables.OperationNotificationOwner.QueueWarning(
                PlaylistOperationNotificationOwner.OperationNotificationText.FromResource(nameof(BeMusicSeeker.Properties.Resources.Msg_warn_playlist_backup)),
                nameof(BeMusicSeeker.Properties.Resources.Warning));
            return;
        }

        string playlistDump = tables.GetPlaylistDump();
        AtomicFileWriter.Write(fileName, stagingStream =>
        {
            byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(playlistDump);
            stagingStream.Write(bytes, 0, bytes.Length);
        });
        tables.OperationNotificationOwner.QueueInformation(
            PlaylistOperationNotificationOwner.OperationNotificationText.FromResource(nameof(BeMusicSeeker.Properties.Resources.Msg_success_playlist_backup)),
            nameof(BeMusicSeeker.Properties.Resources.Success));
    }

    private Task PublishBackupNotification(PlaylistOperationNotificationOwner.OperationNotification notification)
    {
        // storeがない経路では操作sessionがないため、確定した通知からreceiptを直接作ります。
        var receipt = PlaylistOperationNotificationOwner.OperationNotificationReceipt.Create([notification]);
        return noticePresenter.BeginRequest(receipt, "playlist backup notification");
    }

    /// <summary>操作の収集先の通知を、操作の受付を持たない終端で依頼します。依頼の失敗は診断だけに残します。</summary>
    private async Task PublishBackupRestoreNotificationBestEffortAsync(
        PlaylistOperationNotificationOwner.OperationNotificationSession session,
        string routeName)
    {
        try
        {
            await PublishPlaylistOperationNotificationReceiptAsync(session, routeName).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            TryLogBackupRestoreFailure(exception, routeName);
        }
    }

    private static void TryLogBackupRestoreFailure(Exception exception, string routeName)
    {
        try
        {
            Ribbit.Logging.NLogWrapper.FileLogger?.Error(exception, routeName + " failed");
        }
        catch
        {
            // 診断先自身の失敗は、確定した結果へ戻さずこの境界で終えます。
        }
    }
}
