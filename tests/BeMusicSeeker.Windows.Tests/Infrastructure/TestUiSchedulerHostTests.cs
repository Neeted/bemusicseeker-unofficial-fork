using BeMusicSeeker.Models;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Wpf;

namespace BeMusicSeeker.Windows.Tests.Infrastructure;

/// <summary>テスト用UIスケジューラーの既定が、共有ホストのスレッドでだけホストのDispatcherを捕捉することを保証します。</summary>
[TestClass]
public sealed class TestUiSchedulerHostTests
{
    /// <summary>
    /// 共有ホストのスレッドで構築した既定の窓口は、利用可能で、予約した操作をホストのDispatcherへ投稿して未実行のまま保ち、
    /// 消化後にホストのスレッドで実行します。ホストの起動中でも、ホスト外のスレッドでは<see cref="TestUiScheduler.ResolveOwnedDispatcher"/>がnullを返します。
    /// 常にnullを返してホストへ投稿しない誤りと、CheckAccessを外してホスト外のスレッドでもホストのDispatcherを返す誤りを検出します。
    /// </summary>
    [TestMethod]
    public void CreateDefault_OnHostThread_PostsToHostDispatcherAndWorkerResolvesNull()
    {
        int hostThreadId = 0;
        int ranThreadId = 0;
        bool available = false;
        bool completedAfterSchedule = true;
        TestUiDispatcherHost.Invoke(() =>
        {
            hostThreadId = Environment.CurrentManagedThreadId;
            var scheduler = TestUiScheduler.CreateDefault();
            available = scheduler.IsAvailable;
            IUiScheduledOperation operation = scheduler.Schedule(() => ranThreadId = Environment.CurrentManagedThreadId);
            completedAfterSchedule = operation.IsCompleted;
        });

        Assert.IsTrue(available, "ホストのDispatcherを捕捉した窓口が利用可能ではありません。");
        Assert.IsFalse(completedAfterSchedule, "予約した操作がホストのDispatcherへ投稿されず、その場で完了しています。");

        TestUiDispatcherHost.ProcessQueuedBackground();

        Assert.AreEqual(hostThreadId, ranThreadId, "予約した操作が、消化後にホストのスレッドで実行されていません。");
        Assert.IsNull(TestUiScheduler.ResolveOwnedDispatcher(), "ホスト外のスレッドでホストのDispatcherを返しています。");
    }
}
