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
    /// <summary>構成後の起動処理は最初の呼出しだけ受理して更新作業の終端まで回収することを確認し、再呼出しで起動更新を重複実行する誤りを検出します。</summary>
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
        Task.WhenAll(startupUpdate.WaitForIdleAsync(), startupUpdate.WaitForTerminalAsync()).GetAwaiter().GetResult();
    }

    /// <summary>初期化を待ちながら初期選択と警告判定を順に行い、完了後の二度目の表示活性化は選択を繰り返さないことを確認し、起動順序の逆転や二重活性化を検出します。</summary>
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
