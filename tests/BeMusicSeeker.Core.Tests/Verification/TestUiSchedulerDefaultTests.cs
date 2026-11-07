using BeMusicSeeker.Models;
using BeMusicSeeker.TestSupport.Components;

namespace BeMusicSeeker.Core.Tests.Verification;

/// <summary>共有WPFホストを起動せず、テスト用UIスケジューラーの既定が予約した操作を必ず実行することを保証します。</summary>
[TestClass]
public sealed class TestUiSchedulerDefaultTests
{
    /// <summary>
    /// メッセージポンプを持たない専用スレッドで構築した既定の窓口は、予約した操作をその場で実行して受理済みにし、
    /// 別スレッドからのInvokeも戻ることを確認します。
    /// ポンプされないDispatcherを捕捉して予約を実行しない誤りと、ポンプされている別のDispatcherへ投稿する誤りを、
    /// 実行スレッドと完了状態で検出します。旧実装ではSchedule側の表明が先に失敗します。
    /// </summary>
    /// <remarks>
    /// 予約の実行は同期的に判定するため待機しません。同期実行の完了だけを<see cref="ConcurrencyProbe.StallTimeout"/>以内で判定します。
    /// </remarks>
    [TestMethod]
    public void CreateDefault_OnThreadWithoutPumpedDispatcher_ExecutesScheduledAndCrossThreadInvokedActions()
    {
        int scheduledThreadId = 0;
        int constructionThreadId = 0;
        (TestUiScheduler Scheduler, bool Accepted, bool Completed, bool CanExecuteInline) created = ConcurrencyProbe.StartDedicated(() =>
        {
            constructionThreadId = Environment.CurrentManagedThreadId;
            var scheduler = TestUiScheduler.CreateDefault();
            IUiScheduledOperation operation = scheduler.Schedule(() => scheduledThreadId = Environment.CurrentManagedThreadId);
            return (scheduler, operation.IsAccepted, operation.IsCompleted, scheduler.CanExecuteInline);
        }).GetAwaiter().GetResult();

        Assert.IsTrue(created.Accepted, "予約が受理されていません。");
        Assert.IsTrue(created.Completed, "Schedule直後に操作が完了していません。");
        Assert.IsTrue(created.CanExecuteInline, "Dispatcherを持たない窓口がインライン実行可能になっていません。");
        Assert.AreEqual(constructionThreadId, scheduledThreadId, "予約した操作が、Scheduleを呼んだスレッドで実行されていません。");

        bool invokedRan = false;
        int invokedValue = 0;
        Task invoke = ConcurrencyProbe.StartDedicated(() =>
        {
            created.Scheduler.Invoke(() => invokedRan = true);
            invokedValue = created.Scheduler.Invoke(() => 7);
        });
        ConcurrencyProbe.AssertCompletesWhileHeld(invoke, "別スレッドからのUIスケジューラーのInvoke");

        Assert.IsTrue(invokedRan);
        Assert.AreEqual(7, invokedValue);
    }
}
