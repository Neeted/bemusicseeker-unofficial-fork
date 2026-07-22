using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.Update;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ShellShutdownWorkflowOwnerTests
{
    [TestMethod]
    public async Task RepeatedWindowCloseRequestsShareOnePreparationAndCompletion()
    {
        var preparationStarted = new ManualResetEventSlim();
        var preparationRelease = new TaskCompletionSource<ShutdownPreparationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int preparationCount = 0;
        ShellShutdownWorkflowOwner owner = CreateOwner(
            reason =>
            {
                Interlocked.Increment(ref preparationCount);
                preparationStarted.Set();
                return preparationRelease.Task;
            });

        Task<ShellShutdownWorkflowCompletionReceipt> first = owner.RequestWindowCloseAsync();
        Assert.IsTrue(preparationStarted.Wait(TimeSpan.FromSeconds(5)));
        Task<ShellShutdownWorkflowCompletionReceipt> second = owner.RequestWindowCloseAsync();

        Assert.AreSame(first, second);
        Assert.IsTrue(owner.IsClosingOrClosed);
        Assert.IsTrue(owner.IsShutdownPreparationRunning);

        preparationRelease.SetResult(CreatePreparationResult("window_close"));
        ShellShutdownWorkflowCompletionReceipt receipt = await first;

        Assert.IsTrue(receipt.PreparationSucceeded);
        Assert.IsTrue(receipt.CloseAllowed);
        Assert.IsTrue(owner.IsCloseAllowed);
        Assert.AreEqual(1, preparationCount);
    }

    [TestMethod]
    public async Task ActiveStartupUpdateDrainsBeforeWindowPreparation()
    {
        var checkEntered = new ManualResetEventSlim();
        var checkRelease = new TaskCompletionSource<UpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<string>();
        StartupUpdateWorkflowOwner startupUpdate = CreateStartupUpdateOwner(
            () =>
            {
                checkEntered.Set();
                return checkRelease.Task;
            });
        var preparationStarted = new ManualResetEventSlim();
        ShellShutdownWorkflowOwner owner = CreateOwner(
            reason =>
            {
                events.Add("prepare");
                preparationStarted.Set();
                return Task.FromResult(CreatePreparationResult(reason));
            },
            startupUpdate);

        Assert.IsTrue(startupUpdate.Start());
        Assert.IsTrue(checkEntered.Wait(TimeSpan.FromSeconds(5)));
        Task<ShellShutdownWorkflowCompletionReceipt> close = owner.RequestWindowCloseAsync();
        Assert.IsFalse(preparationStarted.IsSet);

        checkRelease.SetResult(UpdateCheckResult.NoUpdate("1.0.0.0"));
        ShellShutdownWorkflowCompletionReceipt receipt = await close;

        Assert.IsTrue(receipt.PreparationSucceeded);
        CollectionAssert.AreEqual(new[] { "prepare" }, events);
        Assert.IsTrue(owner.IsCloseAllowed);
    }

    [TestMethod]
    public async Task WindowCloseWaitsForQueuedStartupTerminalAfterWorkflowBecameIdle()
    {
        var checkEntered = new ManualResetEventSlim();
        var checkRelease = new TaskCompletionSource<UpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminalDispatchEntered = new ManualResetEventSlim();
        Action pendingTerminalDispatch = null!;
        StartupUpdateWorkflowOwner startupUpdate = new(
            () =>
            {
                checkEntered.Set();
                return checkRelease.Task;
            },
            asset => Task.FromResult("package.zip"),
            packagePath => new NoOpPreparedUpdaterLaunch(),
            () => { },
            packagePath => { },
            action => Task.Run(action),
            action =>
            {
                pendingTerminalDispatch = action;
                terminalDispatchEntered.Set();
            });
        ShellShutdownWorkflowOwner owner = CreateOwner(
            reason => Task.FromResult(CreatePreparationResult(reason)),
            startupUpdate);

        Assert.IsTrue(startupUpdate.Start());
        Assert.IsTrue(checkEntered.Wait(TimeSpan.FromSeconds(5)));
        checkRelease.SetResult(UpdateCheckResult.NoUpdate("1.0.0.0"));
        Assert.IsTrue(terminalDispatchEntered.Wait(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(SpinWait.SpinUntil(() => startupUpdate.IsIdle, TimeSpan.FromSeconds(5)));

        Task<ShellShutdownWorkflowCompletionReceipt> close = owner.RequestWindowCloseAsync();
        Assert.IsFalse(close.Wait(TimeSpan.FromMilliseconds(100)));

        pendingTerminalDispatch();
        ShellShutdownWorkflowCompletionReceipt receipt = await close;

        Assert.IsTrue(receipt.PreparationSucceeded);
        Assert.IsTrue(owner.IsCloseAllowed);
    }

    [TestMethod]
    public async Task CloseDuringUpdatePreparationWaitsForUpdaterStartAndTerminal()
    {
        var events = new List<string>();
        var preparationStarted = new ManualResetEventSlim();
        var preparationRelease = new TaskCompletionSource<ShutdownPreparationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var updaterStartEntered = new ManualResetEventSlim();
        var updaterStartRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        System.Diagnostics.Process launchedProcess = null!;
        StartupUpdateWorkflowOwner startupUpdate = new(
            () => Task.FromResult(CreateAvailableResult()),
            asset =>
            {
                events.Add("download");
                return Task.FromResult("package.zip");
            },
            packagePath => new PreparedUpdaterLaunch(() =>
            {
                events.Add("start");
                updaterStartEntered.Set();
                updaterStartRelease.Task.GetAwaiter().GetResult();
                launchedProcess = new System.Diagnostics.Process();
                return launchedProcess;
            }),
            () => { },
            packagePath => { },
            action => Task.Run(action),
            action => action());
        ShellShutdownWorkflowOwner owner = CreateOwner(
            reason =>
            {
                events.Add("prepare");
                preparationStarted.Set();
                return preparationRelease.Task;
            },
            startupUpdate);
        startupUpdate.PresentationRequested += request => request.Complete(CreateAvailableResult().Assets[0]);
        startupUpdate.TerminalPublished += _ => events.Add("terminal");

        Assert.IsTrue(startupUpdate.Start());
        Assert.IsTrue(preparationStarted.Wait(TimeSpan.FromSeconds(5)));
        Task<ShellShutdownWorkflowCompletionReceipt> close = owner.RequestWindowCloseAsync();
        preparationRelease.SetResult(CreatePreparationResult("update"));

        try
        {
            Assert.IsTrue(updaterStartEntered.Wait(TimeSpan.FromSeconds(5)));
            Assert.IsTrue(owner.IsShutdownPrepared);
            Assert.IsFalse(owner.IsCloseAllowed);
            updaterStartRelease.SetResult(true);

            ShellShutdownWorkflowCompletionReceipt receipt = await close;
            events.Add("close_receipt");

            Assert.IsTrue(receipt.PreparationSucceeded);
            Assert.IsTrue(events.IndexOf("start") >= 0);
            Assert.IsTrue(events.IndexOf("terminal") >= 0);
            Assert.IsTrue(events.IndexOf("start") < events.IndexOf("terminal"));
            Assert.IsTrue(events.IndexOf("terminal") < events.IndexOf("close_receipt"));
            Assert.IsTrue(owner.IsCloseAllowed);
        }
        finally
        {
            updaterStartRelease.TrySetResult(true);
            launchedProcess?.Dispose();
        }
    }

    [TestMethod]
    public async Task UpdatePreparationAndConcurrentWindowCloseShareCanonicalPreparation()
    {
        var preparationRelease = new TaskCompletionSource<ShutdownPreparationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int preparationCount = 0;
        ShellShutdownWorkflowOwner owner = CreateOwner(
            reason =>
            {
                Interlocked.Increment(ref preparationCount);
                return preparationRelease.Task;
            });

        Task<ShutdownPreparationResult> updatePreparation = owner.PrepareForStartupUpdateAsync("update");
        Task<ShellShutdownWorkflowCompletionReceipt> close = owner.RequestWindowCloseAsync();
        Assert.IsTrue(owner.IsShutdownPreparationStarted);
        Assert.IsTrue(owner.IsShutdownPreparationRunning);

        preparationRelease.SetResult(CreatePreparationResult("update"));
        ShutdownPreparationResult updateResult = await updatePreparation;
        ShellShutdownWorkflowCompletionReceipt closeResult = await close;

        Assert.AreEqual("update", updateResult.Reason);
        Assert.IsTrue(closeResult.PreparationSucceeded);
        Assert.AreEqual(1, preparationCount);
    }

    [TestMethod]
    public async Task PendingUiPreparationReservesCanonicalTaskBeforeDispatch()
    {
        Func<Task> pendingDispatch = null!;
        var preparationStarted = new ManualResetEventSlim();
        var preparationRelease = new TaskCompletionSource<ShutdownPreparationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        ShellShutdownWorkflowOwner owner = CreateOwner(
            reason =>
            {
                preparationStarted.Set();
                return preparationRelease.Task;
            },
            dispatch: action =>
            {
                pendingDispatch = action;
                return Task.CompletedTask;
            });

        Task<ShutdownPreparationResult> updatePreparation = owner.PrepareForStartupUpdateAsync("update");
        Assert.IsTrue(owner.IsShutdownPreparationStarted);
        Task<ShellShutdownWorkflowCompletionReceipt> close = owner.RequestWindowCloseAsync();
        Assert.IsFalse(preparationStarted.IsSet);

        Task pendingPreparation = pendingDispatch();
        Assert.IsTrue(preparationStarted.Wait(TimeSpan.FromSeconds(5)));
        preparationRelease.SetResult(CreatePreparationResult("update"));

        await pendingPreparation;
        Assert.IsTrue((await updatePreparation) != null);
        Assert.IsTrue((await close).PreparationSucceeded);
    }

    [TestMethod]
    public async Task NormalPreparationFailureAllowsCloseWithoutRetry()
    {
        int preparationCount = 0;
        ShellShutdownWorkflowOwner owner = CreateOwner(
            reason =>
            {
                Interlocked.Increment(ref preparationCount);
                return Task.FromException<ShutdownPreparationResult>(new InvalidOperationException("normal preparation failed"));
            });

        ShellShutdownWorkflowCompletionReceipt receipt = await owner.RequestWindowCloseAsync();

        Assert.IsFalse(receipt.PreparationSucceeded);
        Assert.IsNotNull(receipt.Exception);
        Assert.IsTrue(receipt.CloseAllowed);
        Assert.IsTrue(owner.IsCloseAllowed);
        Assert.AreEqual(1, preparationCount);
        Assert.AreSame(owner.RequestWindowCloseAsync(), owner.RequestWindowCloseAsync());
    }

    [TestMethod]
    public async Task UpdatePreparationFailureIsPresentableAndLeavesLaterClosePossible()
    {
        ShellShutdownWorkflowOwner owner = CreateOwner(
            reason => Task.FromException<ShutdownPreparationResult>(new InvalidOperationException("update preparation failed")));

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => owner.PrepareForStartupUpdateAsync("update"));

        Assert.IsTrue(owner.ConsumeUpdatePreparationFailure());
        Assert.IsFalse(owner.ConsumeUpdatePreparationFailure());
        Assert.IsTrue(owner.IsCloseAllowed);
        ShellShutdownWorkflowCompletionReceipt receipt = await owner.RequestWindowCloseAsync();
        Assert.IsFalse(receipt.PreparationSucceeded);
        Assert.IsTrue(receipt.CloseAllowed);
    }

    [TestMethod]
    public async Task UiPreparationDispatchFailureSettlesPreparationAndAllowsWindowClose()
    {
        ShellShutdownWorkflowOwner owner = CreateOwner(
            reason => Task.FromResult(CreatePreparationResult(reason)),
            dispatch: action => Task.FromException(new InvalidOperationException("UI dispatcher is shutting down.")));

        InvalidOperationException exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => owner.PrepareForStartupUpdateAsync("update"));

        Assert.AreEqual("UI dispatcher is shutting down.", exception.Message);
        Assert.IsTrue(owner.ConsumeUpdatePreparationFailure());
        ShellShutdownWorkflowCompletionReceipt receipt = await owner.RequestWindowCloseAsync();

        Assert.IsFalse(receipt.PreparationSucceeded);
        Assert.IsTrue(receipt.CloseAllowed);
        Assert.IsTrue(owner.IsCloseAllowed);
    }

    [TestMethod]
    public async Task CoordinatedShutdownIsMarkedBeforePreparationAndCloseReceipt()
    {
        var events = new List<string>();
        ShellShutdownWorkflowOwner owner = CreateOwner(
            reason =>
            {
                events.Add("prepare");
                return Task.FromResult(CreatePreparationResult(reason));
            },
            markShutdown: reason => events.Add("mark"));

        ShellShutdownWorkflowCompletionReceipt receipt = await owner.RequestWindowCloseAsync();

        Assert.IsTrue(receipt.PreparationSucceeded);
        CollectionAssert.AreEqual(new[] { "mark", "prepare" }, events);
    }

    private static ShellShutdownWorkflowOwner CreateOwner(
        Func<string, Task<ShutdownPreparationResult>> prepare,
        StartupUpdateWorkflowOwner? startupUpdate = null,
        Action<string>? markShutdown = null,
        Func<Func<Task>, Task>? dispatch = null)
    {
        startupUpdate ??= CreateStartupUpdateOwner(() => Task.FromResult(UpdateCheckResult.NoUpdate("1.0.0.0")));
        var elevated = new ElevatedProcessWarningWorkflowOwner(() => false);
        return new ShellShutdownWorkflowOwner(
            startupUpdate,
            elevated,
            prepare,
            markShutdown ?? (_ => { }),
            dispatch ?? (action => action()));
    }

    private static StartupUpdateWorkflowOwner CreateStartupUpdateOwner(Func<Task<UpdateCheckResult>> check)
    {
        return new StartupUpdateWorkflowOwner(
            check,
            asset => Task.FromResult("package.zip"),
            packagePath => new NoOpPreparedUpdaterLaunch(),
            () => { },
            packagePath => { },
            action => Task.Run(action),
            action => action());
    }

    private static ShutdownPreparationResult CreatePreparationResult(string reason)
    {
        return new ShutdownPreparationResult(reason, 1L, false, 0);
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

    private sealed class NoOpPreparedUpdaterLaunch : IPreparedUpdaterLaunch
    {
        public System.Diagnostics.Process Start()
        {
            return new System.Diagnostics.Process();
        }
    }

    private sealed class PreparedUpdaterLaunch : IPreparedUpdaterLaunch
    {
        private readonly Func<System.Diagnostics.Process> start;

        internal PreparedUpdaterLaunch(Func<System.Diagnostics.Process> start)
        {
            this.start = start;
        }

        public System.Diagnostics.Process Start()
        {
            return start();
        }
    }
}
