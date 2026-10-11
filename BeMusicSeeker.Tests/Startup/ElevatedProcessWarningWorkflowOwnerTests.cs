using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ElevatedProcessWarningWorkflowOwnerTests
{
    /// <summary>
    /// 保証：非昇格プロセスでは権限を一度調べるだけで警告を抑止し、通常利用へ不要な提示を要求しない。
    /// </summary>
    [TestMethod]
    public async Task NonElevatedProcess_IsSuppressedWithoutPresentation()
    {
        int probeCount = 0;
        int presentationCount = 0;
        ElevatedProcessWarningWorkflowOwner owner = new(() =>
        {
            probeCount++;
            return false;
        });
        owner.PresentationRequested += _ => presentationCount++;

        Assert.IsTrue(owner.Start(() => true));
        Assert.AreEqual(ElevatedProcessWarningWorkflowOutcome.Suppressed, await owner.Completion);
        Assert.AreEqual(1, probeCount);
        Assert.AreEqual(0, presentationCount);
    }

    /// <summary>
    /// 保証：昇格したプロセスでは警告を一度提示し、検出と表示成立の記録を分けて提示前に表示済みとしない。
    /// </summary>
    [TestMethod]
    public async Task ElevatedProcess_PresentsOnceAndPublishesDetectionAndShownLogs()
    {
        var infoMessages = new List<string>();
        int presentationCount = 0;
        ElevatedProcessWarningWorkflowOwner owner = new(
            () => true,
            infoLog: infoMessages.Add);
        owner.PresentationRequested += request =>
        {
            presentationCount++;
            request.Complete(true);
        };

        Assert.IsTrue(owner.Start(() => true));
        Assert.AreEqual(ElevatedProcessWarningWorkflowOutcome.Shown, await owner.Completion);
        Assert.AreEqual(1, presentationCount);
        CollectionAssert.AreEqual(
            new[]
            {
                "process_elevated drag_drop_limited_warning_detected=true",
                "process_elevated drag_drop_limited_warning_shown=true"
            },
            infoMessages);
    }

    /// <summary>
    /// 保証：昇格警告は一度だけ開始でき、提示で抑止された後の再開始でも警告を二重に要求しない。
    /// </summary>
    [TestMethod]
    public async Task Start_IsOneShot()
    {
        int presentationCount = 0;
        ElevatedProcessWarningWorkflowOwner owner = new(() => true);
        owner.PresentationRequested += request =>
        {
            presentationCount++;
            request.Complete(false);
        };

        Assert.IsTrue(owner.Start(() => true));
        Assert.AreEqual(ElevatedProcessWarningWorkflowOutcome.Suppressed, await owner.Completion);
        Assert.IsFalse(owner.Start(() => true));
        Assert.AreEqual(1, presentationCount);
    }

    /// <summary>
    /// 保証：必須の提示窓口が未接続なら提示失敗として記録・終端し、警告を表示した成功へ読み替えない。
    /// </summary>
    [TestMethod]
    public async Task MissingPresentationRoute_IsReportedAsWorkflowFailure()
    {
        var warningContexts = new List<string>();
        ElevatedProcessWarningWorkflowOwner owner = new(
            () => true,
            warningLog: (_, context) => warningContexts.Add(context));

        Assert.IsTrue(owner.Start(() => true));
        Assert.AreEqual(ElevatedProcessWarningWorkflowOutcome.PresentationFailed, await owner.Completion);
        CollectionAssert.AreEqual(
            new[] { "process_elevated_warning_presentation_unavailable" },
            warningContexts);
    }

    /// <summary>
    /// 保証：nullの提示条件を拒否しても所有者の開始状態を壊さず、後の有効な条件で開始できる。
    /// </summary>
    [TestMethod]
    public async Task NullPresentationGuard_DoesNotCorruptOwnerState()
    {
        ElevatedProcessWarningWorkflowOwner owner = new(() => true);

        Assert.ThrowsException<ArgumentNullException>(() => owner.Start((Func<bool>)null!));
        Assert.IsTrue(owner.Start(() => false));
        Assert.AreEqual(ElevatedProcessWarningWorkflowOutcome.Suppressed, await owner.Completion);
    }

    /// <summary>
    /// 保証：提示不可の状態なら権限調査前に抑止し、閉鎖等の不要な場面で調査・提示を始めない。
    /// </summary>
    [TestMethod]
    public async Task PresentationGuardFailure_SuppressesWarningWithoutProbe()
    {
        int probeCount = 0;
        ElevatedProcessWarningWorkflowOwner owner = new(() =>
        {
            probeCount++;
            return true;
        });

        Assert.IsTrue(owner.Start(() => false));
        Assert.AreEqual(ElevatedProcessWarningWorkflowOutcome.Suppressed, await owner.Completion);
        Assert.AreEqual(0, probeCount);
    }

    /// <summary>
    /// 保証：権限調査の例外は調査失敗として終端と診断へ返し、未確認の昇格警告を提示しない。
    /// </summary>
    [TestMethod]
    public async Task ProbeFailure_IsReportedAndDoesNotRequestPresentation()
    {
        var warningContexts = new List<string>();
        int presentationCount = 0;
        ElevatedProcessWarningWorkflowOwner owner = new(
            () => throw new InvalidOperationException("probe failed"),
            warningLog: (_, context) => warningContexts.Add(context));
        owner.PresentationRequested += _ => presentationCount++;

        Assert.IsTrue(owner.Start(() => true));
        Assert.AreEqual(ElevatedProcessWarningWorkflowOutcome.ProbeFailed, await owner.Completion);
        CollectionAssert.AreEqual(new[] { "process_elevation_check_failed" }, warningContexts);
        Assert.AreEqual(0, presentationCount);
    }

    /// <summary>
    /// 保証：提示条件の評価例外は提示失敗として元の段階を診断へ残し、単なる抑止や表示成功にしない。
    /// </summary>
    [TestMethod]
    public async Task PresentationGuardFailure_IsReportedAsWorkflowFailure()
    {
        var warningContexts = new List<string>();
        ElevatedProcessWarningWorkflowOwner owner = new(
            () => true,
            warningLog: (_, context) => warningContexts.Add(context));

        Assert.IsTrue(owner.Start(() => throw new InvalidOperationException("window state failed")));
        Assert.AreEqual(ElevatedProcessWarningWorkflowOutcome.PresentationFailed, await owner.Completion);
        CollectionAssert.AreEqual(
            new[] { "process_elevated_warning_workflow_failed" },
            warningContexts);
    }

    /// <summary>
    /// 保証：ダイアログ側から提示失敗が返ったら表示失敗の診断を残し、表示済みとして完了しない。
    /// </summary>
    [TestMethod]
    public async Task PresentationFailure_IsReportedAsWarning()
    {
        var warningContexts = new List<string>();
        ElevatedProcessWarningWorkflowOwner owner = new(
            () => true,
            warningLog: (_, context) => warningContexts.Add(context));
        owner.PresentationRequested += request => request.Fail(new InvalidOperationException("dialog failed"));

        Assert.IsTrue(owner.Start(() => true));
        Assert.AreEqual(ElevatedProcessWarningWorkflowOutcome.PresentationFailed, await owner.Completion);
        CollectionAssert.AreEqual(
            new[] { "process_elevated drag_drop_limited_warning_failed" },
            warningContexts);
    }

    /// <summary>
    /// 保証：権限調査中の閉鎖は後から昇格が判明しても警告を出さず、開始済み調査を回収して閉鎖終端にする。
    /// </summary>
    [TestMethod]
    public async Task ClosingWhileProbeIsPending_SuppressesPresentation()
    {
        var probeEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int presentationCount = 0;
        ElevatedProcessWarningWorkflowOwner owner = new(() =>
        {
            probeEntered.TrySetResult(true);
            probeRelease.Task.GetAwaiter().GetResult();
            return true;
        });
        owner.PresentationRequested += _ => presentationCount++;

        Task<bool> startTask = Task.Run(() => owner.Start(() => true));
        try
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(probeEntered.Task, startTask, "elevated-warning.probe");
            Assert.IsTrue(owner.NotifyClosing());
            probeRelease.SetResult(true);
            Assert.IsTrue(await startTask);
            Assert.AreEqual(ElevatedProcessWarningWorkflowOutcome.Closing, await owner.Completion);
            Assert.AreEqual(0, presentationCount);
        }
        finally
        {
            probeRelease.TrySetResult(true);
            await startTask;
        }
    }

    /// <summary>
    /// 保証：提示待ちの閉鎖後に表示済み通知が届いても閉鎖終端を保ち、遅い通知で表示成功へ戻さない。
    /// </summary>
    [TestMethod]
    public async Task ClosingWhilePresentationIsPending_DropsShownResult()
    {
        var requestReceived = new TaskCompletionSource<ElevatedProcessWarningPresentationRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        ElevatedProcessWarningWorkflowOwner owner = new(() => true);
        owner.PresentationRequested += request => requestReceived.SetResult(request);

        Assert.IsTrue(owner.Start(() => true));
        await TestUiDispatcherHost.AwaitNotificationAsync(requestReceived.Task, owner.Completion, "pending-elevated-warning-presentation");
        ElevatedProcessWarningPresentationRequest request = await requestReceived.Task;
        Assert.IsTrue(owner.NotifyClosing());
        request.Complete(true);

        Assert.AreEqual(ElevatedProcessWarningWorkflowOutcome.Closing, await owner.Completion);
    }
}
