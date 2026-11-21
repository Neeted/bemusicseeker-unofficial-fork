using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.SelectedChartAudioConversionWorkflowOwnerTestSupport;
using static BeMusicSeeker.TestSupport.Components.SplitPlacementTestSupport;
using static BeMusicSeeker.TestSupport.Wpf.RegularChartNavigationTestFixture;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 分裂配置のツリーと一覧の実際の接続（ツリーの選択から探索、ツリーの項目、一覧の表示、失効、重複との独立）を、実ライブラリと標準のVMで確認します。
/// 探索の手順の細部は <c>SplitPlacementSearchWorkflowOwnerTests</c>、一覧の中身は通常一覧の構築のテストが担当します。
/// </summary>
public sealed partial class RegularChartNavigationTests
{
    /// <summary>探索の入力を設定します。重複の準備データの二つの譜面フォルダ（主ハッシュが同じ）の一方と、別の譜面フォルダが音源を共有します。</summary>
    private static (string Related, string Target) SetSplitPlacementIndex(DuplicateNavigationTestFixture fixture)
    {
        string related = Path.GetDirectoryName(fixture.Duplicates[0].Path)!;
        string target = Path.GetDirectoryName(fixture.Target.Path)!;
        SetIndex(fixture.Library, new(related, Keys(1, 10)), new(target, Keys(1, 10)));
        return (related, target);
    }

    private static void AwaitIdleFence()
        => TestUiDispatcherHost.AwaitTaskOnDispatcher(
            TestUiDispatcherHost.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task, "dispatcher idle fence");

    private static string[] RowPaths(MainWindowViewModel vm) => [.. vm.MainChartList.Rows.Cast<LibraryChartRow>().Select(row => row.Chart.Path).Order()];

    /// <summary>
    /// 保証：分裂配置のルートの選択で、探索の結果がツリーの項目（結果と同じインスタンス）へ反映され、選択時の文脈で分裂配置の一覧が表示され（グループの譜面だけ）、
    /// 重複の解析も重複の結果も作らず、受付は解放される。続けて重複の解析を行っても、分裂配置の結果・ツリーは変わらない。
    /// 検出する誤り：分裂配置の選択による重複の解析の開始、ツリーへの未反映、重複の解析による分裂配置の結果の置換、受付の解放漏れ。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void SplitPlacement_SelectionSearchesShowsTreeAndListWithoutDuplicateAnalysis()
    {
        var dialogs = new RecordingDialogService(new EventLog());
        RunColdDuplicateFixture(fixture =>
        {
            MainWindowViewModel vm = fixture.ViewModel;
            (string related, _) = SetSplitPlacementIndex(fixture);
            var treeGroups = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ((INotifyCollectionChanged)vm.MaintenanceTree.SplitPlacementTreeChildren).CollectionChanged += (_, _) =>
            { if (vm.MaintenanceTree.SplitPlacementTreeChildren.Count == 2) { treeGroups.TrySetResult(); } };

            TestUiDispatcherHost.AwaitTaskOnDispatcher(vm.SplitPlacementSearch.SelectAsync(null), "split placement root selection");
            TestUiDispatcherHost.AwaitTaskOnDispatcher(treeGroups.Task, "split placement tree reflection");

            List<DuplicateGroup> result = fixture.Library.SplitPlacementGroups!;
            Assert.AreEqual(1, result.Count);
            Assert.AreSame(vm.MaintenanceTree.SplitPlacementCandidateListNodeItem, vm.MaintenanceTree.SplitPlacementTreeChildren[0], "先頭は統合候補一覧のノード。");
            Assert.AreSame(result[0], vm.MaintenanceTree.SplitPlacementTreeChildren[1], "ツリーの項目は結果のグループ。");
            Assert.AreEqual(MainViewUpdateMode.SplitPlacementFilterSelected, vm.MainChartList.LastAppliedColumnMode);
            CollectionAssert.AreEqual(new[] { fixture.Duplicates[0].Path, fixture.Target.Path }.Order().ToArray(), RowPaths(vm), "グループの譜面だけを表示する。");
            Assert.IsNull(fixture.Library.DuplicateChartGroups, "重複の解析を始めない。");
            Assert.IsTrue(vm.RegularChartList.PrepareDuplicateDataAsync().IsCompleted, "重複の準備も始めない。");
            Assert.AreEqual(0, vm.MaintenanceTree.DuplicateTreeItems.Count);
            Assert.IsTrue(fixture.Library.OperationAdmission.CanEnter, "受付を解放している。");
            Assert.AreEqual(0, dialogs.Messages.Count);
            Assert.AreEqual(related, result[0].Folders[0]);

            PrepareReadyDuplicates(fixture);
            AwaitIdleFence();

            Assert.AreSame(result, fixture.Library.SplitPlacementGroups, "重複の解析で分裂配置の結果は変わらない。");
            Assert.AreSame(result[0], vm.MaintenanceTree.SplitPlacementTreeChildren.OfType<DuplicateGroup>().Single(), "重複の解析で分裂配置のツリーは変わらない。");
        }, dialogs: dialogs);
    }

    /// <summary>
    /// 保証：分裂配置の一覧を表示中に結果が失効すると、ツリーの子と一覧が空になり、自動では探索し直さない（進捗も受付も新たに使わない）。
    /// 検出する誤り：失効後の古い結果の表示の残存、失効による自動の再探索。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void SplitPlacement_InvalidationEmptiesTreeAndDisplayedListWithoutResearching()
    {
        var dialogs = new RecordingDialogService(new EventLog());
        RunColdDuplicateFixture(fixture =>
        {
            MainWindowViewModel vm = fixture.ViewModel;
            SetSplitPlacementIndex(fixture);
            TestUiDispatcherHost.AwaitTaskOnDispatcher(vm.SplitPlacementSearch.SelectAsync(null), "split placement root selection");
            Assert.AreEqual(2, vm.MainChartList.Rows.Count);
            UiProgressRequestHolder before = new(dialogs.ProgressRequest);
            var emptied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            PropertyChangedEventHandler rowsChanged = (_, args) =>
            { if (args.PropertyName == nameof(MainChartListViewModel.Rows) && vm.MainChartList.Rows.Count == 0) { emptied.TrySetResult(); } };
            vm.MainChartList.PropertyChanged += rowsChanged;
            try
            {
                fixture.Library.BmsCharts = fixture.AllCharts;

                TestUiDispatcherHost.AwaitTaskOnDispatcher(emptied.Task, "displayed split placement list invalidation");
                AwaitIdleFence();
            }
            finally { vm.MainChartList.PropertyChanged -= rowsChanged; }

            Assert.IsNull(fixture.Library.SplitPlacementGroups);
            Assert.AreEqual(0, vm.MaintenanceTree.SplitPlacementTreeChildren.Count, "ツリーの子は空。");
            Assert.AreEqual(0, vm.MainChartList.Rows.Count, "一覧も空。");
            Assert.AreSame(before.Request, dialogs.ProgressRequest, "自動で探索し直さない。");
            Assert.IsTrue(fixture.Library.OperationAdmission.CanEnter);
        }, dialogs: dialogs);
    }

    /// <summary>失効の通知の配送が、新しい結果の格納より後になる状況を作るための、通知の保留の道具です。</summary>
    private sealed class LateInvalidationGate
    {
        private int armed;
        private readonly ManualResetEventSlim release = new(false);

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>次に届く分裂配置の結果の変更通知を、解放まで保留します。</summary>
        internal void Arm() => Volatile.Write(ref armed, 1);

        internal void Release() => release.Set();

        internal void OnNotification(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName != "SplitPlacementGroups" || Interlocked.Exchange(ref armed, 0) == 0) { return; }
            Entered.TrySetResult();
            release.Wait();
        }
    }

    /// <summary>
    /// 保証：失効の通知が、その後に格納された新しい結果より遅れて届いても、ツリーも一覧も新しい結果を表示したままにする。
    /// 検出する誤り：通知の理由（失効）で子と一覧を空にし、直後に格納された新しい結果を消す。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void SplitPlacement_LateInvalidationNotificationKeepsNewResultInTreeAndList()
    {
        var dialogs = new RecordingDialogService(new EventLog());
        var gate = new LateInvalidationGate();
        RunColdDuplicateFixture(fixture =>
        {
            try
            {
                MainWindowViewModel vm = fixture.ViewModel;
                SetSplitPlacementIndex(fixture);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(vm.SplitPlacementSearch.SelectAsync(null), "split placement first selection");
                Assert.AreEqual(2, vm.MainChartList.Rows.Count);
                var lateDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                vm.MaintenanceTree.SplitPlacementPresentationChanged += (_, _) => lateDelivered.TrySetResult();

                gate.Arm();
                fixture.Library.BmsCharts = fixture.AllCharts;
                TestUiDispatcherHost.AwaitTaskOnDispatcher(gate.Entered.Task, "held invalidation notification");
                Assert.IsNull(fixture.Library.SplitPlacementGroups, "失効した状態で、通知だけが保留されている。");
                SetSplitPlacementIndex(fixture);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(vm.SplitPlacementSearch.SelectAsync(null), "split placement second selection");
                List<DuplicateGroup> latest = fixture.Library.SplitPlacementGroups!;
                Assert.AreEqual(1, latest.Count);

                gate.Release();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(lateDelivered.Task, "late invalidation notification");
                AwaitIdleFence();

                Assert.AreSame(latest[0], vm.MaintenanceTree.SplitPlacementTreeChildren.OfType<DuplicateGroup>().Single(), "遅れて届いた失効の通知で新しい結果を消さない。");
                Assert.AreEqual(2, vm.MainChartList.Rows.Count, "一覧も新しい結果のまま。");
            }
            finally { gate.Release(); }
        }, beforeOwnerNotification: gate.OnNotification, dialogs: dialogs);
    }

    private static void SelectSplitCandidateList(DuplicateNavigationTestFixture fixture, string stage)
    {
        TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.ViewModel.SplitPlacementSearch.SelectAsync(DuplicateViewContext.ForCandidateList()), stage);
        AwaitIdleFence();
    }

    /// <summary>三つのフォルダの音源の関係を設定します。<paramref name="duplicateBToo"/> が真なら、二つ目の重複フォルダも対象フォルダと関係になります。</summary>
    private static void SetSplitPlacementRelations(DuplicateNavigationTestFixture fixture, bool duplicateBToo, bool unrelated = false)
    {
        string a = Path.GetDirectoryName(fixture.Duplicates[0].Path)!, b = Path.GetDirectoryName(fixture.Duplicates[1].Path)!;
        string target = Path.GetDirectoryName(fixture.Target.Path)!;
        if (unrelated) { SetIndex(fixture.Library, new(a, Keys(1, 10)), new(b, Keys(100, 10)), new(target, Keys(200, 10))); }
        else { SetIndex(fixture.Library, new(a, Keys(1, 10)), new(b, duplicateBToo ? Keys(1, 10) : Keys(100, 10)), new(target, Keys(1, 10))); }
    }

    /// <summary>
    /// 保証：分裂配置の統合候補一覧を表示中に結果がなくなると、行0件・件数0・捕捉できる対象0件になり、グループの状態（SELECT・KEEP）・並べ替え・除外語を保ち、構築も探索もしない。
    /// 次の結果の採用では、引き継いだインスタンスは状態を保ち、フォルダの集合が変わった新しいインスタンスは初期値になって、結果にないグループの状態は破棄される。
    /// 0件の結果では行0件でツリーのノードもない。重複の一覧主体と重複の解析は動かない。
    /// 検出する誤り：結果なしでの状態の破棄（再探索の後に状態が戻らない）、行の残存、新しいインスタンスへの状態の引継ぎ、重複側への波及。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void SplitPlacement_CandidateListKeepsStateWithoutResultAndFollowsNextResults()
    {
        var dialogs = new RecordingDialogService(new EventLog());
        RunColdDuplicateFixture(fixture =>
        {
            MainWindowViewModel vm = fixture.ViewModel;
            DuplicateMergeCandidateListOwner list = vm.SplitPlacementMergeCandidateList;
            SetSplitPlacementRelations(fixture, duplicateBToo: false);
            SelectSplitCandidateList(fixture, "split placement candidate list selection");
            CandidateListAwaitSupport.AwaitActive(list, "split placement candidate list adoption");
            Assert.AreSame(list, vm.DisplayedCandidateList, "分裂配置の画面種別では、表示中の一覧主体は分裂配置の一覧");
            Assert.AreEqual(2, list.RowSet.Count, "前提：一つのグループの二つのフォルダ");
            Assert.AreEqual(0, vm.MainChartList.Rows.Count, "譜面一覧は行0件");
            DuplicateGroup first = fixture.Library.SplitPlacementGroups!.Single();
            Assert.IsTrue(list.ToggleSelect(list.RowSet[0]), "前提：SELECTを変える");
            Assert.IsTrue(list.SetKeep(list.RowSet.First(row => !row.IsKeep)), "前提：KEEPを変える");
            list.RequestSort("Path", System.ComponentModel.ListSortDirection.Descending);
            list.ExcludeText = "no-such-folder-text";
            Assert.IsTrue(list.TryGetState(first, out DuplicateMergeCandidateGroupState? firstState));
            bool selected = firstState!.IsSelected;
            string? keep = firstState.KeepFolder;
            Assert.IsFalse(vm.DuplicateMergeCandidateList.IsViewActive, "重複の一覧主体は動かない");

            // 結果がなくなる：行0件、状態は保つ。
            fixture.Library.BmsCharts = [.. fixture.Library.BmsCharts]; // 所持の項目を保ったまま全置換して、結果を失効させる（インスタンスの引継ぎの前提）
            CandidateListAwaitSupport.AwaitRows(list, 0);
            AwaitIdleFence();
            Assert.IsNull(fixture.Library.SplitPlacementGroups);
            Assert.AreEqual(0, list.RowSet.Count);
            Assert.AreEqual(0, list.VisibleGroupCount);
            Assert.AreEqual(0, list.SelectedGroupCount);
            Assert.AreEqual(0, list.CaptureSelectedGroups().Count, "捕捉できる対象も0件");
            Assert.IsFalse(list.IsViewActive);
            Assert.AreEqual("Path", list.SortColumnName);
            Assert.AreEqual("no-such-folder-text", list.ExcludeText);
            Assert.IsTrue(list.TryGetState(first, out DuplicateMergeCandidateGroupState? keptState) && ReferenceEquals(firstState, keptState), "状態を保つ");

            // 次の結果（同じ関係）：引き継いだインスタンスは状態を保つ。
            SetSplitPlacementRelations(fixture, duplicateBToo: false);
            list.ExcludeText = string.Empty;
            SelectSplitCandidateList(fixture, "split placement candidate list reselection");
            CandidateListAwaitSupport.AwaitActive(list, "split placement candidate list readoption");
            Assert.AreSame(first, fixture.Library.SplitPlacementGroups!.Single(), "前提：結果のグループのインスタンスが引き継がれる");
            Assert.AreEqual(2, list.RowSet.Count);
            Assert.IsTrue(list.TryGetState(first, out DuplicateMergeCandidateGroupState? restored));
            Assert.AreSame(firstState, restored, "引き継いだグループは同じ状態");
            Assert.AreEqual((selected, keep), (restored!.IsSelected, restored.KeepFolder), "SELECT・KEEPを保つ");
            Assert.AreEqual("Path", list.SortColumnName, "並べ替えを保つ");

            // 結果がなくなった後、フォルダの集合が変わった新しいグループ：初期値で、古いグループの状態は破棄される。
            fixture.Library.BmsCharts = [.. fixture.Library.BmsCharts]; // 所持の項目を保ったまま全置換して、結果を失効させる（インスタンスの引継ぎの前提）
            CandidateListAwaitSupport.AwaitRows(list, 0);
            AwaitIdleFence();
            SetSplitPlacementRelations(fixture, duplicateBToo: true);
            SelectSplitCandidateList(fixture, "split placement candidate list with a new group");
            CandidateListAwaitSupport.AwaitActive(list, "split placement candidate list adoption of a new group");
            DuplicateGroup second = fixture.Library.SplitPlacementGroups!.Single();
            Assert.AreNotSame(first, second, "前提：フォルダの集合が変わって新しいインスタンス");
            Assert.AreEqual(3, list.RowSet.Count);
            Assert.IsTrue(list.TryGetState(second, out DuplicateMergeCandidateGroupState? fresh));
            Assert.IsFalse(fresh!.IsSelected, "新しいインスタンスは初期値");
            Assert.AreEqual(DuplicateMergeVerificationStage.Unverified, fresh.Verification.Stage);
            Assert.IsFalse(list.TryGetState(first, out _), "結果にないグループの状態は破棄される");

            // 0件の結果：行0件でノードもない。
            fixture.Library.BmsCharts = [.. fixture.Library.BmsCharts]; // 所持の項目を保ったまま全置換して、結果を失効させる（インスタンスの引継ぎの前提）
            CandidateListAwaitSupport.AwaitRows(list, 0);
            AwaitIdleFence();
            SetSplitPlacementRelations(fixture, duplicateBToo: false, unrelated: true);
            SelectSplitCandidateList(fixture, "split placement candidate list with no groups");
            Assert.AreEqual(0, fixture.Library.SplitPlacementGroups!.Count);
            Assert.AreEqual(0, list.RowSet.Count);
            Assert.AreEqual(0, vm.MaintenanceTree.SplitPlacementTreeChildren.Count, "0件ではノードもない");
            Assert.IsNull(fixture.Library.DuplicateChartGroups, "重複の解析を始めない");
            Assert.IsFalse(vm.DuplicateMergeCandidateList.IsViewActive);
            Assert.IsTrue(fixture.Library.OperationAdmission.CanEnter);
        }, dialogs: dialogs);
    }

    /// <summary>
    /// 保証：構築を止めた間に、結果が置き換わる・ライブラリを交換する・後から別の画面（フォルダ）を選ぶのいずれかが起きても、古い構築を採用しない
    /// （表示中にならず、古いグループの状態表も作らない）。
    /// 検出する誤り：置換後の結果への古い構築の採用、交換後の旧ライブラリの構築の採用、後発の画面を古い構築が上書きする誤り。
    /// </summary>
    [TestMethod]
    [DataRow("resultReplaced")]
    [DataRow("libraryReplaced")]
    [DataRow("laterScreenSelected")]
    [DoNotParallelize]
    public void SplitPlacement_CandidateListDoesNotAdoptAStaleBuild(string condition)
    {
        using var gate = new CandidateListBuildGate();
        var dialogs = new RecordingDialogService(new EventLog());
        RunColdDuplicateFixture(fixture =>
        {
            MainWindowViewModel vm = fixture.ViewModel;
            DuplicateMergeCandidateListOwner list = vm.SplitPlacementMergeCandidateList;
            try
            {
                SetSplitPlacementRelations(fixture, duplicateBToo: false);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(vm.SplitPlacementSearch.SelectAsync(null), "split placement root selection");
                AwaitIdleFence();
                DuplicateGroup stale = fixture.Library.SplitPlacementGroups!.Single();
                Assert.IsFalse(list.IsViewActive, "前提：一覧はまだ表示しない");
                gate.Install(fixture.Library);
                Task<bool> node = fixture.NavigateDuplicate(MainViewUpdateMode.SplitPlacementFilterSelected, DuplicateViewContext.ForCandidateList());
                TestUiDispatcherHost.AwaitPresentationOnDispatcher(gate.Reached.Task, "split placement candidate list build reached");
                Task<bool>? later = null;
                switch (condition)
                {
                    case "resultReplaced":
                        // 結果を、関係のない（0件の）別の結果へ置き換える。後発の構築は0件を採用し、止めた構築は結果が置き換わったので採用しない。
                        SetSplitPlacementRelations(fixture, duplicateBToo: false, unrelated: true);
                        fixture.Library.SearchSplitPlacementGroups(null, CancellationToken.None);
                        Assert.AreNotSame(stale, fixture.Library.SplitPlacementGroups!.FirstOrDefault());
                        CandidateListAwaitSupport.AwaitActive(list, "split placement candidate list adopts the replaced result");
                        break;
                    case "libraryReplaced":
                        list.AttachLibrary(new object());
                        break;
                    default:
                        later = fixture.NavigateDuplicate(MainViewUpdateMode.SplitPlacementFilterSelected, DuplicateViewContext.ForFolder(fixture.Duplicates[0].Directory!));
                        fixture.AwaitOwned(later, "later folder request");
                        break;
                }
                gate.Release();
                fixture.AwaitOwned(node, "stale candidate list request");
                fixture.AwaitOwned(vm.RegularChartList.StopAsync(), "collect the stale build");
                AwaitIdleFence();

                Assert.IsFalse(list.TryGetState(stale, out _), "古い構築のグループの状態表を作らない");
                Assert.AreEqual(0, list.RowSet.Count);
                if (condition != "resultReplaced") { Assert.IsFalse(list.IsViewActive, "古い構築を採用しない"); }
                else { Assert.IsTrue(list.IsViewActive, "後発の構築（0件の結果）が採用される"); }
            }
            finally { gate.Release(); }
        }, dialogs: dialogs);
    }

    /// <summary>進捗の要求の参照を、後で同一性を比べるために保持します。</summary>
    private sealed record UiProgressRequestHolder(BeMusicSeeker.Views.Dialogs.UiProgressRequest? Request);
}
