using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using static BeMusicSeeker.TestSupport.Wpf.RegularChartNavigationTestFixture;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace BeMusicSeeker.Tests;

public sealed partial class RegularChartCommitAndLifecycleTests
{
    /// <summary>
    /// 保証：統合候補一覧の構築（TITLE・ARTISTの解決）の例外は失敗通知を一度だけ出し、取消（停止で取り消された構築）は通知せず、
    /// 構築中の停止は構築Taskの終端まで待って通知せずに回収する。いずれも一覧を採用しない。
    /// 検出する誤り：失敗の握りつぶし・二重通知、取消の通知、停止が構築を待たず取り残す、停止後の採用。
    /// </summary>
    [TestMethod]
    [DataRow("fault")]
    [DataRow("canceledByStop")]
    [DataRow("stopWhileBuilding")]
    [DoNotParallelize]
    public void CandidateListBuild_FailureNotifiesOnceCancelIsSilentAndStopCollectsBuild(string condition)
    {
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialogs = new FileDbReportRecordingDialogs
        {
            MessageHandler = _ =>
            {
                notified.TrySetResult();
                return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
            }
        };
        using var gate = new CandidateListBuildGate();
        RunColdDuplicateFixture(fixture =>
        {
            MainWindowViewModel vm = fixture.ViewModel;
            DuplicateMergeCandidateListOwner owner = vm.DuplicateMergeCandidateList;
            try
            {
                fixture.StartDuplicate();
                fixture.CompleteDuplicate();
                switch (condition)
                {
                    case "fault":
                        gate.Install(fixture.Library, _ => throw new InvalidOperationException("resolve failed"), hold: false);
                        fixture.AwaitOwned(fixture.NavigateDuplicate(MainViewUpdateMode.DuplicateFilterSelected, DuplicateViewContext.ForCandidateList()), "faulting candidate list request");
                        TestUiDispatcherHost.AwaitPresentationOnDispatcher(notified.Task, "candidate list failure notification");
                        Exception? buildFault = null;
                        try { fixture.AwaitOwned(fixture.AwaitReadyAsync(awaitRows: false), "failing candidate list projection"); }
                        catch (Exception exception) { buildFault = exception; }
                        Assert.AreEqual("resolve failed", buildFault?.Message, "構築の失敗が画面構築Taskの失敗として終端します。");
                        fixture.AwaitOwned(vm.RegularChartList.StopAsync(), "collect failing build");
                        Assert.AreEqual(1, dialogs.Messages.Count, "失敗通知は一度だけです。");
                        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Msg_error_unexpected, dialogs.Messages.Single().MessageBoxText);
                        break;
                    case "canceledByStop":
                        gate.Install(fixture.Library, token => token.ThrowIfCancellationRequested());
                        fixture.AwaitOwned(fixture.NavigateDuplicate(MainViewUpdateMode.DuplicateFilterSelected, DuplicateViewContext.ForCandidateList()), "candidate list request");
                        TestUiDispatcherHost.AwaitPresentationOnDispatcher(gate.Reached.Task, "candidate list build reached");
                        Task canceledStop = vm.RegularChartList.StopAsync();
                        gate.Release();
                        fixture.AwaitOwned(canceledStop, "stop collects canceled build");
                        Assert.IsTrue(gate.ObservedCancellation, "停止で構築の取消が要求されています。");
                        Assert.AreEqual(0, dialogs.Messages.Count, "取消は通知しません。");
                        break;
                    default:
                        gate.Install(fixture.Library);
                        fixture.AwaitOwned(fixture.NavigateDuplicate(MainViewUpdateMode.DuplicateFilterSelected, DuplicateViewContext.ForCandidateList()), "candidate list request");
                        TestUiDispatcherHost.AwaitPresentationOnDispatcher(gate.Reached.Task, "candidate list build reached");
                        Task stop = vm.RegularChartList.StopAsync();
                        Assert.IsFalse(stop.IsCompleted, "停止は構築Taskの終端を待ちます。");
                        gate.Release();
                        fixture.AwaitOwned(stop, "stop collects build");
                        Assert.AreEqual(0, dialogs.Messages.Count, "停止での回収は通知しません。");
                        break;
                }
                Assert.IsFalse(owner.IsViewActive, "失敗・取消・停止した構築は採用しません。");
                Assert.AreEqual(0, owner.RowSet.Count);
            }
            finally { gate.Release(); }
        }, dialogs: dialogs);
    }
}
