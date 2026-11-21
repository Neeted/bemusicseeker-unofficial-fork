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
            { if (vm.MaintenanceTree.SplitPlacementTreeChildren.Count == 1) { treeGroups.TrySetResult(); } };

            TestUiDispatcherHost.AwaitTaskOnDispatcher(vm.SplitPlacementSearch.SelectAsync(null), "split placement root selection");
            TestUiDispatcherHost.AwaitTaskOnDispatcher(treeGroups.Task, "split placement tree reflection");

            List<DuplicateGroup> result = fixture.Library.SplitPlacementGroups!;
            Assert.AreEqual(1, result.Count);
            Assert.AreSame(result[0], vm.MaintenanceTree.SplitPlacementTreeChildren[0], "ツリーの項目は結果のグループ。");
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
            Assert.AreSame(result[0], vm.MaintenanceTree.SplitPlacementTreeChildren.Cast<object>().Single(), "重複の解析で分裂配置のツリーは変わらない。");
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

                Assert.AreSame(latest[0], vm.MaintenanceTree.SplitPlacementTreeChildren.Cast<object>().Single(), "遅れて届いた失効の通知で新しい結果を消さない。");
                Assert.AreEqual(2, vm.MainChartList.Rows.Count, "一覧も新しい結果のまま。");
            }
            finally { gate.Release(); }
        }, beforeOwnerNotification: gate.OnNotification, dialogs: dialogs);
    }

    /// <summary>進捗の要求の参照を、後で同一性を比べるために保持します。</summary>
    private sealed record UiProgressRequestHolder(BeMusicSeeker.Views.Dialogs.UiProgressRequest? Request);
}
