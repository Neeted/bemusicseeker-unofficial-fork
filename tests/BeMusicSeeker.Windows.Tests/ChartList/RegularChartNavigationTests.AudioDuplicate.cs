using System.Collections.Specialized;
using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.SelectedChartAudioConversionWorkflowOwnerTestSupport;
using static BeMusicSeeker.TestSupport.Wpf.RegularChartNavigationTestFixture;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 重複音源のツリーと一覧の実際の接続（ツリーの選択から探索、ツリーの項目、一覧の表示、失効、結果がない間の状態の保持、重複・分裂配置との独立）を、実ライブラリと標準のVMで確認します。
/// 探索の手順の細部は <c>AudioDuplicateSearchWorkflowOwnerTests</c>、一覧の中身は通常一覧の構築のテストが担当します。
/// </summary>
public sealed partial class RegularChartNavigationTests
{
    /// <summary>二つの重複フォルダのうち一つ目のフォルダに、同名のWAVとOGGを作ります。探索は登録BMSルート（ケースの専用ディレクトリ）の配下を列挙します。</summary>
    private static string CreateAudioPair(DuplicateNavigationTestFixture fixture)
    {
        string folder = Path.GetDirectoryName(fixture.Duplicates[0].Path)!;
        File.WriteAllBytes(Path.Combine(folder, "x.wav"), []);
        File.WriteAllBytes(Path.Combine(folder, "x.ogg"), []);
        // 登録BMSルートを、フォルダの置き場（ケース専用のディレクトリ）にする。
        Settings settings = fixture.Settings;
        string root = Path.GetDirectoryName(folder)!;
        settings.StandaloneBmsRootPaths = StandaloneBmsRootPathSettings.Serialize([root]);
        settings.BMSRootPath = root;
        return folder;
    }

    private static void SelectAudioDuplicateCandidateList(DuplicateNavigationTestFixture fixture, string stage)
    {
        TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.ViewModel.AudioDuplicateSearch.SelectAsync(DuplicateViewContext.ForCandidateList()), stage);
        AwaitIdleFence();
    }

    /// <summary>
    /// 保証：重複音源のルートの選択で、探索の結果がツリーの項目（結果と同じインスタンス）へ反映され、選択時の文脈で一覧が表示され（そのフォルダの譜面だけ）、
    /// 重複・分裂配置の解析も結果も作らず、受付は解放される。
    /// 検出する誤り：重複音源の選択による重複・分裂配置の解析の開始、ツリーへの未反映、選択の文脈の取り違え、受付の解放漏れ。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void AudioDuplicate_SelectionSearchesShowsTreeAndListWithoutOtherAnalyses()
    {
        var dialogs = new RecordingDialogService(new EventLog());
        RunColdDuplicateFixture(fixture =>
        {
            MainWindowViewModel vm = fixture.ViewModel;
            string folder = CreateAudioPair(fixture);
            var treeGroups = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ((INotifyCollectionChanged)vm.MaintenanceTree.AudioDuplicateTreeChildren).CollectionChanged += (_, _) =>
            { if (vm.MaintenanceTree.AudioDuplicateTreeChildren.Count == 2) { treeGroups.TrySetResult(); } };

            TestUiDispatcherHost.AwaitTaskOnDispatcher(vm.AudioDuplicateSearch.SelectAsync(null), "audio duplicate root selection");
            TestUiDispatcherHost.AwaitTaskOnDispatcher(treeGroups.Task, "audio duplicate tree reflection");

            List<AudioDuplicateFolder> result = fixture.Library.AudioDuplicateFolders!;
            Assert.AreEqual(1, result.Count);
            Assert.IsTrue(string.Equals(folder, result[0].Folder, StringComparison.OrdinalIgnoreCase));
            Assert.AreSame(vm.MaintenanceTree.AudioDuplicateCandidateListNodeItem, vm.MaintenanceTree.AudioDuplicateTreeChildren[0], "先頭は整理候補一覧のノード。");
            Assert.AreSame(result[0].Group, vm.MaintenanceTree.AudioDuplicateTreeChildren[1], "ツリーの項目は結果のグループ。");
            Assert.AreEqual(MainViewUpdateMode.AudioDuplicateFilterSelected, vm.MainChartList.LastAppliedColumnMode);
            CollectionAssert.AreEqual(new[] { fixture.Duplicates[0].Path }, RowPaths(vm), "フォルダの譜面だけを表示する。");
            Assert.IsNull(fixture.Library.DuplicateChartGroups, "重複の解析を始めない。");
            Assert.IsNull(fixture.Library.SplitPlacementGroups, "分裂配置の結果を作らない。");
            Assert.AreEqual(0, vm.MaintenanceTree.DuplicateTreeItems.Count);
            Assert.AreEqual(0, vm.MaintenanceTree.SplitPlacementTreeChildren.Count);
            Assert.IsTrue(fixture.Library.OperationAdmission.CanEnter, "受付を解放している。");
            Assert.AreEqual(0, dialogs.Messages.Count);
        }, dialogs: dialogs, rootFileEnumerator: new BeMusicSeeker.Models.Utils.FastRootFileEnumerator());
    }

    /// <summary>
    /// 保証：重複音源の一覧を表示中に結果が失効すると、ツリーの子と一覧が空になり、自動では探索し直さない（進捗も受付も新たに使わない）。
    /// 検出する誤り：失効後の古い結果の表示の残存、失効による自動の再探索。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void AudioDuplicate_InvalidationEmptiesTreeAndDisplayedListWithoutResearching()
    {
        var dialogs = new RecordingDialogService(new EventLog());
        RunColdDuplicateFixture(fixture =>
        {
            MainWindowViewModel vm = fixture.ViewModel;
            CreateAudioPair(fixture);
            TestUiDispatcherHost.AwaitTaskOnDispatcher(vm.AudioDuplicateSearch.SelectAsync(null), "audio duplicate root selection");
            Assert.AreEqual(1, vm.MainChartList.Rows.Count);
            UiProgressRequestHolder before = new(dialogs.ProgressRequest);
            var emptied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            System.ComponentModel.PropertyChangedEventHandler rowsChanged = (_, args) =>
            { if (args.PropertyName == nameof(MainChartListViewModel.Rows) && vm.MainChartList.Rows.Count == 0) { emptied.TrySetResult(); } };
            vm.MainChartList.PropertyChanged += rowsChanged;
            try
            {
                fixture.Library.BmsCharts = fixture.AllCharts;

                TestUiDispatcherHost.AwaitTaskOnDispatcher(emptied.Task, "displayed audio duplicate list invalidation");
                AwaitIdleFence();
            }
            finally { vm.MainChartList.PropertyChanged -= rowsChanged; }

            Assert.IsNull(fixture.Library.AudioDuplicateFolders);
            Assert.AreEqual(0, vm.MaintenanceTree.AudioDuplicateTreeChildren.Count, "ツリーの子は空。");
            Assert.AreEqual(0, vm.MainChartList.Rows.Count, "一覧も空。");
            Assert.AreSame(before.Request, dialogs.ProgressRequest, "自動で探索し直さない。");
            Assert.IsTrue(fixture.Library.OperationAdmission.CanEnter);
        }, dialogs: dialogs, rootFileEnumerator: new BeMusicSeeker.Models.Utils.FastRootFileEnumerator());
    }

    /// <summary>
    /// 保証：整理候補一覧を表示中に結果がなくなると、行0件・件数0・捕捉できる対象0件になり、フォルダの状態（SELECT）・並べ替え・衝突整理を保ち、構築も探索もしない。
    /// 次の結果の採用では、引き継いだインスタンスは状態を保つ。重複・分裂配置の一覧主体は動かない。
    /// 検出する誤り：結果なしでの状態の破棄、行の残存、重複側への波及。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void AudioDuplicate_CandidateListKeepsStateWithoutResultAndFollowsNextResult()
    {
        var dialogs = new RecordingDialogService(new EventLog());
        RunColdDuplicateFixture(fixture =>
        {
            MainWindowViewModel vm = fixture.ViewModel;
            AudioDuplicateCandidateListOwner list = vm.AudioDuplicateCandidateList;
            CreateAudioPair(fixture);
            SelectAudioDuplicateCandidateList(fixture, "audio duplicate candidate list selection");
            CandidateListAwaitSupport.AwaitActive(list, "audio duplicate candidate list adoption");
            Assert.IsTrue(list.IsViewActive);
            Assert.AreSame(vm.AudioDuplicateCandidateList, vm.DisplayedCandidatePresentation, "整理候補一覧の画面種別では、共用の欄は整理候補一覧へ接続する");
            Assert.AreEqual(1, list.RowSet.Count);
            Assert.AreEqual(0, vm.MainChartList.Rows.Count, "譜面一覧は行0件");
            DuplicateGroup first = fixture.Library.AudioDuplicateFolders!.Single().Group;
            Assert.IsTrue(list.ToggleSelect(list.RowSet[0]), "前提：SELECTを変える");
            list.RequestSort("Path", System.ComponentModel.ListSortDirection.Descending);
            list.CollisionCleanupMode = AudioDuplicateCollisionCleanupMode.KeepWav;
            Assert.IsFalse(vm.DuplicateMergeCandidateList.IsViewActive || vm.SplitPlacementMergeCandidateList.IsViewActive, "重複・分裂配置の一覧主体は動かない");

            // 結果がなくなる：行0件、状態は保つ。
            fixture.Library.BmsCharts = [.. fixture.Library.BmsCharts];
            CandidateListAwaitSupport.AwaitRows(list, 0);
            AwaitIdleFence();
            Assert.IsNull(fixture.Library.AudioDuplicateFolders);
            Assert.AreEqual(0, list.RowSet.Count);
            Assert.AreEqual(0, list.CaptureSelectedFolders().Count, "捕捉できる対象も0件");
            Assert.IsFalse(list.IsViewActive);
            Assert.AreEqual("Path", list.SortColumnName);
            Assert.AreEqual(AudioDuplicateCollisionCleanupMode.KeepWav, list.CollisionCleanupMode);
            Assert.IsTrue(list.TryGetState(first, out AudioDuplicateCandidateState? kept) && kept!.IsSelected, "状態を保つ");

            // 次の結果（同じ配置）：引き継いだインスタンスは状態を保つ。
            SelectAudioDuplicateCandidateList(fixture, "audio duplicate candidate list reselection");
            CandidateListAwaitSupport.AwaitActive(list, "audio duplicate candidate list readoption");
            Assert.AreSame(first, fixture.Library.AudioDuplicateFolders!.Single().Group, "前提：結果のグループのインスタンスが引き継がれる");
            Assert.AreEqual(1, list.RowSet.Count);
            Assert.IsTrue(list.TryGetState(first, out AudioDuplicateCandidateState? restored));
            Assert.AreSame(kept, restored, "引き継いだフォルダは同じ状態");
            Assert.IsTrue(restored!.IsSelected, "SELECTを保つ");
            Assert.AreEqual("Path", list.SortColumnName, "並べ替えを保つ");
        }, dialogs: dialogs, rootFileEnumerator: new BeMusicSeeker.Models.Utils.FastRootFileEnumerator());
    }
}
