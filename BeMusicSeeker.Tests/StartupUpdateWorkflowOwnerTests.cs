using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    public void Start_CleansUpChecksAndPublishesNoUpdateTerminal()
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
                return Task.Run(action);
            });
        StartupUpdateWorkflowCompletionReceipt receipt = null!;
        owner.TerminalPublished += published =>
        {
            events.Add("terminal");
            receipt = published;
        };

        Assert.IsTrue(owner.Start());
        Assert.IsFalse(owner.Start());
        Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle && receipt != null, TimeSpan.FromSeconds(5)));

        CollectionAssert.AreEqual(new[] { "schedule", "cleanup", "check", "terminal" }, events);
        Assert.IsNotNull(receipt);
        Assert.AreEqual(StartupUpdateWorkflowOutcome.NoUpdate, receipt.Outcome);
    }

    [TestMethod]
    public void AvailableUpdate_CancelledPresentationPublishesCancelledTerminal()
    {
        var events = new List<string>();
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () => Task.FromResult(CreateAvailableResult()),
            schedule: action => Task.Run(action));
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
        Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle && receipt != null, TimeSpan.FromSeconds(5)));

        CollectionAssert.AreEqual(new[] { "present", "terminal" }, events);
        Assert.AreEqual(StartupUpdateWorkflowOutcome.Cancelled, receipt.Outcome);
    }

    [TestMethod]
    public void AvailableUpdate_CompletesDownloadPrepareShutdownStartBeforeApplicationShutdown()
    {
        var events = new List<string>();
        var applicationShutdown = new ManualResetEventSlim();
        Process process = new();
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
                    return process;
                });
            },
            schedule: action => Task.Run(action));
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
            applicationShutdown.Set();
        };
        StartupUpdateWorkflowCompletionReceipt receipt = null!;
        owner.TerminalPublished += published =>
        {
            events.Add("terminal");
            receipt = published;
        };

        Assert.IsTrue(owner.Start());
        Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle && receipt != null && applicationShutdown.IsSet, TimeSpan.FromSeconds(5)));

        CollectionAssert.AreEqual(
            new[] { "present", "download", "prepare", "shutdown_prepare", "start", "terminal", "application_shutdown" },
            events);
        Assert.AreEqual(StartupUpdateWorkflowOutcome.Applied, receipt.Outcome);
        Assert.IsTrue(receipt.ShutdownPrepared);
        process.Dispose();
    }

    [TestMethod]
    public void ClosingWhileCheckIsPendingSuppressesPresentation()
    {
        var checkCompletion = new TaskCompletionSource<UpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int presentationCount = 0;
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () => checkCompletion.Task,
            schedule: action => Task.Run(action));
        owner.PresentationRequested += request => Interlocked.Increment(ref presentationCount);
        StartupUpdateWorkflowCompletionReceipt receipt = null!;
        owner.TerminalPublished += published => receipt = published;

        Assert.IsTrue(owner.Start());
        owner.NotifyClosing();
        checkCompletion.SetResult(CreateAvailableResult());

        Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle && receipt != null, TimeSpan.FromSeconds(5)));
        Assert.AreEqual(0, presentationCount);
        Assert.AreEqual(StartupUpdateWorkflowOutcome.Closing, receipt.Outcome);
    }

    [TestMethod]
    public void ClosingWhileDownloadIsPendingCancelsBeforeShutdownPreparation()
    {
        var downloadStarted = new ManualResetEventSlim();
        var downloadCompletion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        int shutdownPreparationCount = 0;
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () => Task.FromResult(CreateAvailableResult()),
            download: asset =>
            {
                downloadStarted.Set();
                return downloadCompletion.Task;
            },
            schedule: action => Task.Run(action));
        owner.PresentationRequested += request => request.Complete(CreateAvailableResult().Assets[0]);
        owner.BindShutdownPreparation(_ =>
        {
            Interlocked.Increment(ref shutdownPreparationCount);
            return Task.FromResult(new ShutdownPreparationResult("update", 1L, false, 0));
        });
        StartupUpdateWorkflowCompletionReceipt receipt = null!;
        owner.TerminalPublished += published => receipt = published;

        Assert.IsTrue(owner.Start());
        Assert.IsTrue(downloadStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(owner.NotifyClosing());
        downloadCompletion.SetResult("package.zip");

        Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle && receipt != null, TimeSpan.FromSeconds(5)));
        Assert.AreEqual(StartupUpdateWorkflowOutcome.Closing, receipt.Outcome);
        Assert.AreEqual(0, shutdownPreparationCount);
    }

    [TestMethod]
    public void DownloadFailureRequestsErrorPresentationWithoutApplicationShutdown()
    {
        int failurePresentationCount = 0;
        int shutdownCount = 0;
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () => Task.FromResult(CreateAvailableResult()),
            download: asset => throw new InvalidOperationException("download failed"),
            schedule: action => Task.Run(action));
        owner.PresentationRequested += request => request.Complete(CreateAvailableResult().Assets[0]);
        owner.FailurePresentationRequested += _ => Interlocked.Increment(ref failurePresentationCount);
        owner.ApplicationShutdownRequested += () => Interlocked.Increment(ref shutdownCount);
        StartupUpdateWorkflowCompletionReceipt receipt = null!;
        owner.TerminalPublished += published => receipt = published;

        Assert.IsTrue(owner.Start());
        Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle && receipt != null, TimeSpan.FromSeconds(5)));

        Assert.AreEqual(1, failurePresentationCount);
        Assert.AreEqual(0, shutdownCount);
        Assert.AreEqual(StartupUpdateWorkflowOutcome.Failed, receipt.Outcome);
        Assert.IsFalse(receipt.ShutdownPrepared);
    }

    [TestMethod]
    public void ShutdownPreparationFailureRequestsErrorPresentationWithoutApplicationShutdown()
    {
        int failurePresentationCount = 0;
        int shutdownCount = 0;
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () => Task.FromResult(CreateAvailableResult()),
            download: asset => Task.FromResult("package.zip"),
            prepare: packagePath => new FakePreparedUpdaterLaunch(() => new Process()),
            schedule: action => Task.Run(action));
        owner.PresentationRequested += request => request.Complete(CreateAvailableResult().Assets[0]);
        owner.BindShutdownPreparation(_ => Task.FromException<ShutdownPreparationResult>(new InvalidOperationException("shutdown preparation failed")));
        owner.FailurePresentationRequested += _ => Interlocked.Increment(ref failurePresentationCount);
        owner.ApplicationShutdownRequested += () => Interlocked.Increment(ref shutdownCount);
        StartupUpdateWorkflowCompletionReceipt receipt = null!;
        owner.TerminalPublished += published => receipt = published;

        Assert.IsTrue(owner.Start());
        Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle && receipt != null, TimeSpan.FromSeconds(5)));

        Assert.AreEqual(1, failurePresentationCount);
        Assert.AreEqual(0, shutdownCount);
        Assert.AreEqual(StartupUpdateWorkflowOutcome.Failed, receipt.Outcome);
        Assert.IsFalse(receipt.ShutdownPrepared);
    }

    [TestMethod]
    public void AvailableUpdateWithoutShutdownPreparationPortFailsExplicitly()
    {
        int failurePresentationCount = 0;
        int shutdownCount = 0;
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () => Task.FromResult(CreateAvailableResult()),
            prepare: packagePath => new FakePreparedUpdaterLaunch(() => new Process()),
            schedule: action => Task.Run(action));
        owner.PresentationRequested += request => request.Complete(CreateAvailableResult().Assets[0]);
        owner.FailurePresentationRequested += _ => Interlocked.Increment(ref failurePresentationCount);
        owner.ApplicationShutdownRequested += () => Interlocked.Increment(ref shutdownCount);
        StartupUpdateWorkflowCompletionReceipt receipt = null!;
        owner.TerminalPublished += published => receipt = published;

        Assert.IsTrue(owner.Start());
        Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle && receipt != null, TimeSpan.FromSeconds(5)));

        Assert.AreEqual(1, failurePresentationCount);
        Assert.AreEqual(0, shutdownCount);
        Assert.AreEqual(StartupUpdateWorkflowOutcome.Failed, receipt.Outcome);
        Assert.IsFalse(receipt.ShutdownPrepared);
    }

    [TestMethod]
    public void BindShutdownPreparationRejectsReplacementWhileActive()
    {
        var checkStarted = new ManualResetEventSlim();
        var checkCompletion = new TaskCompletionSource<UpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () =>
            {
                checkStarted.Set();
                return checkCompletion.Task;
            },
            schedule: action => Task.Run(action));
        Func<string, Task<ShutdownPreparationResult>> firstPort = _ => Task.FromResult(new ShutdownPreparationResult("update", 1L, false, 0));
        owner.BindShutdownPreparation(firstPort);

        Assert.IsTrue(owner.Start());
        Assert.IsTrue(checkStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.ThrowsException<InvalidOperationException>(() => owner.BindShutdownPreparation(_ => Task.FromResult(new ShutdownPreparationResult("update", 2L, false, 0))));

        owner.NotifyClosing();
        checkCompletion.SetResult(CreateAvailableResult());
        Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle, TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public void ShutdownPreparationFailureRetainsBoundaryForDelayedUiNotification()
    {
        var failurePresentation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool shutdownPreparationWasObserved = false;
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () => Task.FromResult(CreateAvailableResult()),
            prepare: packagePath => new FakePreparedUpdaterLaunch(() => new Process()),
            schedule: action => Task.Run(action),
            dispatch: action => _ = Task.Run(action));
        owner.PresentationRequested += request => request.Complete(CreateAvailableResult().Assets[0]);
        owner.BindShutdownPreparation(_ => Task.FromException<ShutdownPreparationResult>(new InvalidOperationException("shutdown preparation failed")));
        owner.FailurePresentationRequested += _ =>
        {
            shutdownPreparationWasObserved = owner.IsShutdownPreparationStarted;
            failurePresentation.TrySetResult(true);
        };
        StartupUpdateWorkflowCompletionReceipt receipt = null!;
        owner.TerminalPublished += published => receipt = published;

        Assert.IsTrue(owner.Start());
        Assert.IsTrue(failurePresentation.Task.Wait(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle && receipt != null, TimeSpan.FromSeconds(5)));

        Assert.IsTrue(shutdownPreparationWasObserved);
        Assert.AreEqual(StartupUpdateWorkflowOutcome.Failed, receipt.Outcome);
    }

    [TestMethod]
    public void ProcessStartFailureAfterShutdownPreparationDeletesPackageAndStillShutsDown()
    {
        var events = new List<string>();
        var applicationShutdown = new ManualResetEventSlim();
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () => Task.FromResult(CreateAvailableResult()),
            download: asset => Task.FromResult("package.zip"),
            prepare: packagePath => new FakePreparedUpdaterLaunch(() => null!),
            delete: packagePath => events.Add("delete"),
            schedule: action => Task.Run(action));
        owner.PresentationRequested += request => request.Complete(CreateAvailableResult().Assets[0]);
        owner.BindShutdownPreparation(_ => Task.FromResult(new ShutdownPreparationResult("update", 1L, false, 0)));
        owner.ApplicationShutdownRequested += () =>
        {
            events.Add("application_shutdown");
            applicationShutdown.Set();
        };
        owner.FailurePresentationRequested += _ => events.Add("failure_presentation");
        StartupUpdateWorkflowCompletionReceipt receipt = null!;
        owner.TerminalPublished += published =>
        {
            events.Add("terminal");
            receipt = published;
        };

        Assert.IsTrue(owner.Start());
        Assert.IsTrue(SpinWait.SpinUntil(() => owner.IsIdle && receipt != null && applicationShutdown.IsSet, TimeSpan.FromSeconds(5)));

        CollectionAssert.AreEqual(new[] { "delete", "terminal", "application_shutdown" }, events);
        Assert.AreEqual(StartupUpdateWorkflowOutcome.Failed, receipt.Outcome);
        Assert.IsTrue(receipt.ShutdownPrepared);
    }

    [TestMethod]
    public void SchedulerFailurePublishesFailureAndLeavesOwnerIdle()
    {
        int failurePresentationCount = 0;
        StartupUpdateWorkflowOwner owner = CreateOwner(
            () => Task.FromResult(UpdateCheckResult.NoUpdate("1.0.0.0")),
            schedule: action => throw new InvalidOperationException("scheduler failed"));
        owner.FailurePresentationRequested += _ => Interlocked.Increment(ref failurePresentationCount);
        StartupUpdateWorkflowCompletionReceipt receipt = null!;
        owner.TerminalPublished += published => receipt = published;

        Assert.IsTrue(owner.Start());
        Assert.IsTrue(owner.IsIdle);
        Assert.AreEqual(1, failurePresentationCount);
        Assert.AreEqual(StartupUpdateWorkflowOutcome.Failed, receipt.Outcome);
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
            prepare ?? (packagePath => new FakePreparedUpdaterLaunch(() => new Process())),
            cleanup ?? (() => { }),
            delete ?? (_ => { }),
            schedule ?? (action => Task.Run(action)),
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
        private readonly Func<Process> start;

        internal FakePreparedUpdaterLaunch(Func<Process> start)
        {
            this.start = start;
        }

        public Process Start()
        {
            return start();
        }
    }
}
