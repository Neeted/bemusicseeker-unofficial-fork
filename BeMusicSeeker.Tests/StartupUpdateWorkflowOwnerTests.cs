using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.Update;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class StartupUpdateWorkflowOwnerTests
{
    [TestMethod]
    public async Task Start_CleansUpChecksAndPublishesNoUpdateTerminal()
    {
        var events = new List<string>();
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () =>
            {
                events.Add("check");
                return Task.FromResult(UpdateCheckResult.NoUpdate("1.0.0.0"));
            },
            cleanup: () => events.Add("cleanup"),
            schedule: action =>
            {
                events.Add("schedule");
                return StartLongRunningAsync(action);
            });
        StartupUpdateWorkflowCompletionReceipt receipt = null!;
        owner.TerminalPublished += published =>
        {
            events.Add("terminal");
            receipt = published;
        };

        Assert.IsTrue(owner.Start());
        Assert.IsFalse(owner.Start());
        await WaitForCompletion(owner);

        CollectionAssert.AreEqual(new[] { "schedule", "cleanup", "check", "terminal" }, events);
        Assert.IsNotNull(receipt);
        Assert.AreEqual(StartupUpdateWorkflowOutcome.NoUpdate, receipt.Outcome);
    }

    [TestMethod]
    public async Task CleanupFailurePublishesFailureBeforeCheckingForUpdates()
    {
        int checkCount = 0;
        int failurePresentationCount = 0;
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () =>
            {
                Interlocked.Increment(ref checkCount);
                return Task.FromResult(UpdateCheckResult.NoUpdate("1.0.0.0"));
            },
            cleanup: () => throw new IOException("cleanup failed"),
            schedule: action => StartLongRunningAsync(action));
        StartupUpdateWorkflowCompletionReceipt receipt = null!;
        owner.TerminalPublished += published => receipt = published;
        owner.FailurePresentationRequested += _ => Interlocked.Increment(ref failurePresentationCount);

        Assert.IsTrue(owner.Start());
        await owner.WaitForIdleAsync();
        await owner.WaitForTerminalAsync();
        Assert.IsNotNull(receipt);
        Assert.AreEqual(StartupUpdateWorkflowOutcome.Failed, receipt.Outcome);
        Assert.IsNotNull(receipt.Exception);
        Assert.AreEqual(1, failurePresentationCount);
        Assert.AreEqual(0, checkCount);
    }

    [TestMethod]
    public async Task FailurePresentationAcknowledgesDurableReceiptOnlyAfterPresentationSucceeds()
    {
        int presentationCount = 0;
        bool acknowledged = false;
        var receiptException = new UpdateFailureReceiptException(
            "durable update failure",
            () => acknowledged = true);
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () => Task.FromResult(UpdateCheckResult.NoUpdate("1.0.0.0")),
            cleanup: () => throw receiptException,
            schedule: action => StartLongRunningAsync(action));
        owner.FailurePresentationRequested += exception =>
        {
            Assert.AreSame(receiptException, exception);
            Interlocked.Increment(ref presentationCount);
        };

        Assert.IsTrue(owner.Start());
        await owner.WaitForIdleAsync();
        await owner.WaitForTerminalAsync();
        Assert.AreEqual(1, presentationCount);
        Assert.IsTrue(acknowledged);
    }

    [TestMethod]
    public async Task FailurePresentationKeepsDurableReceiptWhenPresentationFails()
    {
        bool acknowledged = false;
        var receiptException = new UpdateFailureReceiptException(
            "durable update failure",
            () => acknowledged = true);
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () => Task.FromResult(UpdateCheckResult.NoUpdate("1.0.0.0")),
            cleanup: () => throw receiptException,
            schedule: action => StartLongRunningAsync(action));
        owner.FailurePresentationRequested += _ => throw new InvalidOperationException("presentation failed");

        Assert.IsTrue(owner.Start());
        await owner.WaitForIdleAsync();
        await owner.WaitForTerminalAsync();
        Assert.IsFalse(acknowledged);
    }

    [TestMethod]
    public async Task FailurePresentationKeepsDurableReceiptWhenPresentationIsDeferred()
    {
        bool acknowledged = false;
        var receiptException = new UpdateFailureReceiptException(
            "durable update failure",
            () => acknowledged = true);
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () => Task.FromResult(UpdateCheckResult.NoUpdate("1.0.0.0")),
            cleanup: () => throw receiptException,
            schedule: action => StartLongRunningAsync(action));
        owner.FailurePresentationRequested += _ => receiptException.DeferAcknowledge();

        Assert.IsTrue(owner.Start());
        await owner.WaitForIdleAsync();
        await owner.WaitForTerminalAsync();
        Assert.IsFalse(acknowledged);
    }

    [TestMethod]
    public async Task AvailableUpdate_CancelledPresentationPublishesCancelledTerminal()
    {
        var events = new List<string>();
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () => Task.FromResult(CreateAvailableResult()),
            schedule: action => StartLongRunningAsync(action));
        owner.PresentationRequested += request =>
        {
            events.Add("present");
            request.Complete(null);
        };
        StartupUpdateWorkflowCompletionReceipt receipt = null!;
        owner.TerminalPublished += published =>
        {
            events.Add("terminal");
            receipt = published;
        };

        Assert.IsTrue(owner.Start());
        await WaitForCompletion(owner);

        CollectionAssert.AreEqual(new[] { "present", "terminal" }, events);
        Assert.AreEqual(StartupUpdateWorkflowOutcome.Cancelled, receipt.Outcome);
    }

    [TestMethod]
    public async Task AvailableUpdate_CompletesDownloadPrepareShutdownStartBeforeApplicationShutdown()
    {
        var events = new List<string>();
        var applicationShutdown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () => Task.FromResult(CreateAvailableResult()),
            download: asset =>
            {
                events.Add("download");
                return Task.FromResult("package.zip");
            },
            prepare: packagePath =>
            {
                events.Add("prepare");
                return new FakePreparedUpdaterLaunch(() =>
                {
                    events.Add("start");
                    return new UpdaterLaunchReceipt();
                });
            },
            schedule: action => StartLongRunningAsync(action));
        owner.PresentationRequested += request =>
        {
            events.Add("present");
            request.Complete(CreateAvailableResult().Assets[0]);
        };
        owner.BindShutdownPreparation(reason =>
        {
            events.Add("shutdown_prepare");
            return Task.FromResult(new ShutdownPreparationResult(reason, 12L, false, 0));
        });
        owner.ApplicationShutdownRequested += () =>
        {
            events.Add("application_shutdown");
            applicationShutdown.TrySetResult(true);
        };
        StartupUpdateWorkflowCompletionReceipt receipt = null!;
        owner.TerminalPublished += published =>
        {
            events.Add("terminal");
            receipt = published;
        };

        Assert.IsTrue(owner.Start());
        await WaitForCompletion(owner);
        await applicationShutdown.Task;

        CollectionAssert.AreEqual(
            new[] { "present", "download", "prepare", "start", "shutdown_prepare", "terminal", "application_shutdown" },
            events);
        Assert.AreEqual(StartupUpdateWorkflowOutcome.Applied, receipt.Outcome);
        Assert.IsTrue(receipt.ShutdownPrepared);
    }

    [TestMethod]
    public async Task ClosingWhileCheckIsPendingSuppressesPresentation()
    {
        var checkCompletion = new TaskCompletionSource<UpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int presentationCount = 0;
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () => checkCompletion.Task,
            schedule: action => StartLongRunningAsync(action));
        owner.PresentationRequested += request => Interlocked.Increment(ref presentationCount);
        StartupUpdateWorkflowCompletionReceipt receipt = null!;
        owner.TerminalPublished += published => receipt = published;

        Assert.IsTrue(owner.Start());
        owner.NotifyClosing();
        checkCompletion.SetResult(CreateAvailableResult());

        await WaitForCompletion(owner);
        Assert.AreEqual(0, presentationCount);
        Assert.AreEqual(StartupUpdateWorkflowOutcome.Closing, receipt.Outcome);
    }

    [TestMethod]
    public async Task ClosingWhileDownloadIsPendingCancelsBeforeShutdownPreparation()
    {
        var downloadStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloadCompletion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        int shutdownPreparationCount = 0;
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () => Task.FromResult(CreateAvailableResult()),
            download: asset =>
            {
                downloadStarted.TrySetResult(true);
                return downloadCompletion.Task;
            },
            schedule: action => StartLongRunningAsync(action));
        owner.PresentationRequested += request => request.Complete(CreateAvailableResult().Assets[0]);
        owner.BindShutdownPreparation(_ =>
        {
            Interlocked.Increment(ref shutdownPreparationCount);
            return Task.FromResult(new ShutdownPreparationResult("update", 1L, false, 0));
        });
        StartupUpdateWorkflowCompletionReceipt receipt = null!;
        owner.TerminalPublished += published => receipt = published;

        Assert.IsTrue(owner.Start());
        await downloadStarted.Task;
        Assert.IsTrue(owner.NotifyClosing());
        downloadCompletion.SetResult("package.zip");

        await WaitForCompletion(owner);
        Assert.AreEqual(StartupUpdateWorkflowOutcome.Closing, receipt.Outcome);
        Assert.AreEqual(0, shutdownPreparationCount);
    }

    [TestMethod]
    public async Task DownloadFailureRequestsErrorPresentationWithoutApplicationShutdown()
    {
        var applicationShutdown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int failurePresentationCount = 0;
        int shutdownCount = 0;
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () => Task.FromResult(CreateAvailableResult()),
            download: asset => throw new InvalidOperationException("download failed"),
            schedule: action => StartLongRunningAsync(action));
        owner.PresentationRequested += request => request.Complete(CreateAvailableResult().Assets[0]);
        owner.FailurePresentationRequested += _ => Interlocked.Increment(ref failurePresentationCount);
        owner.ApplicationShutdownRequested += () =>
        {
            Interlocked.Increment(ref shutdownCount);
            applicationShutdown.TrySetResult(true);
        };
        StartupUpdateWorkflowCompletionReceipt receipt = null!;
        owner.TerminalPublished += published => receipt = published;

        Assert.IsTrue(owner.Start());
        await WaitForCompletion(owner);

        Assert.AreEqual(1, failurePresentationCount);
        Assert.AreEqual(0, shutdownCount);
        Assert.IsFalse(applicationShutdown.Task.IsCompleted);
        Assert.AreEqual(StartupUpdateWorkflowOutcome.Failed, receipt.Outcome);
        Assert.IsFalse(receipt.ShutdownPrepared);
    }

    [TestMethod]
    public async Task ShutdownPreparationFailureRequestsErrorPresentationAndApplicationShutdown()
    {
        var applicationShutdown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int failurePresentationCount = 0;
        int shutdownCount = 0;
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () => Task.FromResult(CreateAvailableResult()),
            download: asset => Task.FromResult("package.zip"),
            prepare: packagePath => new FakePreparedUpdaterLaunch(() => new UpdaterLaunchReceipt()),
            schedule: action => StartLongRunningAsync(action));
        owner.PresentationRequested += request => request.Complete(CreateAvailableResult().Assets[0]);
        owner.BindShutdownPreparation(_ => Task.FromException<ShutdownPreparationResult>(new InvalidOperationException("shutdown preparation failed")));
        owner.FailurePresentationRequested += _ => Interlocked.Increment(ref failurePresentationCount);
        owner.ApplicationShutdownRequested += () =>
        {
            Interlocked.Increment(ref shutdownCount);
            applicationShutdown.TrySetResult(true);
        };
        StartupUpdateWorkflowCompletionReceipt receipt = null!;
        owner.TerminalPublished += published => receipt = published;

        Assert.IsTrue(owner.Start());
        await WaitForCompletion(owner);

        Assert.AreEqual(1, failurePresentationCount);
        await applicationShutdown.Task;
        Assert.AreEqual(1, Volatile.Read(ref shutdownCount));
        Assert.AreEqual(StartupUpdateWorkflowOutcome.Failed, receipt.Outcome);
        Assert.IsFalse(receipt.ShutdownPrepared);
    }

    [TestMethod]
    public async Task AvailableUpdateWithoutShutdownPreparationPortFailsExplicitly()
    {
        var applicationShutdown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int failurePresentationCount = 0;
        int shutdownCount = 0;
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () => Task.FromResult(CreateAvailableResult()),
            prepare: packagePath => new FakePreparedUpdaterLaunch(() => new UpdaterLaunchReceipt()),
            schedule: action => StartLongRunningAsync(action));
        owner.PresentationRequested += request => request.Complete(CreateAvailableResult().Assets[0]);
        owner.FailurePresentationRequested += _ => Interlocked.Increment(ref failurePresentationCount);
        owner.ApplicationShutdownRequested += () =>
        {
            Interlocked.Increment(ref shutdownCount);
            applicationShutdown.TrySetResult(true);
        };
        StartupUpdateWorkflowCompletionReceipt receipt = null!;
        owner.TerminalPublished += published => receipt = published;

        Assert.IsTrue(owner.Start());
        await WaitForCompletion(owner);

        Assert.AreEqual(1, failurePresentationCount);
        await applicationShutdown.Task;
        Assert.AreEqual(1, Volatile.Read(ref shutdownCount));
        Assert.AreEqual(StartupUpdateWorkflowOutcome.Failed, receipt.Outcome);
        Assert.IsFalse(receipt.ShutdownPrepared);
    }

    [TestMethod]
    public async Task BindShutdownPreparationRejectsReplacementWhileActive()
    {
        var checkStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkCompletion = new TaskCompletionSource<UpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () =>
            {
                checkStarted.TrySetResult(true);
                return checkCompletion.Task;
            },
            schedule: action => StartLongRunningAsync(action));
        Func<string, Task<ShutdownPreparationResult>> firstPort = _ => Task.FromResult(new ShutdownPreparationResult("update", 1L, false, 0));
        owner.BindShutdownPreparation(firstPort);

        Assert.IsTrue(owner.Start());
        await checkStarted.Task;
        Assert.ThrowsException<InvalidOperationException>(() => owner.BindShutdownPreparation(_ => Task.FromResult(new ShutdownPreparationResult("update", 2L, false, 0))));

        owner.NotifyClosing();
        checkCompletion.SetResult(CreateAvailableResult());
        await owner.WaitForIdleAsync();
    }

    [TestMethod]
    public async Task ShutdownPreparationFailureRetainsBoundaryForDelayedUiNotification()
    {
        var failurePresentation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var applicationShutdown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int launchAbortCount = 0;
        int applicationShutdownCount = 0;
        bool shutdownPreparationWasObserved = false;
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () => Task.FromResult(CreateAvailableResult()),
            prepare: packagePath => new FakePreparedUpdaterLaunch(
                () => new UpdaterLaunchReceipt(() => Interlocked.Increment(ref launchAbortCount))),
            schedule: action => StartLongRunningAsync(action),
            dispatch: action => _ = Task.Run(action));
        owner.PresentationRequested += request => request.Complete(CreateAvailableResult().Assets[0]);
        owner.BindShutdownPreparation(_ => Task.FromException<ShutdownPreparationResult>(new InvalidOperationException("shutdown preparation failed")));
        owner.FailurePresentationRequested += _ =>
        {
            shutdownPreparationWasObserved = owner.IsShutdownPreparationStarted;
            failurePresentation.TrySetResult(true);
        };
        owner.ApplicationShutdownRequested += () =>
        {
            Interlocked.Increment(ref applicationShutdownCount);
            applicationShutdown.TrySetResult(true);
        };
        StartupUpdateWorkflowCompletionReceipt receipt = null!;
        owner.TerminalPublished += published => receipt = published;

        Assert.IsTrue(owner.Start());
        await failurePresentation.Task;
        await WaitForCompletion(owner);

        Assert.IsTrue(shutdownPreparationWasObserved);
        Assert.AreEqual(1, launchAbortCount);
        Assert.AreEqual(StartupUpdateWorkflowOutcome.Failed, receipt.Outcome);
        await applicationShutdown.Task;
        Assert.AreEqual(1, Volatile.Read(ref applicationShutdownCount));
    }

    [TestMethod]
    public async Task ProcessStartFailureBeforeShutdownPreparationDeletesPackageAndKeepsApplicationOpen()
    {
        var events = new List<string>();
        var applicationShutdown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () => Task.FromResult(CreateAvailableResult()),
            download: asset => Task.FromResult("package.zip"),
            prepare: packagePath => new FakePreparedUpdaterLaunch(() => null!),
            delete: packagePath => events.Add("delete"),
            schedule: action => StartLongRunningAsync(action));
        owner.PresentationRequested += request => request.Complete(CreateAvailableResult().Assets[0]);
        int shutdownPreparationCount = 0;
        owner.BindShutdownPreparation(_ =>
        {
            Interlocked.Increment(ref shutdownPreparationCount);
            return Task.FromResult(new ShutdownPreparationResult("update", 1L, false, 0));
        });
        owner.ApplicationShutdownRequested += () =>
        {
            events.Add("application_shutdown");
            applicationShutdown.TrySetResult(true);
        };
        owner.FailurePresentationRequested += _ => events.Add("failure_presentation");
        StartupUpdateWorkflowCompletionReceipt receipt = null!;
        owner.TerminalPublished += published =>
        {
            events.Add("terminal");
            receipt = published;
        };

        Assert.IsTrue(owner.Start());
        await WaitForCompletion(owner);

        CollectionAssert.AreEqual(new[] { "delete", "failure_presentation", "terminal" }, events);
        Assert.AreEqual(StartupUpdateWorkflowOutcome.Failed, receipt.Outcome);
        Assert.AreEqual(0, shutdownPreparationCount);
        Assert.IsFalse(receipt.ShutdownPrepared);
        Assert.IsFalse(applicationShutdown.Task.IsCompleted);
    }

    [TestMethod]
    public async Task SchedulerFailurePublishesFailureAndLeavesOwnerIdle()
    {
        int failurePresentationCount = 0;
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () => Task.FromResult(UpdateCheckResult.NoUpdate("1.0.0.0")),
            schedule: action => throw new InvalidOperationException("scheduler failed"));
        owner.FailurePresentationRequested += _ => Interlocked.Increment(ref failurePresentationCount);
        StartupUpdateWorkflowCompletionReceipt receipt = null!;
        owner.TerminalPublished += published => receipt = published;

        Assert.IsTrue(owner.Start());
        await owner.WaitForIdleAsync();
        Assert.IsTrue(owner.IsIdle);
        Assert.AreEqual(1, failurePresentationCount);
        Assert.AreEqual(StartupUpdateWorkflowOutcome.Failed, receipt.Outcome);
    }

    private static Task StartLongRunningAsync(Func<Task> action)
    {
        return Task.Factory.StartNew(
                action,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default)
            .Unwrap();
    }

    private static async Task WaitForCompletion(StartupUpdateWorkflowOwner owner)
    {
        await Task.WhenAll(owner.WaitForIdleAsync(), owner.WaitForTerminalAsync());
    }

    private static StartupUpdateWorkflowOwner CreateOwner(
        Func<Task<UpdateCheckResult>> check,
        Func<UpdateAssetInfo, Task<string>>? download = null,
        Func<string, IPreparedUpdaterLaunch>? prepare = null,
        Action? cleanup = null,
        Action<string>? delete = null,
        Func<Func<Task>, Task>? schedule = null,
        Action<Action>? dispatch = null)
    {
        return new StartupUpdateWorkflowOwner(
            check,
            download ?? (asset => Task.FromResult("package.zip")),
            prepare ?? (packagePath => new FakePreparedUpdaterLaunch(() => new UpdaterLaunchReceipt())),
            cleanup ?? (() => { }),
            delete ?? (_ => { }),
            schedule ?? (action => StartLongRunningAsync(action)),
            dispatch ?? (action => action()));
    }

    private static UpdateCheckResult CreateAvailableResult()
    {
        return UpdateCheckResult.Available(
            new Version(2, 0, 0, 0),
            "1.0.0.0",
            "2.0.0.0",
            [new UpdateAssetInfo
            {
                Kind = "app",
                Label = "App",
                FileName = "app.zip",
                Url = "https://example.test/app.zip",
                Sha256 = new string('a', 64),
                SizeBytes = 1,
                IncludesChartInfoMetadata = false
            }],
            "https://example.test/releases/v2.0.0.0");
    }

    private sealed class FakePreparedUpdaterLaunch : IPreparedUpdaterLaunch
    {
        private readonly Func<UpdaterLaunchReceipt> start;

        internal FakePreparedUpdaterLaunch(Func<UpdaterLaunchReceipt> start)
        {
            this.start = start;
        }

        public UpdaterLaunchReceipt Start()
        {
            return start();
        }
    }
}
