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
                PublishBackupNotification(
                    BeMusicSeeker.Properties.Resources.Msg_warn_playlist_backup,
                    BeMusicSeeker.Properties.Resources.Warning,
                    PlaylistOperationNotificationOwner.OperationNotificationSeverity.Warning);
            }
            else
            {
                TryLogBackupRestoreFailure(new InvalidOperationException("Playlist persistence is not available."),
                    "playlist backup path=" + fileName);
                PublishBackupNotification(
                    BeMusicSeeker.Properties.Resources.Msg_failed_playlist_backup,
                    BeMusicSeeker.Properties.Resources.Failure,
                    PlaylistOperationNotificationOwner.OperationNotificationSeverity.Error);
            }
            return Task.CompletedTask;
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
                BeMusicSeeker.Properties.Resources.Msg_failed_playlist_backup
                    + Environment.NewLine + Environment.NewLine + ex.Message,
                BeMusicSeeker.Properties.Resources.Failure);
        }
        PublishBackupRestoreNotificationBestEffort(session, "playlist backup notification");
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
                BeMusicSeeker.Properties.Resources.Msg_warn_playlist_backup,
                BeMusicSeeker.Properties.Resources.Warning);
            return;
        }

        string playlistDump = tables.GetPlaylistDump();
        AtomicFileWriter.Write(fileName, stagingStream =>
        {
            byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(playlistDump);
            stagingStream.Write(bytes, 0, bytes.Length);
        });
        tables.OperationNotificationOwner.QueueInformation(
            BeMusicSeeker.Properties.Resources.Msg_success_playlist_backup,
            BeMusicSeeker.Properties.Resources.Success);
    }

    private void PublishBackupNotification(
        string message,
        string caption,
        PlaylistOperationNotificationOwner.OperationNotificationSeverity severity)
    {
        PlaylistOperationNotificationOwner owner = new();
        using PlaylistOperationNotificationOwner.OperationNotificationSession session = owner.BeginSession();
        switch (severity)
        {
            case PlaylistOperationNotificationOwner.OperationNotificationSeverity.Information:
                owner.QueueInformation(message, caption);
                break;
            case PlaylistOperationNotificationOwner.OperationNotificationSeverity.Warning:
                owner.QueueWarning(message, caption);
                break;
            case PlaylistOperationNotificationOwner.OperationNotificationSeverity.Error:
                owner.QueueError(message, caption);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(severity), severity, null);
        }
        PublishBackupRestoreNotificationBestEffort(session, "playlist backup notification");
    }

    private void PublishBackupRestoreNotificationBestEffort(
        PlaylistOperationNotificationOwner.OperationNotificationSession session,
        string routeName)
    {
        try
        {
            PublishPlaylistOperationNotificationReceipt(session, routeName);
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
