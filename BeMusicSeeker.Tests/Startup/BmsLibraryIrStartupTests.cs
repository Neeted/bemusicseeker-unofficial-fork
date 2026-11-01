#nullable disable
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

using static BeMusicSeeker.TestSupport.Components.IrStartupTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>startup の実入口から IR 失敗と shutdown drain を検証します。</summary>
[TestClass]
public sealed class BmsLibraryIrStartupTests
{
    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>実起動の既存/新規cache成績をAAA/90で公開し、既存通常ownerのrank検索への加入と同snapshotのDJ集計へ接続します。</summary>
    /// <remarks>局所の高/低/同点条件表はサービス試験へ分担し、ここでは実DB/XML・worker・通常配送の終端と購読解放を確認します。</remarks>
    [TestMethod]
    public async Task InitializeStartup_RankingCacheDerivedValuesReachNormalSearchAndLampAggregation()
    {
        await using var fixture = new StartupFixture(databaseScope, waitForCancellation: false, failFirstRequest: false, exerciseDerivedValues: true);
        await fixture.InitializeAsync();
        await TestUiDispatcherHost.AwaitNotificationAsync(fixture.Client.Started.Task, fixture.WaitForWorkersAsync(), "本人IR通信開始");
        using var table = new MainChartListViewModel(action => action());
        using PlaylistWorkspaceViewModel workspace = RegularChartListOwnerTestSupport.CreateWorkspaceForOwner(table);
        using RegularChartListOwner owner = RegularChartListOwnerTestSupport.CreateOwner(table, workspace);
        owner.AttachNormalLibraryRefreshSource(fixture.Library);
        await owner.DeliverPendingLibraryChangesAsync(fixture.Library, CancellationToken.None);
        Assert.IsTrue(owner.TryApplyVirtualNormalLibrary(new RegularVirtualNormalLibraryApplyRequest
        {
            Library = fixture.Library,
            ModeFilter = ChartModeFilter.All,
            KeywordFilter = "rank:AAA",
            SortColumnName = nameof(LibraryChartRow.Title),
            SortDirection = System.ComponentModel.ListSortDirection.Ascending,
            ColumnSelection = new MainChartListColumnSelection(new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD), false, 0,
                MainViewUpdateMode.FullScanAllChartsFilterSelected, System.Windows.Visibility.Collapsed, new PlaylistSummaryColumnSettings()),
            Mode = MainViewUpdateMode.FullScanAllChartsFilterSelected
        }).WasCommitted);
        Assert.AreEqual(0, table.Rows.Count, "更新前のAとスコア未取得はAAA検索へ入りません。");
        try
        {
            fixture.Client.Release.TrySetResult();
            await fixture.WaitForWorkersAsync();
            await owner.DeliverPendingLibraryChangesAsync(fixture.Library, CancellationToken.None);

            Assert.IsFalse(fixture.FailedReport);
            string[] hashes = [StartupFixture.Hash, StartupFixture.CacheHash];
            BMSLibrary.ScoreSnapshot snapshot = fixture.Library.CaptureScoreSnapshotForView();
            foreach (string hash in hashes)
            {
                BMSScore adopted = snapshot.ScoresByHash[hash];
                Assert.AreEqual(1800, adopted.score);
                Assert.AreEqual(1000, adopted.totalnotes);
                Assert.AreEqual(RankType.AAA, adopted.rank);
                Assert.AreEqual(90, adopted.rate);
                ChartScoreSnapshot projected = fixture.Library.ResolveChartScoreSnapshot(ChartFileKind.Bms, hash + ".bms", hash, null, snapshot);
                Assert.AreEqual(RankType.AAA, projected.Rank);
                Assert.AreEqual(90, projected.Rate);
            }
            LibraryChartRow[] rows = table.Rows.Cast<LibraryChartRow>().ToArray();
            CollectionAssert.AreEquivalent(hashes, rows.Select(row => row.hash).ToArray());
            Assert.IsTrue(rows.All(row => row.rank == RankType.AAA && row.rate == 90));

            var lampScores = PlaylistLampScoreSnapshot.FromBmsLibrarySnapshot(snapshot);
            PlaylistLampAggregationResult result = new PlaylistLampAggregationService().Aggregate(new PlaylistLampAggregationRequest(
                "ir-derived-values", ["folder"], hashes.Select(hash => new PlaylistLampEntrySnapshot("folder", hash, true, md5: hash)), lampScores));
            Assert.AreEqual(2, result.RankSegments.Single(segment => segment.RankCategory == PlaylistLampRankCategory.AAA).Count);
            Assert.AreEqual(0, result.RankSegments.Where(segment => segment.RankCategory != PlaylistLampRankCategory.AAA).Sum(segment => segment.Count));
        }
        finally
        {
            fixture.Client.Release.TrySetResult();
        }
    }

    /// <summary>
    /// 起動時IR先読みの判別不能なHTTP200応答は一度報告して再試行せず、既存score・未送信判定・保存値を保持する。
    /// 独立順位cacheを同周で反映し、次の通常要求では正常な本人IR成績を採用する。
    /// </summary>
    /// <remarks>実起動・所有譜面・score DB・IR解析・順位cache・worker実終端の接続を確認する代表例。応答条件表はサービス試験に集約する。</remarks>
    [TestMethod]
    public async Task InitializeStartup_FailedPrefetchIsReportedWithoutRetryOrScoreMutation()
    {
        await using var fixture = new StartupFixture(databaseScope, waitForCancellation: false, failFirstRequest: false, exerciseUnrecognizedResponse: true);
        await fixture.InitializeAsync();
        await TestUiDispatcherHost.AwaitNotificationAsync(fixture.Client.Started.Task, fixture.WaitForWorkersAsync(), "本人IR通信開始");
        BMSScore before = fixture.Library.GetBMSScores().Single();
        Assert.IsTrue(fixture.Library.GetPlaylistLibraryResolveIndexSnapshot(CancellationToken.None, out _, out _)
            .ContainsBmsMd5(before.hash));
        Assert.IsFalse(before.IsLr2IrScoreUnsent);
        string databaseBefore = fixture.CaptureRawIrDatabase();
        try
        {
            fixture.Client.Release.TrySetResult();
            await fixture.WaitForWorkersAsync();

            Assert.AreEqual(1, fixture.Client.RequestCount);
            Assert.IsTrue(fixture.FailedReport);
            Assert.AreEqual("parse_failed", fixture.FailureDetail);
            Assert.AreSame(before, fixture.Library.GetBMSScores().Single());
            Assert.AreEqual(321, before.perfect);
            Assert.AreEqual(45, before.great);
            Assert.IsFalse(before.IsLr2IrScoreUnsent);
            Assert.AreEqual(databaseBefore, fixture.CaptureRawIrDatabase());
            fixture.AssertDatabaseRetained();
            Assert.AreEqual(1, before.ranking);
            Assert.AreEqual(1, before.rankingNum, "判別不能な本人成績を採用せず、独立した順位cacheは反映します。");
            Assert.IsFalse(fixture.Library.RankingRefreshRunning);
            Assert.AreEqual(fixture.Library.RankingRefreshRequestedVersion, fixture.Library.RankingRefreshCompletedVersion);

            fixture.FailedReport = false;
            await Task.Run(() => fixture.Library.InitializeScoresOnly(null));
            await fixture.WaitForWorkersAsync();

            Assert.AreEqual(2, fixture.Client.RequestCount);
            Assert.IsFalse(fixture.FailedReport);
            BMSScore current = fixture.Library.GetBMSScores().Single();
            Assert.AreEqual(500, current.perfect);
            Assert.AreEqual(100, current.great);
            Assert.IsFalse(current.IsLr2IrScoreUnsent);
            fixture.AssertDatabaseCommitted();
            Assert.IsFalse(fixture.Library.RankingRefreshRunning);
            Assert.AreEqual(fixture.Library.RankingRefreshRequestedVersion, fixture.Library.RankingRefreshCompletedVersion);
        }
        finally
        {
            fixture.Client.Release.TrySetResult();
            await fixture.WaitForWorkersAsync();
        }
    }

    /// <summary>
    /// 起動時IRの通信を保持したまま終了取消を伝えて終了阻害を保ち、通信解放とranking停止状態の通知後に阻害解除・scoreとDBの不変を確認する。
    /// </summary>
    /// <remarks>実起動入口とscore DBを使い、IR通信の取消からranking停止状態の通知への接続を観測する。阻害保持は通信gate中で確認し、rankingの実worker Task終端はfixtureのDisposeAsyncで回収する。</remarks>
    [TestMethod]
    public async Task RequestShutdown_CancelsInflightIrAndDrainsRankingBeforeBecomingIdle()
    {
        await using var fixture = new StartupFixture(databaseScope, waitForCancellation: true);
        await fixture.InitializeAsync();
        await fixture.Client.Started.Task;
        await fixture.RankingStarted.Task;
        BMSScore before = fixture.Library.GetBMSScores().Single();
        fixture.Library.RequestShutdown("ir-test");
        await fixture.Client.Cancelled.Task;
        Assert.IsTrue(fixture.Library.HasShutdownBlockingWork, "通信が完了するまでは idle にしない。");
        fixture.Client.Release.TrySetResult();
        await fixture.Client.Completed.Task;
        await fixture.RankingCompleted.Task;

        Assert.IsFalse(fixture.Library.HasShutdownBlockingWork);
        Assert.AreEqual(1, fixture.Client.RequestCount);
        Assert.AreSame(before, fixture.Library.GetBMSScores().Single());
        Assert.AreEqual(321, before.perfect);
        fixture.AssertDatabaseRetained();
    }

    /// <summary>
    /// IR通信中は共有操作受付を占有せず、既に持つLの解放後に現モデルだけを保存して退役モデルの結果を破棄する。
    /// </summary>
    /// <remarks>実起動・score DB・IR XML解析・ranking workerを接続し、通信と保存の受付分離および終了時の資源回収を確認する。</remarks>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task IrCommunication_LeavesAdmissionFreeAndRetiredResultDoesNotCommit(bool retire)
    {
        await using var fixture = new StartupFixture(databaseScope, waitForCancellation: false, failFirstRequest: false);
        await fixture.InitializeAsync();
        await fixture.Client.Started.Task;
        Assert.IsTrue(fixture.Library.OperationAdmission.TryEnter(out IDisposable parent), "通信中に設定の新規Lを拒否しません。");
        try
        {
            fixture.Client.Release.TrySetResult();
            await fixture.Client.Completed.Task;
            Assert.IsTrue(fixture.Library.RankingRefreshRunning);
            fixture.AssertDatabaseRetained();
            if (retire)
            {
                fixture.Library.RequestStop("replaced_pair");
                Assert.IsFalse(fixture.Library.OperationAdmission.IsAdmissionClosed, "モデル退役は構成の共有受付を閉じません。");
            }
        }
        finally { parent.Dispose(); }
        await fixture.RankingCompleted.Task;
        if (retire) { fixture.AssertDatabaseRetained(); }
        else { fixture.AssertDatabaseCommitted(); }
        Assert.IsTrue(fixture.Library.OperationAdmission.TryEnter(out IDisposable next));
        next.Dispose();
    }

    /// <summary>
    /// score再読込みで旧購読を外し、通常setterの版・snapshot通知故障を局所化して実lookupを保持し、終了後は全購読を解除する。
    /// </summary>
    /// <remarks>実起動・score DB・IR XML解析・ranking workerを接続し、通信と保存の受付分離および終了時の資源回収を確認する。</remarks>
    [TestMethod]
    public async Task ScoreSubscriptions_ReloadAndShutdownDetachPreviousScore()
    {
        await using var fixture = new StartupFixture(databaseScope, waitForCancellation: false);
        fixture.Client.Release.TrySetResult();
        await fixture.InitializeAsync();
        await fixture.RankingCompleted.Task;
        await fixture.WaitForWorkersAsync();
        BMSScore previous = fixture.Library.GetBMSScores().Single();
        Assert.AreEqual(1, fixture.Library.ScoreSubscriptionCount);
        Assert.AreEqual(previous.score, fixture.Library.ResolveChartScoreSnapshot(
            ChartFileKind.Bms, "captured.bms", previous.hash, string.Empty).Score);
        Assert.IsNull(fixture.Library.ResolveChartScoreSnapshot(
            ChartFileKind.Bms, "missing.bms", "missing", string.Empty).Score);
        Assert.IsNull(fixture.Library.ResolveChartScoreSnapshot(
            ChartFileKind.Bmson, "captured.bmson", previous.hash, string.Empty).Score);

        var reloadCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int rankingVersion = fixture.Library.RankingRefreshCompletedVersion;
        System.ComponentModel.PropertyChangedEventHandler reloadHandler = (_, args) =>
        {
            if (args.PropertyName == nameof(BMSLibrary.RankingRefreshCompletedVersion)
                && fixture.Library.RankingRefreshCompletedVersion > rankingVersion)
            {
                reloadCompleted.TrySetResult();
            }
        };
        fixture.Library.PropertyChanged += reloadHandler;
        int count = 0;
        ScoreSnapshotChange lastChange = null;
        System.ComponentModel.PropertyChangedEventHandler scoreHandler = null;
        try
        {
            await Task.Run(() => fixture.Library.InitializeScoresOnly(null));
            await reloadCompleted.Task;
            await fixture.WaitForWorkersAsync();
            BMSScore current = fixture.Library.GetBMSScores().Single();
            Assert.AreNotSame(previous, current);
            Assert.AreEqual(1, fixture.Library.ScoreSubscriptionCount);
            // reload/順位の登録範囲は完了済み。ここから旧実体の解除と新実体の局所変更だけを観測します。
            scoreHandler = ScoreSnapshotTestSupport.CreateChangeHandler(fixture.Library, change => { count++; lastChange = change; });
            fixture.Library.PropertyChanged += scoreHandler;
            previous.ranking = 1;
            current.ranking = 2;
            Assert.AreEqual(1, count);
            CollectionAssert.AreEqual(new[] { current.hash }, lastChange.Md5Keys.ToArray());
            Assert.AreEqual(fixture.Library.ScoreSnapshotVersion, lastChange.Version);
            System.ComponentModel.PropertyChangedEventHandler failingVersion = (_, args) =>
            {
                if (args.PropertyName == nameof(BMSLibrary.ScoreSnapshotVersion))
                {
                    throw new InvalidOperationException("controlled version notification failure");
                }
            };
            fixture.Library.PropertyChanged += failingVersion;
            try
            {
                int version = fixture.Library.ScoreSnapshotVersion;
                current.ranking = 4;
                Assert.AreEqual(version + 1, fixture.Library.ScoreSnapshotVersion);
                Assert.AreEqual(fixture.Library.GetScoreSnapshotForDiagnostics().Version, fixture.Library.ScoreSnapshotVersion);
                Assert.AreEqual(2, count, "版通知の故障後にも独立したsnapshot通知を試みます。");
                Assert.AreEqual(4, fixture.Library.ResolveChartScoreSnapshot(
                    ChartFileKind.Bms, "captured.bms", current.hash, string.Empty).Ranking);
            }
            finally { fixture.Library.PropertyChanged -= failingVersion; }
            System.ComponentModel.PropertyChangedEventHandler failingSnapshot = ScoreSnapshotTestSupport.CreateChangeHandler(fixture.Library, _ => throw new InvalidOperationException("controlled snapshot notification failure"));
            fixture.Library.PropertyChanged += failingSnapshot;
            try
            {
                int version = fixture.Library.ScoreSnapshotVersion;
                current.ranking = 5;
                Assert.AreEqual(version + 1, fixture.Library.ScoreSnapshotVersion);
                Assert.AreEqual(5, fixture.Library.ResolveChartScoreSnapshot(
                    ChartFileKind.Bms, "captured.bms", current.hash, string.Empty).Ranking);
            }
            finally { fixture.Library.PropertyChanged -= failingSnapshot; }
            current.IsLr2IrScoreUnsent = true;
            Assert.IsTrue(fixture.Library.ResolveChartScoreSnapshot(
                ChartFileKind.Bms, "captured.bms", current.hash, string.Empty).IsLr2IrScoreUnsent);
            CollectionAssert.AreEqual(new[] { current.hash }, lastChange.Md5Keys.ToArray());
            fixture.Library.RequestShutdown("score-subscription-test");
            current.ranking = 3;
            Assert.AreEqual(4, count);
            Assert.AreEqual(0, fixture.Library.ScoreSubscriptionCount);
        }
        finally
        {
            fixture.Library.PropertyChanged -= reloadHandler;
            fixture.Library.PropertyChanged -= scoreHandler;
        }
    }

    /// <summary>
    /// 連続するranking要求は受付時のreporterと要求世代で開始・終端を通知し、次reporterの例外でも旧通知を混同せず全作業を終端する。
    /// </summary>
    /// <remarks>実起動・score DB・IR XML解析・ranking workerを接続し、通信と保存の受付分離および終了時の資源回収を確認する。</remarks>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task RankingRefresh_UsesAcceptanceReporterForNextRequestAndIsolatesPublicationFailure(bool failNextPublication)
        => VerifyAcceptanceReporterAsync(failNextPublication);

    private async Task VerifyAcceptanceReporterAsync(bool failNextPublication)
    {
        await using var fixture = new StartupFixture(databaseScope, waitForCancellation: false);
        const string taskName = "ranking_refresh_deferred";
        var reports = new ConcurrentQueue<(int Callback, int Version, bool Running)>();
        var firstStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        int captures = 0;
        fixture.Library.StartupExecutionProgressReporterFactory = name =>
        {
            if (name != taskName)
            {
                return null;
            }
            int callback = Interlocked.Increment(ref captures);
            return (version, running) =>
            {
                reports.Enqueue((callback, version, running));
                if (callback == 1 && running)
                {
                    firstStarted.TrySetResult(version);
                    release.Wait();
                }
                if (callback == 2 && failNextPublication)
                {
                    throw new InvalidOperationException("controlled progress publication failure");
                }
            };
        };
        Task initialize = Task.CompletedTask;
        Task initializeScores = Task.CompletedTask;
        fixture.Client.Release.TrySetResult();
        try
        {
            initialize = fixture.InitializeAsync();
            await initialize;
            int firstVersion = await firstStarted.Task;
            initializeScores = Task.Run(() => fixture.Library.InitializeScoresOnly(null));
            await initializeScores;
            int nextVersion = fixture.Library.RankingRefreshRequestedVersion;
            Assert.IsTrue(nextVersion > firstVersion);
            release.Set();
            await fixture.RankingCompleted.Task;

            Assert.IsTrue(reports.Contains((1, firstVersion, false)), "旧周の終端が旧callbackと要求版へ届きませんでした。");
            Assert.IsFalse(reports.Any(report => report.Callback == 1 && report.Version == nextVersion));
            Assert.IsTrue(reports.Contains((2, nextVersion, true)));
            Assert.IsTrue(reports.Contains((2, nextVersion, false)));
            Assert.AreEqual(nextVersion, fixture.Library.RankingRefreshCompletedVersion);
            Assert.IsFalse(fixture.Library.ScoreHydrationRunning);
            Assert.IsFalse(fixture.Library.RankingRefreshRunning);
            Assert.IsFalse(fixture.Library.HasShutdownBlockingWork);
        }
        finally
        {
            release.Set();
            fixture.Client.Release.TrySetResult();
            await Task.WhenAll(initialize, initializeScores);
            if (fixture.Library.RankingRefreshRunning)
            {
                await fixture.RankingCompleted.Task;
            }
            if (fixture.Client.Started.Task.IsCompleted)
            {
                await fixture.Client.Completed.Task;
            }
        }
    }

    /// <summary>順位DB読取り失敗はfailedで終端して順位対象を保持し、独立本人IRの正常採用と障害除去後の次要求を妨げない。</summary>
    /// <remarks>実起動・通信gate・順位DB実体化・背景仕事の報告と要求版・実worker終端を接続する。読取り失敗時の全保存列保持はサービス試験と分担する。</remarks>
    [TestMethod]
    public async Task InitializeStartup_SavedRankingReadFailurePreservesCacheAndAllowsNextRequest()
    {
        await using var fixture = new StartupFixture(databaseScope, waitForCancellation: false, failFirstRequest: false, exerciseRankingReadFailure: true);
        await fixture.InitializeAsync();
        await TestUiDispatcherHost.AwaitNotificationAsync(fixture.Client.Started.Task, fixture.WaitForWorkersAsync(), "本人IR通信開始");
        BMSScore before = fixture.Library.GetBMSScores().Single(score => score.hash == StartupFixture.CacheHash);
        string scoreBefore = JsonConvert.SerializeObject(before);
        int requestedVersion = fixture.Library.RankingRefreshRequestedVersion;
        fixture.CorruptSavedRanking();
        string databaseBefore = fixture.CaptureRawRankingDatabase();
        try
        {
            fixture.Client.Release.TrySetResult();
            await fixture.WaitForWorkersAsync();

            Assert.AreEqual(1, fixture.Client.RequestCount, "同じ要求内で取得を再試行しません。");
            Assert.IsTrue(fixture.FailedReport);
            CollectionAssert.AreEqual(new[] { ("failed", true) }, fixture.RankingReports.Where(report => report.Status is not ("queued" or "start")).ToArray());
            Assert.AreSame(before, fixture.Library.GetBMSScores().Single(score => score.hash == StartupFixture.CacheHash));
            Assert.AreEqual(scoreBefore, JsonConvert.SerializeObject(before));
            Assert.AreEqual(databaseBefore, fixture.CaptureRawRankingDatabase());
            fixture.AssertDatabaseCommitted();
            Assert.AreEqual(500, fixture.Library.GetBMSScores().Single(score => score.hash != StartupFixture.CacheHash).perfect,
                "別hashの本人IRは正常に確定・採用します。");
            Assert.IsFalse(fixture.Library.RankingRefreshRunning);
            Assert.AreEqual(requestedVersion, fixture.Library.RankingRefreshCompletedVersion);
            Assert.IsTrue(fixture.Library.OperationAdmission.TryEnter(out IDisposable next));
            next.Dispose();

            fixture.RestoreSavedRanking();
            fixture.FailedReport = false;
            await Task.Run(() => fixture.Library.InitializeScoresOnly(null));
            await fixture.WaitForWorkersAsync();

            Assert.AreEqual(2, fixture.Client.RequestCount);
            Assert.IsFalse(fixture.FailedReport);
            CollectionAssert.AreEqual(new[] { ("failed", true), ("done", false) }, fixture.RankingReports.Where(report => report.Status is not ("queued" or "start")).ToArray());
            BMSScore current = fixture.Library.GetBMSScores().Single(score => score.hash == StartupFixture.CacheHash);
            Assert.AreEqual(500, current.perfect);
            Assert.AreEqual(100, current.great);
            Assert.AreEqual(1, current.ranking);
            Assert.AreEqual(1, current.rankingNum);
            fixture.AssertRankingCacheCommitted();
            Assert.IsFalse(fixture.Library.RankingRefreshRunning);
            Assert.IsTrue(fixture.Library.RankingRefreshRequestedVersion > requestedVersion);
            Assert.AreEqual(fixture.Library.RankingRefreshRequestedVersion, fixture.Library.RankingRefreshCompletedVersion);
        }
        finally
        {
            fixture.Client.Release.TrySetResult();
            await fixture.WaitForWorkersAsync();
        }
    }

    /// <summary>
    /// 起動中の保存IR成績読取り失敗は一度報告し、スコア・未送信判定・保存値を保持する。
    /// 独立順位cacheは同周で反映し、障害除去後の次の明示要求は通常保存・メモリ更新を完了する。
    /// </summary>
    /// <remarks>実InitializeStartupから通信gate・DB実体化・model非採用・順位XML・worker実終端を接続する代表例。読取り条件の組合せはサービス側で保証する。</remarks>
    [TestMethod]
    public async Task InitializeStartup_SavedIrScoreReadFailurePreservesScoresAndAllowsNextRequest()
    {
        await using var fixture = new StartupFixture(databaseScope, waitForCancellation: false, failFirstRequest: false, exerciseReadFailure: true);
        await fixture.InitializeAsync();
        await TestUiDispatcherHost.AwaitNotificationAsync(fixture.Client.Started.Task, fixture.WaitForWorkersAsync(), "本人IR通信開始");
        BMSScore before = fixture.Library.GetBMSScores().Single();
        Assert.IsTrue(fixture.Library.GetPlaylistLibraryResolveIndexSnapshot(CancellationToken.None, out _, out _)
            .ContainsBmsMd5(before.hash), "未送信判定の対象となる所持譜面が必要です。");
        Assert.IsFalse(before.IsLr2IrScoreUnsent);
        fixture.CorruptSavedIrScore();
        string databaseBefore = fixture.CaptureRawIrDatabase();
        try
        {
            fixture.Client.Release.TrySetResult();
            await fixture.WaitForWorkersAsync();

            Assert.AreEqual(1, fixture.Client.RequestCount, "同じ失敗更新をその場で再要求しません。");
            Assert.IsTrue(fixture.FailedReport);
            Assert.AreEqual("db_read_failed", fixture.FailureDetail, "空応答の解析失敗でDB読取りの検証を迂回しません。");
            Assert.AreSame(before, fixture.Library.GetBMSScores().Single());
            Assert.AreEqual(321, before.perfect);
            Assert.AreEqual(45, before.great);
            Assert.IsFalse(before.IsLr2IrScoreUnsent, "失敗を正常空として採用すると所持譜面が未送信へ変わります。");
            Assert.AreEqual(databaseBefore, fixture.CaptureRawIrDatabase());
            Assert.AreEqual(1, before.ranking);
            Assert.AreEqual(1, before.rankingNum, "本人成績失敗とは独立して順位cacheを反映します。");
            Assert.IsFalse(fixture.Library.RankingRefreshRunning);
            Assert.AreEqual(fixture.Library.RankingRefreshRequestedVersion, fixture.Library.RankingRefreshCompletedVersion);

            fixture.RestoreSavedIrScore();
            fixture.FailedReport = false;
            await Task.Run(() => fixture.Library.InitializeScoresOnly(null));
            await fixture.WaitForWorkersAsync();

            Assert.AreEqual(2, fixture.Client.RequestCount);
            Assert.IsFalse(fixture.FailedReport);
            BMSScore current = fixture.Library.GetBMSScores().Single();
            Assert.AreEqual(500, current.perfect);
            Assert.AreEqual(100, current.great);
            Assert.IsFalse(current.IsLr2IrScoreUnsent);
            fixture.AssertDatabaseCommitted();
            Assert.IsFalse(fixture.Library.RankingRefreshRunning);
            Assert.AreEqual(fixture.Library.RankingRefreshRequestedVersion, fixture.Library.RankingRefreshCompletedVersion);
        }
        finally
        {
            fixture.Client.Release.TrySetResult();
            await fixture.WaitForWorkersAsync();
        }
    }
    /// <summary>
    /// 正常な異成績取得でもmetadata保存が失敗したら、一度失敗報告し本人score・未送信判定・保存値を保持する。
    /// 独立cacheの同周反映と、障害除去後の次の明示要求での通常保存・メモリ採用を確認する。
    /// </summary>
    /// <remarks>実InitializeStartup・通信gate・DB transaction・model非採用・順位cache・worker実終端を接続する。保存失敗の条件分担はサービス側で検証する。</remarks>
    [TestMethod]
    public async Task InitializeStartup_IrMetadataSaveFailurePreservesScoresAndAllowsNextRequest()
    {
        await using var fixture = new StartupFixture(databaseScope, waitForCancellation: false, failFirstRequest: false, exerciseSaveFailure: true);
        await fixture.InitializeAsync();
        await TestUiDispatcherHost.AwaitNotificationAsync(fixture.Client.Started.Task, fixture.WaitForWorkersAsync(), "本人IR通信開始");
        BMSScore before = fixture.Library.GetBMSScores().Single();
        Assert.IsTrue(fixture.Library.GetPlaylistLibraryResolveIndexSnapshot(CancellationToken.None, out _, out _)
            .ContainsBmsMd5(before.hash));
        Assert.IsFalse(before.IsLr2IrScoreUnsent);
        fixture.InstallMetadataWriteFailure();
        string databaseBefore = fixture.CaptureRawIrDatabase();
        try
        {
            fixture.Client.Release.TrySetResult();
            await fixture.WaitForWorkersAsync();

            Assert.AreEqual(1, fixture.Client.RequestCount);
            Assert.IsTrue(fixture.FailedReport);
            Assert.AreSame(before, fixture.Library.GetBMSScores().Single());
            Assert.AreEqual(321, before.perfect);
            Assert.AreEqual(45, before.great);
            Assert.IsFalse(before.IsLr2IrScoreUnsent);
            Assert.AreEqual(databaseBefore, fixture.CaptureRawIrDatabase());
            Assert.AreEqual(1, before.ranking);
            Assert.AreEqual(1, before.rankingNum, "本人成績の保存失敗とは独立して順位cacheを反映します。");
            Assert.IsFalse(fixture.Library.RankingRefreshRunning);
            Assert.AreEqual(fixture.Library.RankingRefreshRequestedVersion, fixture.Library.RankingRefreshCompletedVersion);

            fixture.RemoveMetadataWriteFailure();
            fixture.FailedReport = false;
            await Task.Run(() => fixture.Library.InitializeScoresOnly(null));
            await fixture.WaitForWorkersAsync();

            Assert.AreEqual(2, fixture.Client.RequestCount);
            Assert.IsFalse(fixture.FailedReport);
            BMSScore current = fixture.Library.GetBMSScores().Single();
            Assert.AreEqual(500, current.perfect);
            Assert.AreEqual(100, current.great);
            Assert.IsFalse(current.IsLr2IrScoreUnsent);
            fixture.AssertDatabaseCommitted();
            Assert.IsFalse(fixture.Library.RankingRefreshRunning);
            Assert.AreEqual(fixture.Library.RankingRefreshRequestedVersion, fixture.Library.RankingRefreshCompletedVersion);
        }
        finally
        {
            fixture.Client.Release.TrySetResult();
            await fixture.WaitForWorkersAsync();
        }
    }

    /// <summary>本人IRと独立cacheの新hashを実起動で採用し、版またはsnapshot通知の通常故障でも保存・lookup・購読・要求終端を保持します。</summary>
    /// <remarks>通信gate後に故障購読を接続し、callbackは採用状態とL解放を記録して処理後に検証します。全subscriber配送や初期化の通知時点は保証しません。</remarks>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InitializeStartup_NotificationFailurePreservesCommittedSnapshotAndIndependentCache(bool failSnapshot)
    {
        await using var fixture = new StartupFixture(databaseScope, waitForCancellation: false, failFirstRequest: false, exercisePublication: true);
        await fixture.InitializeAsync();
        await TestUiDispatcherHost.AwaitNotificationAsync(fixture.Client.Started.Task, fixture.WaitForWorkersAsync(), "本人IR通信開始");
        var observations = new ConcurrentQueue<(int Version, int SnapshotVersion, bool Ready, int Subscriptions, int? Score, bool AdmissionFree)>();
        void RecordAndFail()
        {
            BMSLibrary.ScoreSnapshot snapshot = fixture.Library.GetScoreSnapshotForDiagnostics();
            bool admitted = fixture.Library.OperationAdmission.TryEnter(out IDisposable lease);
            lease?.Dispose();
            observations.Enqueue((fixture.Library.ScoreSnapshotVersion, snapshot.Version, fixture.Library.ScoreSnapshotReady,
                fixture.Library.ScoreSubscriptionCount, fixture.Library.ResolveChartScoreSnapshot(
                    ChartFileKind.Bms, "new-ir.bms", StartupFixture.IrHash, string.Empty).Score, admitted));
            throw new InvalidOperationException("controlled score publication failure");
        }
        System.ComponentModel.PropertyChangedEventHandler propertyHandler = (_, args) =>
        {
            if (args.PropertyName == nameof(BMSLibrary.ScoreSnapshotVersion)) { RecordAndFail(); }
        };
        System.ComponentModel.PropertyChangedEventHandler snapshotHandler = ScoreSnapshotTestSupport.CreateChangeHandler(fixture.Library, _ => RecordAndFail());
        if (failSnapshot) { fixture.Library.PropertyChanged += snapshotHandler; }
        else { fixture.Library.PropertyChanged += propertyHandler; }
        try
        {
            fixture.Client.Release.TrySetResult();
            await fixture.WaitForWorkersAsync();
            Assert.IsFalse(fixture.FailedReport);
            Assert.AreEqual(1, fixture.Client.RequestCount);
            fixture.AssertDatabaseCommitted();
            Assert.IsTrue(observations.Count >= 2, "本人IR通知の故障後にも独立cacheを公開します。");
            foreach ((int Version, int SnapshotVersion, bool Ready, int Subscriptions, int? Score, bool AdmissionFree) observation in observations)
            {
                Assert.AreEqual(observation.Version, observation.SnapshotVersion);
                Assert.IsTrue(observation.Ready);
                Assert.IsTrue(observation.Subscriptions >= 2);
                Assert.AreEqual(1100, observation.Score);
                Assert.IsTrue(observation.AdmissionFree, "IRとcacheはL解放後に公開します。");
            }
            Assert.AreEqual(1100, fixture.Library.ResolveChartScoreSnapshot(
                ChartFileKind.Bms, "new-cache.bms", StartupFixture.CacheHash, string.Empty).Score);
            Assert.AreEqual(fixture.Library.GetBMSScores().Count, fixture.Library.ScoreSubscriptionCount);
            Assert.IsFalse(fixture.Library.ResolveChartScoreSnapshot(ChartFileKind.Bms, "new-ir.bms", StartupFixture.IrHash, string.Empty).IsLr2IrScoreUnsent);
            Assert.IsFalse(fixture.Library.RankingRefreshRunning);
            Assert.AreEqual(fixture.Library.RankingRefreshRequestedVersion, fixture.Library.RankingRefreshCompletedVersion);
        }
        finally
        {
            fixture.Library.PropertyChanged -= propertyHandler;
            fixture.Library.PropertyChanged -= snapshotHandler;
            fixture.Client.Release.TrySetResult();
            await fixture.WaitForWorkersAsync();
        }
        BMSScore adopted = fixture.Library.GetBMSScores().Single(score => score.hash == StartupFixture.IrHash);
        ScoreSnapshotChange change = null;
        System.ComponentModel.PropertyChangedEventHandler recordChange = ScoreSnapshotTestSupport.CreateChangeHandler(fixture.Library, current => change = current);
        fixture.Library.PropertyChanged += recordChange;
        try
        {
            int version = fixture.Library.ScoreSnapshotVersion;
            adopted.ranking = 7;
            Assert.AreEqual(version + 1, fixture.Library.ScoreSnapshotVersion);
            Assert.AreEqual(7, fixture.Library.ResolveChartScoreSnapshot(ChartFileKind.Bms, "new-ir.bms", adopted.hash, string.Empty).Ranking);
            CollectionAssert.Contains(change.Md5Keys.ToArray(), adopted.hash);
        }
        finally { fixture.Library.PropertyChanged -= recordChange; }
        await Task.Run(() => fixture.Library.InitializeScoresOnly(null));
        await fixture.WaitForWorkersAsync();
        Assert.AreEqual(2, fixture.Client.RequestCount);
        Assert.IsFalse(fixture.FailedReport);
        Assert.IsFalse(fixture.Library.RankingRefreshRunning);
        Assert.AreEqual(fixture.Library.RankingRefreshRequestedVersion, fixture.Library.RankingRefreshCompletedVersion);
    }

    /// <summary>score取得元未設定のon-demand lookupは空snapshotを一度採用し、readyをfalseに保ちながら公開版と実snapshot版を一致させます。</summary>
    [TestMethod]
    public void GetBMSScores_UnconfiguredSourcePublishesOneEmptySnapshotDespiteNotificationFailure()
    {
        string root = Path.Combine(Path.GetTempPath(), "BmsScoreOnDemand", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string songPath = Path.Combine(root, "song.db");
        using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songPath)) { db.CreateTable<LR2SongDB.song>(); }
        var library = new TestBmsLibrary(databaseScope, songPath);
        int attempts = 0;
        System.ComponentModel.PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName == nameof(BMSLibrary.ScoreSnapshotVersion))
            {
                attempts++;
                throw new InvalidOperationException("controlled on-demand notification failure");
            }
        };
        library.PropertyChanged += handler;
        try
        {
            Assert.AreEqual(0, library.GetBMSScores().Count);
            Assert.IsFalse(library.ScoreSnapshotReady);
            int version = library.ScoreSnapshotVersion;
            Assert.IsTrue(version > 0);
            Assert.AreEqual(version, library.GetScoreSnapshotForDiagnostics().Version);
            Assert.AreEqual(0, library.GetBMSScores().Count);
            Assert.AreEqual(version, library.ScoreSnapshotVersion);
            Assert.AreEqual(1, attempts);
        }
        finally
        {
            library.PropertyChanged -= handler;
            library.RequestShutdown("on-demand-fixture-cleanup");
            Directory.Delete(root, recursive: true);
        }
    }
}
