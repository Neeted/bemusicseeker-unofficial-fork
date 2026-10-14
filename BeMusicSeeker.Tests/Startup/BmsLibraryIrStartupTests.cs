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

namespace BeMusicSeeker.Tests;

/// <summary>startup の実入口から IR 失敗と shutdown drain を検証します。</summary>
[TestClass]
public sealed class BmsLibraryIrStartupTests
{
    /// <summary>
    /// 起動時IR先読みが通信失敗したら一度報告して再試行せず、既存scoreとIR保存値・更新metadataを保持する。
    /// </summary>
    /// <remarks>実起動・score DB・IR XML解析・ranking workerを接続し、通信と保存の受付分離および終了時の資源回収を確認する。</remarks>
    [TestMethod]
    public async Task InitializeStartup_FailedPrefetchIsReportedWithoutRetryOrScoreMutation()
    {
        await using var fixture = new StartupFixture(waitForCancellation: false);
        await fixture.InitializeAsync();
        await fixture.Client.Started.Task;
        BMSScore before = fixture.Library.GetBMSScores().Single();
        fixture.Client.Release.TrySetResult();
        await fixture.RankingCompleted.Task;

        Assert.AreEqual(1, fixture.Client.RequestCount);
        Assert.IsTrue(fixture.FailedReport);
        Assert.AreSame(before, fixture.Library.GetBMSScores().Single());
        Assert.AreEqual(321, before.perfect);
        fixture.AssertDatabaseRetained();
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
    /// score再読込みで古いscoreの購読を外し、現在scoreだけの変更を通知して終了後は全購読を解除する。
    /// </summary>
    /// <remarks>実起動・score DB・IR XML解析・ranking workerを接続し、通信と保存の受付分離および終了時の資源回収を確認する。</remarks>
    [TestMethod]
    public async Task ScoreSubscriptions_ReloadAndShutdownDetachPreviousScore()
    {
        await using var fixture = new StartupFixture(waitForCancellation: false);
        fixture.Client.Release.TrySetResult();
        await fixture.InitializeAsync();
        await fixture.RankingCompleted.Task;
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
        Action<ScoreSnapshotChange> scoreHandler = change => { count++; lastChange = change; };
        try
        {
            await Task.Run(() => fixture.Library.InitializeScoresOnly(null));
            await reloadCompleted.Task;
            BMSScore current = fixture.Library.GetBMSScores().Single();
            Assert.AreNotSame(previous, current);
            Assert.AreEqual(1, fixture.Library.ScoreSubscriptionCount);
            fixture.Library.ScoreSnapshotChanged += scoreHandler;
            previous.ranking = 1;
            current.ranking = 2;
            Assert.AreEqual(1, count);
            CollectionAssert.AreEqual(new[] { current.hash }, lastChange.Md5Keys.ToArray());
            Assert.AreEqual(fixture.Library.ScoreSnapshotVersion, lastChange.Version);
            fixture.Library.RequestShutdown("score-subscription-test");
            current.ranking = 3;
            Assert.AreEqual(1, count);
            Assert.AreEqual(0, fixture.Library.ScoreSubscriptionCount);
        }
        finally
        {
            fixture.Library.PropertyChanged -= reloadHandler;
            fixture.Library.ScoreSnapshotChanged -= scoreHandler;
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

    private sealed class StartupFixture : IAsyncDisposable
    {
        private const string Hash = "abcdefabcdefabcdefabcdefabcdefab";
        private readonly string root = Path.Combine(Path.GetTempPath(), "BmsIrStartup", Guid.NewGuid().ToString("N"));
        private readonly BmsLibraryDbGateway gateway;
        private readonly DateTime metadataUpdatedAt;
        internal readonly ControlledIrClient Client;
        internal readonly TestBmsLibrary Library;
        internal readonly TaskCompletionSource RankingStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource RankingCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool FailedReport;

        private readonly ConcurrentBag<Task> scheduledWorkers = [];

        internal StartupFixture(bool waitForCancellation, bool failFirstRequest = true, bool exerciseReadFailure = false, bool exerciseSaveFailure = false)
        {
            bool exerciseRankingCache = exerciseReadFailure || exerciseSaveFailure;
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
            Client = new ControlledIrClient(waitForCancellation, failFirstRequest, emptyFirstRequest: exerciseReadFailure);
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
            Library.StartupBackgroundTaskReporter = (name, status, _, failed, _) =>
            {
                if (name != "ranking_refresh_deferred")
                {
                    return;
                }

                if (status == "start")
                {
                    RankingStarted.TrySetResult();
                }

                if (failed)
                {
                    FailedReport = true;
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
            LR2IRScore score = gateway.LoadIrScoreRows().Single();
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

    internal sealed class ControlledIrClient(bool waitForCancellation, bool failFirstRequest, bool ignoreCancellation = false, bool emptyFirstRequest = false) : IBmsLibraryIrClient
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
                    return "<root></root>";
                }

                return "<root>\n\t<score>\n\t\t<hash>abcdefabcdefabcdefabcdefabcdefab</hash>\n"
                    + "\t\t<clear>4</clear>\n\t\t<notes>1000</notes>\n\t\t<combo>900</combo>\n"
                    + "\t\t<pg>500</pg>\n\t\t<gr>100</gr>\n\t\t<gd>10</gd>\n\t\t<bd>2</bd>\n"
                    + "\t\t<pr>1</pr>\n\t\t<minbp>12</minbp>\n\t\t<option>0</option>\n"
                    + "\t\t<lastupdate>20261010</lastupdate>\n\t</score>\n</root>\n";
            }
            finally { Completed.TrySetResult(); }
        }

        public List<BMSLibrary.IRDataCacheInfo> GetRankingInfo(Uri rankingInfoUrl, IEnumerable<string> md5s)
            => throw new AssertFailedException("ランキングキャッシュの通信は対象外。");

        public void DownloadRankingData(Uri rankingDataUrl, string md5, string destinationPath)
            => throw new AssertFailedException("ランキングキャッシュの通信は対象外。");
    }
}
