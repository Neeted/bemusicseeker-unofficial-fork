using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using static BeMusicSeeker.TestSupport.Components.MaintenanceRescanWorkflowOwnerTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class MaintenanceRescanWorkflowOwnerTests
{
    /// <summary>
    /// 確認のCancelは拒否で終え、再走査executorを始めず、未承認の全件処理を検出する。
    /// </summary>
    [TestMethod]
    public async Task RequestStartAsync_RejectedConfirmationDoesNotSchedule()
    {
        int executionCalls = 0;
        var dialogs = new AcceptedDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel)
        };
        var owner = new MaintenanceRescanWorkflowOwner(
            (current, progress, token, capability, readFailures) =>
            {
                executionCalls++;
                return new MaintenanceWorkflowResult();
            },
            action =>
            {
                action();
                return Task.CompletedTask;
            },
            action => action(),
            dialogs: dialogs);

        MaintenanceRescanStartResult result = await owner.RequestStartAsync();

        Assert.AreEqual(MaintenanceRescanStartStatus.Rejected, result.Status);
        Assert.AreEqual(0, executionCalls);
        await owner.WaitForIdleAsync();
    }

    /// <summary>
    /// 確認画面の例外は開始失敗と原因を返し、画面失敗を拒否や成功へ読み替える誤りを検出する。
    /// </summary>
    [TestMethod]
    public async Task RequestStartAsync_DialogFailureIsExplicit()
    {
        var dialogs = new AcceptedDialogService
        {
            ConfirmationResult = UiDialogResult.Failed(new InvalidOperationException("dialog unavailable"))
        };
        var owner = new MaintenanceRescanWorkflowOwner(
            (current, progress, token, capability, readFailures) => new MaintenanceWorkflowResult(),
            action =>
            {
                action();
                return Task.CompletedTask;
            },
            action => action(),
            dialogs: dialogs);

        MaintenanceRescanStartResult result = await owner.RequestStartAsync();

        Assert.AreEqual(MaintenanceRescanStartStatus.Failed, result.Status);
        Assert.IsNotNull(result.Failure);
        await owner.WaitForIdleAsync();
    }

    /// <summary>
    /// 再走査の確認要求を一回渡し、未選択閉鎖を拒否として扱って、閉鎖による誤承認を検出する。
    /// </summary>
    [TestMethod]
    public async Task RequestStartAsync_PreservesConfirmationRequestAndTreatsWindowCloseAsRejected()
    {
        var dialogs = new AcceptedDialogService
        {
            ConfirmationResult = UiDialogResult.ClosedByUser(MessageBoxResult.Cancel)
        };
        var owner = new MaintenanceRescanWorkflowOwner(
            (current, progress, token, capability, readFailures) => new MaintenanceWorkflowResult(),
            action =>
            {
                action();
                return Task.CompletedTask;
            },
            action => action(),
            dialogs: dialogs);

        MaintenanceRescanStartResult result = await owner.RequestStartAsync();

        Assert.AreEqual(MaintenanceRescanStartStatus.Rejected, result.Status);
        Assert.AreEqual(1, dialogs.ConfirmationCalls);
        Assert.IsNotNull(dialogs.LastConfirmationRequest);
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Msg_rescan_all_charts_confirm, dialogs.LastConfirmationRequest.MessageBoxText);
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Confirm, dialogs.LastConfirmationRequest.Caption);
        Assert.AreEqual(MessageBoxButton.OKCancel, dialogs.LastConfirmationRequest.Button);
        Assert.AreEqual(MessageBoxImage.Question, dialogs.LastConfirmationRequest.Icon);
        Assert.AreEqual(MessageBoxResult.Cancel, dialogs.LastConfirmationRequest.DefaultResult);
    }

    /// <summary>
    /// Dispatcher不在で確認できない場合は開始失敗を返し、利用者による拒否と混同する誤りを検出する。
    /// </summary>
    [TestMethod]
    public async Task RequestStartAsync_DialogUnavailableIsFailureInsteadOfRejection()
    {
        var dialogs = new AcceptedDialogService
        {
            ConfirmationResult = UiDialogResult.NotShown(UiDialogStatus.DispatcherUnavailable)
        };
        var owner = new MaintenanceRescanWorkflowOwner(
            (current, progress, token, capability, readFailures) => new MaintenanceWorkflowResult(),
            action =>
            {
                action();
                return Task.CompletedTask;
            },
            action => action(),
            dialogs: dialogs);

        MaintenanceRescanStartResult result = await owner.RequestStartAsync();

        Assert.AreEqual(MaintenanceRescanStartStatus.Failed, result.Status);
        Assert.IsNotNull(result.Failure);
        await owner.WaitForIdleAsync();
    }

    /// <summary>
    /// 全体の再走査の読込み失敗は、完了・取消・失敗のいずれでも受付の解放後に一つの通知へまとめて表示し、
    /// 世代の失効と終了の要求では表示しない。表示は終端の報告（完了・失敗の公開）より後で、
    /// 実行の終端（待機の完了）は通知の表示の完了を待たない。集めた失敗を失う誤り（取消・失敗の経路）、
    /// 受付を保持したままの表示、失効・終了後の表示、表示の完了を待つ終端を検出する。
    /// </summary>
    [TestMethod]
    [DataRow("completed", true)]
    [DataRow("canceled", true)]
    [DataRow("failed", true)]
    [DataRow("stale", false)]
    [DataRow("shutdown", false)]
    public async Task RealRescan_ShowsGroupedReadFailuresAfterAdmissionOnlyWhenCurrent(string scenario, bool shown)
    {
        await MaintenanceReadFailureFixture.RunAsync(async fixture =>
        {
            if (scenario == "failed")
            {
                fixture.FailMaintenanceCommit();
            }
            BMSLibrary library = fixture.Library;
            BMSLibrary replacement = UninitializedBmsLibrary.Create();
            var display = new TaskCompletionSource<UiDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requested = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var decided = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
            bool admissionFreeAtDisplay = false;
            var dialogs = new FileDbReportRecordingDialogs { MessageHandler = _ => display.Task };
            dialogs.OnMessage = () =>
            {
                admissionFreeAtDisplay = library.OperationAdmission.TryEnter(out IDisposable? lease);
                lease?.Dispose();
                events.Enqueue("notice");
                requested.TrySetResult(true);
            };
            var owner = new MaintenanceRescanWorkflowOwner(
                (current, progress, token, capability, readFailures) => current.RescanAllOwnedChartMaintenance(progress, token, capability, readFailures),
                action => Task.Run(action),
                action => action(),
                logInfo: message =>
                {
                    if (message.StartsWith("maintenance_rescan read_failure_notice ", StringComparison.Ordinal))
                    {
                        decided.TrySetResult(message);
                    }
                },
                dialogs: dialogs);
            MaintenanceRescanCompletionReceipt? completion = null;
            MaintenanceRescanFailure? failure = null;
            int failureCount = 0;
            owner.CompletionPublished += receipt => { completion = receipt; events.Enqueue("completion"); };
            owner.FailurePublished += published => { failure = published; failureCount++; events.Enqueue("failure"); };
            bool triggered = false;
            owner.ProgressChanged += progress =>
            {
                if (triggered || progress.TotalCount <= 1 || progress.EvaluatedCount != progress.TotalCount)
                {
                    return;
                }
                triggered = true;
                switch (scenario)
                {
                    case "canceled": owner.Cancel(); break;
                    case "stale": owner.AttachLibrary(replacement); break;
                    case "shutdown": owner.RequestShutdown(); break;
                }
            };
            owner.AttachLibrary(library);
            try
            {
                Assert.AreEqual(MaintenanceRescanStartStatus.Started, (await owner.RequestStartAsync()).Status);

                // 表示の要否の決定は、実行の終端（待機の完了）より前に診断へ残る。
                string decision = await decided.Task;
                await owner.WaitForIdleAsync();
                Assert.IsTrue(triggered || scenario is "completed" or "failed");
                if (shown)
                {
                    StringAssert.Contains(decision, "presented");
                    await requested.Task;
                    UiMessageRequest request = dialogs.Messages.Single();
                    Assert.AreEqual(BeMusicSeeker.Properties.Resources.MessageBoxTitle_Error, request.Caption);
                    Assert.AreEqual(MessageBoxImage.Hand, request.Icon);
                    Assert.AreEqual(fixture.ExpectedBody(), request.MessageBoxText);
                    Assert.IsTrue(admissionFreeAtDisplay, "表示の時点で受付が取得できる。");
                    Assert.IsFalse(display.Task.IsCompleted, "実行の終端は通知の表示を待たない。");
                    string terminal = scenario == "failed" ? "failure" : "completion";
                    CollectionAssert.AreEqual(new[] { terminal, "notice" }, events.ToArray(), "通知は終端の報告より後。");
                }
                else
                {
                    StringAssert.Contains(decision, "skipped");
                    Assert.AreEqual(0, dialogs.Messages.Count);
                }
                Assert.AreEqual(0, fixture.LibraryDialogs.ModelMessages);
                Assert.AreEqual(0, fixture.LibraryDialogs.Messages.Count);
                switch (scenario)
                {
                    case "failed":
                        Assert.IsNotNull(failure);
                        Assert.AreEqual(1, failureCount, "失敗は一度だけ公開する。");
                        Assert.IsNull(completion, "失敗では完了を公開しない。");
                        MaintenanceReadFailureFixture.AssertCommitFailure(failure.Exception);
                        break;
                    case "canceled":
                        Assert.IsNotNull(completion);
                        Assert.IsTrue(completion.Canceled);
                        Assert.IsNull(failure);
                        break;
                    case "completed":
                        Assert.IsNotNull(completion);
                        Assert.IsFalse(completion.Canceled);
                        Assert.IsNull(failure);
                        break;
                }
            }
            finally
            {
                display.TrySetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
                await owner.WaitForIdleAsync();
            }
        });
    }
}
