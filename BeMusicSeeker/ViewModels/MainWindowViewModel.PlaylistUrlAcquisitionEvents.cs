using System;
using Livet;

namespace BeMusicSeeker.ViewModels;

public partial class MainWindowViewModel
{
    private void PlaylistWorkspacePlaylistUrlDownloadStatusChanged(
        object sender,
        PlaylistUrlDownloadStatusSnapshot snapshot)
    {
        DispatchMainChartListAction(() => ProgressHub.UpdatePlaylistUrlDownloadStatus(snapshot));
    }
}
