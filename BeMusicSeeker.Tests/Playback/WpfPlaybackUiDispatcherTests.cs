using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class WpfPlaybackUiDispatcherTests
{
    /// <summary>
    /// 保証：Dispatcher未指定または所有thread上ならactionを即時実行し、不要なキュー待ちや呼出し欠落を起こさない。
    /// </summary>
    [TestMethod]
    public void Dispatch_RunsInlineWhenDispatcherIsUnavailableOrCurrent()
    {
        int callCount = 0;
        new WpfPlaybackUiDispatcher(new WpfUiScheduler(() => null!)).Dispatch(() => callCount++);
        new WpfPlaybackUiDispatcher(new WpfUiScheduler(() => Dispatcher.CurrentDispatcher)).Dispatch(() => callCount++);

        Assert.AreEqual(2, callCount);
    }

    /// <summary>
    /// 保証：Dispatcher終了後の再生UI通知はactionを実行せず、終了済み画面へ遅れて反映しない。
    /// </summary>
    /// <remarks>終了済みDispatcherの拒否を確認するため専用STAを停止してから本番adapterへ入力する。</remarks>
    [TestMethod]
    public void Dispatch_DropsActionAfterDispatcherShutdown()
    {
        Dispatcher? shutdownDispatcher = null;
        var thread = new Thread(() =>
        {
            shutdownDispatcher = Dispatcher.CurrentDispatcher;
            shutdownDispatcher.InvokeShutdown();
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.IsNotNull(shutdownDispatcher);
        int callCount = 0;
        var dispatcher = new WpfPlaybackUiDispatcher(new WpfUiScheduler(() => shutdownDispatcher));

        dispatcher.Dispatch(() => callCount++);

        Assert.AreEqual(0, callCount);
    }

    /// <summary>
    /// 別threadからの再生UI通知を所有Dispatcherで一度受信し、投稿元での実行や通知欠落を検出する。
    /// </summary>
    /// <remarks>本番のvoid通知経路を実Dispatcherへ接続し、投稿とaction末尾の受信完了をそれぞれ待つ。</remarks>
    [TestMethod]
    public async Task Dispatch_PostsActionToDispatcherOwnerThread()
    {
        Dispatcher dispatcher = TestUiDispatcherHost.Dispatcher;
        int dispatcherThreadId = -1;
        TestUiDispatcherHost.Invoke(() => dispatcherThreadId = Thread.CurrentThread.ManagedThreadId);
        int postingThreadId = -1;
        int actionThreadId = -1;
        int actionCount = 0;
        bool actionHasDispatcherAccess = false;
        var received = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new WpfPlaybackUiDispatcher(new WpfUiScheduler(() => dispatcher));

        var posted = Task.Run(() =>
        {
            postingThreadId = Thread.CurrentThread.ManagedThreadId;
            adapter.Dispatch(() =>
            {
                actionThreadId = Thread.CurrentThread.ManagedThreadId;
                actionHasDispatcherAccess = dispatcher.CheckAccess();
                Interlocked.Increment(ref actionCount);
                received.TrySetResult(null);
            });
        });
        await posted;
        await received.Task;

        Assert.AreEqual(dispatcherThreadId, actionThreadId);
        Assert.AreNotEqual(postingThreadId, actionThreadId);
        Assert.IsTrue(actionHasDispatcherAccess);
        Assert.AreEqual(1, actionCount);
    }
}
