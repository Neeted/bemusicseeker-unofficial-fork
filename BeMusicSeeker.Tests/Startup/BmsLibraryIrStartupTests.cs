#nullable disable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    [TestMethod]
    public async Task RequestShutdown_CancelsInflightIrAndDrainsRankingBeforeBecomingIdle()
    {
        await using var fixture = new StartupFixture(waitForCancellation: true);
        await fixture.InitializeAsync();
        await fixture.Client.Started.Task;
        await fixture.RankingStarted.Task;
        BMSScore before = fixture.Library.GetBMSScores().Single();
        fixture.Library.RequestShutdown("ir-test");
        try { await fixture.Client.Cancelled.Task; }
        catch (TimeoutException) { Assert.Fail("shutdown のキャンセルが開始済み IR 取得へ伝播していない。"); }
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

    /// <summary>実XMLの通信・解析と確定を分け、現モデルはL解放後に保存し、退役モデルは保存しません。</summary>
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

        internal StartupFixture(bool waitForCancellation, bool failFirstRequest = true)
        {
            Directory.CreateDirectory(root);
            string songPath = Path.Combine(root, "song.db");
            string scorePath = Path.Combine(root, "score.db");
            using (var db = new LR2SongDBExtended(songPath))
            {
                db.CreateTable<LR2SongDB.song>();
                db.CreateTable<LR2SongDB.folder>();
                db.CreateTable<LR2SongDBExtended.install>();
                db.CreateTable<LR2SongDBExtended.ir_score>();
                db.CreateTable<LR2SongDBExtended.ir_score_refresh_metadata>();
            }
            using (var db = new LR2ScoreDBExtended(scorePath))
            {
                db.CreateTable<LR2ScoreDB.player>();
                db.CreateTable<LR2ScoreDB.score>();
                db.Insert(new LR2ScoreDB.player { id = "ir-startup", irid = 123 });
                db.Insert(new LR2ScoreDB.score { hash = Hash, perfect = 321, great = 45 });
            }
            gateway = new BmsLibraryDbGateway(songPath, scorePath);
            gateway.ReplaceIrScoreTable([new LR2IRScore { hash = Hash, pg = 123, gr = 67 }]);
            gateway.UpsertIrScoreRefreshMetadata(123, "retained-startup-digest");
            metadataUpdatedAt = gateway.LoadIrScoreRefreshMetadata(123).updated_at;
            Client = new ControlledIrClient(waitForCancellation, failFirstRequest);
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
                LR2RootPath = root,
                EnableDownloadLr2IrScoreAndDetectUnsent = true,
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

    internal sealed class ControlledIrClient(bool waitForCancellation, bool failFirstRequest, bool ignoreCancellation = false) : IBmsLibraryIrClient
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
