using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    internal async Task RestorePlaylistBackupAsync(string fileName)
    {
        if (fileName == null)
        {
            throw new ArgumentNullException(nameof(fileName));
        }

        string playlistDump = await Task.Run(() => File.ReadAllText(fileName, Encoding.UTF8));
        await playlistRestoreUiApplyScheduler(() => RestorePlaylistBackup(playlistDump));
    }

    private void RestorePlaylistBackup(string playlistDump)
    {
        BMSPlaylist tables = GetPlaylistStore();
        using PlaylistOperationNotificationOwner.OperationNotificationSession session =
            tables.OperationNotificationOwner.BeginSession();
        bool unlockAfterOperation = !LR2SongDBExtended.IsProcessLockEnteredByCurrentThread();
        bool lockAcquired = false;
        try
        {
            if (!playlistRestoreUiThreadCheck())
            {
                throw new InvalidOperationException("Playlist restore requires the configured UI thread.");
            }
            if (!LR2SongDBExtended.Lock(new TimeSpan(0, 1, 0)))
            {
                throw new TimeoutException(BeMusicSeeker.Properties.Resources.Msg_error_timeout_dblock_restore);
            }
            lockAcquired = true;
            tables.LoadPlaylistDump(playlistDump);
            tables.ReloadTables();
            tables.BmtOutput.QueueBeatorajaBmtExportAll("RestoreBMSTables");
            tables.OperationNotificationOwner.QueueInformation(
                BeMusicSeeker.Properties.Resources.Msg_success_playlist_restore,
                BeMusicSeeker.Properties.Resources.Success);
        }
        catch (Exception ex)
        {
            tables.OperationNotificationOwner.QueueError(
                BeMusicSeeker.Properties.Resources.Msg_failed_playlist_restore
                    + Environment.NewLine
                    + Environment.NewLine
                    + ex.Message,
                BeMusicSeeker.Properties.Resources.Error);
        }
        finally
        {
            if (unlockAfterOperation && lockAcquired)
            {
                LR2SongDBExtended.Unlock();
            }
            PublishPlaylistOperationNotificationReceipt(session, "playlist restore notification");
        }
    }
}
