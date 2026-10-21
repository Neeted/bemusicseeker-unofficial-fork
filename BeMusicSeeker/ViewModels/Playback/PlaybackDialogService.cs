using System;
using System.Threading.Tasks;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Exposes the user decisions and notifications required by playback workflows.
/// </summary>
internal interface IPlaybackDialogService
{
    bool ConfirmTemporaryInstallPlayback();

    void NotifyPlaybackFailure(Exception exception);

    /// <summary>再生設定の項目保存失敗を、再生失敗と区別して通知します。</summary>
    void NotifySettingsSaveFailure(Exception exception);

    /// <summary>一時試聴の回収後、残留を操作単位で一度だけ案内して表示終端を待ちます。表示失敗の隔離は操作所有者が行います。</summary>
    /// <param name="directory">回収できない一時物がある配置先。</param>
    Task NotifyTemporaryFilesRemainAsync(string directory);
}
