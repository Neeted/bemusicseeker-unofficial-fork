using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using static BeMusicSeeker.TestSupport.Components.RegularChartListOwnerTestSupport;
using static BeMusicSeeker.TestSupport.Wpf.RegularChartNavigationTestFixture;

namespace BeMusicSeeker.Tests;


/// <summary>通常一覧ownerの画面遷移と編集要求の配送をWindowなしで確認します。</summary>
/// <remarks>実libraryを使う重複caseはgroup構築から通知までを担当し、compiledセル・treeの接続はMainWindowの実画面caseが分担します。</remarks>
[TestClass]
public sealed class RegularChartNavigationTests
{
    /// <summary>後発選択と表示失敗の診断を、MSTestが指定する結果ディレクトリへ保存します。</summary>
    public TestContext? TestContext { get; set; }
    /// <summary>新規解析がBusyなら有効なサマリーと検索条件を保ち、解放後も予約を実行しません。準備済み結果の表示は、別のL操作中でも解析を追加せず進みます。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [DoNotParallelize]
    public void DuplicateAdmission_BusyPreservesViewAndCachedDisplayDoesNotReenter(bool cached)
    {
        var dialogs = new FileDbReportRecordingDialogs();
        RunColdDuplicateFixture(fixture =>
        {
            MainWindowViewModel vm = fixture.ViewModel;
            if (cached)
            {
                fixture.AwaitOwned(fixture.NavigateDuplicate(MainViewUpdateMode.DuplicateFilterSelected, null), "initial cached duplicate navigation");
                fixture.CompleteDuplicate();
            }
            vm.ChartFilters.KeywordFilter = "target";
            vm.PlaylistWorkspace.RequestSummarySelection();
            fixture.AwaitSummary();
            RegularChartNavigationRequest selection = vm.RegularChartList.CaptureNavigation();
            IList rows = vm.MainChartList.Rows;
            Assert.IsTrue(fixture.Library.OperationAdmission.TryEnter(out IDisposable other));
            try
            {
                Task<bool> request = fixture.NavigateDuplicate(MainViewUpdateMode.DuplicateFilterSelected, null);
                fixture.AwaitOwned(request, "duplicate request under another operation");
                Assert.AreEqual(cached, request.Result);
                if (cached)
                {
                    fixture.AwaitOwned(fixture.AwaitReadyAsync(), "cached duplicate actual rows");
                    Assert.AreEqual(0, vm.MainChartList.Rows.Count);
                    Assert.AreEqual(0, dialogs.Messages.Count);
                    Assert.IsTrue(fixture.Library.OperationAdmission.IsActive);
                }
                else
                {
                    Assert.AreSame(selection, vm.RegularChartList.CaptureNavigation());
                    Assert.AreSame(rows, vm.MainChartList.Rows);
                    Assert.IsTrue(vm.PlaylistWorkspace.IsPlaylistSummaryModeRequested);
                    Assert.AreEqual("target", vm.ChartFilters.KeywordFilter);
                    Assert.IsNull(fixture.Library.DuplicateChartGroups);
                    Assert.AreEqual(1, dialogs.Messages.Count);
                    Assert.AreEqual(Resources.Warn_LibraryOperationBusy, dialogs.Messages.Single().MessageBoxText);
                }
            }
            finally { other.Dispose(); }
            if (!cached)
            {
                fixture.AwaitOwned(fixture.AwaitReadyAsync(awaitRows: false), "rejected request cleanup");
                Assert.IsNull(fixture.Library.DuplicateChartGroups, "解放後の予約解析を行いません。");
                vm.ChartFilters.KeywordFilter = string.Empty;
                fixture.AwaitOwned(fixture.NavigateDuplicate(MainViewUpdateMode.DuplicateFilterSelected, null), "fresh explicit duplicate request");
                fixture.CompleteDuplicate();
                CollectionAssert.AreEquivalent(fixture.Duplicates.Select(chart => chart.Path).ToArray(), vm.MainChartList.Rows.Cast<LibraryChartRow>().Select(row => row.path).ToArray());
            }
        }, dialogs: dialogs);
    }

    /// <summary>一覧の構築だけを保留し、解析と必要通知を完了した時点で共通Lを解放することを確認します。描画のために次の変更受付を待たせません。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void DuplicateAdmission_RowsBuildDoesNotExtendDataAdmission()
    {
        using var release = new ManualResetEventSlim(false);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RunColdDuplicateFixture(fixture =>
        {
            MainWindowViewModel vm = fixture.ViewModel;
            fixture.AwaitOwned(vm.RegularChartList.NavigateMaintenanceAsync(MainViewUpdateMode.FullScanAllChartsFilterSelected), "initial normal presentation");
            int claimed = 0;
            vm.MainChartList.RowProjection.NormalInputWorkObserver = (kind, _) =>
            {
                if (kind == "order_buffer" && Interlocked.Exchange(ref claimed, 1) == 0)
                { reached.TrySetResult(); release.Wait(); }
            };
            Task? rows = null;
            try
            {
                fixture.StartDuplicate(); fixture.AwaitGate();
                fixture.ReleaseProducer();
                rows = fixture.AwaitReadyAsync();
                fixture.AwaitOwned(TestUiDispatcherHost.AwaitNotificationAsync(reached.Task, rows, "duplicate row build gate"), "actual duplicate row build");
                fixture.AwaitNavigation();
                Assert.IsFalse(rows.IsCompleted);
                Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
                Assert.IsTrue(fixture.Library.OperationAdmission.TryEnter(out IDisposable next));
                next.Dispose();
                release.Set();
                fixture.AwaitOwned(rows, "actual duplicate rows terminal");
                CollectionAssert.AreEquivalent(fixture.Duplicates.Select(chart => chart.Path).ToArray(), vm.MainChartList.Rows.Cast<LibraryChartRow>().Select(row => row.path).ToArray());
            }
            finally
            {
                release.Set(); fixture.ReleaseProducer();
                if (rows != null) { fixture.AwaitOwned(rows, "duplicate rows gate cleanup"); }
                vm.MainChartList.RowProjection.NormalInputWorkObserver = null;
            }
        });
    }

    /// <summary>履歴keyword callbackの遅着を新しい通常ツリー／完成済み重複一覧の構築中に配送し、受付の有効性だけでなく実表示Taskの終端と非空の対象行を維持します。要求置換・古い結果の拒否自体は履歴owner系列へ分担します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [DoNotParallelize]
    public void PlayHistoryRefresh_StaleKeywordRequestKeepsLaterRowsBuild(bool duplicate)
    {
        using var release = new ManualResetEventSlim(false);
        var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduler = new ProjectionTaskCollector(new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher));
        RunColdDuplicateFixture(fixture =>
        {
            MainWindowViewModel vm = fixture.ViewModel;
            if (duplicate)
            {
                fixture.AwaitOwned(fixture.NavigateDuplicate(MainViewUpdateMode.DuplicateFilterSelected, null), "cache duplicate navigation");
                fixture.CompleteDuplicate();
            }
            AwaitHistoryRows(vm, () => vm.PlayHistory.ActivatePeriod(PlayHistoryPeriodRequest.All(), string.Empty));
            Assert.IsTrue(vm.PlayHistory.TryBeginKeywordRefresh(string.Empty, true, out PlayHistoryViewRequest stale));
            int claimed = 0;
            vm.MainChartList.RowProjection.NormalInputWorkObserver = (kind, _) =>
            {
                if (kind == "order_buffer" && Interlocked.Exchange(ref claimed, 1) == 0)
                {
                    bool onUi = TestUiDispatcherHost.Dispatcher.CheckAccess();
                    reached.TrySetResult(onUi);
                    if (onUi) { release.Set(); } else { release.Wait(); }
                }
            };
            Task? rows = null;
            Task? navigation = null;
            var treePresentation = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<RegularChartTreeNavigationPresentationRequestedEventArgs> tree = (_, request) => treePresentation.TrySetResult(request.Presentation);
            vm.RegularChartList.TreeNavigationPresentationRequested += tree;
            try
            {
                Task<Task> presentation = duplicate ? scheduler.ObserveNext() : treePresentation.Task;
                if (duplicate) { navigation = fixture.NavigateDuplicate(MainViewUpdateMode.DuplicateFilterSelected, null); }
                else { Assert.IsTrue(vm.RegularChartList.NavigateTree(RegularChartFolderFilterKind.Directory, fixture.Target.Directory)); }
                fixture.AwaitOwned(presentation, "later actual presentation started");
                rows = presentation.GetAwaiter().GetResult();
                fixture.AwaitOwned(TestUiDispatcherHost.AwaitNotificationAsync(reached.Task, rows, "later rows build gate"), "later rows worker reached");
                Assert.IsFalse(reached.Task.GetAwaiter().GetResult(), "構築の保留はUI外で行います。");
                RegularChartNavigationRequest current = vm.RegularChartList.CaptureNavigation();
                Assert.IsTrue(current.IsCurrent);
                Assert.IsFalse(vm.PlayHistory.IsCurrentRequest(stale.RequestId));
                Assert.IsFalse(rows.IsCompleted);
                fixture.AwaitOwned(RefreshHistoryKeyword(vm, stale), "stale history callback terminal");
                Assert.AreSame(current, vm.RegularChartList.CaptureNavigation());
                Assert.IsTrue(current.IsCurrent);
                release.Set();
                fixture.AwaitOwned(rows, "later actual rows terminal");
                CollectionAssert.AreEquivalent((duplicate ? fixture.Duplicates : [fixture.Target]).Select(chart => chart.Path).ToArray(),
                    vm.MainChartList.Rows.Cast<LibraryChartRow>().Select(row => row.path).ToArray());
                Assert.AreEqual(duplicate ? MainViewUpdateMode.DuplicateFilterSelected : MainViewUpdateMode.FolderFilterSelected,
                    vm.MainChartList.LastAppliedColumnMode);
            }
            finally
            {
                release.Set();
                vm.PlayHistory.CompleteKeywordRefresh(stale.KeywordFilterRevision);
                try { fixture.AwaitOwned(Task.WhenAll(rows ?? Task.CompletedTask, navigation ?? Task.CompletedTask), "later rows build cleanup"); }
                finally
                {
                    vm.RegularChartList.TreeNavigationPresentationRequested -= tree;
                    vm.MainChartList.RowProjection.NormalInputWorkObserver = null;
                }
            }
        }, scheduler: scheduler);
    }

    /// <summary>現行keyword要求は同じcompiled refresh入口で実履歴ownerへ届き、所有rawの二行から検索に合う一行を採用します。全履歴refreshを早期拒否する誤りを区別します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void PlayHistoryRefresh_CurrentKeywordRequestAppliesMatchingHistoryRows()
    {
        RunColdDuplicateFixture(fixture =>
        {
            MainWindowViewModel vm = fixture.ViewModel;
            Lr2PlayHistoryRecord target = new() { history_id = 1, hash = fixture.Target.Md5, played_at = 1000, finalized = 1 };
            Lr2PlayHistoryRecord other = new() { history_id = 2, hash = fixture.Duplicates[0].Md5, played_at = 1001, finalized = 1 };
            var raw = new Lr2PlayHistoryReadResult(PlayHistorySourceProfile.Lr2("owned-score.db"), [target, other], [], Lr2PlayHistorySchemaStatus.Installed);
            var snapshot = new BMSLibrary.ScoreSnapshot
            { PlayHistorySnapshot = new ScorePlayHistorySnapshot(ActiveScoreSource.Lr2, "owned-score.db", raw), SourceGeneration = 1 };
            vm.PlayHistory.ConfigureViewExecution(new PlayHistoryViewExecutionDependencies(() => fixture.Library, () => fixture.Playlist,
                () => PlayHistoryReadSourceContext.FromSnapshot(snapshot), action => { TestUiDispatcherHost.Dispatcher.Invoke(action); return true; },
                (_, _) => { }, () => MainViewUpdateMode.PlayHistorySelected, vm.MainChartList, vm.PlaylistWorkspace));
            AwaitHistoryRows(vm, () => vm.PlayHistory.ActivatePeriod(PlayHistoryPeriodRequest.All(), string.Empty));
            CollectionAssert.AreEquivalent(new long[] { 1, 2 }, vm.MainChartList.Rows.Cast<PlayHistoryRow>().Select(row => row.HistoryId).ToArray());
            const string keyword = "title:target";
            var callback = new TaskCompletionSource<PlayHistoryViewRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.PlayHistory.ConfigureViewRefreshScheduler(() => false, request => callback.TrySetResult(request));
            vm.ChartFilters.KeywordFilter = keyword;
            fixture.AwaitOwned(callback.Task, "current keyword callback captured");
            fixture.AwaitOwned(vm.PlayHistory.WaitForRefreshQueuesIdleAsync(), "current keyword queue terminal");
            PlayHistoryViewRequest current = callback.Task.GetAwaiter().GetResult();
            fixture.AwaitOwned(RefreshHistoryKeyword(vm, current), "current history callback terminal");
            Assert.IsTrue(vm.PlayHistory.IsCurrentRequest(current.RequestId));
            Assert.AreEqual(MainViewUpdateMode.PlayHistorySelected, vm.MainChartList.LastAppliedColumnMode);
            PlayHistoryRow row = vm.MainChartList.Rows.Cast<PlayHistoryRow>().Single();
            Assert.AreSame(target, row.Raw);
            Assert.IsNotNull(row.ResolvedChart);
            Assert.AreEqual(fixture.Target.Path, row.ResolvedChart.Path);
        });
    }

    /// <summary>期間選択で実履歴行が公開されるまで待ち、通知購読を回収します。</summary>
    private static void AwaitHistoryRows(MainWindowViewModel vm, Action activate)
    {
        var rows = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler changed = (_, args) => { if (args.PropertyName == nameof(MainChartListViewModel.Rows)) { rows.TrySetResult(); } };
        vm.MainChartList.PropertyChanged += changed;
        try { activate(); TestUiDispatcherHost.AwaitTaskOnDispatcher(rows.Task, "history rows published"); }
        finally { vm.MainChartList.PropertyChanged -= changed; }
    }

    /// <summary>本番keyword callbackのcompiled入口一箇所に限り、要求の遅延配送順を決定的に制御します。</summary>
    private static Task RefreshHistoryKeyword(MainWindowViewModel vm, PlayHistoryViewRequest request)
    {
        // この機能テストが管理する反射例外。責務上の安定入口へ統合できた時点で退役し、他のprivate操作へ拡張しない。
        MethodInfo method = typeof(MainWindowViewModel).GetMethod("RefreshChartRowsView", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new AssertFailedException("履歴refresh入口がありません。");
        return (Task)(method.Invoke(vm, [MainViewUpdateMode.KeywordFilterUpdated, request, null, null, null])
            ?? throw new AssertFailedException("履歴refreshのTaskがありません。"));
    }

    /// <summary>初回解析のロック解放後の通知を保留し、通常、保守、導入、履歴、プレイリストへの後発移動を行います。解析完了後も後発画面の対象行、表示モード、サマリーを保ち、明示した重複画面への再訪では最新結果を表示することを確認します。</summary>
    /// <remarks>履歴は確定batchで要求が置き換わるため、要求参照ではなく、非既定の期間境界・検索・表示対象と現在要求の有効性を確認します。このconstructor-only fixtureは成績取得組を公開しないため、履歴は規定の取得失敗表示を維持します。正常0件の期間集計を作らず、0件や最終モードだけでも意味上の選択維持を判定しません。</remarks>
    [TestMethod]
    [DataRow("directory")]
    [DataRow("artist")]
    [DataRow("maintenance")]
    [DataRow("install")]
    [DataRow("history")]
    [DataRow("summary")]
    [DataRow("detail")]
    [DoNotParallelize]
    public void ColdDuplicate_LaterNavigationKeepsSemanticTargetThroughCompletion(string destination)
    {
        RunColdDuplicateFixture(fixture =>
        {
            fixture.StartDuplicate();
            fixture.AwaitGate();
            MainWindowViewModel vm = fixture.ViewModel;
            Task? later = null;
            var historyPeriod = PlayHistoryPeriodRequest.CreateDay(2026, 1, 2, TimeZoneInfo.Utc);
            const string historyKeyword = "title:target";
            const string historyTargetIdentity = "playlist:701";
            switch (destination)
            {
                case "directory":
                    AwaitRows(() => Assert.IsTrue(vm.RegularChartList.NavigateTree(RegularChartFolderFilterKind.Directory,
                        fixture.Target.Directory)));
                    break;
                case "artist":
                    AwaitRows(() => Assert.IsTrue(vm.RegularChartList.NavigateTree(RegularChartFolderFilterKind.Artist, "target artist")));
                    break;
                case "maintenance":
                    later = vm.RegularChartList.NavigateMaintenanceAsync(MainViewUpdateMode.FullScanAllChartsFilterSelected);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(later, "later maintenance rows");
                    break;
                case "install":
                    later = vm.RegularChartList.NavigateInstallAsync(MainViewUpdateMode.NewlyInstalledFolderSelected, fixture.Package);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(later, "later package rows");
                    break;
                case "history":
                    vm.ChartFilters.KeywordFilter = historyKeyword;
                    vm.PlayHistory.RefreshDisplayTargetCatalog(queueRefreshWhenSelectionChanges: false);
                    vm.PlayHistory.SelectedDisplayTargetIdentity = historyTargetIdentity;
                    AwaitRows(() => vm.PlayHistory.ActivatePeriod(historyPeriod, historyKeyword));
                    break;
                case "summary":
                    vm.PlaylistWorkspace.RequestSummarySelection();
                    fixture.AwaitSummary();
                    break;
                case "detail":
                    vm.PlaylistWorkspace.RequestDetailSelection(fixture.Table);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(vm.PlaylistWorkspace.WaitForDetailBuildIdleAsync(), "later playlist rows");
                    break;
            }
            AssertDestination();
            var violations = new List<string>();
            bool observe = true;
            PropertyChangedEventHandler rowsChanged = (_, args) =>
            {
                if (args.PropertyName == nameof(MainChartListViewModel.Rows)) { ObserveDestination(); }
            };
            PropertyChangedEventHandler summaryChanged = (_, args) =>
            {
                if (args.PropertyName is nameof(PlaylistWorkspaceViewModel.IsPlaylistSummaryMode)
                    or nameof(PlaylistWorkspaceViewModel.IsPlaylistSummaryModeRequested)) { ObserveDestination(); }
            };
            vm.MainChartList.PropertyChanged += rowsChanged;
            vm.PlaylistWorkspace.PropertyChanged += summaryChanged;
            try
            {
                fixture.CompleteDuplicate();
                AssertDestination();
                Assert.AreEqual(0, violations.Count, string.Join(Environment.NewLine, violations));
                // 同じ対象を使う再ソートでも対象が変わらず、最終モードだけの一致で合格にしません。
                if (destination is "directory" or "artist" or "maintenance" or "install")
                {
                    AwaitRows(() => vm.MainChartList.RequestSort(nameof(LibraryChartRow.Title), ListSortDirection.Descending));
                    AssertDestination();
                }
                else if (destination == "detail")
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(vm.PlaylistWorkspace.RequestDetailRefreshTask(
                        MainViewUpdateMode.TreeViewFilterNotChanged, MainViewUpdateMode.TreeViewFilterNotChanged,
                        MainViewUpdateMode.PlaylistFilterSelected, false, default), "later detail refilter");
                    AssertDestination();
                }
                Assert.IsNotNull(fixture.Library.DuplicateChartGroups);
                Assert.AreEqual(1, fixture.Library.DuplicateChartGroups.Count);
                Assert.IsTrue(fixture.Library.GetNormalLibraryRefreshNotificationsAfter(fixture.InitialNotificationVersion)
                    .CatalogCommits.Any(commit => commit.Changes.Count > 0), "stale表示でも重複警告の確定通知を生産します。");
                // 重複ルートを明示的に選び直すと、完成済みキャッシュから全重複対象を表示する。
                observe = false;
                // 履歴の意味対象を確認し終えた後の別操作として、全重複集合の再訪前に検索を解除する。
                if (destination == "history") { vm.ChartFilters.KeywordFilter = string.Empty; }
                int revision = fixture.Library.CatalogRevision;
                TestUiDispatcherHost.AwaitTaskOnDispatcher(vm.RegularChartList.NavigateMaintenanceAsync(
                    MainViewUpdateMode.DuplicateFilterSelected), "explicit duplicate after later navigation");
                fixture.AwaitOwned(fixture.AwaitReadyAsync(), "explicit duplicate actual Rows terminal");
                CollectionAssert.AreEquivalent(fixture.Duplicates.Select(chart => chart.Path).ToArray(),
                    vm.MainChartList.Rows.Cast<LibraryChartRow>().Select(row => row.Chart.Path).ToArray());
                Assert.AreEqual(revision, fixture.Library.CatalogRevision);
            }
            finally
            {
                vm.MainChartList.PropertyChanged -= rowsChanged;
                vm.PlaylistWorkspace.PropertyChanged -= summaryChanged;
                if (later != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(later, "later navigation cleanup"); }
            }

            void ObserveDestination()
            {
                if (!observe) { return; }
                try { AssertDestination(); }
                catch (AssertFailedException exception) { violations.Add(exception.Message); }
            }

            void AssertDestination()
            {
                if (destination == "summary")
                {
                    Assert.IsTrue(vm.PlaylistWorkspace.IsPlaylistSummaryModeRequested);
                    Assert.IsTrue(vm.PlaylistWorkspace.IsPlaylistSummaryMode);
                    CollectionAssert.AreEqual(new[] { "target playlist" }, vm.PlaylistWorkspace.PlaylistSummaryView.Select(row => row.Name).ToArray());
                    return;
                }
                Assert.IsFalse(vm.PlaylistWorkspace.IsPlaylistSummaryModeRequested);
                if (destination == "history")
                {
                    Assert.AreEqual(MainViewUpdateMode.PlayHistorySelected, vm.MainChartList.LastAppliedColumnMode);
                    PlayHistoryViewRequest current = vm.PlayHistory.ActiveRequest;
                    Assert.IsNotNull(current);
                    Assert.IsTrue(vm.PlayHistory.IsViewActive);
                    Assert.IsTrue(vm.PlayHistory.IsCurrentRequest(current.RequestId));
                    Assert.AreEqual(PlayHistoryPeriodKind.Day, current.PeriodRequest.Kind);
                    Assert.AreEqual(historyPeriod.PlayedAtFromInclusive, current.PeriodRequest.PlayedAtFromInclusive);
                    Assert.AreEqual(historyPeriod.PlayedAtToExclusive, current.PeriodRequest.PlayedAtToExclusive);
                    Assert.AreEqual(historyPeriod.FinalizationFilter, current.PeriodRequest.FinalizationFilter);
                    Assert.AreEqual(historyKeyword, vm.ChartFilters.KeywordFilter);
                    Assert.AreEqual(historyKeyword, vm.PlayHistory.CurrentKeywordIdentity);
                    Assert.AreEqual(vm.PlayHistory.KeywordRevision, current.KeywordFilterRevision);
                    Assert.AreEqual(historyTargetIdentity, vm.PlayHistory.SelectedDisplayTargetIdentity);
                    Assert.AreEqual(historyTargetIdentity, vm.PlayHistory.CurrentDisplayTargetIdentity);
                    Assert.AreEqual(vm.PlayHistory.DisplayTargetRevision, current.DisplayTargetRevision);
                    Assert.AreEqual(Resources.Play_history_read_failed, vm.MainChartList.SummaryText);
                    Assert.AreEqual(Resources.Play_history_read_failed, vm.PlayHistory.SummaryDiagnosticText);
                    Assert.AreEqual(0, vm.PlayHistory.SummaryCards.Count);
                    Assert.AreEqual(0, vm.MainChartList.Rows.Count);
                }
                else if (destination == "detail")
                {
                    Assert.AreEqual(MainViewUpdateMode.PlaylistFilterSelected, vm.MainChartList.LastAppliedColumnMode);
                    CollectionAssert.AreEqual(new[] { fixture.Entry }, vm.MainChartList.Rows.Cast<PlaylistDetailRow>().Select(row => row.Entry).ToArray());
                }
                else
                {
                    MainViewUpdateMode expected = destination == "maintenance" ? MainViewUpdateMode.FullScanAllChartsFilterSelected
                        : destination == "install" ? MainViewUpdateMode.NewlyInstalledFolderSelected : MainViewUpdateMode.FolderFilterSelected;
                    Assert.AreEqual(expected, vm.MainChartList.LastAppliedColumnMode);
                    string[] paths = destination == "maintenance" ? fixture.AllCharts.Select(chart => chart.Path).ToArray() : [fixture.Target.Path];
                    CollectionAssert.AreEquivalent(paths, vm.MainChartList.Rows.Cast<LibraryChartRow>().Select(row => row.Chart.Path).ToArray());
                    if (destination == "install") { Assert.AreSame(fixture.Package.ChartEntries[0], vm.MainChartList.Rows.Cast<LibraryChartRow>().Single().PackageEntry); }
                }
            }

            void AwaitRows(Action action)
            {
                var rows = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                PropertyChangedEventHandler changed = (_, args) => { if (args.PropertyName == nameof(MainChartListViewModel.Rows)) { rows.TrySetResult(); } };
                vm.MainChartList.PropertyChanged += changed;
                try { action(); TestUiDispatcherHost.AwaitTaskOnDispatcher(rows.Task, "later target rows"); }
                finally { vm.MainChartList.PropertyChanged -= changed; }
            }
        });
    }

    /// <summary>解析完了後のUI配送を保留してプレイリストのサマリーを選び、遅れて届く重複表示要求がサマリーを解除しないことを確認します。</summary>
    [TestMethod]
    public void DuplicateNavigation_QueuedPresentationCannotClearLaterSummary()
    {
        var scheduler = new PendingInstallTargetScheduler(new ProjectionTaskCollector(new WpfUiScheduler(() => TestUiDispatcherHost.Dispatcher)));
        RunColdDuplicateFixture(fixture =>
        {
            fixture.StartDuplicate(); fixture.AwaitGate(); fixture.AwaitGroupsWhileGated();
            Task data = fixture.CaptureData();
            scheduler.ArmReady(); fixture.ReleaseProducer();
            bool failed = false;
            PropertyChangedEventHandler? changed = null;
            try
            {
                TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(scheduler.Held.Task, data,
                    "ready UI Func before receiver"), "ready receiver queued");
                Assert.IsNotNull(fixture.Library.DuplicateChartGroups);
                Assert.AreEqual(1, fixture.Library.DuplicateChartGroups.Count);
                fixture.ViewModel.PlaylistWorkspace.RequestSummarySelection(); fixture.AwaitSummary();
                var summaryChanges = new List<bool>();
                changed = (_, args) =>
                { if (args.PropertyName == nameof(PlaylistWorkspaceViewModel.IsPlaylistSummaryMode)) { summaryChanges.Add(fixture.ViewModel.PlaylistWorkspace.IsPlaylistSummaryModeRequested); } };
                fixture.ViewModel.PlaylistWorkspace.PropertyChanged += changed;
                scheduler.Release(); fixture.CompleteDuplicate();
                Assert.IsTrue(fixture.ViewModel.PlaylistWorkspace.IsPlaylistSummaryModeRequested);
                Assert.IsFalse(summaryChanges.Contains(false), "途中のsummary解除も禁止します。");
            }
            catch { failed = true; throw; }
            finally
            {
                fixture.ReleaseProducer(); scheduler.Release();
                try
                {
                    Task stop = failed ? fixture.ViewModel.RegularChartList.StopAsync() : Task.CompletedTask;
                    var started = new List<Task> { data, stop };
                    if (scheduler.Held.Task.IsCompletedSuccessfully) { started.Add(scheduler.Held.Task.Result); }
                    fixture.AwaitOwned(Task.WhenAll(started), "queued ready started Task cleanup");
                }
                catch (Exception cleanup) when (failed) { Console.WriteLine("queued ready cleanup fault: " + cleanup.GetType().Name); }
                finally
                {
                    try { fixture.AwaitOwned(scheduler.DrainRelatedAsync(), "queued ready actual Func/operation cleanup"); }
                    catch (Exception cleanup) when (failed) { Console.WriteLine("queued ready delivery cleanup fault: " + cleanup.GetType().Name); }
                    finally { if (changed != null) { fixture.ViewModel.PlaylistWorkspace.PropertyChanged -= changed; } }
                }
            }
        }, scheduler: scheduler);
    }

    /// <summary>初期画面、別の保守画面、解析中のフォルダ選択、解析済み0件、表示失敗を分担します。データ確定と必要警告、選択対象の行集合、実表示の失敗と通知を、それぞれ観測します。</summary>
    [TestMethod]
    [DataRow("initial")]
    [DataRow("maintenance")]
    [DataRow("folder")]
    [DataRow("empty")]
    [DataRow("fault")]
    [DoNotParallelize]
    public void ColdDuplicate_OwnNotificationsKeepAcceptedRootAndReadyRows(string condition)
    {
        var notificationReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notificationRelease = new TaskCompletionSource<UiDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialogs = new FileDbReportRecordingDialogs
        {
            MessageHandler = _ => { notificationReached.TrySetResult(); return notificationRelease.Task; }
        };
        PendingTargetPhaseLog? phases = condition == "fault"
            ? new PendingTargetPhaseLog(TestContext ?? throw new AssertFailedException("標準TestContextが未注入です。")) : null;
        Exception? primaryFailure = null;
        try
        {
            phases?.Write("fault-case-start");
            RunColdDuplicateFixture(fixture =>
            {
                phases?.Write("fault-fixture-ready");
                MainWindowViewModel vm = fixture.ViewModel;
                if (condition == "empty")
                {
                    fixture.Library.BmsCharts = [fixture.AllCharts[0], fixture.AllCharts[1] with { Md5 = new string('c', 32) }, fixture.Target];
                    fixture.AwaitOwned(vm.RegularChartList.DeliverPendingLibraryChangesAsync(fixture.Library, CancellationToken.None), "nonduplicate real source");
                }
                if (condition == "maintenance")
                {
                    fixture.AwaitOwned(vm.RegularChartList.NavigateMaintenanceAsync(MainViewUpdateMode.FullScanAllChartsFilterSelected), "initial real maintenance view");
                    Assert.AreEqual(MainViewUpdateMode.FullScanAllChartsFilterSelected, vm.MainChartList.LastAppliedColumnMode);
                    CollectionAssert.AreEquivalent(fixture.AllCharts.Select(chart => chart.Path).ToArray(), vm.MainChartList.Rows.Cast<LibraryChartRow>().Select(row => row.path).ToArray());
                }
                var failure = new InvalidOperationException("ready row preparation failed");
                int replacing = 0;
                int canceled = 0;
                EventHandler throwing = (_, _) => { if (Interlocked.Increment(ref replacing) == 1) { throw failure; } };
                EventHandler cancel = (_, _) => canceled++;
                if (condition == "fault") { vm.MainChartList.RowsReplacing += throwing; vm.MainChartList.RowsReplacementCanceled += cancel; }
                Exception? rowBodyFailure = null;
                try
                {
                    fixture.StartDuplicate();
                    if (condition == "empty")
                    {
                        // 重複のない入力では警告のカタログ変更が生じないため、その通知を待たない。
                        // 正規要求が開始したデータ準備と表示を、移動要求の完了とは別に観測する。
                        fixture.AwaitNavigation();
                        fixture.AwaitOwned(fixture.AwaitReadyAsync(), "empty cache actual data/ready terminals");
                    }
                    else
                    {
                        fixture.AwaitGate(); phases?.Write("fault-producer-gated");
                        fixture.AwaitGroupsWhileGated(); phases?.Write("fault-groups-delivered");
                        Assert.AreNotEqual(MainViewUpdateMode.DuplicateFilterSelected, vm.MainChartList.LastAppliedColumnMode, "data必須配送前に初回Duplicateを採用しません。");
                        if (condition == "folder")
                        {
                            _ = fixture.NavigateDuplicate(MainViewUpdateMode.DuplicateFilterSelected, fixture.Duplicates[0].Directory);
                        }
                        Task data = fixture.CaptureData();
                        Assert.IsFalse(fixture.CaptureData().IsCompleted);
                        Assert.IsTrue(fixture.Library.OperationAdmission.IsActive);
                        phases?.TaskState("fault-data-before-release", data);
                        fixture.ReleaseProducer(); fixture.AwaitOwned(data, "data cache/catalog/ready ack terminal");
                        phases?.TaskState("fault-data-terminal", data);
                        Assert.IsTrue(data.IsCompletedSuccessfully);
                    }
                    if (condition == "fault")
                    {
                        Task actualRows = fixture.AwaitReadyAsync();
                        Exception? rowFault = null;
                        try { fixture.AwaitOwned(actualRows, "actual Rows fault terminal"); }
                        catch (Exception exception) { rowFault = exception; }
                        Assert.AreSame(failure, rowFault);
                        fixture.AwaitNavigation();
                        Assert.IsTrue(fixture.NavigationTask.IsCompletedSuccessfully);
                        Assert.AreEqual(1, canceled);
                        Assert.AreEqual(1, replacing);
                        fixture.AwaitOwned(TestUiDispatcherHost.AwaitNotificationAsync(notificationReached.Task,
                            vm.RegularChartList.StopAsync(), "duplicate presentation failure notification"), "actual display failure notification");
                        Task stop = vm.RegularChartList.StopAsync();
                        Assert.IsFalse(stop.IsCompleted, "停止は表示失敗通知の終端を回収します。");
                        Assert.AreEqual(1, dialogs.Messages.Count);
                        Assert.AreEqual(Resources.Msg_error_unexpected, dialogs.Messages.Single().MessageBoxText);
                        notificationRelease.TrySetResult(UiDialogResult.FromMessageBoxResult(System.Windows.MessageBoxResult.OK));
                        fixture.AwaitOwned(stop, "failed presentation and notification Stop terminal");
                        Assert.AreEqual(1, dialogs.Messages.Count);
                        return;
                    }
                    fixture.CompleteDuplicate();
                    Assert.AreEqual(MainViewUpdateMode.DuplicateFilterSelected, vm.MainChartList.LastAppliedColumnMode);
                    CollectionAssert.AreEquivalent(condition == "empty" ? Array.Empty<string>() : (condition == "folder" ? fixture.Duplicates[..1] : fixture.Duplicates).Select(chart => chart.Path).ToArray(),
                        vm.MainChartList.Rows.Cast<LibraryChartRow>().Select(row => row.path).ToArray());
                    Assert.IsTrue(fixture.CacheReadyAtPresentation);
                    Assert.IsNotNull(fixture.Library.DuplicateChartGroups);
                    Assert.AreEqual(condition == "empty" ? 0 : 1, fixture.Library.DuplicateChartGroups.Count);
                    if (condition != "empty")
                    { Assert.IsTrue(fixture.Library.GetNormalLibraryRefreshNotificationsAfter(fixture.InitialNotificationVersion).CatalogCommits.Any(commit => commit.Changes.Count > 0)); }
                    int revision = fixture.Library.CatalogRevision;
                    fixture.AwaitOwned(fixture.NavigateDuplicate(MainViewUpdateMode.DuplicateFilterSelected, null), "warm current root actual presentation");
                    Assert.AreEqual(revision, fixture.Library.CatalogRevision);
                }
                catch (Exception exception) { rowBodyFailure = exception; throw; }
                finally
                {
                    notificationRelease.TrySetResult(UiDialogResult.FromMessageBoxResult(System.Windows.MessageBoxResult.OK));
                    fixture.ReleaseProducer();
                    try
                    {
                        if (rowBodyFailure != null)
                        {
                            try
                            {
                                Task stop = vm.RegularChartList.StopAsync();
                                phases?.TaskState("fault-failure-stop-start", stop);
                                try { fixture.AwaitOwned(stop, "cold self failure Stop before hook removal"); }
                                finally { phases?.TaskState("fault-failure-stop-terminal", stop); }
                            }
                            catch (Exception cleanup) { rowBodyFailure.Data["ColdSelfStopCleanup"] = cleanup; }
                        }
                    }
                    finally
                    {
                        if (condition == "fault") { vm.MainChartList.RowsReplacing -= throwing; vm.MainChartList.RowsReplacementCanceled -= cancel; }
                    }
                }
            }, report: (name, task) => phases?.TaskState(name, task), dialogs: dialogs);
            phases?.Write("fault-case-completed");
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            phases?.Write("fault-case-failed", exception.GetType().Name);
            throw;
        }
        finally
        {
            notificationRelease.TrySetResult(UiDialogResult.FromMessageBoxResult(System.Windows.MessageBoxResult.OK));
            phases?.Complete(primaryFailure);
        }
    }

    /// <summary>準備済み表示のUI配送を保留しても、実移動の仕上げは親Lで新しい解析と必要通知を完了します。非表示なら失効だけを残し、新規解析をBusyで拒んだ後も予約せず、次の明示表示で最新化します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [DoNotParallelize]
    public void DuplicateDependencyInvalidation_PreparesOutsideUiAndKeepsLaterSummary(bool displayed)
    {
        using var release = new ManualResetEventSlim(false);
        var reached = new TaskCompletionSource<(bool OnUi, bool LocksReleased)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialogs = new FileDbReportRecordingDialogs();
        var cachedPresentation = new DuplicateContinuationUiGate(new WpfUiScheduler(() => TestUiDispatcherHost.Dispatcher));
        var continuation = new DuplicateContinuationUiGate(cachedPresentation);
        RunColdDuplicateFixture(fixture =>
        {
            MainWindowViewModel vm = fixture.ViewModel;
            FieldInfo dialogField = typeof(SelectedChartMutationWorkflowOwner).GetField("dialogs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new AssertFailedException("実変更の通知窓口がありません。");
            dialogField.SetValue(vm.SelectedChartMutations, dialogs);
            fixture.AwaitOwned(fixture.NavigateDuplicate(MainViewUpdateMode.DuplicateFilterSelected, null), "initial real duplicate root");
            fixture.CompleteDuplicate();
            if (!displayed)
            { fixture.AwaitOwned(vm.RegularChartList.NavigateMaintenanceAsync(MainViewUpdateMode.FullScanAllChartsFilterSelected), "normal view before real move"); }
            ChartFile original = fixture.Library.BmsCharts.Single(chart => chart.Path == fixture.Duplicates[0].Path);
            string destination = Path.Combine(Path.GetDirectoryName(original.Directory) ?? throw new AssertFailedException(), "moved");
            Directory.CreateDirectory(destination);
            string? movedPath = null;
            int claimed = 0;
            continuation.ShouldHold = () =>
            {
                if (!displayed) { return false; }
                ChartFile? moved = fixture.Library.BmsCharts.FirstOrDefault(chart => chart.Token == original.Token && chart.Path != original.Path);
                if (moved == null || fixture.Library.DuplicateChartGroups?.SelectMany(group => group.ChartFiles).Any(chart => chart.Path == moved.Path) != true) { return false; }
                Volatile.Write(ref movedPath, moved.Path);
                reached.TrySetResult((TestUiDispatcherHost.Dispatcher.CheckAccess(), !fixture.Library.IsWriteLockHeldInitializeMin
                    && !fixture.Library.IsWriteLockHeldInitializeBMSFiles && !fixture.Library.IsWriteLockHeldDuplicateChartGroups));
                return true;
            };
            PropertyChangedEventHandler notification = (_, args) =>
            {
                if (displayed || args.PropertyName != nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion)) { return; }
                ChartFile? moved = fixture.Library.BmsCharts.FirstOrDefault(chart => chart.Token == original.Token && chart.Path != original.Path);
                if (moved == null) { return; }
                if (Interlocked.Exchange(ref claimed, 1) != 0) { return; }
                Volatile.Write(ref movedPath, moved.Path);
                reached.TrySetResult((TestUiDispatcherHost.Dispatcher.CheckAccess(), !fixture.Library.IsWriteLockHeldInitializeMin
                    && !fixture.Library.IsWriteLockHeldInitializeBMSFiles && !fixture.Library.IsWriteLockHeldDuplicateChartGroups));
                release.Wait();
            };
            fixture.Library.PropertyChanged += notification;
            Task<SelectedChartMutationResult>? operation = null;
            Task<bool>? cachedNavigation = null;
            Task? cachedDisplay = null;
            try
            {
                if (displayed)
                {
                    cachedPresentation.ShouldHold = () => true;
                    cachedNavigation = fixture.NavigateDuplicate(MainViewUpdateMode.DuplicateFilterSelected, null);
                    fixture.AwaitOwned(Task.WhenAny(cachedPresentation.HeldOperation, cachedNavigation), "cached display started or navigation failed");
                    if (cachedNavigation.IsFaulted || cachedNavigation.IsCanceled) { fixture.AwaitOwned(cachedNavigation, "cached navigation failure"); }
                    fixture.AwaitOwned(cachedPresentation.HeldOperation, "cached display actual UI delivery");
                    cachedDisplay = cachedPresentation.HeldOperation.GetAwaiter().GetResult();
                    Assert.IsFalse(cachedDisplay.IsCompleted);
                    Assert.IsFalse(fixture.Library.OperationAdmission.IsActive, "準備済み表示の遅い配送はLを保持しません。");
                }
                var target = new ChartOperationTarget(original, null, ChartOperationSourceScope.Library, true, false, false, ChartOperationCapabilities.MoveInLibrary);
                operation = vm.SelectedChartMutations.MoveAsync(new SelectedChartMoveRequest([target], destination));
                fixture.AwaitOwned(TestUiDispatcherHost.AwaitNotificationAsync(reached.Task, operation, "real move notification"), "real move required notification");
                Assert.IsFalse(reached.Task.Result.OnUi);
                Assert.IsTrue(reached.Task.Result.LocksReleased);
                Assert.IsFalse(operation.IsCompleted);
                Assert.IsTrue(fixture.Library.OperationAdmission.IsActive);
                Assert.IsFalse(fixture.Library.OperationAdmission.TryEnter(out IDisposable rejected));
                Assert.IsNull(rejected);
                if (displayed)
                {
                    Assert.IsNotNull(fixture.Library.DuplicateChartGroups);
                    Assert.IsNotNull(cachedDisplay);
                    Assert.IsFalse(cachedDisplay.IsCompleted, "旧キャッシュ表示を保留したまま、新しい実解析が完了します。");
                    Assert.IsTrue(fixture.Library.DuplicateChartGroups.SelectMany(group => group.ChartFiles).Any(chart => chart.Path == movedPath));
                    Assert.AreEqual(0, dialogs.Messages.Count, "内部継続では自己Busyを通知しません。");
                    vm.PlaylistWorkspace.RequestSummarySelection(); fixture.AwaitSummary();
                }
                else
                {
                    RegularChartNavigationRequest selection = vm.RegularChartList.CaptureNavigation();
                    IList rows = vm.MainChartList.Rows;
                    Task<bool> analysis = fixture.NavigateDuplicate(MainViewUpdateMode.DuplicateFilterSelected, null);
                    fixture.AwaitOwned(analysis, "cold analysis rejected during real move");
                    Assert.IsFalse(analysis.Result);
                    Assert.AreSame(selection, vm.RegularChartList.CaptureNavigation());
                    Assert.AreSame(rows, vm.MainChartList.Rows);
                    Assert.IsNull(fixture.Library.DuplicateChartGroups);
                    Assert.AreEqual(Resources.Warn_LibraryOperationBusy, dialogs.Messages.Single().MessageBoxText);
                }
                release.Set(); continuation.Release(); cachedPresentation.Release();
                fixture.AwaitOwned(operation, "real move and required duplicate data terminal");
                Assert.IsNull(operation.Result.Failure, operation.Result.Failure?.ToString());
                Assert.IsTrue(operation.Result.HasDurableCommit);
                Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
                if (cachedNavigation != null) { fixture.AwaitOwned(cachedNavigation, "cached navigation terminal"); }
                if (cachedDisplay != null) { fixture.AwaitOwned(cachedDisplay, "old cached display terminal"); }
                Assert.AreEqual(displayed ? 0 : 1, dialogs.Messages.Count);
                Assert.IsFalse(File.Exists(original.Path));
                string actualPath = movedPath ?? throw new AssertFailedException("実移動のパス通知がありません。");
                Assert.IsTrue(File.Exists(actualPath));
                using (LR2SongDBExtended read = fixture.Library.DatabaseScope.OpenSongWrite(Path.Combine(Path.GetDirectoryName(fixture.Target.Directory) ?? throw new AssertFailedException(), "song.db")))
                { Assert.IsTrue(read.Table<LR2SongDB.song>().Any(row => row.path == actualPath)); }
                if (displayed)
                {
                    Assert.IsTrue(vm.PlaylistWorkspace.IsPlaylistSummaryModeRequested);
                    CollectionAssert.AreEqual(new[] { "target playlist" }, vm.PlaylistWorkspace.PlaylistSummaryView.Select(row => row.Name).ToArray());
                    Assert.IsNotNull(fixture.Library.DuplicateChartGroups);
                }
                else { Assert.IsNull(fixture.Library.DuplicateChartGroups, "非表示時は失効だけを残し、解放後にも予約しません。"); }
                fixture.AwaitOwned(fixture.NavigateDuplicate(MainViewUpdateMode.DuplicateFilterSelected, null), "explicit duplicate after real move");
                fixture.AwaitOwned(fixture.AwaitReadyAsync(), "latest moved duplicate rows");
                CollectionAssert.AreEquivalent(new[] { actualPath, fixture.Duplicates[1].Path }, vm.MainChartList.Rows.Cast<LibraryChartRow>().Select(row => row.path).ToArray());
            }
            finally
            {
                release.Set(); continuation.Release(); cachedPresentation.Release();
                try
                {
                    fixture.AwaitOwned(Task.WhenAll((Task?)operation ?? Task.CompletedTask, (Task?)cachedNavigation ?? Task.CompletedTask,
                        cachedDisplay ?? Task.CompletedTask), "real move and cached display cleanup");
                }
                finally { fixture.Library.PropertyChanged -= notification; }
            }
        }, scheduler: continuation, dialogs: dialogs);
    }

    /// <summary>現在選択を保ったライブラリ交換で、旧一覧候補と旧解析結果を採用しないことを確認します。旧一覧の構築中と旧重複解析中を分担し、開始済みTaskの回収、新ライブラリの通常ルート表示、重複画面への再訪を確認します。</summary>
    [TestMethod]
    [DataRow("oldRows")]
    [DataRow("oldDupData")]
    [DoNotParallelize]
    public void SourceAuthorityReplacement_KeepsCurrentSelectionAndRejectsOldSourceWork(string oldWork)
    {
        WithTemporarySongDb(newSongDb => RunColdDuplicateFixture(fixture =>
        {
            MainWindowViewModel vm = fixture.ViewModel;
            ApplicationDatabaseScope databaseScope = MainWindowViewModelTestFactory.GetComposition(vm).DatabaseScope;
            Settings settings = TestSettingsFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = false;
                values.StartupSelectInstallPending = false;
                values.UseBeatorajaScoreDb = false;
            });
            string directory = Path.GetDirectoryName(newSongDb) ?? throw new AssertFailedException();
            ChartFile NewChart(string folder, string title, string artist)
            {
                string parent = Path.Combine(directory, folder);
                Directory.CreateDirectory(parent);
                string path = Path.Combine(parent, "chart.bms");
                File.WriteAllText(path, "#PLAYER 1\n#TITLE " + title + "\n#ARTIST " + artist + "\n#BPM 120\n");
                return CreateTestableBmsFile(path) with { Md5 = new string('d', 32), Title = title, RawTitle = title, Artist = artist, RawArtist = artist };
            }
            ChartFile[] newCharts = [NewChart("new-a", "New Alpha", "Z"), NewChart("new-b", "New Zeta", "A")];
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(newSongDb)) { db.CreateTable<LR2SongDBExtended.bmson_song>(); }
            TestBmsLibrary newLibrary = MainWindowViewModelTestFactory.CreateLibrary(databaseScope, newSongDb, settings, vm);
            newLibrary.BmsCharts = newCharts;
            new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), newSongDb).UpsertSongs(newCharts);
            using var release = new ManualResetEventSlim(false);
            var workerReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var rootStarted = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sortStarted = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            int workerClaimed = 0;
            int oldReady = 0;
            int watch = 0;
            var violations = new List<string>();
            System.Collections.IList previousRows = vm.MainChartList.Rows;
            var started = new List<Task>();
            Task? oldPresentation = null;
            Task? oldData = null;
            Task<bool>? oldNavigation = null;
            Task[] sourceParents = [];
            var newRowsApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool sourceObserved = false;
            HashSet<Task> oldBaseline = [];
            System.Reflection.FieldInfo syncField = typeof(RegularChartListOwner).GetField("syncRoot", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new AssertFailedException("実準備の同期境界がありません。");
            System.Reflection.FieldInfo tasksField = typeof(RegularChartListOwner).GetField("viewPreparationTasks", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new AssertFailedException("実準備Task集合がありません。");
            Task[] Snapshot()
            {
                object synchronization = syncField.GetValue(vm.RegularChartList) ?? throw new AssertFailedException();
                lock (synchronization)
                {
                    if (tasksField.GetValue(vm.RegularChartList) is not HashSet<Task> tasks) { throw new AssertFailedException(); }
                    return tasks.ToArray();
                }
            }
            EventHandler<RegularChartTreeNavigationPresentationRequestedEventArgs> rootRequest = (_, args) => rootStarted.TrySetResult(args.Presentation);
            EventHandler<RegularChartSortRefreshRequestedEventArgs> sortRequest = (_, args) => sortStarted.TrySetResult(args.Presentation);
            EventHandler<RegularDuplicateDataReadyEventArgs> ready = (_, args) =>
            { if (ReferenceEquals(args.Library, fixture.Library) && Volatile.Read(ref watch) != 0) { oldReady++; } };
            PropertyChangedEventHandler rows = (_, args) =>
            {
                if (args.PropertyName != nameof(MainChartListViewModel.Rows)) { return; }
                if (Volatile.Read(ref watch) != 0 && !ReferenceEquals(previousRows, vm.MainChartList.Rows)
                    && vm.MainChartList.Rows.Cast<LibraryChartRow>().Any(row => fixture.AllCharts.Any(chart => chart.Path == row.path)))
                { violations.Add("source交換後に旧libraryの新Rowsを採用しました。"); }
                previousRows = vm.MainChartList.Rows;
                if (vm.MainChartList.Rows.Cast<LibraryChartRow>().Select(row => row.path).Order(StringComparer.Ordinal)
                    .SequenceEqual(newCharts.Select(chart => chart.Path).Order(StringComparer.Ordinal))) { newRowsApplied.TrySetResult(); }
            };
            EventHandler<NormalLibraryRefreshAppliedEventArgs> source = (_, args) =>
            {
                if (oldWork != "oldRows" || Volatile.Read(ref watch) == 0
                    || !args.NotificationBatch.HasEffect(LibraryChartRefreshEffects.SourceChanged)) { return; }
                sourceObserved = true;
                Task[] captured = Snapshot().Where(task => !oldBaseline.Contains(task)).ToArray();
                started.AddRange(captured);
                sourceParents = captured;
            };
            vm.RegularChartList.TreeNavigationPresentationRequested += rootRequest;
            vm.RegularChartList.SortRefreshRequested += sortRequest;
            vm.RegularChartList.DuplicateDataReady += ready;
            vm.RegularChartList.NormalLibraryRefreshApplied += source;
            vm.MainChartList.PropertyChanged += rows;
            Exception? failure = null;
            try
            {
                if (oldWork == "oldRows")
                {
                    Assert.IsTrue(vm.RegularChartList.NavigateTree(null));
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(rootStarted.Task, "old source actual Root typed arrival");
                    Task root = rootStarted.Task.GetAwaiter().GetResult();
                    started.Add(root);
                    fixture.AwaitOwned(root, "old source Root presentation");
                    fixture.AwaitOwned(Task.WhenAll(Snapshot()), "old source Root parent terminals");
                    CollectionAssert.AreEquivalent(fixture.AllCharts.Select(chart => chart.Path).ToArray(), vm.MainChartList.Rows.Cast<LibraryChartRow>().Select(row => row.path).ToArray());
                    vm.MainChartList.RowProjection.NormalInputWorkObserver = (kind, _) =>
                    {
                        if (kind != "order_buffer" || Interlocked.CompareExchange(ref workerClaimed, 1, 0) != 0) { return; }
                        bool onUi = TestUiDispatcherHost.Dispatcher.CheckAccess();
                        workerReached.TrySetResult(onUi);
                        if (onUi) { release.Set(); } else { release.Wait(); }
                    };
                    vm.MainChartList.RequestSort(nameof(LibraryChartRow.Artist), ListSortDirection.Ascending);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(sortStarted.Task, "old source actual sort presentation arrival");
                    oldPresentation = sortStarted.Task.GetAwaiter().GetResult();
                    started.Add(oldPresentation);
                    fixture.AwaitOwned(TestUiDispatcherHost.AwaitNotificationAsync(workerReached.Task, oldPresentation, "old source order worker"), "old source worker gate");
                    Assert.IsFalse(workerReached.Task.GetAwaiter().GetResult(), "旧order構築はUI外です。");
                    Assert.IsFalse(oldPresentation.IsCompleted);
                    // 新ライブラリの表示完了は旧構築の完了を待たず、停止時には旧構築も回収する。
                    oldBaseline = Snapshot().ToHashSet();
                    var ui = new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher);
                    fixture.AwaitOwned(ui.InvokeAsync(() => Assert.IsTrue(TestUiDispatcherHost.Dispatcher.CheckAccess())), "old worker held UI marker");
                }
                else
                {
                    fixture.StartDuplicate(); fixture.AwaitGate(); fixture.AwaitGroupsWhileGated();
                    oldNavigation = fixture.NavigationTask;
                    oldData = fixture.CaptureData();
                    started.Add(oldNavigation); started.Add(oldData);
                }
                if (oldWork == "oldDupData")
                {
                    RegularChartNavigationRequest current = vm.RegularChartList.CaptureNavigation();
                    string keyword = vm.ChartFilters.KeywordFilter;
                    Assert.ThrowsExactly<InvalidOperationException>(() => ((IStartupLibraryApplicationPort)vm).AttachStartupLibrary(newLibrary));
                    Assert.AreSame(current, vm.RegularChartList.CaptureNavigation());
                    Assert.AreEqual(keyword, vm.ChartFilters.KeywordFilter);
                    Assert.IsNull(newLibrary.DuplicateChartGroups);
                    fixture.CompleteDuplicate();
                    CollectionAssert.AreEquivalent(fixture.Duplicates.Select(chart => chart.Path).ToArray(),
                        vm.MainChartList.Rows.Cast<LibraryChartRow>().Select(row => row.path).ToArray());
                    Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
                    return;
                }
                Volatile.Write(ref watch, 1);
                ((IStartupLibraryApplicationPort)vm).AttachStartupLibrary(newLibrary);
                vm.MaintenanceTree.AttachLibrary(newLibrary);
                Task delivery = vm.RegularChartList.DeliverPendingLibraryChangesAsync(newLibrary, CancellationToken.None);
                started.Add(delivery);
                fixture.AwaitOwned(delivery, "new source actual notification delivery");
                if (oldWork == "oldRows")
                {
                    Assert.IsTrue(sourceObserved);
                    fixture.AwaitOwned(newRowsApplied.Task, "new source actual Rows publication");
                    fixture.AwaitOwned(Task.WhenAll(sourceParents), "new source started parent terminals");
                    Assert.IsFalse(release.IsSet, "旧worker解放前に新Rootを実採用します。");
                    Assert.AreEqual(MainViewUpdateMode.FolderFilterSelected, vm.MainChartList.LastAppliedColumnMode);
                    Assert.IsNull(vm.RegularChartList.CaptureTreeFilter(enabled: true));
                    CollectionAssert.AreEquivalent(newCharts.Select(chart => chart.Path).ToArray(), vm.MainChartList.Rows.Cast<LibraryChartRow>().Select(row => row.path).ToArray());
                    System.Collections.IList newRows = vm.MainChartList.Rows;
                    Task stop = vm.RegularChartList.StopAsync(); started.Add(stop);
                    Assert.IsFalse(stop.IsCompleted, "旧workerを保持したままStopが同実Taskを待ちます。");
                    release.Set();
                    fixture.AwaitOwned(stop, "source swap Stop before old worker await");
                    Assert.IsNotNull(oldPresentation);
                    Assert.IsTrue(oldPresentation.IsCompleted);
                    fixture.AwaitOwned(oldPresentation, "old source actual Rows terminal");
                    Assert.AreSame(newRows, vm.MainChartList.Rows);
                }
                Assert.AreEqual(0, violations.Count, string.Join(Environment.NewLine, violations));
            }
            catch (Exception exception) { failure = exception; throw; }
            finally
            {
                release.Set(); fixture.ReleaseProducer();
                try
                {
                    Task stop = vm.RegularChartList.StopAsync(); started.Add(stop);
                    started.Add(fixture.AwaitReadyAsync(awaitRows: false));
                    fixture.AwaitOwned(Task.WhenAll(started), "source authority all accepted/data/ready Tasks/Stop cleanup");
                }
                catch (Exception cleanup) when (failure != null) { failure.Data["SourceAuthorityCleanupFailure"] = cleanup; }
                finally
                {
                    vm.MainChartList.RowProjection.NormalInputWorkObserver = null;
                    vm.RegularChartList.TreeNavigationPresentationRequested -= rootRequest;
                    vm.RegularChartList.SortRefreshRequested -= sortRequest;
                    vm.RegularChartList.DuplicateDataReady -= ready;
                    vm.RegularChartList.NormalLibraryRefreshApplied -= source;
                    vm.MainChartList.PropertyChanged -= rows;
                    fixture.Library.RequestShutdown("source_authority_old_fixture");
                    newLibrary.RequestShutdown("source_authority_new_fixture");
                }
            }
        }));
    }

    /// <summary>特定の導入項目への移動を受理してUI適用を保留し、先行する重複解析と通知を完了させます。保留中に旧重複ルートを採用せず、UI適用後は指定した導入項目だけを表示することを確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void ColdDuplicate_AutoRefreshCannotAdoptOldTargetWhileAcceptedInstallUiIsPending()
    {
        var databaseScope = new ApplicationDatabaseScope();
        var phase = new PendingTargetPhaseLog(TestContext ?? throw new AssertFailedException("標準TestContextが注入されていません。"));
        Exception? primaryFailure = null;
        var probe = new ProjectionTaskCollector(new WpfUiScheduler(() => TestUiDispatcherHost.Dispatcher));
        var scheduler = new PendingInstallTargetScheduler(probe);
        using var companionRelease = new ManualResetEventSlim(false);
        using var groupsRelease = new ManualResetEventSlim(false);
        using var publicationRelease = new ManualResetEventSlim(false);
        var companionReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var groupsReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new TaskCompletionSource<(CatalogCommitReceipt Receipt, bool LocksReleased, bool OnUi)>(TaskCreationOptions.RunContinuationsAsynchronously);
        int enabled = 0;
        int companionClaimed = 0;
        int groupsClaimed = 0;
        int publicationClaimed = 0;
        string? addedPath = null;
        PropertyChangedEventHandler gate = (sender, args) =>
        {
            if (Volatile.Read(ref enabled) == 0 || sender is not BMSLibrary library) { return; }
            if (args.PropertyName == nameof(BMSLibrary.DuplicateChartGroupsInvalidationVersion)
                && Interlocked.CompareExchange(ref companionClaimed, 1, 0) == 0)
            { companionReached.TrySetResult(); companionRelease.Wait(); return; }
            if (args.PropertyName == nameof(BMSLibrary.DuplicateChartGroups) && addedPath != null
                && library.DuplicateChartGroups?.SelectMany(group => group.ChartFiles).Any(chart => chart.Path == addedPath) == true
                && Interlocked.CompareExchange(ref groupsClaimed, 1, 0) == 0)
            { groupsReached.TrySetResult(); groupsRelease.Wait(); return; }
            if (args.PropertyName != nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion) || addedPath == null
                || library.DuplicateChartGroups?.SelectMany(group => group.ChartFiles).Any(chart => chart.Path == addedPath) != true) { return; }
            CatalogCommitReceipt? receipt = library.GetNormalLibraryRefreshNotificationsAfter(0).CatalogCommits.FirstOrDefault(commit =>
                commit.Changes.Any(change => change.After?.Path == addedPath
                    && change.After.Warnings.Any(warning => warning.Kind == ChartWarningKind.DuplicateChart)));
            if (receipt == null || Interlocked.CompareExchange(ref publicationClaimed, 1, 0) != 0) { return; }
            published.TrySetResult((receipt, !library.IsWriteLockHeldInitializeMin
                && !library.IsWriteLockHeldInitializeBMSFiles && !library.IsWriteLockHeldDuplicateChartGroups,
                TestUiDispatcherHost.Dispatcher.CheckAccess()));
            if (TestUiDispatcherHost.Dispatcher.CheckAccess()) { publicationRelease.Set(); return; }
            publicationRelease.Wait();
        };
        try
        {
            phase.Write("case-start");
            RunColdDuplicateFixture(fixture =>
            {
                MainWindowViewModel vm = fixture.ViewModel;
                string root = Path.GetDirectoryName(fixture.Target.Directory) ?? throw new AssertFailedException();
                ChartFile Extra(string directory, char hash)
                {
                    string folder = Path.Combine(root, directory); Directory.CreateDirectory(folder);
                    string path = Path.Combine(folder, "chart.bms");
                    File.WriteAllText(path, "#PLAYER 1\n#TITLE " + directory + "\n#BPM 120\n");
                    return CreateTestableBmsFile(path) with { Md5 = new string(hash, 32), Title = directory };
                }
                ChartFile sentinel = Extra("installed-sentinel", 'c');
                ChartFile added = Extra("accepted-target-added", 'a');
                var sentinelPackage = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(sentinel)]);
                fixture.Library.BmsCharts = [.. fixture.Library.BmsCharts, sentinel];
                new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), Path.Combine(root, "song.db")).UpsertSongs([sentinel, added]);
                fixture.Library.ChartPackagesInstalled = new ObservableCollection<ChartPackage>([fixture.Package, sentinelPackage]);
                Task initialSource = vm.RegularChartList.DeliverPendingLibraryChangesAsync(fixture.Library, CancellationToken.None);
                phase.TaskState("initial-source-await", initialSource);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(initialSource, "pending target initial source");
                phase.TaskState("initial-source-done", initialSource);
                Task initialRoot = fixture.NavigateDuplicate(MainViewUpdateMode.DuplicateFilterSelected, null);
                phase.TaskState("initial-Root-await", initialRoot);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(initialRoot, "pending target initial Root");
                phase.TaskState("initial-Root-done", initialRoot);
                fixture.CompleteDuplicate();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(probe.InvokeAsync(() => { }), "pending target initial UI flush");
                Task initialFuncs = probe.DrainPriorAsync(phase.TaskState);
                phase.TaskState("initial-func-await", initialFuncs);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(initialFuncs, "pending target initial Func terminals");
                phase.TaskState("initial-func-done", initialFuncs);
                CollectionAssert.AreEquivalent(fixture.Duplicates.Select(chart => chart.Path).ToArray(), vm.MainChartList.Rows.Cast<LibraryChartRow>().Select(row => row.path).ToArray());
                var sourceApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var groupsApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var warningApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var companionConsumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var violations = new List<string>();
                object violationLock = new object();
                var forbiddenAdoption = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                void RecordViolation(string violation)
                {
                    lock (violationLock)
                    {
                        violations.Add(violation);
                        forbiddenAdoption.TrySetResult(violations.ToArray());
                    }
                }
                string[] CaptureViolations() { lock (violationLock) { return violations.ToArray(); } }
                int watch = 0;
                System.Collections.IList previousRows = vm.MainChartList.Rows;
                PropertyChangedEventHandler rowsChanged = (_, args) =>
                {
                    if (args.PropertyName != nameof(MainChartListViewModel.Rows) || Volatile.Read(ref watch) == 0) { return; }
                    if (!ReferenceEquals(previousRows, vm.MainChartList.Rows)
                        && vm.MainChartList.LastAppliedColumnMode == MainViewUpdateMode.DuplicateFilterSelected)
                    { RecordViolation("B受付後に旧Duplicate対象の新Rowsを採用しました。"); }
                    previousRows = vm.MainChartList.Rows;
                };
                Action<MainViewUpdateMode> modeApplied = mode =>
                { if (Volatile.Read(ref watch) != 0 && mode == MainViewUpdateMode.DuplicateFilterSelected) { RecordViolation("B受付後にAのDuplicate modeを確定しました。"); } };
                PropertyChangedEventHandler summaryChanged = (_, args) =>
                {
                    if (Volatile.Read(ref watch) != 0 && args.PropertyName == nameof(PlaylistWorkspaceViewModel.IsPlaylistSummaryMode)
                        && !vm.PlaylistWorkspace.IsPlaylistSummaryModeRequested) { RecordViolation("B受付後にA由来のsummary解除を公開しました。"); }
                };
                EventHandler<NormalLibraryRefreshAppliedEventArgs> applied = (_, args) =>
                {
                    if (args.NotificationBatch.HasEffect(LibraryChartRefreshEffects.SourceChanged)) { sourceApplied.TrySetResult(); }
                    if (args.NotificationBatch.CatalogCommits.Any(commit => commit.Changes.Any(change => change.After?.Path == added.Path
                            && change.After.Warnings.Any(warning => warning.Kind == ChartWarningKind.DuplicateChart)))) { warningApplied.TrySetResult(); }
                };
                PropertyChangedEventHandler groupChanged = (_, args) =>
                {
                    if (args.PropertyName == nameof(MaintenanceTreeViewModel.DuplicateChartGroups)
                        && vm.MaintenanceTree.DuplicateChartGroups?.SelectMany(group => group.ChartFiles).Any(chart => chart.Path == added.Path) == true) { groupsApplied.TrySetResult(); }
                };
                PropertyChangedEventHandler companion = (_, args) =>
                { if (Volatile.Read(ref enabled) != 0 && args.PropertyName == nameof(BMSLibrary.DuplicateChartGroupsInvalidationVersion)) { companionConsumed.TrySetResult(); } };
                vm.MainChartList.PropertyChanged += rowsChanged;
                vm.MainChartList.AppliedColumnModeCommitted += modeApplied;
                vm.PlaylistWorkspace.PropertyChanged += summaryChanged;
                vm.RegularChartList.NormalLibraryRefreshApplied += applied;
                vm.MaintenanceTree.PropertyChanged += groupChanged;
                fixture.Library.PropertyChanged += companion;
                var coordinator = Task.Run(async () =>
                {
                    Task<bool>? a = null;
                    Task<bool>? b = null;
                    bool failed = false;
                    var actions = new List<Task>();
                    Task Own(Task task) { actions.Add(task); return task; }
                    void AssertNoForbiddenAdoption()
                    {
                        if (!forbiddenAdoption.Task.IsCompletedSuccessfully) { return; }
                        string[] snapshot = forbiddenAdoption.Task.Result;
                        phase.Write("forbidden-adoption", string.Join(" | ", snapshot));
                        Assert.AreEqual(0, snapshot.Length, string.Join(Environment.NewLine, snapshot));
                    }
                    async Task AwaitActualAsync(Task task, string name)
                    {
                        phase.TaskState(name + "-await", task);
                        try
                        {
                            AssertNoForbiddenAdoption();
                            await Task.WhenAny(task, forbiddenAdoption.Task);
                            if (task.IsFaulted || task.IsCanceled) { phase.TaskState(name + "-fault-or-cancel", task); }
                            AssertNoForbiddenAdoption();
                            await task;
                            AssertNoForbiddenAdoption();
                            phase.TaskState(name + "-done", task);
                        }
                        catch (Exception exception) { phase.Write(name + "-failed", exception.ToString()); throw; }
                    }
                    async Task AwaitEventAsync(Task notification, Task producer, string name, bool producerMustRemainPending = false)
                    {
                        phase.TaskState(name + "-event-await", notification);
                        phase.TaskState(name + "-producer", producer);
                        Task? pendingProducer = producer;
                        while (!notification.IsCompleted)
                        {
                            AssertNoForbiddenAdoption();
                            Task[] candidates = pendingProducer == null
                                ? [notification, forbiddenAdoption.Task]
                                : [notification, forbiddenAdoption.Task, pendingProducer];
                            await Task.WhenAny(candidates);
                            if (pendingProducer != null && (pendingProducer.IsFaulted || pendingProducer.IsCanceled))
                            { phase.TaskState(name + "-producer-fault-or-cancel", pendingProducer); }
                            AssertNoForbiddenAdoption();
                            if (notification.IsCompleted) { break; }
                            if (pendingProducer != null && pendingProducer.IsCompleted)
                            {
                                await pendingProducer;
                                if (producerMustRemainPending) { Assert.Fail(name + ": 正規要求が必要通知前に終端しました。"); }
                                // 解析本体の完了後も、別に配送されるグループ変更と失効通知を回収する。
                                pendingProducer = null;
                            }
                        }
                        await notification;
                        AssertNoForbiddenAdoption();
                        phase.TaskState(name + "-event-done", notification);
                    }
                    try
                    {
                        Volatile.Write(ref addedPath, added.Path); Volatile.Write(ref enabled, 1);
                        Task replacement = Own(probe.InvokeAsync(() => fixture.Library.BmsCharts = [.. fixture.Library.BmsCharts, added]));
                        phase.TaskState("source-replacement-await", replacement); await replacement; phase.TaskState("source-replacement-done", replacement);
                        phase.TaskState("companion-gate-await", companionReached.Task); await companionReached.Task; phase.TaskState("companion-gate-reached", companionReached.Task);
                        Task source = Own(vm.RegularChartList.DeliverPendingLibraryChangesAsync(fixture.Library, CancellationToken.None));
                        phase.TaskState("source-delivery-await", source); await source; phase.TaskState("source-delivery-done", source);
                        phase.TaskState("source-applied-await", sourceApplied.Task); await sourceApplied.Task; phase.TaskState("source-applied-done", sourceApplied.Task);
                        await Own(probe.InvokeAsync(() => { }));
                        Task prior = Own(probe.DrainPriorAsync(phase.TaskState)); phase.TaskState("source-prior-func-await", prior); await prior; phase.TaskState("source-prior-func-done", prior);
                        Assert.IsNull(fixture.Library.DuplicateChartGroups);
                        a = vm.RegularChartList.NavigateMaintenanceAsync(MainViewUpdateMode.DuplicateFilterSelected);
                        phase.TaskState("A-started", a);
                        phase.TaskState("A-public-gate-await", published.Task);
                        await TestUiDispatcherHost.AwaitNotificationAsync(published.Task, a, "A actual public gate");
                        phase.TaskState("A-public-gate-reached", published.Task);
                        phase.TaskState("A-groups-gate-await", groupsReached.Task);
                        await TestUiDispatcherHost.AwaitNotificationAsync(groupsReached.Task, a, "A new groups gate");
                        phase.TaskState("A-groups-gate-reached", groupsReached.Task);
                        Assert.IsFalse(a.IsCompleted);
                        Assert.IsTrue((await published.Task).LocksReleased);
                        Assert.IsFalse((await published.Task).OnUi, "公開gateをUIで永久保持しません。");
                        Task data = Own(fixture.CaptureData());
                        scheduler.Arm(); Volatile.Write(ref watch, 1);
                        b = vm.RegularChartList.NavigateInstallAsync(MainViewUpdateMode.NewlyInstalledFolderSelected, fixture.Package);
                        phase.TaskState("B-accepted", b);
                        await AwaitEventAsync(scheduler.Held.Task, b, "B-held-action", producerMustRemainPending: true);
                        Task held = await scheduler.Held.Task;
                        Assert.IsFalse(held.IsCompleted); Assert.IsFalse(b.IsCompleted);
                        phase.TaskState("B-held", held);
                        groupsRelease.Set(); publicationRelease.Set(); companionRelease.Set(); phase.Write("A-gates-released");
                        await AwaitActualAsync(data, "A-data-cache-catalog-ready-ack");
                        await AwaitActualAsync(a, "A-stale-navigation");
                        await AwaitActualAsync(Own(fixture.AwaitReadyAsync()), "A-ready-presentations");
                        await AwaitEventAsync(groupsApplied.Task, a, "groups-applied");
                        await AwaitEventAsync(companionConsumed.Task, a, "companion-consumed");
                        Task delivery = Own(vm.RegularChartList.DeliverPendingLibraryChangesAsync(fixture.Library, CancellationToken.None));
                        await AwaitActualAsync(delivery, "catalog-delivery");
                        Assert.IsTrue(warningApplied.Task.IsCompletedSuccessfully);
                        Assert.IsTrue((await published.Task).Receipt.Changes.Any(change => change.After?.Path == added.Path));
                        Task related = Own(scheduler.DrainRelatedAsync(phase.TaskState));
                        await AwaitActualAsync(related, "A-related");
                        Assert.IsFalse(held.IsCompleted, "A関連Taskの終端でBの保留を先に成功させません。");
                        Assert.IsFalse(b.IsCompleted);
                        await AwaitActualAsync(Own(probe.InvokeAsync(() =>
                        {
                            string[] snapshot = CaptureViolations();
                            Assert.AreEqual(0, snapshot.Length, string.Join(Environment.NewLine, snapshot));
                        })), "A-final-zero");
                        scheduler.Release(); phase.Write("B-released");
                        await AwaitActualAsync(b, "B"); Assert.IsTrue(await b);
                        await AwaitActualAsync(held, "B-held-action-terminal");
                        await AwaitActualAsync(Own(scheduler.DrainRelatedAsync(phase.TaskState)), "B-related");
                        await AwaitActualAsync(Own(probe.InvokeAsync(() =>
                        {
                            string[] snapshot = CaptureViolations();
                            Assert.AreEqual(0, snapshot.Length, string.Join(Environment.NewLine, snapshot));
                            Assert.AreEqual(MainViewUpdateMode.NewlyInstalledFolderSelected, vm.MainChartList.LastAppliedColumnMode);
                            LibraryChartRow[] rows = vm.MainChartList.Rows.Cast<LibraryChartRow>().ToArray();
                            CollectionAssert.AreEqual(new[] { fixture.Target.Path }, rows.Select(row => row.path).ToArray());
                            Assert.AreSame(fixture.Package.ChartEntries[0], rows.Single().PackageEntry);
                            Assert.IsFalse(rows.Any(row => row.path == sentinel.Path), "特定Bをall-installedへ広げません。");
                        })), "B-final-assert");
                    }
                    catch (Exception exception) { failed = true; phase.Write("coordinator-failed", exception.ToString()); throw; }
                    finally
                    {
                        groupsRelease.Set(); publicationRelease.Set(); companionRelease.Set(); scheduler.Release();
                        phase.Write("cleanup-gates-released", "failed=" + failed);
                        Task StartStop()
                        {
                            phase.Write("Stop-start");
                            try { return vm.RegularChartList.StopAsync(); }
                            catch (Exception exception) { phase.Write("Stop-start-failed", exception.ToString()); return Task.FromException(exception); }
                        }
                        Task stop = failed ? StartStop() : Task.CompletedTask;
                        Task cleanupDrain = scheduler.DrainRelatedAsync(phase.TaskState);
                        IEnumerable<Task> started = actions.Append((Task?)a ?? Task.CompletedTask).Append((Task?)b ?? Task.CompletedTask)
                            .Append(cleanupDrain).Append(stop);
                        if (scheduler.Held.Task.IsCompletedSuccessfully) { started = started.Append(scheduler.Held.Task.Result); }
                        Task[] ownedTasks = started.ToArray();
                        phase.Write("cleanup-owned", string.Join("; ", ownedTasks.Select(task => task.Id + ":" + task.Status)));
                        var join = Task.WhenAll(ownedTasks);
                        Exception? cleanupFailure = null;
                        phase.TaskState("cleanup-join-await", join);
                        try { await join; phase.TaskState("cleanup-join-done", join); }
                        catch (Exception exception)
                        {
                            cleanupFailure = join.Exception ?? exception;
                            phase.Write("cleanup-join-failed", cleanupFailure.ToString());
                        }
                        if (!failed)
                        {
                            stop = StartStop();
                            try { await stop; }
                            catch (Exception exception)
                            {
                                phase.Write("cleanup-Stop-failed", exception.ToString());
                                cleanupFailure = cleanupFailure == null ? exception : new AggregateException(cleanupFailure, exception);
                            }
                        }
                        phase.TaskState("Stop-terminal", stop);
                        if (!failed && cleanupFailure != null) { throw cleanupFailure; }
                    }
                });
                bool coordinatorFailed = false;
                try { TestUiDispatcherHost.AwaitTaskOnDispatcher(coordinator, "accepted B target pending while A completes"); }
                catch { coordinatorFailed = true; throw; }
                finally
                {
                    groupsRelease.Set(); publicationRelease.Set(); companionRelease.Set(); scheduler.Release();
                    try { TestUiDispatcherHost.AwaitTaskOnDispatcher(coordinator, "pending target coordinator cleanup"); }
                    catch (Exception exception) when (coordinatorFailed) { phase.Write("coordinator-reobservation", exception.ToString()); }
                    finally
                    {
                        vm.MainChartList.PropertyChanged -= rowsChanged;
                        vm.MainChartList.AppliedColumnModeCommitted -= modeApplied;
                        vm.PlaylistWorkspace.PropertyChanged -= summaryChanged;
                        vm.RegularChartList.NormalLibraryRefreshApplied -= applied;
                        vm.MaintenanceTree.PropertyChanged -= groupChanged;
                        fixture.Library.PropertyChanged -= companion;
                    }
                }
            }, gate, scheduler);
            phase.Write("case-completed");
        }
        catch (Exception exception) { primaryFailure = exception; phase.Write("case-failed", exception.ToString()); throw; }
        finally { phase.Complete(primaryFailure); }
    }

    /// <summary>重複データの準備中に検索語またはキーモードを続けて変更し、検索語側では並び順も変更します。同じ解析へ合流し、完成時の最新条件と順序で表示すること、全対象のキャッシュを保つこと、完成後の絞込みで正当な0件を表示することを確認します。</summary>
    [TestMethod]
    [DoNotParallelize]
    [DataRow(false)]
    [DataRow(true)]
    public void DuplicateInvalidation_NormalFiltersAwaitReadyInputAndAdoptLatestFilter(bool modeFilter)
    {
        var databaseScope = new ApplicationDatabaseScope();
        var scheduler = new ProjectionTaskCollector(new WpfUiScheduler(() => TestUiDispatcherHost.Dispatcher));
        using var companionRelease = new ManualResetEventSlim(false);
        using var searchRelease = new ManualResetEventSlim(false);
        var companionReached = new TaskCompletionSource<(bool OnUi, bool LocksReleased)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var companionConsumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var searchReached = new TaskCompletionSource<(bool OnUi, bool LocksReleased, CatalogCommitReceipt Receipt)>(TaskCreationOptions.RunContinuationsAsynchronously);
        int hold = 0;
        int companionClaimed = 0;
        int searchClaimed = 0;
        string? addedPath = null;
        PropertyChangedEventHandler publication = (sender, args) =>
        {
            if (sender is not BMSLibrary library || Volatile.Read(ref hold) == 0) { return; }
            if (args.PropertyName == nameof(BMSLibrary.DuplicateChartGroupsInvalidationVersion)
                && Interlocked.CompareExchange(ref companionClaimed, 1, 0) == 0)
            {
                bool companionOnUi = TestUiDispatcherHost.Dispatcher.CheckAccess();
                companionReached.TrySetResult((companionOnUi, !library.IsWriteLockHeldInitializeMin
                    && !library.IsWriteLockHeldInitializeBMSFiles && !library.IsWriteLockHeldDuplicateChartGroups));
                if (companionOnUi) { companionRelease.Set(); } else { companionRelease.Wait(); }
                return;
            }
            if (args.PropertyName != nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion) || addedPath == null
                || library.DuplicateChartGroups?.SelectMany(group => group.ChartFiles).Any(chart => chart.Path == addedPath) != true) { return; }
            CatalogCommitReceipt? receipt = library.GetNormalLibraryRefreshNotificationsAfter(0).CatalogCommits.FirstOrDefault(commit =>
                commit.Changes.Any(change => change.After?.Path == addedPath
                    && change.After.Warnings.Any(warning => warning.Kind == ChartWarningKind.DuplicateChart)));
            if (receipt == null || Interlocked.CompareExchange(ref searchClaimed, 1, 0) != 0) { return; }
            bool onUi = TestUiDispatcherHost.Dispatcher.CheckAccess();
            searchReached.TrySetResult((onUi, !library.IsWriteLockHeldInitializeMin
                && !library.IsWriteLockHeldInitializeBMSFiles && !library.IsWriteLockHeldDuplicateChartGroups, receipt));
            if (onUi) { searchRelease.Set(); } else { searchRelease.Wait(); }
        };
        RunColdDuplicateFixture(fixture =>
        {
            MainWindowViewModel vm = fixture.ViewModel;
            string root = Path.GetDirectoryName(fixture.Target.Directory) ?? throw new AssertFailedException();
            ChartFile Extra(string name, string hash, string title, int mode)
            {
                string folder = Path.Combine(root, name);
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, "chart.bms");
                File.WriteAllText(path, "#PLAYER 1\n#TITLE " + title + "\n#BPM 120\n");
                return CreateTestableBmsFile(path) with { Md5 = hash, Title = title, RawTitle = title, Mode = mode };
            }
            ChartFile[] initial = [fixture.AllCharts[0] with { Title = "Keep A", RawTitle = "Keep A", Mode = 7 },
                fixture.AllCharts[1] with { Title = "Keep B", RawTitle = "Keep B", Mode = 7 },
                fixture.AllCharts[2] with { Title = "Other A", RawTitle = "Other A", Mode = 14 },
                Extra("other-b", fixture.Target.Md5, "Other B", 14)];
            ChartFile added = Extra("keep-added", fixture.Duplicates[0].Md5, "Keep C", 7);
            new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), Path.Combine(root, "song.db")).UpsertSongs([.. initial, added]);
            fixture.Library.BmsCharts = initial;
            TestUiDispatcherHost.AwaitTaskOnDispatcher(vm.RegularChartList.DeliverPendingLibraryChangesAsync(fixture.Library, CancellationToken.None), "filter fixture initial source");
            TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.NavigateDuplicate(MainViewUpdateMode.DuplicateFilterSelected, null), "filter fixture actual root");
            fixture.CompleteDuplicate();
            // 先行する表示とUI配送を回収し、キャッシュ失効後の条件変更を確認する。
            TestUiDispatcherHost.AwaitTaskOnDispatcher(scheduler.InvokeAsync(() => { }), "prior filter fixture UI delivery");
            TestUiDispatcherHost.AwaitTaskOnDispatcher(scheduler.DrainPriorAsync(), "prior filter fixture Func terminals");
            CollectionAssert.AreEquivalent(initial.Select(chart => chart.Path).ToArray(), vm.MainChartList.Rows.Cast<LibraryChartRow>().Select(row => row.path).ToArray());
            string[] firstPaths = [.. initial.Where(chart => chart.Mode == 7).Select(chart => chart.Path), added.Path];
            string[] latestPaths = initial.Where(chart => chart.Mode == 14).Select(chart => chart.Path).ToArray();
            var sortStarted = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<RegularChartSortRefreshRequestedEventArgs> sortChanged = (_, args) => sortStarted.TrySetResult(args.Presentation);
            vm.RegularChartList.SortRefreshRequested += sortChanged;
            var emptyApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var latestApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var trueEmptyApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sourceApplied = new TaskCompletionSource<NormalLibraryRefreshNotificationBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
            var groupsApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var violations = new List<string>();
            int phase = 0;
            PropertyChangedEventHandler rowsChanged = (_, args) =>
            {
                if (args.PropertyName != nameof(MainChartListViewModel.Rows)) { return; }
                string[] paths = vm.MainChartList.Rows.Cast<LibraryChartRow>().Select(row => row.path).ToArray();
                if (phase is 1 or 2 && paths.Length == 0) { emptyApplied.TrySetResult(); }
                if (phase == 2 && paths.Order(StringComparer.Ordinal).SequenceEqual(firstPaths.Order(StringComparer.Ordinal)))
                { violations.Add("F2入力後に古いF1集合を採用しました。"); }
                if (phase == 2 && paths.Order(StringComparer.Ordinal).SequenceEqual(latestPaths.Order(StringComparer.Ordinal))) { latestApplied.TrySetResult(); }
                if (phase == 3 && paths.Length == 0) { trueEmptyApplied.TrySetResult(); }
            };
            EventHandler<NormalLibraryRefreshAppliedEventArgs> applied = (_, args) =>
            { if (args.NotificationBatch.HasEffect(LibraryChartRefreshEffects.SourceChanged)) { sourceApplied.TrySetResult(args.NotificationBatch); } };
            PropertyChangedEventHandler delivered = (_, args) =>
            { if (args.PropertyName == nameof(BMSLibrary.DuplicateChartGroupsInvalidationVersion) && Volatile.Read(ref hold) != 0) { companionConsumed.TrySetResult(); } };
            PropertyChangedEventHandler groups = (_, args) =>
            {
                if (args.PropertyName == nameof(MaintenanceTreeViewModel.DuplicateChartGroups)
                    && vm.MaintenanceTree.DuplicateChartGroups?.SelectMany(group => group.ChartFiles).Any(chart => chart.Path == added.Path) == true) { groupsApplied.TrySetResult(); }
            };
            vm.MainChartList.PropertyChanged += rowsChanged;
            vm.RegularChartList.NormalLibraryRefreshApplied += applied;
            fixture.Library.PropertyChanged += delivered;
            vm.MaintenanceTree.PropertyChanged += groups;
            var actions = new List<Task>();
            var coordinator = Task.Run(async () =>
            {
                bool failed = false;
                Task? data = null;
                try
                {
                    Volatile.Write(ref addedPath, added.Path); Volatile.Write(ref hold, 1);
                    Task replacement = scheduler.InvokeAsync(() => fixture.Library.BmsCharts = [.. fixture.Library.BmsCharts, added]); actions.Add(replacement); await replacement;
                    (bool OnUi, bool LocksReleased) companion = await companionReached.Task;
                    Assert.IsFalse(companion.OnUi); Assert.IsTrue(companion.LocksReleased);
                    Task source = vm.RegularChartList.DeliverPendingLibraryChangesAsync(fixture.Library, CancellationToken.None); actions.Add(source); await source;
                    Assert.IsTrue((await sourceApplied.Task).ResetsPriorNotifications);
                    await scheduler.InvokeAsync(() => { }); await scheduler.DrainPriorAsync();
                    Assert.IsNull(fixture.Library.DuplicateChartGroups);
                    Task explicitAnalysis = fixture.NavigateDuplicate(MainViewUpdateMode.DuplicateFilterSelected, null);
                    actions.Add(explicitAnalysis);
                    Task setter1 = scheduler.InvokeAsync(() =>
                    {
                        phase = 1;
                        if (modeFilter) { vm.ChartFilters.ModeFilter = ChartModeFilter._7KEYS; }
                        else { vm.ChartFilters.KeywordFilter = "Keep"; }
                    }); actions.Add(setter1); await setter1;
                    await Task.WhenAny(emptyApplied.Task, searchReached.Task);
                    Assert.IsFalse(emptyApplied.Task.IsCompleted, "F1の未準備nullを完成emptyへ採用しません。");
                    (bool OnUi, bool LocksReleased, CatalogCommitReceipt Receipt) search = await searchReached.Task;
                    Assert.IsFalse(search.OnUi); Assert.IsTrue(search.LocksReleased);
                    data = fixture.CaptureData();
                    Assert.IsFalse(fixture.CaptureData().IsCompleted);
                    Assert.IsFalse(data.IsCompleted);
                    Assert.IsTrue(search.Receipt.Changes.Any(change => change.After?.Path == added.Path));
                    Task marker = scheduler.InvokeAsync(() => Assert.IsTrue(TestUiDispatcherHost.Dispatcher.CheckAccess())); actions.Add(marker); await marker;
                    Assert.IsFalse(searchRelease.IsSet);
                    Task setter2 = scheduler.InvokeAsync(() =>
                    {
                        if (!modeFilter) { vm.MainChartList.RequestSort(nameof(LibraryChartRow.Title), ListSortDirection.Descending); }
                        phase = 2;
                        if (modeFilter) { vm.ChartFilters.ModeFilter = ChartModeFilter._14KEYS; }
                        else { vm.ChartFilters.KeywordFilter = "Other"; }
                    }); actions.Add(setter2); await setter2;
                    if (!modeFilter)
                    {
                        Task sortPresentation = await sortStarted.Task;
                        actions.Add(sortPresentation);
                        await sortPresentation;
                    }
                    Assert.IsFalse(emptyApplied.Task.IsCompleted);
                    searchRelease.Set(); await data;
                    Task ready = fixture.AwaitReadyAsync(); actions.Add(ready); await ready;
                    Assert.IsTrue(latestApplied.Task.IsCompletedSuccessfully, "dataとは別のready実PresentationがF2を採用します。");
                    await scheduler.InvokeAsync(() =>
                    {
                        Assert.AreEqual(0, violations.Count, string.Join(Environment.NewLine, violations));
                        string[] paths = vm.MainChartList.Rows.Cast<LibraryChartRow>().Select(row => row.path).ToArray();
                        if (modeFilter) { CollectionAssert.AreEquivalent(latestPaths, paths); Assert.AreEqual(ChartModeFilter._14KEYS, vm.ChartFilters.ModeFilter); }
                        else { CollectionAssert.AreEqual(latestPaths.Reverse().ToArray(), paths); Assert.AreEqual("Other", vm.ChartFilters.KeywordFilter); }
                        CollectionAssert.AreEquivalent(initial.Select(chart => chart.Path).Append(added.Path).ToArray(),
                            fixture.Library.DuplicateChartGroups.SelectMany(group => group.ChartFiles).Select(chart => chart.Path).ToArray());
                    });
                    await groupsApplied.Task;
                    Task delivery = vm.RegularChartList.DeliverPendingLibraryChangesAsync(fixture.Library, CancellationToken.None); actions.Add(delivery); await delivery;
                    Assert.IsFalse(emptyApplied.Task.IsCompleted);
                    await scheduler.DrainPriorAsync();
                    Task<Task> emptyPresentation = scheduler.ObserveNext();
                    Task setter3 = scheduler.InvokeAsync(() =>
                    {
                        phase = 3;
                        if (modeFilter) { vm.ChartFilters.ModeFilter = ChartModeFilter._9KEYS; }
                        else { vm.ChartFilters.KeywordFilter = "absent-filter-result"; }
                    }); actions.Add(setter3); await setter3;
                    Task actualEmpty = await emptyPresentation; actions.Add(actualEmpty); await actualEmpty;
                    await scheduler.DrainPriorAsync();
                    Assert.IsTrue(trueEmptyApplied.Task.IsCompletedSuccessfully);
                    await scheduler.InvokeAsync(() => Assert.AreEqual(0, vm.MainChartList.Rows.Count));
                    companionRelease.Set(); await companionConsumed.Task;
                }
                catch { failed = true; throw; }
                finally
                {
                    searchRelease.Set(); companionRelease.Set();
                    Task stop = failed ? vm.RegularChartList.StopAsync() : Task.CompletedTask;
                    try { await Task.WhenAll(actions.Append(data ?? Task.CompletedTask).Append(stop)); await scheduler.DrainPriorAsync(); }
                    catch when (failed) { /* 元assert/実Task faultを保持する。 */ }
                    finally { try { await vm.RegularChartList.StopAsync(); } catch when (failed) { } }
                }
            });
            try { TestUiDispatcherHost.AwaitTaskOnDispatcher(coordinator, "normal filter invalidation F1 F2 F3"); }
            finally
            {
                searchRelease.Set(); companionRelease.Set();
                try { TestUiDispatcherHost.AwaitTaskOnDispatcher(coordinator, "normal filter coordinator cleanup"); }
                finally
                {
                    vm.RegularChartList.SortRefreshRequested -= sortChanged;
                    vm.MainChartList.PropertyChanged -= rowsChanged;
                    vm.RegularChartList.NormalLibraryRefreshApplied -= applied;
                    fixture.Library.PropertyChanged -= delivered;
                    vm.MaintenanceTree.PropertyChanged -= groups;
                }
            }
        }, publication, scheduler);
    }

    /// <summary>指定した条件のUI配送を一度保留し、開始した配送Taskとその失敗を呼出元へ返します。</summary>
    private sealed class DuplicateContinuationUiGate(IUiScheduler scheduler) : IUiScheduler
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object sync = new();
        private readonly TaskCompletionSource<Task> heldOperation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<Task> HeldOperation => heldOperation.Task;
        private bool held;
        internal Func<bool>? ShouldHold { get; set; }
        internal void Release() => release.TrySetResult();
        public bool IsAvailable => scheduler.IsAvailable;
        public bool CanExecuteInline => scheduler.CanExecuteInline;
        public bool CheckAccess() => scheduler.CheckAccess();
        public IUiScheduledOperation Schedule(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal) => scheduler.Schedule(action, priority);
        public void Invoke(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal) => scheduler.Invoke(action, priority);
        public T Invoke<T>(Func<T> action, UiSchedulePriority priority = UiSchedulePriority.Normal) => scheduler.Invoke(action, priority);
        public Task InvokeAsync(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal) => scheduler.InvokeAsync(action, priority);
        public Task InvokeAsync(Func<Task> action, UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            bool hold;
            lock (sync)
            {
                hold = !held && ShouldHold?.Invoke() == true;
                if (hold) { held = true; }
            }
            if (!hold) { return scheduler.InvokeAsync(action, priority); }
            Task forwarding = ForwardAsync();
            heldOperation.TrySetResult(forwarding);
            return forwarding;
            async Task ForwardAsync()
            {
                await release.Task.ConfigureAwait(false);
                await scheduler.InvokeAsync(action, priority).ConfigureAwait(false);
            }
        }
    }

    /// <summary>テストの進行とTask状態を標準の結果ディレクトリへ逐次記録し、テスト完了時に閉じます。記録処理の失敗はテスト本体の失敗と区別します。</summary>
    private sealed class PendingTargetPhaseLog
    {
        private readonly object gate = new();
        private readonly TestContext context;
        private readonly StreamWriter writer;
        private readonly string path;
        private readonly List<Exception> failures = [];
        internal PendingTargetPhaseLog(TestContext context)
        {
            this.context = context;
            // 配置ディレクトリの名前から逆算せず、MTPが提供する結果保存先へ逐次記録し、終了時に添付する。
            string? resultsDirectory = context.ResultsDirectory;
            if (string.IsNullOrWhiteSpace(resultsDirectory) || !Path.IsPathFullyQualified(resultsDirectory))
            { throw new AssertFailedException("標準test結果directoryを取得できません。"); }
            string directory = Path.GetFullPath(resultsDirectory);
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, "duplicate-navigation-order-accepted-target-" + Guid.NewGuid().ToString("N") + ".phase.log");
            writer = new StreamWriter(path, append: false, encoding: new System.Text.UTF8Encoding(false)) { AutoFlush = true };
            try
            {
                context.WriteLine("accepted-target phase file: " + Path.GetFileName(path));
            }
            catch (Exception primary)
            {
                try { writer.Dispose(); }
                catch (Exception secondary) { throw new AggregateException(primary, secondary); }
                throw;
            }
        }
        internal void TaskState(string phase, Task task) => Write(phase, "task=" + task.Id + " status=" + task.Status
            + (task.Exception == null ? string.Empty : " fault=" + string.Join(",", task.Exception.InnerExceptions.Select(exception => exception.GetType().Name))));
        internal void Write(string phase, string detail = "")
        {
            // 例外に含まれ得る一時データの絶対パスを複写せず、失敗の種類を記録する。
            if (phase.EndsWith("-failed", StringComparison.Ordinal) || phase == "coordinator-reobservation")
            {
                int separator = detail.IndexOf(':');
                detail = separator < 0 ? "fault" : detail[..separator];
            }
            lock (gate)
            {
                try { writer.WriteLine(DateTime.UtcNow.ToString("O") + " " + phase + " " + detail); }
                catch (Exception exception)
                {
                    failures.Add(exception);
                    try { context.WriteLine("phase diagnostic failed: " + exception.GetType().Name); } catch (Exception secondary) { failures.Add(secondary); }
                }
            }
        }
        internal void Complete(Exception? primaryFailure)
        {
            lock (gate)
            {
                try { writer.Dispose(); }
                catch (Exception exception) { failures.Add(exception); }
                // AddResultFileはパスを登録するため、出力を閉じた後で登録する。GetResultFilesは取得した一覧を消費する。
                try { context.AddResultFile(path); }
                catch (Exception exception) { failures.Add(exception); }
                try
                {
                    // 添付ファイルに加え、正常終了と失敗終了の記録本文をTRXにも保存する。
                    context.WriteLine("accepted-target phase begin: " + Path.GetFileName(path));
                    foreach (string line in File.ReadLines(path)) { context.WriteLine(line); }
                    context.WriteLine("accepted-target phase end");
                }
                catch (Exception exception) { failures.Add(exception); }
                if (failures.Count == 0) { return; }
                try { context.WriteLine("phase diagnostic faults: " + string.Join(",", failures.Select(exception => exception.GetType().Name))); }
                catch (Exception exception) { failures.Add(exception); }
                if (primaryFailure == null) { throw new AggregateException("phase診断の保存に失敗しました。", failures); }
            }
        }
    }

    /// <summary>後発の導入選択、またはデータ完成通知のUI配送を一回保留します。再開後は元の配送処理と開始済みTaskを完了まで回収します。</summary>
    private sealed class PendingInstallTargetScheduler(ProjectionTaskCollector scheduler) : IUiScheduler
    {
        private readonly object gate = new();
        private readonly List<Task> related = [];
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool armed;
        private bool readyArmed;
        private bool recording;
        internal TaskCompletionSource<Task> Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Arm() { lock (gate) { armed = true; recording = true; } }
        internal void ArmReady() { lock (gate) { readyArmed = true; recording = true; } }
        internal void Release() => release.TrySetResult();
        private void Record(Task task) { lock (gate) { if (recording) { related.Add(task); } } }
        internal async Task DrainRelatedAsync(Action<string, Task>? report = null)
        {
            while (true)
            {
                Task[] pending;
                lock (gate) { pending = [.. related]; related.Clear(); }
                var actions = Task.WhenAll(pending);
                report?.Invoke("related-ui-await", actions);
                await actions;
                report?.Invoke("related-ui-done", actions);
                await scheduler.DrainPriorAsync(report);
                Task marker = scheduler.InvokeAsync(() => { });
                report?.Invoke("related-marker-await", marker);
                await marker;
                report?.Invoke("related-marker-done", marker);
                lock (gate) { if (related.Count == 0 && !scheduler.HasPrior) { return; } }
            }
        }
        public bool IsAvailable => scheduler.IsAvailable;
        public bool CanExecuteInline => scheduler.CanExecuteInline;
        public bool CheckAccess() => scheduler.CheckAccess();
        public IUiScheduledOperation Schedule(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
        { IUiScheduledOperation operation = scheduler.Schedule(action, priority); Record(operation.Completion); return operation; }
        public void Invoke(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal) => scheduler.Invoke(action, priority);
        public T Invoke<T>(Func<T> action, UiSchedulePriority priority = UiSchedulePriority.Normal) => scheduler.Invoke(action, priority);
        public Task InvokeAsync(Func<Task> action, UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            bool hold;
            lock (gate) { hold = readyArmed; readyArmed = false; }
            if (!hold) { return scheduler.InvokeAsync(action, priority); }
            Task task = ForwardHeldAsync();
            Held.TrySetResult(task);
            return task;
            async Task ForwardHeldAsync()
            {
                await release.Task.ConfigureAwait(false);
                await scheduler.InvokeAsync(action, priority).ConfigureAwait(false);
            }
        }
        public Task InvokeAsync(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            bool hold;
            lock (gate) { hold = armed; armed = false; }
            if (hold)
            {
                Task task = ForwardHeldAsync(); Held.TrySetResult(task); return task;
            }
            Task forwarded = scheduler.InvokeAsync(action, priority); Record(forwarded); return forwarded;
            async Task ForwardHeldAsync() { await release.Task.ConfigureAwait(false); await scheduler.InvokeAsync(action, priority).ConfigureAwait(false); }
        }
    }

}
