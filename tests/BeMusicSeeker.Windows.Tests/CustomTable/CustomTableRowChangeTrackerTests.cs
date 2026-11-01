using System.Windows.Threading;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.Views;

namespace BeMusicSeeker.Tests;


[TestClass]
public sealed class CustomTableRowChangeTrackerTests
{

    /// <summary>Dispatcher実行前の三要求を一回へまとめ、完了後の次要求は新たに実行することを確認し、過剰な再描画や次回要求の取り落としを検出します。</summary>
    [TestMethod]
    public void RedrawScheduler_CoalescesRequestsUntilDispatcherRuns()
    {
        TestUiDispatcherHost.Invoke(delegate
        {
            int redrawCount = 0;
            var redrawApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var scheduler = new CustomTableRedrawScheduler(
                Dispatcher.CurrentDispatcher,
                delegate { redrawCount++; redrawApplied.TrySetResult(); });

            scheduler.Request();
            scheduler.Request();
            scheduler.Request();

            Assert.AreEqual(1, scheduler.ScheduledCount);
            Assert.AreEqual(0, redrawCount);

            TestUiDispatcherHost.AwaitTaskOnDispatcher(redrawApplied.Task, "redraw-first-action");

            Assert.AreEqual(1, redrawCount);

            redrawApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            scheduler.Request();
            TestUiDispatcherHost.AwaitTaskOnDispatcher(redrawApplied.Task, "redraw-second-action");

            Assert.AreEqual(2, scheduler.ScheduledCount);
            Assert.AreEqual(2, redrawCount);
        });
    }

    /// <summary>workerからの複数再描画要求はまとめてUIスレッドへ配送することを確認し、workerで描画を行う誤りや二重配送を検出します。</summary>
    [TestMethod]
    public void RedrawScheduler_BackgroundRequestRunsActionOnDispatcherThread()
    {
        TestUiDispatcherHost.Invoke(delegate
        {
            int dispatcherThreadId = Thread.CurrentThread.ManagedThreadId;
            int actionThreadId = -1;
            var redrawApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var scheduler = new CustomTableRedrawScheduler(
                Dispatcher.CurrentDispatcher,
                delegate { actionThreadId = Thread.CurrentThread.ManagedThreadId; redrawApplied.TrySetResult(); });

            var worker = new Thread((ThreadStart)delegate
            {
                scheduler.Request();
                scheduler.Request();
            });
            worker.Start();
            worker.Join();

            Assert.AreEqual(-1, actionThreadId);

            TestUiDispatcherHost.AwaitTaskOnDispatcher(redrawApplied.Task, "redraw-worker-action");

            Assert.AreEqual(dispatcherThreadId, actionThreadId);
            Assert.AreEqual(1, scheduler.ScheduledCount);
        });
    }

}
