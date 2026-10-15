using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class StartupLibraryInitializationWorkflowOwnerTests
{
    /// <summary>バックアップ・修復・完了案内の独立した通知を全て試み、表示のIO失敗と所有していない取消しを初期化へ再送出しません。</summary>
    /// <remarks>Windowを作らず、実通知管理主体と表示依存だけを用います。失敗位置と例外種類を変えて後続通知の欠落も検査します。</remarks>
    [DataTestMethod]
    [DataRow(0, false)]
    [DataRow(1, false)]
    [DataRow(0, true)]
    [DataRow(1, true)]
    public async Task CompletionNotifications_FailurePreservesIndependentNotices(int failureIndex, bool unownedCancellation)
    {
        using var gate = new SemaphoreSlim(1, 1);
        var owner = new StartupLibraryInitializationWorkflowOwner(gate);
        var messages = new List<string>();
        var dialogs = new MainWindowViewModelStartupProgressTests.CompletionDialogs
        {
            MessageObservedAsync = request =>
            {
                messages.Add(request.MessageBoxText);
                return messages.Count - 1 == failureIndex
                    ? Task.FromException(unownedCancellation ? new OperationCanceledException("notification cancelled") : new IOException("notification unavailable"))
                    : Task.CompletedTask;
            }
        };
        var repairFailure = new IOException("repair failed");
        var repair = new LeapYearFolderRepairNotification(true, [("failed-folder", repairFailure)]);
        var backup = new UiMessageRequest("backup", "test", UiDialogButton.OK, UiDialogIcon.Exclamation, UiDialogDefaultResult.OK);

        await owner.PresentCompletionNotificationsAsync(repair, true, true, dialogs, () => false, () => true, [backup]);

        CollectionAssert.AreEqual(new[]
        {
            "backup",
            string.Format(Resources.Error_FailedToChangeDate, repairFailure.Message) + Environment.NewLine + "failed-folder",
            Resources.Warn_LR2LeapYearBugDetected,
            Resources.Warning_LR2RootPathNotSet,
            Resources.Msg_init_completed
        }, messages);
        Assert.AreEqual(0, dialogs.ConfirmationCount, "事後通知の局所化で事前の承認を代替しません。");
    }

    /// <summary>通知待機後に終了または新要求が先着した場合は、それ以降の旧要求の案内を開始しません。</summary>
    /// <remarks>表示は実Taskで待ち、終了だけを取消しとして区別します。新要求では旧要求を静穏に終えます。</remarks>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CompletionNotifications_ShutdownOrStaleRequestStopsSubsequentNotices(bool shutdown)
    {
        using var gate = new SemaphoreSlim(1, 1);
        var owner = new StartupLibraryInitializationWorkflowOwner(gate);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int count = 0;
        bool stopped = false;
        var dialogs = new MainWindowViewModelStartupProgressTests.CompletionDialogs
        {
            MessageObservedAsync = _ => { count++; entered.TrySetResult(); return release.Task; }
        };
        Task operation = owner.PresentCompletionNotificationsAsync(null, true, true, dialogs,
            () => stopped && shutdown, () => !stopped || shutdown);
        try
        {
            await Task.WhenAny(entered.Task, operation);
            Assert.IsTrue(entered.Task.IsCompleted);
            Assert.IsFalse(operation.IsCompleted);
            stopped = true;
            release.TrySetResult();
            if (shutdown) { await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => operation); }
            else { await operation; }
            Assert.AreEqual(1, count);
        }
        finally
        {
            release.TrySetResult();
            try { await operation; }
            catch (OperationCanceledException) when (shutdown && stopped) { }
        }
    }

    /// <summary>
    /// 保証：初期化gateの権限を重複解放しても一度だけ返却し、待っていた次の初期化へ直ちに引き渡す。
    /// </summary>
    [TestMethod]
    public async Task GateLease_ReleasesExactlyOnceAndAllowsImmediateNextAcquire()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var owner = new StartupLibraryInitializationWorkflowOwner(gate);
        StartupLibraryInitializationGateLease first = await owner.AcquireGateAsync();
        Task<StartupLibraryInitializationGateLease> secondAcquire = owner.AcquireGateAsync();

        Assert.AreEqual(0, gate.CurrentCount);
        Assert.IsFalse(secondAcquire.IsCompleted);

        first.Dispose();
        first.Dispose();

        using StartupLibraryInitializationGateLease second =
            await secondAcquire;
        Assert.AreEqual(0, gate.CurrentCount);
        second.Dispose();
        Assert.AreEqual(1, gate.CurrentCount);
    }

    /// <summary>
    /// 保証：必須プレイリストの完了通知で待機を終える一方、独立した導入推定準備完了を先に公開しない。
    /// </summary>
    [TestMethod]
    public async Task StartupReadiness_TracksRequiredCompletionIndependentlyFromInstallEstimation()
    {
        var coordinator = new StartupReadinessCoordinator();
        coordinator.BeginPlaylistInitialization("test");
        Task readiness = coordinator.WaitForRequiredPlaylistReadinessAsync();
        Assert.IsFalse(readiness.IsCompleted);
        coordinator.MarkRequiredPlaylistReady();
        await readiness;
        Assert.IsTrue(coordinator.IsRequiredPlaylistReady);
        Assert.IsFalse(coordinator.IsInstallEstimationReady);
    }

    /// <summary>
    /// 保証：終了取消のcallbackが失敗しても他callbackと必須・導入推定の待機を終端させ、元のcallback失敗を保持する。
    /// </summary>
    [TestMethod]
    public async Task StartupReadiness_ShutdownTerminalizesWaitersDespiteCallbackFailure()
    {
        var coordinator = new StartupReadinessCoordinator();
        coordinator.BeginPlaylistInitialization("test");
        Task capturedRequiredReadiness = coordinator.RequiredPlaylistReadiness;
        Task capturedInstallEstimationReadiness = coordinator.InstallEstimationReadiness;
        Task readinessWaiter = coordinator.WaitForRequiredPlaylistReadinessAsync();
        var cancellationCallbackFailure = new InvalidOperationException("shutdown cancellation callback failed");
        bool callbackAfterFailureRan = false;
        using CancellationTokenRegistration callbackAfterFailure = coordinator.ShutdownToken.Register(() => callbackAfterFailureRan = true);
        using CancellationTokenRegistration failingCallback = coordinator.ShutdownToken.Register(() => throw cancellationCallbackFailure);
        AggregateException shutdownFailure = Assert.ThrowsException<AggregateException>(() => coordinator.RequestShutdown("test"));
        Assert.AreSame(cancellationCallbackFailure, shutdownFailure.Flatten().InnerExceptions[0]);
        Assert.IsTrue(callbackAfterFailureRan);
        Assert.IsTrue(capturedRequiredReadiness.IsCanceled);
        Assert.IsTrue(capturedInstallEstimationReadiness.IsCanceled);
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => readinessWaiter);
    }

    /// <summary>
    /// 保証：プレイリスト初期化の失敗は元の例外で待機側へ伝わり、失敗した必須処理を準備完了として公開しない。
    /// </summary>
    [TestMethod]
    public async Task StartupReadiness_FailedInitializationPreservesOriginalFailure()
    {
        var coordinator = new StartupReadinessCoordinator();
        coordinator.BeginPlaylistInitialization("test");
        Task readinessWaiter = coordinator.WaitForRequiredPlaylistReadinessAsync();
        var failure = new InvalidOperationException("playlist initialization failed");
        coordinator.FailRequiredPlaylistReadiness(failure);
        Exception observed = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => readinessWaiter);
        Assert.AreSame(failure, observed);
        Assert.IsFalse(coordinator.IsRequiredPlaylistReady);
    }

    /// <summary>
    /// 保証：初期化の継続処理が失敗すると元例外をAggregateExceptionへ保持し、完了待ちだけで成功扱いしない。
    /// </summary>
    [TestMethod]
    public void InitializationService_RunInitialization_PropagatesContinuationFailure()
    {
        var service = new BmsLibraryInitializationService();
        using var semaphore = new SemaphoreSlim(1, 1);
        var failure = new InvalidOperationException("initialization continuation failed");

        AggregateException exception = Assert.ThrowsException<AggregateException>(() =>
            service.RunInitialization(
                [
                    () =>
                    {
                        try
                        {
                            throw failure;
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }
                ],
                semaphore,
                loadSavedData: null,
                applyFilesAndProjection: null));

        Assert.AreSame(failure, exception.InnerException);
    }
}
