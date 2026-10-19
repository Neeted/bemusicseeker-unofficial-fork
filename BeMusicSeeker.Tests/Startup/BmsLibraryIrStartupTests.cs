#nullable disable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

namespace BeMusicSeeker.Tests;

/// <summary>startup の実入口から IR 失敗と shutdown drain を検証します。</summary>
[TestClass]
public sealed class BmsLibraryIrStartupTests
{
    /// <summary>
    /// 起動時IR先読みの判別不能なHTTP200応答は一度報告して再試行せず、既存score・未送信判定・保存値を保持する。
    /// 独立順位cacheを同周で反映し、次の通常要求では正常な本人IR成績を採用する。
    /// </summary>
    /// <remarks>実起動・所有譜面・score DB・IR解析・順位cache・worker実終端の接続を確認する代表例。応答条件表はサービス試験に集約する。</remarks>
    [TestMethod]
    public async Task InitializeStartup_FailedPrefetchIsReportedWithoutRetryOrScoreMutation()
    {
        await using var fixture = new StartupFixture(waitForCancellation: false, failFirstRequest: false, exerciseUnrecognizedResponse: true);
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
        await using var fixture = new StartupFixture(waitForCancellation: true);
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
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task IrCommunication_LeavesAdmissionFreeAndRetiredResultDoesNotCommit(bool retire)
    {
        await using var fixture = new StartupFixture(waitForCancellation: false, failFirstRequest: false);
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
        await using var fixture = new StartupFixture(waitForCancellation: false);
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
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task RankingRefresh_UsesAcceptanceReporterForNextRequestAndIsolatesPublicationFailure(bool failNextPublication)
        => VerifyAcceptanceReporterAsync(failNextPublication);

    private static async Task VerifyAcceptanceReporterAsync(bool failNextPublication)
    {
        await using var fixture = new StartupFixture(waitForCancellation: false);
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
        await using var fixture = new StartupFixture(waitForCancellation: false, failFirstRequest: false, exerciseRankingReadFailure: true);
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
        await using var fixture = new StartupFixture(waitForCancellation: false, failFirstRequest: false, exerciseReadFailure: true);
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
        await using var fixture = new StartupFixture(waitForCancellation: false, failFirstRequest: false, exerciseSaveFailure: true);
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
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InitializeStartup_NotificationFailurePreservesCommittedSnapshotAndIndependentCache(bool failSnapshot)
    {
        await using var fixture = new StartupFixture(waitForCancellation: false, failFirstRequest: false, exercisePublication: true);
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
        using (var db = new LR2SongDBExtended(songPath)) { db.CreateTable<LR2SongDB.song>(); }
        var library = new TestBmsLibrary(songPath);
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

    private sealed class StartupFixture : IAsyncDisposable
    {
        internal const string IrHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        internal const string CacheHash = "cccccccccccccccccccccccccccccccc";
        private readonly bool exercisePublication;
        private const string Hash = "abcdefabcdefabcdefabcdefabcdefab";
        private readonly string root = Path.Combine(Path.GetTempPath(), "BmsIrStartup", Guid.NewGuid().ToString("N"));
        private readonly BmsLibraryDbGateway gateway;
        private readonly DateTime metadataUpdatedAt;
        internal readonly ControlledIrClient Client;
        internal readonly TestBmsLibrary Library;
        internal readonly TaskCompletionSource RankingStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource RankingCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ConcurrentQueue<(string Status, bool Failed)> RankingReports = new();
        internal bool FailedReport;
        internal string FailureDetail;

        private readonly ConcurrentBag<Task> scheduledWorkers = [];

        internal StartupFixture(bool waitForCancellation, bool failFirstRequest = true, bool exerciseReadFailure = false, bool exerciseSaveFailure = false, bool exercisePublication = false, bool exerciseRankingReadFailure = false, bool exerciseUnrecognizedResponse = false)
        {
            this.exercisePublication = exercisePublication;
            bool exerciseRankingCache = exerciseReadFailure || exerciseSaveFailure || exercisePublication || exerciseRankingReadFailure || exerciseUnrecognizedResponse;
            Directory.CreateDirectory(root);
            string songPath = Path.Combine(root, "song.db");
            string scorePath = exerciseRankingCache
                ? Path.Combine(root, "LR2files", "Database", "score.db")
                : Path.Combine(root, "score.db");
            Directory.CreateDirectory(Path.GetDirectoryName(scorePath));
            using (var db = new LR2SongDBExtended(songPath))
            {
                db.RunInTransaction(() =>
                {
                    db.CreateTable<LR2SongDB.song>();
                    db.CreateTable<LR2SongDB.folder>();
                    db.CreateTable<LR2SongDBExtended.install>();
                    db.CreateTable<LR2SongDBExtended.ir_score>();
                    db.CreateTable<LR2SongDBExtended.ir_score_refresh_metadata>();
                    if (exerciseRankingCache)
                    {
                        db.Insert(new LR2SongDB.song { hash = Hash, path = Path.Combine(root, "owned.bms"), title = "Owned chart" });
                    }
                });
            }
            using (var db = new LR2ScoreDBExtended(scorePath))
            {
                db.RunInTransaction(() =>
                {
                    db.CreateTable<LR2ScoreDB.player>();
                    db.CreateTable<LR2ScoreDB.score>();
                    db.Insert(new LR2ScoreDB.player { id = "ir-startup", irid = 123 });
                    db.Insert(new LR2ScoreDB.score { hash = Hash, perfect = 321, great = 45 });
                    if (exerciseRankingReadFailure)
                    {
                        db.Insert(new LR2ScoreDB.score { hash = CacheHash, perfect = 222, great = 33 });
                    }
                });
            }
            gateway = new BmsLibraryDbGateway(songPath, scorePath);
            gateway.ReplaceIrScoreTableAndRefreshMetadata([new LR2IRScore { hash = Hash, pg = 123, gr = 67 }], 123, "retained-startup-digest");
            metadataUpdatedAt = gateway.LoadIrScoreRefreshMetadata(123).updated_at;
            if (exerciseRankingCache)
            {
                string irDirectory = Path.Combine(root, "Ir");
                Directory.CreateDirectory(irDirectory);
                File.WriteAllText(Path.Combine(irDirectory, Hash + ".xml"),
                    "<root>\n<ranking>\n\t\t<score>\n\t\t<id>123</id>\n\t\t<clear>0</clear>\n"
                    + "\t\t<notes>1000</notes>\n\t\t<combo>300</combo>\n\t\t<pg>321</pg>\n"
                    + "\t\t<gr>45</gr>\n\t\t<minbp>20</minbp>\n\t\t</score>\n</ranking>\n"
                    + "<lastupdate>2026-10-10 12:00:00</lastupdate>\n", Encoding.UTF8);
            }
            if (exercisePublication || exerciseRankingReadFailure)
            {
                File.Copy(Path.Combine(root, "Ir", Hash + ".xml"), Path.Combine(root, "Ir", CacheHash + ".xml"));
                string cachePath = Path.Combine(root, "Ir", CacheHash + ".xml");
                File.WriteAllText(cachePath, File.ReadAllText(cachePath).Replace("<pg>321</pg>", "<pg>500</pg>").Replace("<gr>45</gr>", "<gr>100</gr>"), Encoding.UTF8);
            }
            Client = new ControlledIrClient(waitForCancellation, failFirstRequest, emptyFirstRequest: exerciseReadFailure,
                extraScoreHash: exercisePublication ? IrHash : null, unrecognizedFirstResponse: exerciseUnrecognizedResponse);
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
                LR2RootPath = root,
                EnableDownloadLr2IrScoreAndDetectUnsent = true,
                UpdateLr2IrRankingCacheOnStartup = exerciseRankingCache,
                PendingInstallEstimateMaxParallelPackages = 1
            };
            Library = new TestBmsLibrary(songPath, null, scorePath, null, () => options,
                TestBmsFactory.MissingEverythingBridge,
                uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher), irClient: Client);
            Library.StartupBackgroundTaskScheduler = (name, _, _, process) =>
            {
                if (name != "ranking_refresh_deferred") { return false; }
                scheduledWorkers.Add(Task.Run(process));
                return true;
            };
            Library.StartupBackgroundTaskReporter = (name, status, _, failed, detail) =>
            {
                if (name != "ranking_refresh_deferred")
                {
                    return;
                }
                RankingReports.Enqueue((status, failed));

                if (status == "start")
                {
                    RankingStarted.TrySetResult();
                }

                if (failed)
                {
                    FailedReport = true;
                    FailureDetail = detail;
                }
            };
            Library.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(BMSLibrary.RankingRefreshRunning) && !Library.RankingRefreshRunning)
                {
                    RankingCompleted.TrySetResult();
                }
            };
        }

        internal Task InitializeAsync() => Task.Run(() => Library.InitializeStartup(null));

        internal Task WaitForWorkersAsync() => Task.WhenAll(scheduledWorkers.ToArray());

        internal void CorruptSavedRanking()
        {
            // 必須の実起動が順位schemaを準備した後、通信gate中に旧行と読取り障害を用意します。
            gateway.UpsertIrData([new LR2IRData(CacheHash)
            {
                lr2id = 123, rank = 20, players_num = 100, pg = 200, gr = 20,
                lastupdate = new DateTime(2026, 10, 1)
            }]);
            using (LR2SongDBExtended db = gateway.OpenSongDb())
            {
                db.Execute("INSERT INTO ir_data (hash, lr2id) VALUES ('unmaterializable-hash', 123)");
            }
            bool readFailed = false;
            try { gateway.LoadIrDataWithMetrics(123); }
            catch (Exception) { readFailed = true; }
            Assert.IsTrue(readFailed, "実Gatewayの順位DB読取りが失敗するfixtureが必要です。");
        }

        internal void RestoreSavedRanking()
        {
            using LR2SongDBExtended db = gateway.OpenSongDb();
            db.Execute("DELETE FROM ir_data WHERE hash = 'unmaterializable-hash'");
        }

        internal string CaptureRawRankingDatabase()
        {
            using LR2SongDBExtended db = gateway.OpenSongDbReadOnly();
            return db.ExecuteScalar<string>("SELECT group_concat(value, ';') FROM (SELECT quote(hash)||','||quote(rank)||','||quote(players_num)||','||quote(lr2id)||','||quote(clear)||','||quote(notes)||','||quote(combo)||','||quote(pg)||','||quote(gr)||','||quote(minbp)||','||quote(average)||','||quote(sigma)||','||quote(lastupdate)||','||quote(lastcacheupdate) AS value FROM ir_data ORDER BY lr2id, hash)");
        }

        internal void AssertRankingCacheCommitted()
        {
            LR2IRData row = gateway.LoadIrData(123).Single(data => data.hash == CacheHash);
            Assert.AreEqual(500, row.pg);
            Assert.AreEqual(100, row.gr);
            Assert.AreEqual(1, row.rank);
            Assert.AreEqual(1, row.players_num);
        }

        internal void InstallMetadataWriteFailure()
        {
            using LR2SongDBExtended db = gateway.OpenSongDb();
            db.Execute("CREATE TRIGGER reject_ir_metadata BEFORE INSERT ON ir_score_refresh_metadata BEGIN SELECT RAISE(ABORT, 'controlled metadata failure'); END;");
        }

        internal void RemoveMetadataWriteFailure()
        {
            using LR2SongDBExtended db = gateway.OpenSongDb();
            db.Execute("DROP TRIGGER reject_ir_metadata;");
        }

        internal void CorruptSavedIrScore()
        {
            using (LR2SongDBExtended db = gateway.OpenSongDb())
            {
                db.Execute("UPDATE ir_score SET hash = 'unmaterializable-hash'");
            }
            bool readFailed = false;
            try { gateway.LoadIrScoreRows(); }
            catch (Exception) { readFailed = true; }
            Assert.IsTrue(readFailed, "保存スコアの実読取りが失敗するfixtureが必要です。");
        }

        internal void RestoreSavedIrScore()
        {
            using LR2SongDBExtended db = gateway.OpenSongDb();
            db.Execute("UPDATE ir_score SET hash = ?", Hash);
        }

        internal string CaptureRawIrDatabase()
        {
            using LR2SongDBExtended db = gateway.OpenSongDbReadOnly();
            string scores = db.ExecuteScalar<string>("SELECT group_concat(value, ';') FROM (SELECT quote(hash)||','||quote(clear)||','||quote(notes)||','||quote(combo)||','||quote(pg)||','||quote(gr)||','||quote(gd)||','||quote(bd)||','||quote(pr)||','||quote(minbp)||','||quote(option)||','||quote(lastupdate) AS value FROM ir_score ORDER BY hash)");
            string metadata = db.ExecuteScalar<string>("SELECT group_concat(value, ';') FROM (SELECT quote(lr2id)||','||quote(score_digest_sha256)||','||quote(updated_at) AS value FROM ir_score_refresh_metadata ORDER BY lr2id)");
            string schema = db.ExecuteScalar<string>("SELECT group_concat(value, ';') FROM (SELECT quote(type)||','||quote(name)||','||quote(tbl_name)||','||quote(sql) AS value FROM sqlite_master ORDER BY type, name)");
            return scores + "\n" + metadata + "\n" + schema;
        }
        internal void AssertDatabaseRetained()
        {
            LR2IRScore score = gateway.LoadIrScoreRows().Single();
            Assert.AreEqual(Hash, score.hash);
            Assert.AreEqual(123, score.pg);
            Assert.AreEqual(67, score.gr);
            Assert.AreEqual("retained-startup-digest", gateway.LoadIrScoreRefreshMetadata(123).score_digest_sha256);
            Assert.AreEqual(metadataUpdatedAt, gateway.LoadIrScoreRefreshMetadata(123).updated_at);
        }

        internal void AssertDatabaseCommitted()
        {
            List<LR2IRScore> rows = gateway.LoadIrScoreRows();
            Assert.AreEqual(exercisePublication ? 2 : 1, rows.Count);
            if (exercisePublication) { Assert.AreEqual(500, rows.Single(row => row.hash == IrHash).pg); }
            LR2IRScore score = rows.Single(row => row.hash == Hash);
            Assert.AreEqual(Hash, score.hash);
            Assert.AreEqual(500, score.pg);
            Assert.AreEqual(100, score.gr);
            Assert.AreNotEqual("retained-startup-digest", gateway.LoadIrScoreRefreshMetadata(123).score_digest_sha256);
        }

        public async ValueTask DisposeAsync()
        {
            Library.RequestShutdown("ir-fixture-cleanup");
            Client.Release.TrySetResult();
            if (Client.Started.Task.IsCompleted)
            {
                await Client.Completed.Task;
            }

            if (Library.RankingRefreshRunning)
            {
                await RankingCompleted.Task;
            }

            await Task.WhenAll(scheduledWorkers.ToArray());
            Directory.Delete(root, recursive: true);
        }
    }

    internal sealed class ControlledIrClient(bool waitForCancellation, bool failFirstRequest, bool ignoreCancellation = false, bool emptyFirstRequest = false, string extraScoreHash = null, bool unrecognizedFirstResponse = false) : IBmsLibraryIrClient
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int RequestCount;

        public string GetPlayerScoresXml(int lr2Id, CancellationToken cancellationToken = default)
            => FetchAsync(cancellationToken).GetAwaiter().GetResult();

        private async Task<string> FetchAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref RequestCount);
            using CancellationTokenRegistration registration = cancellationToken.Register(() => Cancelled.TrySetResult());
            Started.TrySetResult();
            try
            {
                // cleanup は production のキャンセル接続を壊した negative control も回収する。
                if (waitForCancellation)
                {
                    await Task.WhenAny(Cancelled.Task, Release.Task);
                }

                await Release.Task;
                if (!ignoreCancellation) { cancellationToken.ThrowIfCancellationRequested(); }
                if (RequestCount == 1 && failFirstRequest)
                {
                    throw new IOException("controlled unavailable IR");
                }

                if (RequestCount == 1 && emptyFirstRequest)
                {
                    return "<scorelist/>";
                }

                if (RequestCount == 1 && unrecognizedFirstResponse)
                {
                    return "<html><body>Service unavailable</body></html>";
                }

                string xml = "<root>\n\t<score>\n\t\t<hash>abcdefabcdefabcdefabcdefabcdefab</hash>\n"
                    + "\t\t<clear>4</clear>\n\t\t<notes>1000</notes>\n\t\t<combo>900</combo>\n"
                    + "\t\t<pg>500</pg>\n\t\t<gr>100</gr>\n\t\t<gd>10</gd>\n\t\t<bd>2</bd>\n"
                    + "\t\t<pr>1</pr>\n\t\t<minbp>12</minbp>\n\t\t<option>0</option>\n"
                    + "\t\t<lastupdate>20261010</lastupdate>\n\t</score>\n</root>\n";
                if (extraScoreHash != null)
                {
                    string extra = xml.Replace("abcdefabcdefabcdefabcdefabcdefab", extraScoreHash).Replace("<root>", "").Replace("</root>", "");
                    xml = xml.Replace("</root>", extra + "</root>");
                }
                return xml;
            }
            finally { Completed.TrySetResult(); }
        }

        public List<BMSLibrary.IRDataCacheInfo> GetRankingInfo(Uri rankingInfoUrl, IEnumerable<string> md5s)
            => throw new AssertFailedException("ランキングキャッシュの通信は対象外。");

        public void DownloadRankingData(Uri rankingDataUrl, string md5, string destinationPath)
            => throw new AssertFailedException("ランキングキャッシュの通信は対象外。");
    }
}
