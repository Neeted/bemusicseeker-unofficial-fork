using BeMusicSeeker.ViewModels;
namespace BeMusicSeeker.TestSupport.Components;

/// <summary>旧テストとCoreテストで同一の入力・期待値・寿命を共有します。</summary>
internal static class PackageInstallPlaybackTestSupport
{

    /// <summary>停止開始を通知し、呼出元が所有するTaskの終端を導入処理へ渡します。</summary>
    internal sealed class AwaitablePlaybackStop(TaskCompletionSource entered, Task completion) : IChartMutationPlaybackPort
    {
        public Task StopPlaybackForMutationAsync()
        {
            entered.TrySetResult();
            return completion;
        }
    }

}
