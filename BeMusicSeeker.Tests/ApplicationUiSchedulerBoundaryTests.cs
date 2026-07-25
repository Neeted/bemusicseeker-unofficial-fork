using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
        Dispatcher? uiDispatcher = null;
        RunOnStaDispatcherThread(() => uiDispatcher = Dispatcher.CurrentDispatcher);
        int providerCalls = 0;
        var scheduler = new WpfUiScheduler(
            () =>
            {
                Interlocked.Increment(ref providerCalls);
                return uiDispatcher!;
            });

        Dispatcher? workerDispatcher = null;
        bool workerHasUiAccess = true;
        var worker = new Thread(() =>
        {
            workerDispatcher = Dispatcher.CurrentDispatcher;
            workerHasUiAccess = scheduler.CheckAccess();
        });
        worker.Start();
        worker.Join();

        Assert.AreSame(uiDispatcher, scheduler.Dispatcher);
        Assert.AreEqual(1, providerCalls);
        Assert.AreNotSame(uiDispatcher, workerDispatcher);
        Assert.IsFalse(workerHasUiAccess);
    }

    [TestMethod]
    public void InMemoryLifetimePreservesExplicitShutdownAndRestartRoutes()
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
        lifetime.RestartApplication();

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

    private static void RunOnStaDispatcherThread(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (exception != null)
        {
            throw new AssertFailedException(exception.ToString());
        }
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

        public void RestartApplication() => restartApplication();
    }
}
