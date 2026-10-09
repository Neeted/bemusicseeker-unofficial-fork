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
    /// <summary>L/Pを副作用前に非待機取得し、バックアップの読込み・DB・UI適用から必要出力・通知の実終端まで保持します。Busyでは読み込みを開始せず、成功後だけ成功通知へ進みます。</summary>
    internal async Task RestorePlaylistBackupAsync(string fileName)
    {
        if (fileName == null)
        {
            throw new ArgumentNullException(nameof(fileName));
        }

        BMSLibrary library = getPlaylistLibrary();
        BMSPlaylist tables = GetPlaylistStore();
        if (library == null || !library.OperationAdmission.TryEnter(out IDisposable libraryLease))
        {
            RaiseMutationRejected(PlaylistWorkspaceMutationKind.Restore, isBusy: true, isStale: false);
            return;
        }
        using IDisposable acceptedLibrary = libraryLease;
        if (!tables.TryEnterPlaylistMutation(out IDisposable playlistLease))
        {
            RaiseMutationRejected(PlaylistWorkspaceMutationKind.Restore, isBusy: true, isStale: false);
            return;
        }
        using IDisposable acceptedPlaylist = playlistLease;
        using LibraryFileMutationCapability authority = tables.CreatePlaylistMutationCapability(acceptedPlaylist);
        string playlistDump = await Task.Run(() => File.ReadAllText(fileName, Encoding.UTF8));
        using PlaylistOperationNotificationOwner.OperationNotificationSession session =
            tables.OperationNotificationOwner.BeginSession();
        ExceptionDispatchInfo failure = null;
        try
        {
            await tables.RestorePlaylistDumpAsync(playlistDump, authority);
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
