#nullable disable
using System;
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
        await fixture.Client.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        BMSScore before = fixture.Library.GetBMSScores().Single();
        fixture.Client.Release.TrySetResult();
        await fixture.RankingCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));

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
        await fixture.Client.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.RankingStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        BMSScore before = fixture.Library.GetBMSScores().Single();
        fixture.Library.RequestShutdown("ir-test");
        try { await fixture.Client.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { Assert.Fail("shutdown のキャンセルが開始済み IR 取得へ伝播していない。"); }
        Assert.IsTrue(fixture.Library.HasShutdownBlockingWork, "通信が完了するまでは idle にしない。");
        fixture.Client.Release.TrySetResult();
        await fixture.Client.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.RankingCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsFalse(fixture.Library.HasShutdownBlockingWork);
        Assert.AreEqual(1, fixture.Client.RequestCount);
        Assert.AreSame(before, fixture.Library.GetBMSScores().Single());
        Assert.AreEqual(321, before.perfect);
        fixture.AssertDatabaseRetained();
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

        internal StartupFixture(bool waitForCancellation)
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
            Client = new ControlledIrClient(waitForCancellation);
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
            Library.StartupBackgroundTaskScheduler = (_, _, _, _) => false;
            Library.StartupBackgroundTaskReporter = (name, status, _, failed, _) =>
            {
                if (name != "ranking_refresh_deferred") return;
                if (status == "start") RankingStarted.TrySetResult();
                if (failed) FailedReport = true;
            };
            Library.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(BMSLibrary.RankingRefreshRunning) && !Library.RankingRefreshRunning)
                    RankingCompleted.TrySetResult();
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

        public async ValueTask DisposeAsync()
        {
            Library.RequestShutdown("ir-fixture-cleanup");
            Client.Release.TrySetResult();
            if (Client.Started.Task.IsCompleted)
                await Client.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (Library.RankingRefreshRunning)
                await RankingCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ControlledIrClient(bool waitForCancellation) : IBmsLibraryIrClient
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
                if (waitForCancellation) await Task.WhenAny(Cancelled.Task, Release.Task);
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(10));
                cancellationToken.ThrowIfCancellationRequested();
                if (RequestCount == 1) throw new IOException("controlled unavailable IR");
                return "<scores />";
            }
            finally { Completed.TrySetResult(); }
        }

        public List<BMSLibrary.IRDataCacheInfo> GetRankingInfo(Uri rankingInfoUrl, IEnumerable<string> md5s)
            => throw new AssertFailedException("ランキングキャッシュの通信は対象外。");

        public void DownloadRankingData(Uri rankingDataUrl, string md5, string destinationPath)
            => throw new AssertFailedException("ランキングキャッシュの通信は対象外。");
    }
}
