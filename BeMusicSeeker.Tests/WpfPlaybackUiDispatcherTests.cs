using System;
using System.Threading;
using System.Windows.Threading;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class WpfPlaybackUiDispatcherTests
{
    [TestMethod]
    public void Dispatch_RunsInlineWhenDispatcherIsUnavailableOrCurrent()
    {
        int callCount = 0;
        new WpfPlaybackUiDispatcher(() => null!).Dispatch(() => callCount++);
        new WpfPlaybackUiDispatcher(() => Dispatcher.CurrentDispatcher).Dispatch(() => callCount++);

        Assert.AreEqual(2, callCount);
    }

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
        var dispatcher = new WpfPlaybackUiDispatcher(() => shutdownDispatcher);

        dispatcher.Dispatch(() => callCount++);

        Assert.AreEqual(0, callCount);
    }

    [TestMethod]
    public void Dispatch_PostsDataBindOperationToDispatcherOwnerThread()
    {
        Dispatcher? dispatcher = null;
        int dispatcherThreadId = -1;
        int actionThreadId = -1;
        int dataBindOperationPosted = 0;
        using var ready = new ManualResetEventSlim();
        using var completed = new ManualResetEventSlim();
        using var shutdown = new CancellationTokenSource();
        var thread = new Thread(() =>
        {
            dispatcherThreadId = Thread.CurrentThread.ManagedThreadId;
            dispatcher = Dispatcher.CurrentDispatcher;
            using CancellationTokenRegistration registration = shutdown.Token.Register(
                () => dispatcher.BeginInvokeShutdown(DispatcherPriority.Send));
            dispatcher.Hooks.OperationPosted += (_, e) =>
            {
                if (e.Operation.Priority == DispatcherPriority.DataBind)
                {
                    Interlocked.Exchange(ref dataBindOperationPosted, 1);
                }
            };
            ready.Set();
            Dispatcher.Run();
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try
        {
            Assert.IsTrue(ready.Wait(TimeSpan.FromSeconds(5)));
            Assert.IsNotNull(dispatcher);
            var adapter = new WpfPlaybackUiDispatcher(() => dispatcher);

            adapter.Dispatch(() =>
            {
                actionThreadId = Thread.CurrentThread.ManagedThreadId;
                completed.Set();
            });

            Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(dispatcherThreadId, actionThreadId);
            Assert.AreNotEqual(Thread.CurrentThread.ManagedThreadId, actionThreadId);
            Assert.AreEqual(1, Volatile.Read(ref dataBindOperationPosted));
        }
        finally
        {
            shutdown.Cancel();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)));
        }
    }
}
