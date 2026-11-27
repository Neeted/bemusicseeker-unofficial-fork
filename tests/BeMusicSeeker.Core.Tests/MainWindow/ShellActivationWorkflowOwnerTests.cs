using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Update;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;

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
            GitHubStarPromptTestSupport.CreateDisabledOwner(),
            () => Task.FromResult(new StartupInitializationResult(StartupInitializationOutcome.Succeeded, CompletionPublished: true)));

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
        var initialization = new TaskCompletionSource<StartupInitializationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        ShellActivationWorkflowOwner owner = new(
            startupUpdate,
            elevatedWarning,
            GitHubStarPromptTestSupport.CreateDisabledOwner(),
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
        initialization.SetResult(new StartupInitializationResult(StartupInitializationOutcome.Succeeded, CompletionPublished: true));
        await activation;
        Assert.IsTrue(activation.IsCompleted);
        Task duplicateActivation = owner.ActivateRenderedShell(
            () => events.Add("duplicate-selection"),
            action => action(),
            () => true);
        Assert.IsTrue(duplicateActivation.IsCompleted);
        CollectionAssert.DoesNotContain(events, "duplicate-selection");
    }

    /// <summary>
    /// 起動時の初期化が成功以外のどの種類で終わっても、実際の更新処理は取得を取り消して起動準備を行わず、取消の終端を一度だけ公開する。
    /// 起動完了の結果・起動処理のTaskは入力どおりのままで、更新の取消が初期化の結果を変えない。
    /// 設定要求・失敗・終了要求・退出要求・完了未公開の成功・失敗を伴う成功・例外・取消を一つのケースの複数の行で確認する。
    /// </summary>
    [TestMethod]
    public async Task NonSuccessInitialization_CancelsStartupUpdateAndKeepsInitializationResultUnchanged()
    {
        var failure = new InvalidOperationException("initialization failed");
        (string Name, StartupInitializationResult? Result, Exception? Exception, bool Canceled)[] rows =
        [
            ("settings required", new(StartupInitializationOutcome.SettingsRequired), null, false),
            ("operation failed", new(StartupInitializationOutcome.OperationFailed, Failure: failure), null, false),
            ("shutdown requested", new(StartupInitializationOutcome.ShutdownRequested), null, false),
            ("exit requested", new(StartupInitializationOutcome.ExitRequested), null, false),
            ("succeeded without completion", new(StartupInitializationOutcome.Succeeded, CompletionPublished: false), null, false),
            ("succeeded with failure", new(StartupInitializationOutcome.Succeeded, CompletionPublished: true, Failure: failure), null, false),
            ("exception", null, new InvalidOperationException("initialize threw"), false),
            ("canceled", null, null, true)
        ];

        foreach ((string name, StartupInitializationResult? result, Exception? exception, bool canceled) in rows)
        {
            var harness = new StartupUpdateTransferHarness();
            var initialization = new TaskCompletionSource<StartupInitializationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            ShellActivationWorkflowOwner owner = new(
                harness.Owner,
                new ElevatedProcessWarningWorkflowOwner(() => false),
                GitHubStarPromptTestSupport.CreateDisabledOwner(),
                () => initialization.Task);
            try
            {
                Assert.IsTrue(owner.ActivateConstructedShell(), name);
                await harness.AwaitAsync(harness.TransferEntered.Task, name);
                Task rendered = owner.ActivateRenderedShell(() => { }, action => action(), () => false);

                if (result != null)
                {
                    initialization.SetResult(result);
                }
                else if (exception != null)
                {
                    initialization.SetException(exception);
                }
                else
                {
                    initialization.SetCanceled();
                }
                await Task.WhenAll(harness.Owner.WaitForIdleAsync(), harness.Owner.WaitForTerminalAsync());

                Assert.AreEqual(StartupUpdateWorkflowOutcome.Cancelled, harness.Receipt!.Outcome, name);
                Assert.AreEqual(1, harness.TerminalCount, name);
                Assert.AreEqual(0, harness.PrepareCount, name);
                Assert.AreEqual(0, harness.DeleteCount, name);
                Assert.AreEqual(0, harness.FailureCount, name);
                Assert.AreEqual(0, harness.ShutdownRequestCount, name);
                Assert.IsTrue(harness.CancelObserved.Task.IsCompleted, name);

                if (result != null)
                {
                    Assert.AreSame(result, await owner.StartupCompletion, name);
                    Assert.IsFalse(await (Task<bool>)rendered, name);
                }
                else if (exception != null)
                {
                    Assert.AreSame(exception, (await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => owner.StartupCompletion)), name);
                    Assert.AreSame(exception, (await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => rendered)), name);
                }
                else
                {
                    await Assert.ThrowsAsync<OperationCanceledException>(() => owner.StartupCompletion);
                    await Assert.ThrowsAsync<OperationCanceledException>(() => rendered);
                }
            }
            finally
            {
                await harness.FinishAsync();
            }
        }
    }

    /// <summary>
    /// 初期化が成功すると、その処理が戻る前は検証済みZIPがあっても更新ツールの起動準備を行わず、戻った後に一度だけ行って適用へ進む。
    /// 初期化の完了を待たずに起動する誤りと、成功の判定を取りこぼして適用しない誤りを検出する。
    /// </summary>
    [TestMethod]
    public async Task SuccessfulInitialization_AllowsLaunchOnlyAfterInitializationReturns()
    {
        var harness = new StartupUpdateTransferHarness();
        var initialization = new TaskCompletionSource<StartupInitializationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        ShellActivationWorkflowOwner owner = new(
            harness.Owner,
            new ElevatedProcessWarningWorkflowOwner(() => false),
            GitHubStarPromptTestSupport.CreateDisabledOwner(),
            () => initialization.Task);
        try
        {
            Assert.IsTrue(owner.ActivateConstructedShell());
            await harness.AwaitAsync(harness.TransferEntered.Task, "transfer");
            Task rendered = owner.ActivateRenderedShell(() => { }, action => action(), () => false);
            harness.Release();
            await harness.AwaitAsync(harness.WaitingObserved.Task, "waiting");

            Assert.AreEqual(0, harness.PrepareCount, "初期化の処理が戻る前に起動準備を行ってはいけません。");
            Assert.IsNull(harness.Receipt);
            initialization.SetResult(new StartupInitializationResult(StartupInitializationOutcome.Succeeded, CompletionPublished: true));
            await harness.AwaitAsync(harness.Terminal.Task, "terminal");

            Assert.IsTrue(await (Task<bool>)rendered);
            Assert.AreEqual(StartupUpdateWorkflowOutcome.Applied, harness.Receipt!.Outcome);
            Assert.AreEqual(1, harness.PrepareCount);
            Assert.AreEqual(0, harness.DeleteCount);
        }
        finally
        {
            await harness.FinishAsync();
        }
    }

    /// <summary>
    /// 初期化待ちで明示取消を受け付けた後に初期化が成功しても、更新は起動せず、起動処理のTaskと起動完了の結果は成功のままである。
    /// </summary>
    [TestMethod]
    public async Task ExplicitCancelWhileWaiting_DoesNotChangeLaterSuccessfulInitialization()
    {
        var harness = new StartupUpdateTransferHarness();
        var initialization = new TaskCompletionSource<StartupInitializationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        ShellActivationWorkflowOwner owner = new(
            harness.Owner,
            new ElevatedProcessWarningWorkflowOwner(() => false),
            GitHubStarPromptTestSupport.CreateDisabledOwner(),
            () => initialization.Task);
        try
        {
            Assert.IsTrue(owner.ActivateConstructedShell());
            await harness.AwaitAsync(harness.TransferEntered.Task, "transfer");
            Task rendered = owner.ActivateRenderedShell(() => { }, action => action(), () => false);
            harness.Release();
            await harness.AwaitAsync(harness.WaitingObserved.Task, "waiting");

            Assert.IsTrue(harness.Owner.RequestCancel());
            await harness.AwaitAsync(harness.Terminal.Task, "terminal");
            var success = new StartupInitializationResult(StartupInitializationOutcome.Succeeded, CompletionPublished: true);
            initialization.SetResult(success);

            Assert.IsTrue(await (Task<bool>)rendered);
            Assert.AreSame(success, await owner.StartupCompletion);
            Assert.AreEqual(StartupUpdateWorkflowOutcome.Cancelled, harness.Receipt!.Outcome);
            Assert.AreEqual(1, harness.DeleteCount);
            Assert.AreEqual(0, harness.PrepareCount);
        }
        finally
        {
            await harness.FinishAsync();
        }
    }

    /// <summary>
    /// 保証：描画後の起動は、初期化の終端と実際の更新確認の「更新なし」を待ってスター依頼を表示へ進め、
    /// その表示が終わらなくても起動処理のTaskと起動完了の結果は入力どおりに完了する。
    /// スター依頼を起動の完了条件へ含めて起動を待たせる誤りと、接続漏れで依頼が届かない誤りを検出する。
    /// </summary>
    [TestMethod]
    public async Task ActivateRenderedShell_StartsStarPromptAfterInitializationWithoutWaitingForIt()
    {
        StartupUpdateWorkflowOwner startupUpdate = CreateStartupUpdateOwner();
        DateTimeOffset now = new(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);
        var store = new RecordingGitHubStarPromptStore(new GitHubStarPromptRecord(GitHubStarPromptState.Unfinished, 9, now.AddDays(-1), 0));
        var starPrompt = new GitHubStarPromptWorkflowOwner(
            enabled: true,
            store,
            () => now,
            async () =>
            {
                await startupUpdate.WaitForTerminalAsync();
                startupUpdate.CaptureState(out StartupUpdateWorkflowCompletionReceipt? receipt);
                return receipt?.Outcome;
            },
            action =>
            {
                action();
                return true;
            });
        var requested = new TaskCompletionSource<GitHubStarPromptPresentationRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        starPrompt.PresentationRequested += request => requested.TrySetResult(request);
        var initialization = new TaskCompletionSource<StartupInitializationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        ShellActivationWorkflowOwner owner = new(startupUpdate, new ElevatedProcessWarningWorkflowOwner(() => false), starPrompt, () => initialization.Task);
        try
        {
            Assert.IsTrue(owner.ActivateConstructedShell());
            Task rendered = owner.ActivateRenderedShell(() => { }, action => action(), () => true);
            var success = new StartupInitializationResult(StartupInitializationOutcome.Succeeded, CompletionPublished: true);
            initialization.SetResult(success);

            await TestUiDispatcherHost.AwaitNotificationAsync(requested.Task, starPrompt.Completion, "GitHub star prompt presentation");

            Assert.IsTrue(await (Task<bool>)rendered);
            Assert.AreSame(success, await owner.StartupCompletion);
            Assert.IsFalse(starPrompt.Completion.IsCompleted, "表示の応答を保留したまま、起動の完了を確認します。");
            requested.Task.Result.Complete(GitHubStarPromptChoice.Later);
            Assert.AreEqual(GitHubStarPromptWorkflowOutcome.Answered, await starPrompt.Completion);
        }
        finally
        {
            initialization.TrySetResult(new StartupInitializationResult(StartupInitializationOutcome.ShutdownRequested));
            if (requested.Task.IsCompleted)
            {
                requested.Task.Result.CompleteNotPresented();
            }
            await Task.WhenAll(startupUpdate.WaitForIdleAsync(), startupUpdate.WaitForTerminalAsync());
            await starPrompt.Completion;
        }
    }

    private static StartupUpdateWorkflowOwner CreateStartupUpdateOwner()
    {
        return new StartupUpdateWorkflowOwner(
            () => Task.FromResult(UpdateCheckResult.NoUpdate("1.0.0.0")),
            (_, _, _) => Task.FromResult(string.Empty),
            _ => throw new InvalidOperationException(),
            () => { },
            _ => { },
            action => ConcurrencyProbe.StartDedicatedAsync(action),
            action => action());
    }
}
