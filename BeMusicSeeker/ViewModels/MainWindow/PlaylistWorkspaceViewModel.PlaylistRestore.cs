using System;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    /// <summary>バックアップを読み込み、DB と UI 適用の成功後だけ出力と成功通知へ進みます。</summary>
    internal async Task RestorePlaylistBackupAsync(string fileName)
    {
        if (fileName == null)
        {
            throw new ArgumentNullException(nameof(fileName));
        }

        string playlistDump = await Task.Run(() => File.ReadAllText(fileName, Encoding.UTF8));
        BMSPlaylist tables = GetPlaylistStore();
        using PlaylistOperationNotificationOwner.OperationNotificationSession session =
            tables.OperationNotificationOwner.BeginSession();
        ExceptionDispatchInfo failure = null;
        try
        {
            await tables.RestorePlaylistDumpAsync(playlistDump);
            tables.BmtOutput.QueueBeatorajaBmtExportAll("RestoreBMSTables");
            tables.OperationNotificationOwner.QueueInformation(
                BeMusicSeeker.Properties.Resources.Msg_success_playlist_restore,
                BeMusicSeeker.Properties.Resources.Success);
        }
        catch (Exception ex)
        {
            failure = ExceptionDispatchInfo.Capture(ex);
            tables.OperationNotificationOwner.QueueError(
                BeMusicSeeker.Properties.Resources.Msg_failed_playlist_restore
                    + Environment.NewLine
                    + Environment.NewLine
                    + ex.Message,
                BeMusicSeeker.Properties.Resources.Error);
        }
        finally
        {
            try
            {
                PublishPlaylistOperationNotificationReceipt(session, "playlist restore notification");
            }
            catch (Exception ex)
            {
                failure ??= ExceptionDispatchInfo.Capture(ex);
            }
        }
        failure?.Throw();
    }
}
