using System;
using Livet;

namespace BeMusicSeeker.ViewModels;

public partial class MainWindowViewModel
{
    private void PlaylistWorkspacePlaylistUrlDownloadStatusChanged(
        object sender,
        PlaylistUrlDownloadStatusSnapshot snapshot)
    {
        Action reflect = delegate
        {
            latestPlaylistUrlDownloadStatus = snapshot ?? PlaylistUrlDownloadStatusSnapshot.Inactive;
            RefreshInstallPipelineStatus();
        };
        if (DispatcherHelper.UIDispatcher == null || DispatcherHelper.UIDispatcher.CheckAccess())
        {
            reflect();
        }
        else
        {
            DispatcherHelper.UIDispatcher.BeginInvoke(reflect);
        }
    }
}
