using BeMusicSeeker.TestSupport.Wpf;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ApplicationStartupCompositionOwnerTests
{
    /// <summary>
    /// 単一起動の所有権を取得できなければ設定準備・構成・失敗表示を一つも開始せず起動を終端する。
    /// </summary>
    [TestMethod]
    public async Task OwnershipDenialSkipsEverySettingsAndCompositionStep()
    {
        int preparations = 0, compositions = 0, failures = 0;
        var owner = new ApplicationStartupCompositionOwner(
            () => { compositions++; return null!; }, _ => Assert.Fail(), _ => throw new AssertFailedException(),
            _ => Assert.Fail(), _ => Assert.Fail(),
            _ => { failures++; return Task.CompletedTask; },
            acquireOwnership: () => false, prepareSettings: () => preparations++);

        await owner.StartAsync();

        Assert.AreEqual(0, preparations);
        Assert.AreEqual(0, compositions);
        Assert.AreEqual(0, failures);
    }

    /// <summary>
    /// 所有権取得後に設定準備が失敗したとき、構成を開始せず元例外を一回だけ終端経路へ渡す。
    /// </summary>
    [TestMethod]
    public async Task PreparationFailureUsesTerminalRouteOnceWithoutComposition()
    {
        var expected = new System.IO.IOException("settings inaccessible");
        var events = new List<string>();
        var owner = new ApplicationStartupCompositionOwner(
            () => { events.Add("compose"); return null!; }, _ => Assert.Fail(), _ => throw new AssertFailedException(),
            _ => Assert.Fail(), _ => Assert.Fail(),
            exception => { Assert.AreSame(expected, exception); events.Add("terminal"); return Task.CompletedTask; },
            acquireOwnership: () => { events.Add("ownership"); return true; },
            prepareSettings: () => { events.Add("prepare"); throw expected; });

        await owner.StartAsync();

        CollectionAssert.AreEqual(new[] { "ownership", "prepare", "terminal" }, events);
    }

    /// <summary>
    /// VM生成の失敗では後続構成を開始せず元例外を一回渡し、失敗handlerの実終端まで起動Taskを完了しない。
    /// </summary>
    [TestMethod]
    public async Task StartupCompositionCreationFailureUsesFailureRoute()
    {
        var events = new List<string>();
        var expected = new InvalidOperationException("create failed");
        Exception? handled = null;
        var failureReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new ApplicationStartupCompositionOwner(
            createViewModel: () => { events.Add("create"); throw expected; },
            assignViewModelResource: _ => Assert.Fail("生成失敗後にresource登録を開始しません。"),
            createMainWindow: _ => throw new AssertFailedException("生成失敗後にWindowを構築しません。"),
            assignApplicationMainWindow: _ => Assert.Fail("生成失敗後にMainWindowを割り当てません。"),
            showMainWindow: _ => Assert.Fail("生成失敗後に表示を要求しません。"),
            handleFailure: async exception =>
            {
                events.Add("failure");
                handled = exception;
                failureReceived.TrySetResult();
                await releaseFailure.Task;
            });

        Task startup = owner.StartAsync();
        try
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(failureReceived.Task, startup, nameof(owner));
            Assert.IsFalse(startup.IsCompleted);
            releaseFailure.TrySetResult();
            await startup;
            CollectionAssert.AreEqual(new[] { "create", "failure" }, events);
            Assert.AreSame(expected, handled);
        }
        finally
        {
            releaseFailure.TrySetResult();
            await startup;
        }
    }

}
