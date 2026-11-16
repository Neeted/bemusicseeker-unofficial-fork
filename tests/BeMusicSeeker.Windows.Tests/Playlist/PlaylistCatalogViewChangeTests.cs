using System.ComponentModel;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.PlaylistCatalogViewTestSupport;

namespace BeMusicSeeker.Tests;


/// <summary>実譜面・SQLite・保守確定・単一配送から実 playlist source/view までを検証します。候補条件の網羅は model 系列へ分担します。</summary>
[TestClass]
[TestCategory("Windows.Shell")]
public sealed class PlaylistCatalogViewChangeTests
{
    public TestContext? TestContext { get; set; }

    /// <summary>worker準備後の未配送header確定をUI採用前に見つけ、古い候補を公開せず実drain後に参照値と適用版を揃えます。</summary>
    [TestMethod]
    public async Task ReferenceCommit_AfterWorkerPreparationWaitsForDeliveryBeforeUiAdoption()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int hold = 1;
        Dispatcher dispatcher = TestUiDispatcherHost.Dispatcher;
        await using var fixture = new Fixture(16, regularDelivery: true, entryDeliveryScheduler: async action =>
        {
            if (Interlocked.Exchange(ref hold, 0) == 1)
            {
                entered.TrySetResult();
                await release.Task;
            }
            await dispatcher.InvokeAsync(action).Task;
        });
        await fixture.DrainLibraryChanges();
        Task refresh = fixture.Refresh();
        ScheduledScoreOperation? delivery = null;
        try
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, refresh, "reference-prepared-ui-candidate");
            BMSTable table = fixture.Playlist.BMSTables.Single();
            using (LibraryFileMutationLease lease = fixture.Playlist.AcquirePlaylistMutationLease("reference_after_worker_preparation"))
            using (LibraryFileMutationCapability capability = lease.CreateMutationCapability())
            {
                fixture.Playlist.CommitPlaylistHeaderEdits(new Dictionary<BMSTable, PlaylistHeaderEdit>
                { [table] = new() { Fields = PlaylistHeaderFields.Symbol, Symbol = "AFTER" } }, capability);
            }
            long required = fixture.Library.PlaylistReferenceRevision;
            release.TrySetResult();
            await TestUiDispatcherHost.AwaitNotificationAsync(fixture.DeliveryRequested.Task, refresh, "reference-prepared-candidate-delivery");
            delivery = await fixture.DeliveryScheduler.NextAsync();
            Assert.IsFalse(refresh.IsCompleted);
            Assert.AreEqual(0, fixture.MainTable.Rows.Count, "未配送の新参照版を持つ旧候補は採用しません。");
            delivery.Run(); delivery.Complete();
            await refresh;
            Assert.AreEqual(16, fixture.MainTable.Rows.Count);
            Assert.IsTrue(fixture.MainTable.Rows.Cast<PlaylistDetailRow>().All(row => row.RefTablesSymbols == "AFTER"));
            Assert.AreEqual(required, fixture.Workspace.DetailViewState.Source.Dependencies?.ReferenceInput?.Revision);
        }
        finally
        {
            release.TrySetResult();
            delivery?.Abort();
            while (fixture.DeliveryScheduler.TryTake(out ScheduledScoreOperation? queued)) { queued.Abort(); }
            fixture.Workspace.CancelDetailBuilds();
            await refresh;
        }
    }

    /// <summary>入力捕捉後の実保守を配送前で止め、旧入力へ新版を付けず、配送後に同じ候補へ局所差分だけを反映します。</summary>
    /// <remarks>Windowを作らず共有Dispatcherへ採用を配送し、RowsReplacingのUI所属と、現役sourceへの合法なUI配送を候補のworker追随から分けます。</remarks>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BuildingCandidate_WaitsForCommitDeliveryAndResumesWithoutRebuild(bool deliverBeforeAdoption)
    {
        System.Windows.Threading.Dispatcher dispatcher = TestUiDispatcherHost.Dispatcher;
        await using var fixture = new Fixture(16, entryDeliveryScheduler: action => dispatcher.InvokeAsync(action).Task);
        await fixture.Refresh();
        var oldRows = (PlaylistDetailVirtualView)fixture.Workspace.DetailViewState.View.Rows;
        var oldFirst = (PlaylistDetailRow)oldRows[0];
        fixture.PauseDelivery = true;
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new List<string>();
        bool changed = false;
        EventHandler callback = (_, _) =>
        {
            Assert.IsTrue(dispatcher.CheckAccess(), "採用前callbackは共有UI dispatcher上で実行します。");
            if (changed) { return; }
            changed = true;
            ChartFile target = fixture.Workspace.DetailViewState.Source.Rows[0].OwnedCurrent;
            fixture.WriteFirst("Z changed");
            // 資源保守は同一encodingのメタデータ再読解を要求しません。題名を変える正規入口を使います。
            fixture.Library.SetBMSFilesEncoding([target], "utf-8");
            Assert.AreEqual("Z changed", fixture.Library.CapturePlaylistCatalogReadSnapshot(
                [new PlaylistEntryLookupKey(PlaylistEntryLookupKeyKind.Md5, target.Md5)], default)
                .Resolve(new PlaylistEntryLookupKey(PlaylistEntryLookupKeyKind.Md5, target.Md5)).Title,
                "正規保守の確定値と、未配送候補への反映を分けて検査します。");
            work.Clear();
            if (deliverBeforeAdoption) { fixture.Deliver(); }
            reached.TrySetResult();
        };
        fixture.MainTable.RowsReplacing += callback;
        fixture.Workspace.DetailWorkObserver = work.Add;
        fixture.Workspace.RequestPlaylistDetailSort(nameof(PlaylistDetailRow.Title), ListSortDirection.Descending);
        Task? refresh = null;
        try
        {
            refresh = fixture.Refresh();
            await Task.WhenAny(reached.Task, refresh);
            if (!reached.Task.IsCompleted) { await refresh; Assert.Fail("実候補の採用境界へ到達しませんでした。"); }
            if (!deliverBeforeAdoption)
            {
                Assert.IsFalse(refresh.IsCompleted);
                Assert.AreSame(oldRows, fixture.Workspace.DetailViewState.View.Rows);
                Assert.AreEqual("A target", oldFirst.Title);
                TestUiDispatcherHost.Invoke(fixture.Deliver);
            }
            await refresh;
            if (fixture.Workspace.DetailViewState.View.Rows[0] is not PlaylistDetailRow adopted)
            { throw new AssertFailedException("Playlist detail row was not adopted."); }
            Assert.AreEqual("Z changed", adopted.Title);
            Assert.IsTrue(work.Count(value => value == "playlist_source_projection") <= 2,
                "既存表示と構築候補の依存一行ずつだけを再投影します。");
        }
        finally
        {
            fixture.MainTable.RowsReplacing -= callback;
            TestUiDispatcherHost.Invoke(fixture.Deliver);
            if (refresh != null) { await refresh; }
        }
    }

    /// <summary>初回A投影後/B投影前の実更新をworkerでrange適用し、rootではUI採用待ち中の別key差分も最初のRows公開へ含めます。</summary>
    /// <remarks>folderは初回投影中の差分、rootは準備後のC差分も保証します。Windowなしの共有Dispatcherで、候補投影・scoreコピーのworker所属とRowsReplacing・Rows公開のUI所属を分担します。</remarks>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ScoreRanges_CommittedDuringInitialProjectionAreAppliedBeforeAdoption(bool folder)
    {
        System.Windows.Threading.Dispatcher dispatcher = TestUiDispatcherHost.Dispatcher;
        var adoptionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adoptionRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int holdAdoption = folder ? 0 : 1;
        await using var fixture = new Fixture(16, firstFolder: folder ? "source" : null, regularDelivery: true, oracleScores: true,
            entryDeliveryScheduler: async action =>
            {
                if (Interlocked.Exchange(ref holdAdoption, 0) == 1)
                {
                    adoptionEntered.TrySetResult();
                    await adoptionRelease.Task;
                }
                await dispatcher.InvokeAsync(action).Task;
            });
        await fixture.DrainLibraryChanges();
        if (folder) { fixture.Workspace.RequestDetailSelection(fixture.Playlist.BMSTables.Single(), PlaylistFolderNode.CreateFolder("source")); }
        fixture.Workspace.RequestPlaylistDetailSort(nameof(PlaylistDetailRow.stddevVal), ListSortDirection.Ascending);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        int projected = 0, uiProjection = 0, uiScoreCopies = 0, publishedRows = 0;
        fixture.Workspace.DetailWorkObserver = kind =>
        {
            // 初回候補だけが存在する区間なので、これらの投影は現役sourceの合法なUI配送ではありません。
            if (kind is "playlist_source_projection" or "playlist_detail_dependency_projection" && dispatcher.CheckAccess())
            { Interlocked.Increment(ref uiProjection); }
            if (kind == "playlist_source_projection" && Interlocked.Increment(ref projected) == 2)
            { reached.TrySetResult(); release.Wait(); }
        };
        fixture.Library.ScoreSnapshotWorkObserver = kind =>
        {
            if (kind == "score_projection_value_copied" && dispatcher.CheckAccess()) { Interlocked.Increment(ref uiScoreCopies); }
        };
        EventHandler replacing = (_, _) => Assert.IsTrue(dispatcher.CheckAccess(), "RowsReplacingはUI採用actionに含めます。");
        PropertyChangedEventHandler published = (_, args) =>
        {
            if (args.PropertyName != nameof(MainChartListViewModel.Rows)) { return; }
            Assert.IsTrue(dispatcher.CheckAccess());
            publishedRows++;
            CollectionAssert.AreEqual(folder ? new[] { "entry 1", "entry 2", "entry 0" } : new[] { "entry 2", "entry 1", "entry 0" },
                fixture.MainTable.Rows.Cast<PlaylistDetailRow>().Take(3).Select(row => row.Entry.title).ToArray(),
                "最初のRows公開から、UI採用待ち中に届いた差分を含む最終順序を公開します。");
            Assert.AreEqual(fixture.Library.GetScoreSnapshotForDiagnostics().Version,
                fixture.Workspace.DetailViewState.Source.LastBuiltScoreSnapshotVersion);
        };
        fixture.MainTable.RowsReplacing += replacing;
        fixture.MainTable.PropertyChanged += published;
        Task refresh = fixture.Refresh();
        try
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(reached.Task, refresh, "initial-score-projection");
            fixture.ChangeScore(0, 40); fixture.ChangeScore(1, 5);
            await fixture.DrainLibraryChanges();
            release.Set();
            if (!folder)
            {
                await TestUiDispatcherHost.AwaitNotificationAsync(adoptionEntered.Task, refresh, "prepared-candidate-ui-adoption");
                Assert.AreEqual(0, publishedRows);
                fixture.ChangeScore(2, 1);
                await fixture.DrainLibraryChanges();
                adoptionRelease.TrySetResult();
            }
            await refresh;
            var rows = (PlaylistDetailVirtualView)fixture.Workspace.DetailViewState.View.Rows;
            CollectionAssert.AreEqual(folder ? new[] { "entry 1", "entry 2", "entry 0" } : new[] { "entry 2", "entry 1", "entry 0" },
                rows.Cast<PlaylistDetailRow>().Take(3).Select(row => row.Entry.title).ToArray());
            Assert.AreEqual(folder ? 5 : 1, ((PlaylistDetailRow)rows[0]).score);
            Assert.AreEqual(40, ((PlaylistDetailRow)rows[2]).score);
            Assert.AreEqual(1, publishedRows);
            Assert.AreEqual(0, uiProjection, "初回候補と依存差分の投影をworkerに保ちます。");
            Assert.AreEqual(0, uiScoreCopies, "候補追随のscoreコピーをUIへ移しません。");
            Assert.AreEqual(fixture.Library.GetScoreSnapshotForDiagnostics().Version,
                fixture.Workspace.DetailViewState.Source.LastBuiltScoreSnapshotVersion);
        }
        finally
        {
            release.Set(); adoptionRelease.TrySetResult();
            fixture.Workspace.CancelDetailBuilds();
            try { await refresh; }
            finally
            {
                fixture.MainTable.RowsReplacing -= replacing;
                fixture.MainTable.PropertyChanged -= published;
                fixture.Workspace.DetailWorkObserver = null;
                fixture.Library.ScoreSnapshotWorkObserver = null;
            }
        }
    }

}
