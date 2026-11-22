using System.Collections;
using System.ComponentModel;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Wpf.RegularChartNavigationTestFixture;

namespace BeMusicSeeker.Tests;

/// <summary>統合候補一覧の構築を、構築の中（UIスレッド外）の到達の待合せで保留・失敗させるための注入です。固定時間の待機は使いません。</summary>
internal sealed class CandidateListBuildGate : IDisposable
{
    private readonly ManualResetEventSlim release = new(false);
    private int claimed;

    internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>保留の中で観測した取消の有無です。</summary>
    internal bool ObservedCancellation { get; private set; }

    internal int Calls;

    /// <summary>最初の構築だけを保留し、解放後に <paramref name="afterRelease"/> を実行します（例外を投げれば構築の失敗になります）。</summary>
    internal void Install(TestBmsLibrary library, Action<CancellationToken>? afterRelease = null, bool hold = true)
    {
        library.DuplicateMergeCandidateResolveHook = token =>
        {
            Interlocked.Increment(ref Calls);
            if (Interlocked.Exchange(ref claimed, 1) != 0) { return; }
            Reached.TrySetResult();
            if (hold) { release.Wait(); }
            ObservedCancellation = token.IsCancellationRequested;
            afterRelease?.Invoke(token);
        };
    }

    internal void Release() => release.Set();

    public void Dispose()
    {
        release.Set();
        release.Dispose();
    }
}

public sealed partial class RegularChartNavigationTests
{
    private static Task<bool> NavigateCandidateList(DuplicateNavigationTestFixture fixture)
        => fixture.NavigateDuplicate(MainViewUpdateMode.DuplicateFilterSelected, DuplicateViewContext.ForCandidateList());

    private static void PrepareReadyDuplicates(DuplicateNavigationTestFixture fixture)
    {
        fixture.StartDuplicate();
        fixture.CompleteDuplicate();
    }

    /// <summary>統合候補一覧の採用（表示中への遷移）を、時間ではなく実際の通知で待ちます。</summary>
    private static void AwaitCandidateListActive(MainWindowViewModel viewModel, string stage)
    {
        DuplicateMergeCandidateListOwner owner = viewModel.DuplicateMergeCandidateList;
        var active = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler changed = (_, args) =>
        { if (args.PropertyName == nameof(DuplicateMergeCandidateListOwner.IsViewActive) && owner.IsViewActive) { active.TrySetResult(); } };
        owner.PropertyChanged += changed;
        try
        {
            if (owner.IsViewActive) { active.TrySetResult(); }
            TestUiDispatcherHost.AwaitPresentationOnDispatcher(active.Task, stage);
        }
        finally { owner.PropertyChanged -= changed; }
    }

    /// <summary>
    /// 保証：未解析で統合候補一覧のノードを選び、共通受付が競合した場合は、Busy案内を一度だけ出し、前の表示・画面受付を保ち、一覧を採用せず、解放後も予約解析をしない。
    /// 検出する誤り：競合時の画面受付の置換、案内の重複・欠落、解放後の暗黙の解析開始、競合中の一覧の採用。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void CandidateList_ColdBusyKeepsViewAndNotifiesOnce()
    {
        var dialogs = new FileDbReportRecordingDialogs();
        RunColdDuplicateFixture(fixture =>
        {
            MainWindowViewModel vm = fixture.ViewModel;
            // 起動直後の初期表示の構築が行を置き換える前に捕捉しないよう、既存のBusyケースと同じ静止点（サマリーの表示）を挟む。
            vm.PlaylistWorkspace.RequestSummarySelection();
            fixture.AwaitSummary();
            RegularChartNavigationRequest selection = vm.RegularChartList.CaptureNavigation();
            IList rows = vm.MainChartList.Rows;
            Assert.IsTrue(fixture.Library.OperationAdmission.TryEnter(out IDisposable other));
            try
            {
                Task<bool> request = NavigateCandidateList(fixture);
                fixture.AwaitOwned(request, "candidate list request under another operation");
                Assert.IsFalse(request.Result);
                Assert.AreSame(selection, vm.RegularChartList.CaptureNavigation());
                Assert.AreSame(rows, vm.MainChartList.Rows);
                Assert.IsNull(fixture.Library.DuplicateChartGroups);
                Assert.AreEqual(1, dialogs.Messages.Count);
                Assert.AreEqual(Resources.Warn_LibraryOperationBusy, dialogs.Messages.Single().MessageBoxText);
                Assert.IsFalse(vm.DuplicateMergeCandidateList.IsViewActive);
            }
            finally { other.Dispose(); }
            fixture.AwaitOwned(fixture.AwaitReadyAsync(awaitRows: false), "rejected candidate list cleanup");
            Assert.IsNull(fixture.Library.DuplicateChartGroups, "解放後の予約解析を行いません。");
            Assert.AreEqual(1, dialogs.Messages.Count);
        }, dialogs: dialogs);
    }

    /// <summary>
    /// 保証：実行中の解析の準備へ合流して一覧を採用し、準備済みなら別のL操作中でも新しい受付を取らず（Busy案内なしで）採用する。
    /// 検出する誤り：実行中の準備への二重解析、準備済みでのBusy拒否、合流後の一覧の未採用。
    /// </summary>
    [TestMethod]
    [DataRow("joinsRunningPreparation")]
    [DataRow("preparedTakesNoNewAdmission")]
    [DoNotParallelize]
    public void CandidateList_JoinsRunningPreparationOrUsesPreparedResultWithoutAdmission(string condition)
    {
        var dialogs = new FileDbReportRecordingDialogs();
        RunColdDuplicateFixture(fixture =>
        {
            MainWindowViewModel vm = fixture.ViewModel;
            IDisposable? other = null;
            try
            {
                if (condition == "joinsRunningPreparation")
                {
                    fixture.StartDuplicate();
                    fixture.AwaitGate();
                    Task<bool> joined = NavigateCandidateList(fixture);
                    fixture.ReleaseProducer();
                    fixture.AwaitOwned(joined, "candidate list joins running preparation");
                    Assert.IsTrue(joined.Result);
                    fixture.AwaitOwned(fixture.AwaitReadyAsync(), "joined candidate list terminal");
                }
                else
                {
                    PrepareReadyDuplicates(fixture);
                    Assert.IsTrue(fixture.Library.OperationAdmission.TryEnter(out other));
                    Task<bool> request = NavigateCandidateList(fixture);
                    fixture.AwaitOwned(request, "prepared candidate list request");
                    Assert.IsTrue(request.Result);
                    fixture.AwaitOwned(fixture.AwaitReadyAsync(), "prepared candidate list terminal");
                    Assert.IsTrue(fixture.Library.OperationAdmission.IsActive, "別の操作の受付は保たれます。");
                }
                AwaitCandidateListActive(vm, "candidate list adoption");
                Assert.AreEqual(0, dialogs.Messages.Count, "Busy案内を出しません。");
                Assert.AreEqual(2, vm.DuplicateMergeCandidateList.RowSet.Count, "重複グループの二つのフォルダ");
                Assert.AreEqual(0, vm.MainChartList.Rows.Count, "譜面一覧は空です。");
            }
            finally { other?.Dispose(); }
        }, dialogs: dialogs);
    }

    /// <summary>
    /// 保証：後から選んだ画面を優先する。構築を止めた間に別の画面（フォルダ）を選ぶと、古い構築を採用せず（表示中にならず状態表も作らない）、
    /// 先にフォルダを選んでからノードを選べば一覧を採用する。
    /// 検出する誤り：後発画面を古い構築が上書きする、ノードの選択を後発のフォルダ選択が取り消す。
    /// </summary>
    [TestMethod]
    [DataRow("nodeThenFolderWhileBuildBlocked")]
    [DataRow("folderThenNode")]
    [DoNotParallelize]
    public void CandidateList_LaterSelectedScreenWins(string condition)
    {
        using var gate = new CandidateListBuildGate();
        RunColdDuplicateFixture(fixture =>
        {
            MainWindowViewModel vm = fixture.ViewModel;
            DuplicateMergeCandidateListOwner owner = vm.DuplicateMergeCandidateList;
            try
            {
                PrepareReadyDuplicates(fixture);
                string folder = fixture.Duplicates[0].Directory!;
                if (condition == "nodeThenFolderWhileBuildBlocked")
                {
                    gate.Install(fixture.Library);
                    Task<bool> node = NavigateCandidateList(fixture);
                    TestUiDispatcherHost.AwaitPresentationOnDispatcher(gate.Reached.Task, "candidate list build reached");
                    Task<bool> later = fixture.NavigateDuplicate(MainViewUpdateMode.DuplicateFilterSelected, folder);
                    fixture.AwaitOwned(node, "superseded candidate list request");
                    fixture.AwaitOwned(later, "later folder request");
                    gate.Release();
                    fixture.AwaitOwned(fixture.AwaitReadyAsync(), "later folder rows");
                    fixture.AwaitOwned(vm.RegularChartList.StopAsync(), "collect the superseded build");
                    Assert.IsFalse(owner.IsViewActive, "古い構築を採用しません。");
                    Assert.AreEqual(0, owner.RowSet.Count);
                    Assert.IsFalse(owner.TryGetState(fixture.Library.DuplicateChartGroups[0], out _));
                    Assert.AreEqual(1, vm.MainChartList.Rows.Count, "後発のフォルダの表示を保ちます。");
                }
                else
                {
                    fixture.AwaitOwned(fixture.NavigateDuplicate(MainViewUpdateMode.DuplicateFilterSelected, folder), "folder request");
                    fixture.AwaitOwned(fixture.AwaitReadyAsync(), "folder rows");
                    Assert.IsFalse(owner.IsViewActive);
                    fixture.AwaitOwned(NavigateCandidateList(fixture), "node after folder");
                    fixture.AwaitOwned(fixture.AwaitReadyAsync(), "node ready");
                    AwaitCandidateListActive(vm, "node after folder adoption");
                    Assert.AreEqual(2, owner.RowSet.Count);
                    Assert.AreEqual(0, vm.MainChartList.Rows.Count);
                }
            }
            finally { gate.Release(); }
        });
    }

    /// <summary>
    /// 保証：表示中に重複結果が変わって再構築しても、影響外のグループは同じインスタンスで状態（SELECT）を保ち、影響したグループ（新しいインスタンス）は初期値になる。
    /// Busy案内は出さず、終端で結果は準備済みである。
    /// 検出する誤り：再構築での状態の全消去、新しいインスタンスへの状態の引継ぎ、Busy案内、未準備の結果の採用。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void CandidateList_RebuildKeepsUnaffectedGroupStateAndResetsAffectedGroup()
    {
        var dialogs = new FileDbReportRecordingDialogs();
        RunColdDuplicateFixture(fixture =>
        {
            MainWindowViewModel vm = fixture.ViewModel;
            DuplicateMergeCandidateListOwner owner = vm.DuplicateMergeCandidateList;
            PrepareReadyDuplicates(fixture);
            fixture.AwaitOwned(NavigateCandidateList(fixture), "first candidate list");
            fixture.AwaitOwned(fixture.AwaitReadyAsync(), "first candidate list ready");
            AwaitCandidateListActive(vm, "first adoption");
            DuplicateGroup kept = fixture.Library.DuplicateChartGroups[0];
            Assert.IsTrue(owner.ToggleSelect(owner.RowSet[0]));
            DuplicateMergeCandidateGroupState keptState = owner.RowSet[0].State;
            var affected = new DuplicateGroup([.. kept.ChartFiles], [kept.Folders[0] + "-moved", kept.Folders[1] + "-moved"]);

            fixture.Library.DuplicateChartGroups = [kept, affected];
            fixture.AwaitOwned(NavigateCandidateList(fixture), "rebuild candidate list");
            fixture.AwaitOwned(fixture.AwaitReadyAsync(), "rebuild candidate list ready");
            var rowsApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            PropertyChangedEventHandler changed = (_, _) => { if (owner.RowSet.Count == 4) { rowsApplied.TrySetResult(); } };
            owner.PropertyChanged += changed;
            try
            {
                changed(owner, new PropertyChangedEventArgs(string.Empty));
                TestUiDispatcherHost.AwaitPresentationOnDispatcher(rowsApplied.Task, "rebuilt rows");
            }
            finally { owner.PropertyChanged -= changed; }

            Assert.IsTrue(owner.TryGetState(kept, out DuplicateMergeCandidateGroupState? same));
            Assert.AreSame(keptState, same, "影響外のグループは同じ状態を保ちます。");
            Assert.IsTrue(same!.IsSelected);
            Assert.IsTrue(owner.TryGetState(affected, out DuplicateMergeCandidateGroupState? fresh));
            Assert.IsFalse(fresh!.IsSelected, "影響したグループは初期値です。");
            Assert.AreEqual(0, dialogs.Messages.Count, "Busy案内を出しません。");
            Assert.IsNotNull(fixture.Library.DuplicateChartGroups, "終端で結果は準備済みです。");
        }, dialogs: dialogs);
    }

    /// <summary>
    /// 保証：ライブラリを交換（解除）した後に構築が終わっても、旧ライブラリの構築を採用しない（表示中にならず、状態表も作らない）。
    /// 検出する誤り：交換後の旧構築の採用、旧ライブラリのグループの状態の復活。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void CandidateList_LibraryReplacementRejectsOldBuild()
    {
        using var gate = new CandidateListBuildGate();
        RunColdDuplicateFixture(fixture =>
        {
            MainWindowViewModel vm = fixture.ViewModel;
            DuplicateMergeCandidateListOwner owner = vm.DuplicateMergeCandidateList;
            try
            {
                PrepareReadyDuplicates(fixture);
                gate.Install(fixture.Library);
                Task<bool> node = NavigateCandidateList(fixture);
                TestUiDispatcherHost.AwaitPresentationOnDispatcher(gate.Reached.Task, "candidate list build reached");
                owner.AttachLibrary(new object());
                gate.Release();
                fixture.AwaitOwned(node, "candidate list request");
                fixture.AwaitOwned(fixture.AwaitReadyAsync(), "candidate list terminal");
                fixture.AwaitOwned(vm.RegularChartList.StopAsync(), "collect the old build");
                Assert.IsFalse(owner.IsViewActive);
                Assert.AreEqual(0, owner.RowSet.Count);
                Assert.IsFalse(owner.TryGetState(fixture.Library.DuplicateChartGroups[0], out _));
            }
            finally { gate.Release(); }
        });
    }
}
