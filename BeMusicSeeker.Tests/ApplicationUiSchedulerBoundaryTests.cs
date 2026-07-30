using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ApplicationUiSchedulerBoundaryTests
{
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

    [TestMethod]
    public async Task InMemoryLifetimePreservesExplicitShutdownAndRestartRoutes()
    {
        int shutdownCount = 0;
        int restartCount = 0;
        var lifetime = new TestApplicationLifetimeWithActions(
            firstStartup: true,
            requestShutdown: () => shutdownCount++,
            restartApplication: () => restartCount++);

        Assert.IsTrue(lifetime.IsFirstStartup);
        lifetime.CompleteFirstStartup();
        lifetime.RequestShutdown();
        await lifetime.RestartApplicationAsync();

        Assert.IsFalse(lifetime.IsFirstStartup);
        Assert.AreEqual(1, shutdownCount);
        Assert.AreEqual(1, restartCount);
    }

    [TestMethod]
    public void CultureCatalogExposesPersistedNameAndValueAsOneBoundary()
    {
        var cultures = new Dictionary<string, string>
        {
            ["日本語"] = "ja-JP",
            ["English"] = "en-US"
        };
        var catalog = new TestCultureCatalog(cultures);

        Assert.AreEqual("ja-JP", catalog.Cultures["日本語"]);
        Assert.AreEqual("English", catalog.Cultures.First(pair => pair.Value == "en-US").Key);
    }

    private sealed class TestApplicationLifetimeWithActions : IApplicationLifetimePort
    {
        private readonly Action requestShutdown;
        private readonly Action restartApplication;
        private bool firstStartup;

        internal TestApplicationLifetimeWithActions(
            bool firstStartup,
            Action requestShutdown,
            Action restartApplication)
        {
            this.firstStartup = firstStartup;
            this.requestShutdown = requestShutdown;
            this.restartApplication = restartApplication;
        }

        public bool IsFirstStartup => firstStartup;

        public void CompleteFirstStartup() => firstStartup = false;

        public void MarkCoordinatedShutdownStarted(string reason)
        {
        }

        public void RequestShutdown() => requestShutdown();

        public Task RestartApplicationAsync()
        {
            restartApplication();
            return Task.CompletedTask;
        }
    }
}
