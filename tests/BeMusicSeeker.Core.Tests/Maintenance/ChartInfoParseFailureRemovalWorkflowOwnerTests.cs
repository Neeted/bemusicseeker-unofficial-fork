using System.Windows;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using static BeMusicSeeker.TestSupport.Components.ChartInfoParseFailureRemovalWorkflowOwnerTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ChartInfoParseFailureRemovalWorkflowOwnerTests
{
    /// <summary>
    /// 空値を除きMD5をtrim・小文字化して重複をまとめ、対象有無を判断することで同一ハッシュの二重削除や空対象の受理を検出する。
    /// </summary>
    [TestMethod]
    public void Request_NormalizesAndFreezesSelectedMd5s()
    {
        ChartInfoParseFailureRemovalRequest request = new(
            [null, " ", new string('A', 32), new string('a', 32), " " + new string('B', 32) + " "]);

        CollectionAssert.AreEqual(
            new[] { new string('a', 32), new string('b', 32) },
            request.Md5s.ToArray());
        Assert.IsTrue(request.HasTargets);
        Assert.IsFalse(new ChartInfoParseFailureRemovalRequest([null, " "]).HasTargets);
    }

    /// <summary>
    /// No・Cancel・未選択閉鎖は拒否で終え、workerと保存削除を始めず、利用者の拒否を承認へ読み替える誤りを検出する。
    /// </summary>
    [TestMethod]
    public async Task RemoveAsync_UserRejectionDoesNotScheduleOrStore()
    {

        foreach (UiDialogResult confirmation in new[]
        {
            UiDialogResult.FromMessageBoxResult(MessageBoxResult.No),
            UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel),
            UiDialogResult.ClosedByUser(MessageBoxResult.Cancel)
        })
        {
            var dialogs = new RecordingDialogService { ConfirmationResult = confirmation };
            var store = new RecordingStore();
            int scheduleCalls = 0;
            var owner = new ChartInfoParseFailureRemovalWorkflowOwner(
                () => null!,
                dialogs,
                action =>
                {
                    scheduleCalls++;
                    return Task.Run(action);
                },
                store);

            ChartInfoParseFailureRemovalResult result = await owner.BeginRemove(
                new ChartInfoParseFailureRemovalRequest([new string('a', 32)])).Completion;

            Assert.AreEqual(ChartInfoParseFailureRemovalStatus.Rejected, result.Status);
            Assert.IsFalse(result.Accepted);
            Assert.AreEqual(0, scheduleCalls);
            Assert.AreEqual(0, store.CallCount);
        }
    }

    /// <summary>
    /// 確認画面の失敗は元原因を含む未受理失敗として返し、保存削除を呼ばず、表示できなかった確認での実行を検出する。
    /// </summary>
    [TestMethod]
    public async Task RemoveAsync_DialogFailureIsExplicitAndDoesNotStore()
    {

        var failure = new InvalidOperationException("dialog unavailable");
        var dialogs = new RecordingDialogService { ConfirmationResult = UiDialogResult.Failed(failure) };
        var store = new RecordingStore();
        var owner = new ChartInfoParseFailureRemovalWorkflowOwner(
            () => null!,
            dialogs,
            action => Task.Run(action),
            store);

        ChartInfoParseFailureRemovalResult result = await owner.BeginRemove(
            new ChartInfoParseFailureRemovalRequest([new string('a', 32)])).Completion;

        Assert.AreEqual(ChartInfoParseFailureRemovalStatus.Failed, result.Status);
        Assert.IsFalse(result.Accepted);
        Assert.AreSame(failure, result.Failure.InnerException);
        Assert.AreEqual(0, store.CallCount);
    }

    /// <summary>
    /// 利用者の承認後にライブラリがなければ受理済み失敗として返し、workerを始めず、対象消失を成功扱いする誤りを検出する。
    /// </summary>
    [TestMethod]
    public async Task RemoveAsync_AcceptedButLibraryUnavailableIsFailure()
    {

        var dialogs = new RecordingDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
        };
        int scheduleCalls = 0;
        var owner = new ChartInfoParseFailureRemovalWorkflowOwner(
            () => null!,
            dialogs,
            action =>
            {
                scheduleCalls++;
                return Task.Run(action);
            },
            new RecordingStore());

        ChartInfoParseFailureRemovalResult result = await owner.BeginRemove(
            new ChartInfoParseFailureRemovalRequest([new string('a', 32)])).Completion;

        Assert.AreEqual(ChartInfoParseFailureRemovalStatus.Failed, result.Status);
        Assert.IsTrue(result.Accepted);
        Assert.IsNotNull(result.Failure);
        Assert.AreEqual(0, scheduleCalls);
    }

}
