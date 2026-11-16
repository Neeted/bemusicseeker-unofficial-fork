using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using static BeMusicSeeker.TestSupport.Components.RegularChartListOwnerTestSupport;

namespace BeMusicSeeker.Tests;


/// <summary>通常一覧の終端公開、summaryの世代判定、予備計算と停止・取消をWindowなしで検証します。</summary>
/// <remarks>gateは実workerの走査やUI公開を止めるために使います。sort値の網羅はLibraryChartRowSortEngineTests、compiled画面接続はMainWindow側が分担します。</remarks>
[TestClass]
public sealed class RegularChartCommitAndLifecycleTests
{


    /// <summary>表示開始の同期例外・非同期失敗・取消を一覧所有者へ渡し、失敗だけを一度通知し、通知の完了まで停止が待つことを確認します。実画面との接続は初回重複の代表ケースが分担します。</summary>
    [TestMethod]
    [DataRow("synchronous")]
    [DataRow("asynchronous")]
    [DataRow("canceled")]
    public async Task DuplicatePresentation_FailureNotifiesOnceAndStopWaitsForNotification(string condition)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<UiDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialogs = new FileDbReportRecordingDialogs { MessageHandler = _ => { reached.TrySetResult(); return release.Task; } };
        using RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), CreateWorkspaceForOwner(), action => action(),
            normalLibraryRefreshUiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher), mutationDialogs: dialogs);
        bool startedOnUi = false;
        var rows = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("duplicate rows failed");
        owner.PresentDuplicateData(() =>
        {
            startedOnUi = TestUiDispatcherHost.Dispatcher.CheckAccess();
            return condition == "synchronous" ? throw failure : rows.Task;
        }, dialogs);
        Task stop = owner.StopAsync();
        try
        {
            if (condition == "canceled")
            {
                rows.SetCanceled();
                await stop;
                Assert.AreEqual(0, dialogs.Messages.Count);
            }
            else
            {
                if (condition == "asynchronous") { rows.SetException(failure); }
                await TestUiDispatcherHost.AwaitNotificationAsync(reached.Task, stop, "display failure notification before Stop terminal");
                Assert.IsFalse(stop.IsCompleted);
                Assert.AreEqual(1, dialogs.Messages.Count);
                Assert.AreEqual(BeMusicSeeker.Properties.Resources.Msg_error_unexpected, dialogs.Messages.Single().MessageBoxText);
                release.SetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
                await stop;
                Assert.AreEqual(1, dialogs.Messages.Count);
            }
            Assert.IsTrue(startedOnUi, "所有者の登録後継続のスレッドによらず、表示開始をUIへ戻します。");
        }
        finally
        {
            rows.TrySetCanceled();
            release.TrySetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
            await stop;
        }
    }

    /// <summary>通常一覧の並べ替えを構築中に停止または別の通常画面移動を行い、開始済みTaskの回収と後発選択の優先を確認します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RegularSort_BlockedOrderIsOwnedByStopAndCannotOverwriteLaterNavigation(bool stopWhileBlocked)
    {
        var databaseScope = new ApplicationDatabaseScope();
        TestUiDispatcherHost.Invoke(() => WithTemporarySongDb(songDb =>
        {
            string directory = System.IO.Path.GetDirectoryName(songDb) ?? throw new AssertFailedException();
            var library = new TestBmsLibrary(databaseScope, songDb)
            {
                BmsCharts = [CreateTestableBmsFile(System.IO.Path.Combine(directory, "alpha.bms")) with { Title = "Alpha", Artist = "Z" },
                    CreateTestableBmsFile(System.IO.Path.Combine(directory, "beta.bms")) with { Title = "Beta", Artist = "M" },
                    CreateTestableBmsFile(System.IO.Path.Combine(directory, "zeta.bms")) with { Title = "Zeta", Artist = "A" }]
            };
            var table = new MainChartListViewModel();
            using RegularChartListOwner owner = CreateOwner(table, CreateWorkspaceForOwner(table),
                action => TestUiDispatcherHost.Dispatcher.Invoke(action), new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher));
            owner.AttachNormalLibraryRefreshSource(library);
            TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.DeliverPendingLibraryChangesAsync(library, CancellationToken.None), "sort fixture initial delivery");
            System.Collections.IList initialRows = table.Rows;
            using var release = new ManualResetEventSlim(false);
            var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sortStarted = new TaskCompletionSource<RegularChartSortRefreshRequestedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var laterStarted = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            int claimed = 0;
            table.RowProjection.NormalInputWorkObserver = (kind, _) =>
            {
                if (kind != "order_buffer" || Interlocked.CompareExchange(ref claimed, 1, 0) != 0) { return; }
                bool onUi = TestUiDispatcherHost.Dispatcher.CheckAccess();
                reached.TrySetResult(onUi);
                if (onUi) { release.Set(); return; }
                release.Wait();
            };
            Task<RegularChartListEntryResult> Present(RegularChartNavigationRequest navigation, string keyword)
                => owner.ApplyMainLibraryViewAsync(new ChartListRefreshRoute(ChartListRefreshRouteKind.ContinueMainLibrary,
                    MainViewUpdateMode.SortUpdated, MainViewUpdateMode.SortUpdated, MainViewUpdateMode.FolderFilterSelected,
                    isPlaylistTreeActive: false, includeBmsonRows: true), library, null, null, false, Stopwatch.StartNew(),
                    new ChartListFilterSnapshot(keyword, ChartModeFilter.All), navigation);
            owner.SortRefreshRequested += (_, request) =>
            {
                request.Presentation = Present(request.Navigation, string.Empty);
                sortStarted.TrySetResult(request);
            };
            owner.TreeNavigationPresentationRequested += (_, request) =>
            {
                request.Presentation = Present(request.Navigation, "Beta");
                laterStarted.TrySetResult(request.Presentation);
            };
            var coordinator = Task.Run(async () =>
            {
                Task stop = Task.CompletedTask;
                Task later = Task.CompletedTask;
                RegularChartSortRefreshRequestedEventArgs? sort = null;
                var scheduler = new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher);
                try
                {
                    await scheduler.InvokeAsync(() => owner.QueueSort(new MainChartListSortRequestedEventArgs(
                        nameof(LibraryChartRow.Artist), ListSortDirection.Ascending, MainChartListSortTarget.Regular)));
                    sort = await sortStarted.Task;
                    await TestUiDispatcherHost.AwaitNotificationAsync(reached.Task, sort.Presentation, "normal sort order gate");
                    bool onUi = await reached.Task;
                    Assert.IsFalse(onUi, "order bufferはUI外で構築します。");
                    Assert.IsFalse(sort.Presentation.IsCompleted);
                    if (stopWhileBlocked)
                    {
                        stop = owner.StopAsync();
                        Assert.IsFalse(stop.IsCompleted, "sortの実workerを先に待たず、Stopの所有を確認します。");
                    }
                    else
                    {
                        await scheduler.InvokeAsync(() => Assert.IsTrue(owner.NavigateTree(null)));
                        later = await laterStarted.Task;
                        await later;
                        await scheduler.InvokeAsync(() => CollectionAssert.AreEqual(new[] { "Beta" }, table.Rows.Cast<LibraryChartRow>().Select(row => row.Title).ToArray()));
                    }
                    Assert.IsFalse(release.IsSet);
                    release.Set();
                    if (stopWhileBlocked)
                    {
                        await stop;
                        Assert.IsTrue(sort.Presentation.IsCompletedSuccessfully, "Stop先行awaitで同じsort表示Taskも終端します。");
                    }
                    await sort.Presentation;
                    await scheduler.InvokeAsync(() =>
                    {
                        if (stopWhileBlocked) { Assert.AreSame(initialRows, table.Rows); }
                        else { CollectionAssert.AreEqual(new[] { "Beta" }, table.Rows.Cast<LibraryChartRow>().Select(row => row.Title).ToArray()); }
                    });
                }
                finally
                {
                    release.Set();
                    try { await Task.WhenAll(stop, later, sort?.Presentation ?? Task.CompletedTask); }
                    finally { table.RowProjection.NormalInputWorkObserver = null; await owner.StopAsync(); }
                }
            });
            try { TestUiDispatcherHost.AwaitTaskOnDispatcher(coordinator, "normal sort lifecycle"); }
            finally
            {
                release.Set();
                try { TestUiDispatcherHost.AwaitTaskOnDispatcher(coordinator, "normal sort lifecycle cleanup"); }
                finally { library.RequestShutdown("normal_sort_lifecycle"); }
            }
        }));
    }

    /// <summary>初回解析を保留して停止を開始し、停止後の依存更新は処理を実行せず未受理となり、明示した画面移動には破棄済み例外を返すことを確認します。停止完了時には開始済み解析、キャッシュ更新、確定通知が完了していることも確認します。</summary>
    [TestMethod]
    public void StopAsync_WaitsForAcceptedDuplicateProducerBeforeItsOwnerNotification()
    {
        var databaseScope = new ApplicationDatabaseScope();
        TestUiDispatcherHost.Invoke(() => WithTemporarySongDb(songDb =>
        {
            var library = new TestBmsLibrary(databaseScope, songDb)
            {
                BmsCharts = [CreateTestableBmsFile(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(songDb) ?? string.Empty, "a", "a.bms")),
                    CreateTestableBmsFile(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(songDb) ?? string.Empty, "b", "b.bms"))]
            };
            using var release = new ManualResetEventSlim(false);
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int enabled = 0;
            int claimed = 0;
            int notifications = 0;
            int initialVersion = 0;
            PropertyChangedEventHandler producerNotification = (_, args) =>
            {
                if (args.PropertyName != nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion) || Volatile.Read(ref enabled) == 0
                    || library.NormalLibraryRefreshNotificationVersion <= initialVersion) { return; }
                if (Interlocked.Exchange(ref claimed, 1) == 0)
                {
                    reached.TrySetResult();
                    release.Wait();
                }
                Interlocked.Increment(ref notifications);
            };
            // 停止後の解析完了を保留し、停止Taskだけを待っても解析と通知が回収されることを確認する。
            library.PropertyChanged += producerNotification;
            var table = new MainChartListViewModel();
            using RegularChartListOwner owner = CreateOwner(table, CreateWorkspaceForOwner(table));
            Task<bool>? navigation = null;
            Task? stop = null;
            Task? data = null;
            Task? lateDependency = null;
            Task? lateData = null;
            try
            {
                owner.AttachNormalLibraryRefreshSource(library);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.DeliverPendingLibraryChangesAsync(library, CancellationToken.None), "initial stop fixture delivery");
                initialVersion = library.NormalLibraryRefreshNotificationVersion;
                owner.MaintenanceNavigationPresentationRequested += (_, _) => { };
                Volatile.Write(ref enabled, 1);
                navigation = owner.NavigateMaintenanceAsync(MainViewUpdateMode.DuplicateFilterSelected);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(reached.Task, navigation,
                    "duplicate stop producer gate"), "duplicate stop gate");
                data = owner.PrepareDuplicateDataAsync("stop_fixture_join");
                stop = owner.StopAsync();
                Assert.IsFalse(navigation.IsCompleted);
                Assert.IsFalse(stop.IsCompleted, "producerの通知前でも開始済みnavigationをStopが所有します。");
                int refreshCalls = 0;
                lateDependency = owner.RefreshCurrentViewAsync(null, () =>
                {
                    Interlocked.Increment(ref refreshCalls);
                    return Task.CompletedTask;
                });
                TestUiDispatcherHost.AwaitTaskOnDispatcher(lateDependency, "stopped dependency request terminal");
                Assert.IsTrue(lateDependency.IsCompletedSuccessfully);
                lateData = owner.PrepareDuplicateDataAsync("stopped_dependency");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(lateData, "stopped data dependency terminal");
                Assert.IsTrue(lateData.IsCompletedSuccessfully);
                Assert.IsFalse(data.IsCompleted);
                Assert.AreEqual(0, Volatile.Read(ref refreshCalls));
                Assert.ThrowsExactly<ObjectDisposedException>(() =>
                {
                    _ = owner.NavigateMaintenanceAsync(MainViewUpdateMode.DuplicateFilterSelected);
                });
                release.Set();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(stop, "stop before awaiting duplicate navigation");
                Assert.IsTrue(navigation.IsCompletedSuccessfully, "Stopを先に待った時点で外部へ返した同じnavigation Taskが終端しています。");
                Assert.IsTrue(navigation.Result);
                Assert.IsTrue(data.IsCompletedSuccessfully);
                Assert.IsNotNull(library.DuplicateChartGroups);
                Assert.AreEqual(1, library.DuplicateChartGroups.Count);
                Assert.AreEqual(2, library.DuplicateChartGroups[0].ChartFiles.Count);
                Assert.AreEqual(1, Volatile.Read(ref notifications));
                Assert.IsTrue(library.GetNormalLibraryRefreshNotificationsAfter(initialVersion).CatalogCommits.Any(),
                    "表示採用は停止しても警告の確定通知を生産します。");
            }
            finally
            {
                release.Set();
                if (stop != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(stop, "duplicate stop cleanup"); }
                if (navigation != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(navigation, "duplicate navigation cleanup"); }
                if (data != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(data, "accepted data cleanup"); }
                if (lateData != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(lateData, "stopped data cleanup"); }
                if (lateDependency != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(lateDependency, "stopped dependency cleanup"); }
                TestUiDispatcherHost.AwaitTaskOnDispatcher(owner.StopAsync(), "duplicate owner cleanup");
                library.PropertyChanged -= producerNotification;
                library.RequestShutdown("duplicate_stop_fixture");
            }
        }));
    }

}
