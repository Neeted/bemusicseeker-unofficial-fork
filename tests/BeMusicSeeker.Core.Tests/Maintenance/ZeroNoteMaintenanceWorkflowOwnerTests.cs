using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using static BeMusicSeeker.TestSupport.Components.SelectedChartMutationWorkflowOwnerTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ZeroNoteMaintenanceWorkflowOwnerTests
{
    /// <summary>
    /// ライブラリ未接続の再確認は通知せず未開始で終え、保守callbackを呼ばず、対象のない実行の開始や不要な警告を検出する。
    /// </summary>
    [TestMethod]
    public async Task RecheckAsync_WithoutAttachedLibraryIsNoOp()
    {
        var synchronizer = new ChartFileOperationSynchronizer();
        var dialogs = new FakeUiDialogService();
        int recheckCount = 0;
        var owner = new ZeroNoteMaintenanceWorkflowOwner(
            () => null!,
            synchronizer,
            dialogs,
            (_, capability) => recheckCount++);

        ZeroNoteRecheckStatus status = await owner.RecheckAsync();

        Assert.AreEqual(ZeroNoteRecheckStatus.NotStarted, status);
        Assert.AreEqual(0, recheckCount);
        Assert.AreEqual(0, dialogs.Messages.Count);
    }

    /// <summary>
    /// 共通受付Lの保持中の再確認は副作用前に拒否し、受付使用中の警告（本文・題名・Exclamation・OK）を一度だけ表示して受付使用中を返す。
    /// 解放だけでは実行せず、解放後の新規要求は警告なしで現在ライブラリを一回処理する。黙った拒否、競合中の実行、自動再実行、永久拒否を検出する。
    /// </summary>
    [TestMethod]
    public async Task RecheckAsync_WarnsOnceWhileSharedChartFileGateIsBusyThenRunsAfterRelease()
    {
        var synchronizer = new ChartFileOperationSynchronizer();
        var dialogs = new FakeUiDialogService();
        BMSLibrary library = UninitializedBmsLibrary.Create();
        int recheckCount = 0;
        var owner = new ZeroNoteMaintenanceWorkflowOwner(
            () => library,
            synchronizer,
            dialogs,
            (actualLibrary, _) =>
            {
                Assert.AreSame(library, actualLibrary);
                Interlocked.Increment(ref recheckCount);
            });
        Assert.IsTrue(synchronizer.TryEnter(out IDisposable incumbent));
        using (incumbent)
        {
            Assert.AreEqual(ZeroNoteRecheckStatus.RejectedBusy, await owner.RecheckAsync());
            Assert.AreEqual(0, Volatile.Read(ref recheckCount));
            UiMessageRequest warning = dialogs.Messages.Single();
            Assert.AreEqual(BeMusicSeeker.Properties.Resources.Warn_LibraryOperationBusy, warning.MessageBoxText);
            Assert.AreEqual(BeMusicSeeker.Properties.Resources.Warning, warning.Caption);
            Assert.AreEqual(MessageBoxImage.Exclamation, warning.Icon);
            Assert.AreEqual(MessageBoxButton.OK, warning.Button);
        }

        Assert.AreEqual(0, Volatile.Read(ref recheckCount), "解放だけでは拒否した要求を実行しません。");
        Assert.AreEqual(ZeroNoteRecheckStatus.Completed, await owner.RecheckAsync());
        Assert.AreEqual(1, Volatile.Read(ref recheckCount));
        Assert.AreEqual(1, dialogs.Messages.Count, "解放後の明示要求では警告しません。");
        Assert.IsTrue(synchronizer.CanEnter);
    }

    /// <summary>
    /// 終了要求で受付が閉じた後の再確認は警告せず未開始を返し、終了中の不要な通知を検出する。
    /// </summary>
    [TestMethod]
    public async Task RecheckAsync_ClosedAdmissionIsSilentlyNotStarted()
    {
        var synchronizer = new ChartFileOperationSynchronizer();
        synchronizer.CloseAdmission();
        var dialogs = new FakeUiDialogService();
        int recheckCount = 0;
        var owner = new ZeroNoteMaintenanceWorkflowOwner(
            () => UninitializedBmsLibrary.Create(),
            synchronizer,
            dialogs,
            (_, _) => recheckCount++);

        Assert.AreEqual(ZeroNoteRecheckStatus.NotStarted, await owner.RecheckAsync());
        Assert.AreEqual(0, recheckCount);
        Assert.AreEqual(0, dialogs.Messages.Count);
    }

    /// <summary>
    /// 受理した再確認の保守callbackが失敗すると元例外を非同期入口へ伝え、成功や受付使用中への読み替えを検出する。
    /// </summary>
    [TestMethod]
    public async Task RecheckAsync_PropagatesLibraryFailure()
    {
        BMSLibrary library = UninitializedBmsLibrary.Create();
        var owner = new ZeroNoteMaintenanceWorkflowOwner(
            () => library,
            new ChartFileOperationSynchronizer(),
            new FakeUiDialogService(),
            (_, capability) => throw new InvalidOperationException("recheck failed"));

        InvalidOperationException exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => owner.RecheckAsync());

        Assert.AreEqual("recheck failed", exception.Message);
    }
}
