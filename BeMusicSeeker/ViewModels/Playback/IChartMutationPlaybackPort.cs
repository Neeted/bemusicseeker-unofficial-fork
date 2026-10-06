using System.Threading.Tasks;

namespace BeMusicSeeker.ViewModels;

/// <summary>譜面・音源の物理変更に先立って、現在曲と先読みを全て停止する共通境界です。</summary>
internal interface IChartMutationPlaybackPort
{
    /// <summary>既存の譜面変更受付を保持して呼び、開始済みの処理とデコードの終了を待ちます。失敗時は物理変更へ進みません。</summary>
    Task StopPlaybackForMutationAsync();
}
