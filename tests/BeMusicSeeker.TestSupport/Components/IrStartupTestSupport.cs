#nullable disable
using System.Collections.Concurrent;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>同じ機能の複数moduleが必要とする入力・観測だけを共用します。</summary>
internal static class IrStartupTestSupport
{

    /// <remarks>分割先のケースが同じ入力・観測を共用します。モデル・Task・媒体の所有と終端は元の呼出元・fixtureに従います。</remarks>
    internal sealed class StartupFixture : IAsyncDisposable
    {
        private readonly TestWorkspace ownedWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        internal readonly ApplicationDatabaseScope databaseScope;
        internal const string IrHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        internal const string CacheHash = "cccccccccccccccccccccccccccccccc";
        private readonly bool exercisePublication;
        internal const string Hash = "abcdefabcdefabcdefabcdefabcdefab";
        private readonly string root;
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

        /// <summary>呼出し元アプリと同じDB所有者で、起動時IR処理の専用入力とライブラリを準備します。</summary>
        /// <param name="databaseScope">接続と終了待機を共有するアプリケーションのDB所有者。</param>
        /// <param name="waitForCancellation">IR要求を取消まで保留するか。</param>
        /// <param name="failFirstRequest">最初のIR要求を失敗させるか。</param>
        /// <param name="exerciseReadFailure">保存済みIR読取りの失敗を準備するか。</param>
        /// <param name="exerciseSaveFailure">IR保存の失敗を準備するか。</param>
        /// <param name="exercisePublication">ランキング結果の公開を観測するか。</param>
        /// <param name="exerciseRankingReadFailure">ランキング読取りの失敗を準備するか。</param>
        /// <param name="exerciseUnrecognizedResponse">認識不能なIR応答を準備するか。</param>
        /// <param name="exerciseDerivedValues">保存済みスコアとランキング由来の値を準備するか。</param>
        internal StartupFixture(ApplicationDatabaseScope databaseScope, bool waitForCancellation, bool failFirstRequest = true, bool exerciseReadFailure = false, bool exerciseSaveFailure = false, bool exercisePublication = false, bool exerciseRankingReadFailure = false, bool exerciseUnrecognizedResponse = false, bool exerciseDerivedValues = false)
        {
            try
            {
                root = Path.Combine(ownedWorkspace.RootDirectory, "BmsIrStartup", Guid.NewGuid().ToString("N"));
                this.databaseScope = databaseScope;
                this.exercisePublication = exercisePublication;
                bool exerciseRankingCache = exerciseReadFailure || exerciseSaveFailure || exercisePublication || exerciseRankingReadFailure || exerciseUnrecognizedResponse || exerciseDerivedValues;
                Directory.CreateDirectory(root);
                string songPath = Path.Combine(root, "song.db");
                string scorePath = exerciseRankingCache
                    ? Path.Combine(root, "LR2files", "Database", "score.db")
                    : Path.Combine(root, "score.db");
                Directory.CreateDirectory(Path.GetDirectoryName(scorePath));
                using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songPath))
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
                        if (exerciseDerivedValues)
                        {
                            db.Insert(new LR2SongDB.song { hash = CacheHash, path = Path.Combine(root, "new-cache.bms"), title = "New cache chart" });
                        }
                    });
                }
                using (LR2ScoreDBExtended db = databaseScope.OpenScoreWrite(scorePath))
                {
                    db.RunInTransaction(() =>
                    {
                        db.CreateTable<LR2ScoreDB.player>();
                        db.CreateTable<LR2ScoreDB.score>();
                        db.Insert(new LR2ScoreDB.player { id = "ir-startup", irid = 123 });
                        var savedScore = new LR2ScoreDB.score { hash = Hash, perfect = 321, great = 45 };
                        if (exerciseDerivedValues)
                        {
                            savedScore.totalnotes = 500;
                            savedScore.rate = 68;
                            savedScore.rank = RankType.A;
                            savedScore.clear = ClearType.HARD;
                        }
                        db.Insert(savedScore);
                        if (exerciseRankingReadFailure)
                        {
                            db.Insert(new LR2ScoreDB.score { hash = CacheHash, perfect = 222, great = 33 });
                        }
                    });
                }
                gateway = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songPath, scorePath);
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
                if (exerciseDerivedValues)
                {
                    string cachePath = Path.Combine(root, "Ir", Hash + ".xml");
                    File.WriteAllText(cachePath, File.ReadAllText(cachePath).Replace("<pg>321</pg>", "<pg>800</pg>")
                        .Replace("<gr>45</gr>", "<gr>200</gr>").Replace("<clear>0</clear>", "<clear>4</clear>"), Encoding.UTF8);
                    File.Copy(cachePath, Path.Combine(root, "Ir", CacheHash + ".xml"));
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
                var lr2Owner = new Lr2ConfigOwner(TestLr2ConfigFactory.CreateLr2Config(root));
                Library = new TestBmsLibrary(databaseScope, songPath, lr2Owner, scorePath, null, () => options,
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
            catch (Exception primaryFailure)
            {
                try { ownedWorkspace.Dispose(); }
                catch (Exception cleanupFailure) { throw new AggregateException(primaryFailure, cleanupFailure); }
                throw;
            }
        }

        internal string SongDbPath => Path.Combine(root, "song.db");

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
            Directory.Delete(root, recursive: true); ownedWorkspace.Dispose();
        }
    }

    /// <remarks>分割先のケースが同じ入力・観測を共用します。モデル・Task・媒体の所有と終端は元の呼出元・fixtureに従います。</remarks>
    internal sealed class ControlledIrClient(bool waitForCancellation, bool failFirstRequest, bool ignoreCancellation = false, bool emptyFirstRequest = false, string extraScoreHash = null, bool unrecognizedFirstResponse = false) : IBmsLibraryIrClient
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int RequestCount;

        public string GetPlayerScoresXml(int lr2Id, CancellationToken cancellationToken = default)
            => FetchAsync(cancellationToken).GetAwaiter().GetResult();

        internal async Task<string> FetchAsync(CancellationToken cancellationToken)
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

    }

}
