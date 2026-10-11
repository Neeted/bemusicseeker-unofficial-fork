using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ApplicationUiSchedulerBoundaryTests
{
    /// <summary>構成時のUI Dispatcherを一回取得し、worker固有のDispatcherではUI所属と判定しないことを確認し、操作するスレッドごとに配送先を取り直す誤りを検出します。</summary>
    [TestMethod]
    public void SchedulerCapturesCompositionDispatcherInsteadOfReResolvingOnWorker()
    {
        Dispatcher uiDispatcher = TestUiDispatcherHost.Dispatcher;
        int providerCalls = 0;
        var scheduler = new WpfUiScheduler(
            () =>
            {
                Interlocked.Increment(ref providerCalls);
                return uiDispatcher!;
            });

        bool uiHasAccess = uiDispatcher.Invoke(() => scheduler.CheckAccess());
        Dispatcher? workerDispatcher = null;
        bool workerHasUiAccess = true;
        var worker = new Thread(() =>
        {
            workerDispatcher = Dispatcher.CurrentDispatcher;
            workerHasUiAccess = scheduler.CheckAccess();
        });
        worker.Start();
        worker.Join();

        Assert.AreEqual(1, providerCalls);
        Assert.IsTrue(uiHasAccess);
        Assert.AreNotSame(uiDispatcher, workerDispatcher);
        Assert.IsFalse(workerHasUiAccess);
    }

    /// <summary>遅延配送と同期呼出し・await後の処理が構成済みUIスレッドで完了することを確認し、完了通知の早期化やworkerへの継続逸脱を検出します。</summary>
    [TestMethod]
    public void SchedulerProvidesNeutralPostInvokeAndAsyncCompletion()
    {
        Dispatcher uiDispatcher = TestUiDispatcherHost.Dispatcher;
        var scheduler = new WpfUiScheduler(() => uiDispatcher);
        bool queuedActionRan = false;
        IUiScheduledOperation queuedOperation = null!;
        uiDispatcher.Invoke(() =>
        {
            queuedOperation = scheduler.Schedule(
                () => queuedActionRan = true,
                UiSchedulePriority.Background);
            Assert.IsFalse(queuedActionRan);
        });
        queuedOperation.Completion.GetAwaiter().GetResult();
        Assert.IsTrue(queuedActionRan);

        int postThreadId = -1;
        IUiScheduledOperation operation = Task.Run(() => scheduler.Schedule(
            () => postThreadId = Thread.CurrentThread.ManagedThreadId,
            UiSchedulePriority.Background)).GetAwaiter().GetResult();

        Assert.IsTrue(operation.IsAccepted);
        operation.Completion.GetAwaiter().GetResult();
        Assert.AreEqual(uiDispatcher.Thread.ManagedThreadId, postThreadId);

        int invokedValue = Task.Run(() => scheduler.Invoke(
            () => 42,
            UiSchedulePriority.ContextIdle)).GetAwaiter().GetResult();
        Assert.AreEqual(42, invokedValue);

        int asyncThreadId = -1;
        Task.Run(() => scheduler.InvokeAsync(
            async () =>
            {
                await Task.Yield();
                asyncThreadId = Thread.CurrentThread.ManagedThreadId;
            },
            UiSchedulePriority.ApplicationIdle)).GetAwaiter().GetResult();
        Assert.AreEqual(uiDispatcher.Thread.ManagedThreadId, asyncThreadId);
    }

}
