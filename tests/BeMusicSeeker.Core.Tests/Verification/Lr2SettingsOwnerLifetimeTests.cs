using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Core.Tests.Verification;

/// <summary>画面やDB接続を生成せず、終了Taskと媒体回収の境界を検査します。</summary>
[TestClass]
public sealed class Lr2SettingsOwnerLifetimeTests
{
    /// <summary>成功時だけ通常削除を許し、準備・Task・待機・設定解放の失敗では元原因と媒体を保持します。</summary>
    /// <remarks>制御Taskの未終端は失敗する待機delegateで表し、製品timeoutや実時間待機を再現しません。遅れて完了しても自動回収しないことを確認します。</remarks>
    [TestMethod]
    [DataRow("success")]
    [DataRow("receipt-failure")]
    [DataRow("fault")]
    [DataRow("cancel")]
    [DataRow("pending")]
    [DataRow("dispose-failure")]
    public void ShutdownOutcomeControlsMediaPreservationAndSettingsDisposal(string condition)
    {
        using var run = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        var workspace = TestWorkspace.Create(run.RootDirectory);
        string[] paths = [workspace.GetPath("table-list.json"), workspace.GetPath("song.db"), workspace.GetPath("user.config")];
        foreach (string path in paths) { File.WriteAllText(path, path); }
        var failure = new IOException(condition);
        var pending = new TaskCompletionSource<ShellShutdownWorkflowCompletionReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Task<ShellShutdownWorkflowCompletionReceipt> shutdown = condition switch
        {
            "receipt-failure" => Task.FromResult(new ShellShutdownWorkflowCompletionReceipt("test", false, failure)),
            "fault" => Task.FromException<ShellShutdownWorkflowCompletionReceipt>(failure),
            "cancel" => Task.FromCanceled<ShellShutdownWorkflowCompletionReceipt>(cancellation.Token),
            "pending" => pending.Task,
            _ => Task.FromResult(new ShellShutdownWorkflowCompletionReceipt("test", true)),
        };
        int disposeCalls = 0;
        int requestCalls = 0;
        try
        {
            void Complete() => Lr2SettingsOwnerTestFixture.CompleteOwnerShutdown(workspace,
                () => { requestCalls++; return shutdown; },
                () => { disposeCalls++; if (condition == "dispose-failure") { throw failure; } },
                (task, _) =>
                {
                    Assert.AreSame(shutdown, task);
                    if (condition == "pending") { Assert.IsFalse(task.IsCompleted); throw failure; }
                });
            if (condition == "success") { Complete(); }
            else if (condition == "cancel")
            {
                OperationCanceledException observed = Assert.Throws<OperationCanceledException>(Complete);
                Assert.AreEqual(cancellation.Token, observed.CancellationToken);
            }
            else { Assert.AreSame(failure, Assert.ThrowsExactly<IOException>(Complete)); }
            Assert.AreEqual(1, requestCalls);
            Assert.AreEqual(condition is "success" or "dispose-failure" ? 1 : 0, disposeCalls);
            workspace.Dispose();
            foreach (string path in paths)
            {
                Assert.AreEqual(condition != "success", File.Exists(path));
                if (condition != "success") { Assert.AreEqual(path, File.ReadAllText(path)); }
            }
            if (condition == "pending")
            {
                pending.SetResult(new ShellShutdownWorkflowCompletionReceipt("late", true));
                pending.Task.GetAwaiter().GetResult();
                Assert.AreEqual(0, disposeCalls);
                workspace.Dispose();
                foreach (string path in paths) { Assert.AreEqual(path, File.ReadAllText(path)); }
            }
        }
        finally
        {
            // 制御Taskの終端後だけ、外側の専有runが検査用に残した媒体を回収します。
            pending.TrySetResult(new ShellShutdownWorkflowCompletionReceipt("cleanup", true));
            pending.Task.GetAwaiter().GetResult();
            workspace.Dispose();
        }
    }
}
