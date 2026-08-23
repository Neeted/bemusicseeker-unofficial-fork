using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.Update;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ShellActivationWorkflowOwnerTests
{
    [TestMethod]
    public void ActivateConstructedShell_StartsStartupUpdateOnce()
    {
        StartupUpdateWorkflowOwner startupUpdate = CreateStartupUpdateOwner();
        ElevatedProcessWarningWorkflowOwner elevatedWarning = new(() => true);
        ShellActivationWorkflowOwner owner = new(
            startupUpdate,
            elevatedWarning,
            () => Task.FromResult(true));

        Assert.IsTrue(owner.ActivateConstructedShell());
        Assert.IsFalse(owner.ActivateConstructedShell());
        Assert.IsTrue(
            Task.WhenAll(startupUpdate.WaitForIdleAsync(), startupUpdate.WaitForTerminalAsync())
                .Wait(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task ActivateRenderedShell_PreservesInitializationSelectionAndWarningOrder()
    {
        StartupUpdateWorkflowOwner startupUpdate = CreateStartupUpdateOwner();
        ElevatedProcessWarningWorkflowOwner elevatedWarning = new(() => true);
        var events = new List<string>();
        var initialization = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ShellActivationWorkflowOwner owner = new(
            startupUpdate,
            elevatedWarning,
            () =>
            {
                events.Add("initialize");
                return initialization.Task;
            });

        Task activation = owner.ActivateRenderedShell(
            () => events.Add("selection"),
            action =>
            {
                events.Add("schedule");
                action();
            },
            () =>
            {
                events.Add("can-present");
                return false;
            });

        CollectionAssert.AreEqual(new[] { "initialize", "selection", "schedule", "can-present" }, events);
        Assert.IsFalse(activation.IsCompleted);
        initialization.SetResult(true);
        await activation;
        Assert.IsTrue(activation.IsCompleted);
        Task duplicateActivation = owner.ActivateRenderedShell(
            () => events.Add("duplicate-selection"),
            action => action(),
            () => true);
        Assert.IsTrue(duplicateActivation.IsCompleted);
        CollectionAssert.DoesNotContain(events, "duplicate-selection");
    }

    private static StartupUpdateWorkflowOwner CreateStartupUpdateOwner()
    {
        return new StartupUpdateWorkflowOwner(
            () => Task.FromResult(UpdateCheckResult.NoUpdate("1.0.0.0")),
            _ => Task.FromResult(string.Empty),
            _ => throw new InvalidOperationException(),
            () => { },
            _ => { },
            action => StartLongRunningAsync(action),
            action => action());
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
}
