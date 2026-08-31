using System;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
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
                PublishBackupNotification(
                    BeMusicSeeker.Properties.Resources.Msg_failed_playlist_backup
                        + Environment.NewLine
                        + Environment.NewLine
                        + "Playlist persistence is not available.",
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
        ExceptionDispatchInfo failure = null;
        try
        {
            await Task.Run(() => BackupPlaylist(tables, fileName)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ExceptionDispatchInfo.Capture(ex);
        }
        finally
        {
            try
            {
                PublishPlaylistOperationNotificationReceipt(session, "playlist backup notification");
            }
            catch (Exception ex)
            {
                failure ??= ExceptionDispatchInfo.Capture(ex);
            }
        }
        failure?.Throw();
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

        try
        {
            string playlistDump = tables.GetPlaylistDump();
            File.WriteAllText(fileName, playlistDump);
            tables.OperationNotificationOwner.QueueInformation(
                BeMusicSeeker.Properties.Resources.Msg_success_playlist_backup,
                BeMusicSeeker.Properties.Resources.Success);
        }
        catch (Exception ex)
        {
            tables.OperationNotificationOwner.QueueError(
                BeMusicSeeker.Properties.Resources.Msg_failed_playlist_backup
                    + Environment.NewLine
                    + Environment.NewLine
                    + ex.Message,
                BeMusicSeeker.Properties.Resources.Failure);
            throw;
        }
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
        PublishPlaylistOperationNotificationReceipt(session, "playlist backup notification");
    }
}
