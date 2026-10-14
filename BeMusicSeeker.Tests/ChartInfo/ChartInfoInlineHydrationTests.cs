using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Tests.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.ChartInfoMetadataTestSupport;
namespace BeMusicSeeker.Tests;

/// <summary>
/// 譜面情報の対象検索・補完・inline評価・候補分類を検査します。
/// </summary>
[TestClass]
public sealed class ChartInfoInlineHydrationTests
{
    private readonly BeMusicSeeker.Properties.Settings testSettings = MainWindowViewModelTestFactory.CreateIsolatedSettings();

    /// <summary>
    /// 保存済み情報の必須反映は任意候補要約を待たずに終端し、後続は元要求を保持して補完する。終了先着時は要約・補完を始める誤りを検出する。
    /// </summary>
    /// <remarks>専用SQLiteに異なる現行条件の保存行を投入し、候補要約の実検索を確認する。合成した要約だけでDB条件の保証を代用しない。</remarks>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RequiredHydration_DefersCandidateSummaryAndBackfillWithOriginalRequest(bool shutdownBeforeFollowup)
    {
        await WithTemporarySongDb(async (root, songDbPath) =>
        {
            string currentMd5 = new('a', 32);
            string currentSha = new('1', 64);
            string currentPath = Path.Combine(root, "current.bms");
            string missingPath = Path.Combine(root, "missing-info.bms");
            File.WriteAllText(missingPath, "#PLAYER 1\n#TITLE followup\n#BPM 130\n#00111:01\n", Encoding.ASCII);
            ChartFile missing = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(missingPath));
            ChartFile current = ChartTestValues.Empty() with { Path = currentPath, Md5 = currentMd5, Sha256 = currentSha };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(db);
                BmsLibraryDbGateway.EnsureChartInfoSchema(db);
                InsertSongForSummary(db, currentPath, currentMd5);
                InsertSongForSummary(db, missingPath, missing.Md5);
                db.InsertOrReplace(CreateChartDigestRow(currentMd5, currentSha), typeof(LR2SongDBExtended.chart_digest_map));
                db.InsertOrReplace(ChartInfoStorageMapping.ToStorage(CreateChartInfoRow(currentSha, currentMd5,
                    BmsLibraryDbGateway.CurrentChartInfoParserVersion)), typeof(LR2SongDBExtended.chart_info));
            }
            var gateway = new BmsLibraryDbGateway(songDbPath);
            var rows = new CatalogOwnedCollectionOwner();
            rows.ReplaceCharts([current, missing], null, replaceBmson: false);
            var mutation = new CatalogMutationOwner(rows, gateway);
            var admission = new ChartFileOperationSynchronizer();
            Assert.IsTrue(admission.TryEnter(out IDisposable parent));
            var summaryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var releaseSummary = new ManualResetEventSlim();
            var workCaptured = new TaskCompletionSource<Func<Task>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var logs = new ConcurrentQueue<string>();
            var progress = new ConcurrentQueue<(OperationProgressRequest Request, bool Running)>();
            bool shutdown = false;
            var owner = new CatalogChartInfoOwner(_ => { }, () => shutdown, (_, _) => shutdown,
                () => (name, reason, dependency, work) =>
                {
                    Assert.AreEqual("chart_info_backfill", name);
                    Assert.IsNull(dependency);
                    workCaptured.TrySetResult(work);
                    return true;
                }, message =>
                {
                    logs.Enqueue(message);
                    if (message.StartsWith("chart_info_backfill candidate_summary_start", StringComparison.Ordinal))
                    {
                        Assert.IsTrue(admission.IsActive);
                        summaryEntered.TrySetResult();
                        releaseSummary.Wait();
                    }
                }, admission);
            owner.ConfigureWorkflow(gateway, mutation, rows, _ => { }, _ => { });
            owner.RequestProgressReporter = (request, running) => progress.Enqueue((request, running));
            var origin = new OperationProgressRequest(7, 11, "required_initialization", 1);
            Task<RequiredChartInfoHydrationResult> required = Task.Run(() => owner.HydrateRequiredForStartup("required_initialization", origin));
            Task? followup = null;
            try
            {
                await Task.WhenAny(required, summaryEntered.Task);
                Assert.IsTrue(required.IsCompleted, "必須読込みが任意の候補要約を待っています。");
                RequiredChartInfoHydrationResult result = await required;
                Assert.IsFalse(workCaptured.Task.IsCompleted, "必須読込みが任意補完を暗黙予約しています。");
                owner.ScheduleRequiredHydrationBackfill("required_initialization", result);
                Assert.IsTrue(workCaptured.Task.IsCompletedSuccessfully);
                Assert.IsFalse(summaryEntered.Task.IsCompleted);
                Assert.IsNotNull(owner.ResolveChartInfo(currentSha, currentMd5));
                Assert.AreEqual(owner.ChartInfoHydrationRequestedVersion, owner.ChartInfoHydrationCompletedVersion);
                Assert.AreEqual(0, owner.ChartInfoBackfillRequestedVersion);
                parent.Dispose();
                Assert.IsFalse(admission.IsActive);
                shutdown = shutdownBeforeFollowup;
                followup = Task.Run(await workCaptured.Task);
                if (!shutdownBeforeFollowup)
                {
                    await Task.WhenAny(summaryEntered.Task, followup);
                    Assert.IsTrue(summaryEntered.Task.IsCompletedSuccessfully);
                    Assert.IsFalse(followup.IsCompleted);
                    Assert.IsTrue(required.IsCompletedSuccessfully);
                    releaseSummary.Set();
                }
                await followup;
                Assert.IsFalse(admission.IsActive);
                if (shutdownBeforeFollowup)
                {
                    Assert.IsFalse(summaryEntered.Task.IsCompleted);
                    Assert.AreEqual(0, owner.ChartInfoBackfillRequestedVersion);
                }
                else
                {
                    Assert.IsTrue(logs.Any(message => message.StartsWith("chart_info_backfill candidate_summary_done", StringComparison.Ordinal)));
                    Assert.IsNotNull(owner.ResolveChartInfo(missing.Sha256, missing.Md5));
                    OperationProgressRequest backfill = owner.BackfillProgressRequest;
                    Assert.AreEqual(origin.Generation, backfill.Generation);
                    Assert.AreEqual(origin.OperationToken, backfill.OperationToken);
                    Assert.AreEqual("chart_info_backfill", backfill.Source);
                    Assert.IsTrue(progress.Contains((backfill, true)));
                    Assert.IsTrue(progress.Contains((backfill, false)));
                    Assert.AreEqual(owner.ChartInfoBackfillRequestedVersion, owner.ChartInfoBackfillCompletedVersion);
                }
            }
            finally
            {
                releaseSummary.Set();
                parent.Dispose();
                await required;
                if (followup != null) { await followup; }
            }
        });
    }

    /// <summary>
    /// 背景失敗行を16から128へ増やしても対象一件だけを読取り、実SQLiteの全走査数とVM処理が背景件数に比例する退行を検出する。
    /// </summary>
    /// <remarks>実SQLiteの対象検索を使う。SQL観測/共有Monitor保持条件は、結果配列だけでは分からない対象読取りの境界を確かめるために必要。</remarks>
    [TestMethod]
    public void LoadCurrentChartInfoParseFailuresByMd5_QueriesOnlyRequestedRows()
    {
        TargetedFailureQueryRun smallRun = ExecuteTargetedFailureQueryCase(backgroundCount: 16);
        TargetedFailureQueryRun largeRun = ExecuteTargetedFailureQueryCase(backgroundCount: 128);

        Assert.AreEqual(1, smallRun.ReturnedRows);
        Assert.AreEqual(1, largeRun.ReturnedRows);
        Assert.IsTrue(smallRun.ProfileCount > 0, "16行背景の対象 failure query のPROFILE callbackを観測できませんでした。");
        Assert.IsTrue(largeRun.ProfileCount > 0, "128行背景の対象 failure query のPROFILE callbackを観測できませんでした。");
        Assert.AreEqual(
            smallRun.FullScanSteps,
            largeRun.FullScanSteps,
            "対象 failure query の FULLSCAN_STEP が背景行数に依存しました。16="
            + smallRun.FullScanSteps + ", 128=" + largeRun.FullScanSteps);
        Assert.AreEqual(
            smallRun.VmSteps,
            largeRun.VmSteps,
            "対象 failure query の VM_STEP が背景行数に依存しました。16="
            + smallRun.VmSteps + ", 128=" + largeRun.VmSteps);
    }

    /// <summary>
    /// 対象MD5が空なら空結果を返して失敗表を読まず、不要な全失敗行検索を始める誤りを実SQL観測で検出する。
    /// </summary>
    /// <remarks>実SQLiteの対象検索を使う。SQL観測/共有Monitor保持条件は、結果配列だけでは分からない対象読取りの境界を確かめるために必要。</remarks>
    [TestMethod]
    public void LoadCurrentChartInfoParseFailuresByMd5_EmptyInputDoesNotReadFailureTable()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            using var observation = new SqliteStatementObservation();
            var gateway = new BmsLibraryDbGateway(songDbPath, songDbFactory: observation.OpenSongDb);

            Dictionary<string, BeMusicSeeker.Models.ChartParseFailure> result = gateway.LoadCurrentChartInfoParseFailuresByMd5(
                [],
                TimeSpan.FromSeconds(60));
            observation.ThrowIfCallbackFailed();

            Assert.AreEqual(0, result.Count);
            Assert.IsFalse(
                observation.Statements.Any(statement => statement.Sql.Contains(
                    "chart_info_parse_failure",
                    StringComparison.OrdinalIgnoreCase)),
                "空の対象集合で failure table への SQL が実行されました。");
        });
    }

    /// <summary>
    /// 対象失敗のparser版とtimeout条件を照合し、古いparserや短い期限の失敗の再利用と、旧未知例外名による現行記録の救済削除を検出する。
    /// </summary>
    /// <remarks>実SQLiteの対象検索を使う。SQL観測/共有Monitor保持条件は、結果配列だけでは分からない対象読取りの境界を確かめるために必要。</remarks>
    [TestMethod]
    public void LoadCurrentChartInfoParseFailuresByMd5_PreservesParserAndTimeoutCurrentness()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string currentMd5 = new('a', 32);
            string staleParserMd5 = new('b', 32);
            string shortTimeoutMd5 = new('c', 32);
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoSchema();
            gateway.UpsertChartInfoParseFailures(
            [
                CreateChartInfoParseFailureRow(
                    currentMd5,
                    new string('1', 64),
                    Path.Combine(tempRootPath, "current.bms"),
                    BmsLibraryDbGateway.CurrentChartInfoParserVersion,
                    "parse_failed",
                    "InvalidOperationException",
                    "current",
                    null),
                CreateChartInfoParseFailureRow(
                    staleParserMd5,
                    new string('2', 64),
                    Path.Combine(tempRootPath, "stale-parser.bms"),
                    BmsLibraryDbGateway.CurrentChartInfoParserVersion - 1,
                    "parse_failed",
                    "InvalidDataException",
                    "stale parser",
                    null),
                CreateChartInfoParseFailureRow(
                    shortTimeoutMd5,
                    new string('3', 64),
                    Path.Combine(tempRootPath, "short-timeout.bms"),
                    BmsLibraryDbGateway.CurrentChartInfoParserVersion,
                    "timeout",
                    "ChartInfoParseTimeoutException",
                    "short timeout",
                    500)
            ]);

            Dictionary<string, BeMusicSeeker.Models.ChartParseFailure> result = gateway.LoadCurrentChartInfoParseFailuresByMd5(
                [currentMd5, staleParserMd5, shortTimeoutMd5],
                TimeSpan.FromSeconds(1));

            Assert.AreEqual(1, result.Count);
            Assert.IsTrue(result.ContainsKey(currentMd5));
            Assert.IsFalse(result.ContainsKey(staleParserMd5));
            Assert.IsFalse(result.ContainsKey(shortTimeoutMd5));
            ChartFile current = ChartTestValues.Empty() with { Path = result[currentMd5].path, Md5 = currentMd5, Sha256 = result[currentMd5].sha256 };
            var service = new ChartInfoBuildService(null, parseBytes: (_, _, _, _, _) => throw new AssertFailedException("現行失敗は再評価しません。"));
            var snapshot = new ChartFileSnapshot(current.Path, Encoding.ASCII.GetBytes("#BPM 120\r\n#00111:01\r\n"), DateTime.UtcNow, current.Md5, current.Sha256);
            ChartInfoBuildService.ChartInfoSnapshotBuildResult skipped = service.EvaluateSnapshot(snapshot, ChartInfoBuildTargetMapper.Create(current), null, result.ContainsKey(currentMd5));
            Assert.IsTrue(skipped.SkippedPersistedFailure);
            Assert.IsFalse(skipped.ParseFailed);
            Assert.IsNull(skipped.ParseFailureRow);
            Assert.IsTrue(string.IsNullOrEmpty(skipped.ParseFailureDeleteMd5));
            using var verify = new LR2SongDBExtended(songDbPath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            Assert.AreEqual("InvalidOperationException", verify.ExecuteScalar<string>("SELECT exception_type FROM chart_info_parse_failure WHERE md5 = ?;", currentMd5));
            Assert.AreEqual(3, verify.ExecuteScalar<int>("SELECT COUNT(1) FROM chart_info_parse_failure;"));
        });
    }

    /// <summary>
    /// 指定SHAだけを読み、同じMD5に複数SHAがある場合は安定した代表を選ぶ。無関係行の混入と投入順に依存する代表選択を検出する。
    /// </summary>
    /// <remarks>実SQLiteの対象検索を使う。SQL観測/共有Monitor保持条件は、結果配列だけでは分からない対象読取りの境界を確かめるために必要。</remarks>
    [TestMethod]
    public void LoadChartInfosByHash_LoadsRequestedRowsAndUsesStableMd5Representative()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            var gateway = new BmsLibraryDbGateway(songDbPath);
            string md5 = new('a', 32);
            string firstSha = new('1', 64);
            string secondSha = new('2', 64);
            string unrelatedSha = new('3', 64);
            gateway.UpsertChartInfos(
            [
                CreateChartInfoRow(secondSha, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion),
                CreateChartInfoRow(firstSha, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion),
                CreateChartInfoRow(unrelatedSha, new string('b', 32), BmsLibraryDbGateway.CurrentChartInfoParserVersion)
            ]);

            Dictionary<string, BeMusicSeeker.Models.ChartDetails> bySha256 = gateway.LoadChartInfosBySha256([secondSha]);
            Dictionary<string, BeMusicSeeker.Models.ChartDetails> byMd5 = gateway.LoadChartInfosByMd5([md5]);

            Assert.AreEqual(1, bySha256.Count);
            Assert.AreEqual(secondSha, bySha256[secondSha].sha256);
            Assert.AreEqual(1, byMd5.Count);
            Assert.AreEqual(firstSha, byMd5[md5].sha256);
            Assert.IsFalse(bySha256.ContainsKey(unrelatedSha));
        });
    }

    /// <summary>
    /// 共有書込みMonitorを別workerが保持しても現行schemaの読取りが保持中に完了し、不必要に書込みlockへ入る誤りを検出する。
    /// </summary>
    /// <remarks>実SQLiteの対象検索を使う。SQL観測/共有Monitor保持条件は、結果配列だけでは分からない対象読取りの境界を確かめるために必要。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public async Task LoadChartInfosByHash_UsesReadOnlyCurrentSchemaWhileProcessLockHeld()
    {
        await WithTemporarySongDb(async (tempRootPath, songDbPath) =>
        {
            var gateway = new BmsLibraryDbGateway(songDbPath);
            string md5 = new('a', 32);
            string sha256 = new('1', 64);
            gateway.UpsertChartInfos([CreateChartInfoRow(sha256, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion)]);
            var lockAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var releaseLock = new ManualResetEventSlim(false);
            var lockHolder = Task.Run(() =>
            {
                Assert.IsTrue(LR2SongDBExtended.Lock(TimeSpan.FromSeconds(5)));
                try
                {
                    lockAcquired.TrySetResult();
                    releaseLock.Wait();
                }
                finally
                {
                    LR2SongDBExtended.Unlock();
                }
            });
            Task<Dictionary<string, BeMusicSeeker.Models.ChartDetails>>? lookupTask = null;
            Exception? primaryFailure = null;
            try
            {
                await TestUiDispatcherHost.AwaitNotificationAsync(lockAcquired.Task, lockHolder, "chart-info.write-lock-acquired");
                Assert.IsFalse(lockHolder.IsCompleted);
                lookupTask = Task.Run(() => gateway.LoadChartInfosBySha256([sha256]));
                // 共有書込みlock保持中に読取りが完了するかを観測する既存2秒。
                Task first = await Task.WhenAny(lookupTask, lockHolder, Task.Delay(TimeSpan.FromSeconds(2)));
                Assert.AreSame(lookupTask, first, "chart_info lookup should not wait for the writable process lock when schema is current.");
                Dictionary<string, BeMusicSeeker.Models.ChartDetails> rows = await lookupTask;
                Assert.IsFalse(lockHolder.IsCompleted);
                Assert.AreEqual(1, rows.Count);
                Assert.AreEqual(md5, rows[sha256].md5);
            }
            catch (Exception exception)
            {
                primaryFailure = exception;
            }
            finally
            {
                releaseLock.Set();
                try
                {
                    await Task.WhenAll(lookupTask is null ? new Task[] { lockHolder } : new Task[] { lockHolder, lookupTask });
                }
                catch (Exception cleanupFailure)
                {
                    if (primaryFailure is null)
                    {
                        primaryFailure = cleanupFailure;
                    }
                    else
                    {
                        primaryFailure.Data["WorkerSecondaryDiagnostics"] = cleanupFailure.ToString();
                    }
                }
            }
            if (primaryFailure is not null)
            {
                ExceptionDispatchInfo.Capture(primaryFailure).Throw();
            }
        });
    }

    /// <summary>
    /// 実保存行から索引を補完し、SHA一致をMD5代表より優先する。補完前後の解決と無関係SHAの直接解決を確かめる。
    /// </summary>
    /// <remarks>実BMSLibraryと専用DBを使い、保存行から索引・所有行・facade通知への接続を観測する。管理主体だけの結果ではこの接続を代用できない。</remarks>
    [TestMethod]
    public async Task DeferredChartInfoHydration_BuildsSessionIndexAndUsesSha256BeforeMd5()
    {

        await WithTemporarySongDb(async delegate (string tempRootPath, string songDbPath)
        {
            var gateway = new BmsLibraryDbGateway(songDbPath);
            string md5 = new('a', 32);
            string firstSha = new('1', 64);
            string secondSha = new('2', 64);
            string unrelatedSha = new('3', 64);
            gateway.UpsertChartInfos(
            [
                CreateChartInfoRow(secondSha, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion),
                CreateChartInfoRow(firstSha, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion),
                CreateChartInfoRow(unrelatedSha, new string('b', 32), BmsLibraryDbGateway.CurrentChartInfoParserVersion)
            ]);
            var library = new TestBmsLibrary(songDbPath, null, null, null, new RecordingDialogService(), settings: testSettings);
            var workers = new ConcurrentBag<Task>();
            library.StartupBackgroundTaskScheduler = (_, _, _, work) =>
            {
                workers.Add(Task.Run(work));
                return true;
            };
            Exception? primaryFailure = null;
            try
            {
                Assert.IsFalse(library.ChartInfoIndexHydrated);
                Assert.IsNull(library.ResolveChartInfo(firstSha, md5));

                InvokeDeferredChartInfoHydration(library, "unit_test", queueFullBackfillAfterHydration: false);

                await TestUiDispatcherHost.AwaitNotificationAsync(
                    AwaitChartInfoHydrationAsync(library), Task.WhenAll(workers), "chart-info.hydration-completion");
                await Task.WhenAll(workers);
                Assert.IsTrue(library.ChartInfoIndexHydrated);
                Assert.IsTrue(library.ChartInfoIndexVersion > 0);
                Assert.AreEqual(secondSha, library.ResolveChartInfo(secondSha, md5).sha256, "sha256 match should win over md5 fallback.");
                Assert.AreEqual(firstSha, library.ResolveChartInfo(null, md5).sha256, "md5 fallback should use the stable sha256-ordered representative.");
                Assert.AreEqual(unrelatedSha, library.ResolveChartInfo(unrelatedSha, null).sha256);
            }
            catch (Exception exception)
            {
                primaryFailure = exception;
            }
            finally
            {
                try
                {
                    await Task.WhenAll(workers);
                }
                catch (Exception cleanupFailure)
                {
                    if (primaryFailure is null) { primaryFailure = cleanupFailure; }
                    else { primaryFailure.Data["WorkerSecondaryDiagnostics"] = cleanupFailure.ToString(); }
                }
            }
            if (primaryFailure is not null)
            {
                ExceptionDispatchInfo.Capture(primaryFailure).Throw();
            }
        });
    }

    /// <summary>
    /// 同内容の二つのsnapshotを一回の解析にまとめつつ両パスへ保存適用を渡し、重複解析と片側の反映欠落を検出する。
    /// </summary>
    /// <remarks>実ファイルsnapshotとDBの既存情報照合を使い、同MD5の解析集約から各パスへの保存適用結果を観測する。commit自体は保存ケースが分担する。</remarks>
    [TestMethod]
    public void ChartInfoInlineBuildService_GroupsDuplicateBmsMd5AndFansOutStorageApplications()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string firstPath = Path.Combine(tempRootPath, "duplicate-a.bms");
            string secondPath = Path.Combine(tempRootPath, "duplicate-b.bms");
            const string content = "#PLAYER 1\r\n#TITLE duplicate\r\n#PLAYLEVEL 9\r\n#BPM 140\r\n#00111:01\r\n";
            File.WriteAllText(firstPath, content, Encoding.ASCII);
            File.WriteAllText(secondPath, content, Encoding.ASCII);
            ChartFileSnapshot firstSnapshot = ChartFileContentReader.ReadSnapshot(firstPath);
            ChartFileSnapshot secondSnapshot = ChartFileContentReader.ReadSnapshot(secondPath);
            ChartFile firstFile = (ChartTestValues.Empty() with { Path = firstPath });
            ChartFile secondFile = (ChartTestValues.Empty() with { Path = secondPath });
            firstFile = firstFile with { Md5 = firstSnapshot.Md5 };
            secondFile = secondFile with { Md5 = secondSnapshot.Md5 };
            var service = new ChartInfoInlineBuildService(new ChartInfoBuildService(), parserDegree: 1);

            ChartInfoInlineBuildResult result = service.BuildForSnapshots(
                new BmsLibraryDbGateway(songDbPath),
                [
                    InlineChartSnapshotTarget.FromChart((firstFile), firstSnapshot),
                    InlineChartSnapshotTarget.FromChart((secondFile), secondSnapshot)
                ],
                new Dictionary<string, BeMusicSeeker.Models.ChartParseFailure>(StringComparer.OrdinalIgnoreCase));

            Assert.AreEqual(firstSnapshot.Md5, secondSnapshot.Md5);
            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(1, result.SuccessCount);
            Assert.AreEqual(1, result.ChartInfoRows.Count);
            Assert.AreEqual(1, result.AppliedRows.Count);
            Assert.AreEqual(2, result.StorageApplications.Count);
            CollectionAssert.AreEquivalent(
                new[] { firstPath, secondPath },
                result.StorageApplications.Select(application => application.Chart.Path).ToArray());
            Assert.IsTrue(result.StorageApplications.All(application =>
                application.Row == result.ChartInfoRows[0]));
        });
    }

    /// <summary>
    /// 読取りbatchを跨ぐ同MD5も一回の解析にまとめて両パスへ適用し、batch境界で重複排除を失う誤りを検出する。
    /// </summary>
    /// <remarks>実ファイルsnapshotとDBの既存情報照合を使い、同MD5の解析集約から各パスへの保存適用結果を観測する。commit自体は保存ケースが分担する。</remarks>
    [TestMethod]
    public void ChartInfoInlineBuildService_GroupsDuplicateBmsMd5AcrossReadBatches()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string firstPath = Path.Combine(tempRootPath, "duplicate-batch-a.bms");
            string secondPath = Path.Combine(tempRootPath, "duplicate-batch-b.bms");
            const string content = "#PLAYER 1\r\n#TITLE duplicate batch\r\n#PLAYLEVEL 6\r\n#BPM 125\r\n#00111:01\r\n";
            File.WriteAllText(firstPath, content, Encoding.ASCII);
            File.WriteAllText(secondPath, content, Encoding.ASCII);
            ChartFileSnapshot firstSnapshot = ChartFileContentReader.ReadSnapshot(firstPath);
            ChartFile firstFile = (ChartTestValues.Empty() with { Path = firstPath });
            ChartFile secondFile = (ChartTestValues.Empty() with { Path = secondPath });
            firstFile = firstFile with { Md5 = firstSnapshot.Md5 };
            secondFile = secondFile with { Md5 = firstSnapshot.Md5 };
            var service = new ChartInfoInlineBuildService(
                new ChartInfoBuildService(),
                parserDegree: 1,
                batchSizeOverride: 1);

            ChartInfoInlineBuildResult result = service.BuildForExistingCharts(
                new BmsLibraryDbGateway(songDbPath),
                [
                    (firstFile),
                    (secondFile)
                ]);

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(1, result.SuccessCount);
            Assert.AreEqual(1, result.ChartInfoRows.Count);
            Assert.AreEqual(1, result.AppliedRows.Count);
            Assert.AreEqual(2, result.StorageApplications.Count);
            CollectionAssert.AreEquivalent(
                new[] { firstPath, secondPath },
                result.StorageApplications.Select(application => application.Chart.Path).ToArray());
        });
    }

    /// <summary>
    /// 現行保存行と現行失敗が併存しても保存行を再利用して適用し、不要な再解析や失敗側を優先する誤りを検出する。
    /// </summary>
    /// <remarks>実DBの現行保存行を解析serviceへ接続し、再利用判定と適用結果を観測する。ここでは新規commitを保証しない。</remarks>
    [TestMethod]
    public void ChartInfoInlineBuildService_AppliesExistingCurrentRowWithoutParsing()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "already-current.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE current\r\n", Encoding.ASCII);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = chartPath
            };
            file = file with { Md5 = snapshot.Md5 };
            file = file with { Sha256 = snapshot.Sha256 };
            var gateway = new BmsLibraryDbGateway(songDbPath);
            BeMusicSeeker.Models.ChartDetails expected = CreateChartInfoRow(file.Sha256, file.Md5, parserVersion: BmsLibraryDbGateway.CurrentChartInfoParserVersion);
            gateway.UpsertChartInfos([expected]);
            var service = new ChartInfoInlineBuildService(new ChartInfoBuildService(), parserDegree: 1);
            var currentFailures = new Dictionary<string, BeMusicSeeker.Models.ChartParseFailure>(StringComparer.OrdinalIgnoreCase)
            {
                [file.Md5] = CreateChartInfoParseFailureRow(
                    file.Md5,
                    file.Sha256,
                    chartPath,
                    BmsLibraryDbGateway.CurrentChartInfoParserVersion,
                    "parse_failed",
                    "InvalidDataException",
                    "current failure must lose to current info",
                    null)
            };

            ChartInfoInlineBuildResult result = service.BuildForSnapshots(
                gateway,
                [InlineChartSnapshotTarget.FromChart((file), snapshot)],
                currentFailures);

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(0, result.SuccessCount);
            Assert.AreEqual(1, result.CurrentSkippedCount);
            Assert.AreEqual(1, result.AppliedRows.Count);
            Assert.AreEqual(expected.sha256, result.AppliedRows[0].sha256);
            Assert.AreEqual(expected.md5, result.AppliedRows[0].md5);
        });
    }

    /// <summary>
    /// 解析不能なbytesでも同一identityの現行行を優先し、現行失敗で有効保存行を隠したり再解析したりする誤りを検出する。
    /// </summary>
    [TestMethod]
    public void ChartInfoSnapshotEvaluator_CurrentRowTakesPrecedenceOverCurrentFailure()
    {
        string md5 = new('a', 32);
        string sha256 = new('b', 64);
        byte[] bytes = Encoding.ASCII.GetBytes("not a parseable chart");
        ChartFile file = (ChartTestValues.Empty() with { Path = @"C:\Charts\current.bms" });
        file = file with { Md5 = md5 };
        file = file with { Sha256 = sha256 };
        var snapshot = new ChartFileSnapshot(file.Path, bytes, DateTime.UtcNow, md5, sha256);
        ChartInfoBuildTarget target = ChartInfoBuildTargetMapper.Create(
            (file));
        BeMusicSeeker.Models.ChartDetails currentRow = CreateChartInfoRow(
            sha256,
            md5,
            BmsLibraryDbGateway.CurrentChartInfoParserVersion);

        ChartInfoBuildService.ChartInfoSnapshotBuildResult result = new ChartInfoBuildService().EvaluateSnapshot(
            snapshot,
            target,
            currentRow,
            hasCurrentParseFailure: true);

        Assert.AreSame(currentRow, result.Row);
        Assert.IsTrue(result.CurrentRowSkipped);
        Assert.IsFalse(result.SkippedPersistedFailure);
        Assert.IsFalse(result.ParseFailed);
    }

    /// <summary>
    /// MD5不一致の保存行を再利用せず実snapshotを解析し、失敗を対象identityと一行の長さ制限付きmessageへ正規化することを確かめる。
    /// </summary>
    [TestMethod]
    public void ChartInfoSnapshotEvaluator_IdentityMismatchParsesAndNormalizesFailure()
    {
        string md5 = new('c', 32);
        string sha256 = new('d', 64);
        byte[] bytes = Encoding.ASCII.GetBytes("#PLAYER 1\r\n#TITLE bad\r\n#00111:01\r\n");
        ChartFile file = (ChartTestValues.Empty() with { Path = @"C:\Charts\mismatch.bms" });
        file = file with { Md5 = md5 };
        file = file with { Sha256 = sha256 };
        var snapshot = new ChartFileSnapshot(file.Path, bytes, DateTime.UtcNow, md5, sha256);
        ChartInfoBuildTarget target = ChartInfoBuildTargetMapper.Create(
            (file));
        BeMusicSeeker.Models.ChartDetails incompatibleRow = CreateChartInfoRow(
            sha256,
            new string('e', 32),
            BmsLibraryDbGateway.CurrentChartInfoParserVersion);

        ChartInfoBuildService.ChartInfoSnapshotBuildResult result = new ChartInfoBuildService().EvaluateSnapshot(
            snapshot,
            target,
            incompatibleRow,
            hasCurrentParseFailure: false);

        Assert.IsTrue(result.ParseFailed);
        Assert.IsFalse(result.CurrentRowSkipped);
        Assert.IsNotNull(result.ParseFailureRow);
        Assert.AreEqual(md5, result.ParseFailureRow.md5);
        Assert.IsFalse(result.ParseFailureRow.message.Contains('\r'));
        Assert.IsFalse(result.ParseFailureRow.message.Contains('\n'));
        Assert.IsTrue(result.ParseFailureRow.message.Length <= 1024);
    }

    /// <summary>
    /// 解析失敗でもsong/digestと失敗行を同じ保存適用へ渡し、元入力を変更せずcommit後に正しいhash・失敗分類を保持することを確かめる。
    /// </summary>
    /// <remarks>実解析結果をCatalogMutationOwnerの保存適用へ渡し、専用SQLiteのdigest/情報/失敗表をcommit後に読んで失敗保持を確かめる。</remarks>
    [TestMethod]
    public void ChartInfoInlineBuildService_ParseFailureCanBePersistedWithSong()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "bad-target.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE bad\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFile captured = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath)) with { Sha256 = string.Empty };
            var gateway = new BmsLibraryDbGateway(songDbPath);
            var service = new ChartInfoInlineBuildService(new ChartInfoBuildService(), parserDegree: 1);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
            }

            ChartInfoInlineBuildResult result = service.BuildForExistingCharts(
                gateway,
                [captured]);

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(1, result.ParseFailedCount);
            Assert.AreEqual(0, result.SuccessCount);
            Assert.IsTrue(string.IsNullOrWhiteSpace(captured.Sha256));
            Assert.AreEqual(1, result.StorageApplications.Count);
            var mutationOwner = new CatalogMutationOwner(new CatalogOwnedCollectionOwner(), gateway);
            CatalogChartInfoStorageWriteReceipt receipt = mutationOwner.ApplyChartInfoStorageWrite(
                new CatalogChartInfoStorageWriteRequest(
                    result.StorageApplications.Select(application => application.CreateCurrentValue()),
                    new CatalogChartInfoWriteRequest(
                        chartInfoRows: result.ChartInfoRows,
                        parseFailureRows: result.ParseFailureRows,
                        parseFailureDeleteMd5s: result.ParseFailureDeleteMd5s)));
            Assert.IsTrue(receipt.Applied);
            ChartFile committed = result.StorageApplications.Single().CreateCurrentValue();
            Assert.IsFalse(string.IsNullOrWhiteSpace(committed.Sha256));
            Assert.IsTrue(string.IsNullOrWhiteSpace(captured.Sha256));
            using var verify = new LR2SongDBExtended(songDbPath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = '" + committed.Md5 + "' AND sha256 = '" + committed.Sha256 + "';"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
            BeMusicSeeker.Models.ChartParseFailure failure = verify.Query<BeMusicSeeker.Models.ChartParseFailure>("SELECT * FROM chart_info_parse_failure WHERE md5 = ?;", committed.Md5).Single();
            Assert.AreEqual(committed.Sha256, failure.sha256);
            Assert.AreEqual(chartPath, failure.path);
            Assert.AreEqual(BmsLibraryDbGateway.CurrentChartInfoParserVersion, failure.parser_version);
            Assert.AreEqual("parse_failed", failure.failure_kind);
            Assert.AreEqual("InvalidDataException", failure.exception_type);
            Assert.IsFalse(failure.parse_timeout_ms.HasValue);
        });
    }

    /// <summary>
    /// BMSとbmsonの保存行を索引へ反映して再要求でも再解析・余計な補完を起こさず、形式片側の解決欠落を検出する。
    /// </summary>
    /// <remarks>実BMSLibraryと専用DBを使い、保存行から索引・所有行・facade通知への接続を観測する。管理主体だけの結果ではこの接続を代用できない。</remarks>
    [TestMethod]
    public async Task DeferredChartInfoHydration_IndexesExistingRowsForBmsAndBmson()
    {

        await WithTemporarySongDb(async delegate (string tempRootPath, string songDbPath)
        {
            string bmsSha = new('1', 64);
            string bmsonSha = new('2', 64);
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = Path.Combine(tempRootPath, "hydrated.bms")
            };
            file = file with { Md5 = new string('a', 32) };
            file = file with { Sha256 = bmsSha };
            ChartFile bmsonSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = Path.Combine(tempRootPath, "hydrated.bmson"),
                Md5 = new string('b', 32),
                Sha256 = bmsonSha
            };
            var gateway = new BmsLibraryDbGateway(songDbPath);
            BeMusicSeeker.Models.ChartDetails bmsRow = CreateChartInfoRow(bmsSha, file.Md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion);
            BeMusicSeeker.Models.ChartDetails bmsonRow = CreateChartInfoRow(bmsonSha, bmsonSong.Md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion);
            gateway.UpsertChartInfos([bmsRow, bmsonRow]);
            var library = new TestBmsLibrary(songDbPath, null, null, null, new RecordingDialogService(), settings: testSettings)
            {
                BmsCharts = [file],
                BmsonCharts = [bmsonSong]
            };
            var workers = new ConcurrentBag<Task>();
            library.StartupBackgroundTaskScheduler = (_, _, _, work) =>
            {
                workers.Add(Task.Run(work));
                return true;
            };
            Exception? primaryFailure = null;
            try
            {
                InvokeDeferredChartInfoHydration(library, "unit_test", queueFullBackfillAfterHydration: false);

                await TestUiDispatcherHost.AwaitNotificationAsync(
                    AwaitChartInfoHydrationAsync(library), Task.WhenAll(workers), "chart-info.hydration-completion");
                await Task.WhenAll(workers);
                Assert.IsFalse(library.ChartInfoHydrationRunning);
                Assert.AreEqual(2, library.ChartInfoHydrationTotalCount);
                Assert.AreEqual(0, library.ChartInfoHydrationAppliedCount);
                BeMusicSeeker.Models.ChartDetails resolvedBmsRow = library.ResolveChartInfo(file.Sha256, file.Md5);
                BeMusicSeeker.Models.ChartDetails resolvedBmsonRow = library.ResolveChartInfo(bmsonSong.Sha256, bmsonSong.Md5);
                Assert.IsNotNull(resolvedBmsRow);
                Assert.AreEqual(bmsSha, resolvedBmsRow.sha256);
                Assert.IsNotNull(resolvedBmsonRow);
                Assert.AreEqual(bmsonSha, resolvedBmsonRow.sha256);
                Assert.AreEqual(0, library.ChartInfoBackfillRequestedVersion);

                InvokeDeferredChartInfoHydration(library, "unit_test_repeat", queueFullBackfillAfterHydration: false);

                await TestUiDispatcherHost.AwaitNotificationAsync(
                    AwaitChartInfoHydrationAsync(library), Task.WhenAll(workers), "chart-info.hydration-completion");
                await Task.WhenAll(workers);
                Assert.AreEqual(2, library.ChartInfoHydrationTotalCount);
                Assert.AreEqual(0, library.ChartInfoHydrationAppliedCount);
            }
            catch (Exception exception)
            {
                primaryFailure = exception;
            }
            finally
            {
                try
                {
                    await Task.WhenAll(workers);
                }
                catch (Exception cleanupFailure)
                {
                    if (primaryFailure is null) { primaryFailure = cleanupFailure; }
                    else { primaryFailure.Data["WorkerSecondaryDiagnostics"] = cleanupFailure.ToString(); }
                }
            }
            if (primaryFailure is not null)
            {
                ExceptionDispatchInfo.Capture(primaryFailure).Throw();
            }
        });
    }

    /// <summary>
    /// 通常/完了済みLR2モードとも実保存行の表示情報を反映し、全対象が現行なら不要な再補完を開始しないことを確かめる。
    /// </summary>
    /// <remarks>実BMSLibraryと専用DBを使い、保存行から索引・所有行・facade通知への接続を観測する。管理主体だけの結果ではこの接続を代用できない。</remarks>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DeferredChartInfoHydration_UsesActualDataInBothModesAndSkipsFullBackfillWhenCurrent(bool operationModeLr2Db)
    {

        await WithTemporarySongDb(async delegate (string tempRootPath, string songDbPath)
        {
            string md5 = new('a', 32);
            string sha = new('1', 64);
            string chartPath = Path.Combine(tempRootPath, "current.bms");
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = chartPath
            };
            file = file with { Md5 = md5 };
            file = file with { Sha256 = sha };
            BeMusicSeeker.Models.ChartDetails row = CreateChartInfoRow(sha, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion);
            row = row with { speedchange = "120.0,0.0;240.0,1000.0" };
            row = row with { lanenotes = "1,2,3,4" };
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
                InsertSongForSummary(songDb, chartPath, md5);
                songDb.InsertOrReplace(CreateChartDigestRow(md5, sha), typeof(LR2SongDBExtended.chart_digest_map));
                songDb.InsertOrReplace(ChartInfoStorageMapping.ToStorage(row), typeof(LR2SongDBExtended.chart_info));
            }
            bool originalOperationMode = testSettings.OperationModeLR2DB;
            try
            {
                testSettings.OperationModeLR2DB = operationModeLr2Db;
                var options = new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = operationModeLr2Db,
                };
                if (operationModeLr2Db)
                {
                    string signature = Lr2SongDbSyncSignatureBuilder.Build(options);
                    using var songDb = new LR2SongDBExtended(songDbPath);
                    Lr2SongDbSyncStatusService.MarkCompleted(songDb, signature, "unit-test", 1, DateTime.UtcNow);
                }
                var library = new TestBmsLibrary(songDbPath, null, null, null, new RecordingDialogService(), settings: testSettings)
                {
                    BmsCharts = [file]
                };
                var workers = new ConcurrentBag<Task>();
                library.StartupBackgroundTaskScheduler = (_, _, _, work) =>
                {
                    workers.Add(Task.Run(work));
                    return true;
                };
                Exception? primaryFailure = null;
                try
                {
                    Assert.IsFalse(library.ChartInfoIndexHydrated);
                    Assert.IsNull(library.ResolveChartInfo(sha, md5));

                    InvokeDeferredChartInfoHydration(library, "unit_test", queueFullBackfillAfterHydration: true);

                    await TestUiDispatcherHost.AwaitNotificationAsync(
                        AwaitChartInfoHydrationAsync(library), Task.WhenAll(workers), "chart-info.hydration-completion");
                    await Task.WhenAll(workers);
                    Assert.IsTrue(library.ChartInfoIndexHydrated);
                    Assert.IsTrue(library.ChartInfoIndexVersion > 0);
                    Assert.AreEqual(1, library.ChartInfoBackfillRequestedVersion);
                    Assert.AreEqual(1, library.ChartInfoBackfillCompletedVersion);
                    Assert.AreEqual(1, library.ChartInfoHydrationTotalCount);

                    BeMusicSeeker.Models.ChartDetails resolved = library.ResolveChartInfo(sha, md5);

                    Assert.IsNotNull(resolved);
                    Assert.AreEqual(sha, resolved.sha256);
                    AssertChartInfoDisplayProjectionEquivalent(row, resolved);
                }
                catch (Exception exception)
                {
                    primaryFailure = exception;
                }
                finally
                {
                    try
                    {
                        await Task.WhenAll(workers);
                    }
                    catch (Exception cleanupFailure)
                    {
                        if (primaryFailure is null) { primaryFailure = cleanupFailure; }
                        else { primaryFailure.Data["WorkerSecondaryDiagnostics"] = cleanupFailure.ToString(); }
                    }
                }
                if (primaryFailure is not null)
                {
                    ExceptionDispatchInfo.Capture(primaryFailure).Throw();
                }

            }
            finally
            {
                testSettings.OperationModeLR2DB = originalOperationMode;
            }
        });
    }

    /// <summary>
    /// 所有snapshotの全行が現行なら候補要約を省いてskipped checkpointを公開し、不要なDB要約や補完を開始する誤りを検出する。
    /// </summary>
    /// <remarks>実CatalogChartInfoOwnerのacceptedWorkと専用SQLiteを使い、受付版・元要求・索引/補完の通知と処理終端を観測する。</remarks>
    [TestMethod]
    public async Task CatalogChartInfoOwner_ActualDataAllCurrentSnapshotSkipsCandidateSummary()
    {
        await WithTemporarySongDb(async delegate (string tempRootPath, string songDbPath)
        {
            string md5 = new('a', 32);
            string sha256 = new('1', 64);
            string chartPath = Path.Combine(tempRootPath, "current-snapshot.bms");
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = chartPath
            };
            file = file with { Md5 = md5 };
            file = file with { Sha256 = sha256 };
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
                InsertSongForSummary(songDb, chartPath, md5);
                songDb.InsertOrReplace(CreateChartDigestRow(md5, sha256), typeof(LR2SongDBExtended.chart_digest_map));
                songDb.InsertOrReplace(ChartInfoStorageMapping.ToStorage(
                    CreateChartInfoRow(sha256, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion)), typeof(LR2SongDBExtended.chart_info));
            }

            var gateway = new BmsLibraryDbGateway(songDbPath);
            var storageRowsOwner = new CatalogOwnedCollectionOwner();
            storageRowsOwner.ReplaceCharts([file], null, replaceBmson: false);
            CatalogOwnedCollectionOwner ownedCollectionOwner = storageRowsOwner;
            var mutationOwner = new CatalogMutationOwner(storageRowsOwner, gateway);
            var logs = new ConcurrentQueue<string>();
            var events = new ConcurrentQueue<CatalogChartInfoOwnerEvent>();
            var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var scheduled = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            CatalogChartInfoOwner? owner = null;
            void SignalCompletion()
            {
                try
                {
                    CatalogChartInfoOwner currentOwner = owner!;
                    if (currentOwner.ChartInfoHydrationRequestedVersion > 0
                        && currentOwner.ChartInfoHydrationCompletedVersion == currentOwner.ChartInfoHydrationRequestedVersion
                        && currentOwner.ChartInfoBackfillRequestedVersion > 0
                        && currentOwner.ChartInfoBackfillCompletedVersion == currentOwner.ChartInfoBackfillRequestedVersion
                        && !currentOwner.ChartInfoHydrationRunning
                        && !currentOwner.ChartInfoBackfillRunning)
                    {
                        completion.TrySetResult(null);
                    }
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }
            owner = new CatalogChartInfoOwner(
                _ => SignalCompletion(),
                () => false,
                (_, _) => false,
                () => (_, _, _, work) =>
                {
                    scheduled.TrySetResult(Task.Run(work));
                    return true;
                },
                logs.Enqueue);
            owner.ConfigureWorkflow(gateway, mutationOwner, ownedCollectionOwner, logs.Enqueue, events.Enqueue);

            Exception? primaryFailure = null;
            try
            {
                owner.QueueDeferredHydration("unit_test_all_current", queueFullBackfillAfterHydration: true);

                Assert.IsTrue(scheduled.Task.IsCompletedSuccessfully, "Hydration must hand its accepted work to the configured scheduler.");
                Task worker = await scheduled.Task;
                SignalCompletion();
                await TestUiDispatcherHost.AwaitNotificationAsync(completion.Task, worker, "chart-info.all-current-completion");
                await worker;
                Assert.IsTrue(logs.Any(message => message.StartsWith("chart_info_backfill skipped reason=hydration_all_current", StringComparison.Ordinal)));
                Assert.IsFalse(logs.Any(message => message.StartsWith("chart_info_backfill candidate_summary_start", StringComparison.Ordinal)));
                Assert.AreEqual(0, owner.ChartInfoBackfillTotalCount);
                Assert.AreEqual(0, owner.ChartInfoBackfillProcessedCount);
                Assert.IsTrue(events.Any(ownerEvent =>
                    ownerEvent.Kind == CatalogChartInfoOwnerEventKind.StartupMemoryCheckpoint
                    && ownerEvent.CheckpointStage == "chart_info_backfill"
                    && ownerEvent.CheckpointStatus == "skipped"));
            }
            catch (Exception exception)
            {
                primaryFailure = exception;
            }
            finally
            {
                if (scheduled.Task.IsCompletedSuccessfully)
                {
                    try
                    {
                        await (await scheduled.Task);
                    }
                    catch (Exception cleanupFailure)
                    {
                        if (primaryFailure is null)
                        {
                            primaryFailure = cleanupFailure;
                        }
                        else
                        {
                            primaryFailure.Data["WorkerSecondaryDiagnostics"] = cleanupFailure.ToString();
                        }
                    }
                }
            }
            if (primaryFailure is not null)
            {
                ExceptionDispatchInfo.Capture(primaryFailure).Throw();
            }
        });
    }

    /// <summary>
    /// 補完中の再受付後も処理済みprogressと開始/終了通知を捕捉した元要求へ紐付け、次要求のtokenや件数で上書きする誤りを検出する。
    /// </summary>
    /// <remarks>実CatalogChartInfoOwnerのacceptedWorkと専用SQLiteを使い、受付版・元要求・索引/補完の通知と処理終端を観測する。</remarks>
    [TestMethod]
    public async Task CatalogChartInfoOwner_BackfillProgressKeepsCapturedRequestWhenAnotherRequestIsAccepted()
    {
        await WithTemporarySongDb(async (tempRootPath, songDbPath) =>
        {
            string chartPath = Path.Combine(tempRootPath, "request-progress.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE progress\r\n#BPM 130\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
                InsertSongForSummary(songDb, chartPath, file.Md5);
            }
            var gateway = new BmsLibraryDbGateway(songDbPath);
            var storageRowsOwner = new CatalogOwnedCollectionOwner();
            storageRowsOwner.ReplaceCharts([file], null, replaceBmson: false);
            CatalogOwnedCollectionOwner ownedCollectionOwner = storageRowsOwner;
            var mutationOwner = new CatalogMutationOwner(storageRowsOwner, gateway);
            long operationToken = 11;
            var execution = new ConcurrentQueue<(OperationProgressRequest Request, bool Running)>();
            var snapshots = new ConcurrentQueue<ChartInfoWorkflowProgressSnapshot>();
            var processedRequests = new ConcurrentQueue<int>();
            var captured = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim(false);
            int gateUsed = 0;
            CatalogChartInfoOwner? owner = null;
            owner = new CatalogChartInfoOwner(
                propertyName =>
                {
                    if (propertyName == nameof(BMSLibrary.ChartInfoBackfillProgressSnapshot)
                        && owner is { } currentOwner)
                    {
                        snapshots.Enqueue(currentOwner.BackfillProgressSnapshot);
                    }
                },
                () => false,
                (_, _) => false,
                null,
                _ => { });
            owner.ProgressRequestFactory = (name, version) => new(7, operationToken, name, version);
            owner.RequestProgressReporter = (request, running) => execution.Enqueue((request, running));
            owner.ConfigureWorkflow(gateway, mutationOwner, ownedCollectionOwner, _ => { }, _ => { }, beginDigestMutationWindow: () =>
                {
                    processedRequests.Enqueue(owner.ChartInfoBackfillRequestedVersion);
                    if (Interlocked.Exchange(ref gateUsed, 1) == 0)
                    {
                        captured.TrySetResult(owner.ChartInfoBackfillRequestedVersion);
                        release.Wait();
                    }
                    return new TestMutationWindow();
                });

            var worker = Task.Run(() => owner.QueueBackfill("first", processSynchronously: true));
            try
            {
                Task first = await Task.WhenAny(captured.Task, worker);
                if (first == worker)
                {
                    await worker;
                    Assert.Fail("Backfillの実処理が要求版捕捉ゲートへ到達しませんでした。");
                }
                int firstVersion = await captured.Task;
                operationToken = 22;
                owner.QueueBackfill("next");
                int nextVersion = owner.ChartInfoBackfillRequestedVersion;
                Assert.IsTrue(nextVersion > firstVersion);
                snapshots.Clear();
                release.Set();
                await worker;

                ChartInfoWorkflowProgressSnapshot retainedFirst = snapshots.First(snapshot =>
                    snapshot.RequestVersion == firstVersion && snapshot.ProcessedCount > 0);
                Assert.AreEqual(1, retainedFirst.TotalCount);
                Assert.AreEqual(1, retainedFirst.ProcessedCount);
                Assert.AreEqual(chartPath, retainedFirst.CurrentPath);
                Assert.IsTrue(snapshots.Any(snapshot => snapshot.RequestVersion == nextVersion
                    && snapshot.TotalCount == 0 && snapshot.ProcessedCount == 0));
                Assert.IsTrue(processedRequests.Contains(nextVersion));
                Assert.AreEqual(nextVersion, owner.BackfillProgressSnapshot.RequestVersion);
                Assert.AreEqual(nextVersion, owner.ChartInfoBackfillCompletedVersion);
                Assert.IsFalse(owner.ChartInfoBackfillRunning);
                Assert.AreEqual(new OperationProgressRequest(7, 11, "chart_info_backfill", firstVersion), retainedFirst.Request);
                Assert.IsTrue(execution.Contains((new(7, 11, "chart_info_backfill", firstVersion), true)));
                Assert.IsTrue(execution.Contains((new(7, 11, "chart_info_backfill", firstVersion), false)));
                Assert.IsTrue(execution.Contains((new(7, 22, "chart_info_backfill", nextVersion), true)));
                Assert.IsTrue(execution.Contains((new(7, 22, "chart_info_backfill", nextVersion), false)));
                Assert.AreEqual(firstVersion, retainedFirst.RequestVersion);
                Assert.AreEqual(1, retainedFirst.ProcessedCount);
                Assert.AreEqual(chartPath, retainedFirst.CurrentPath);
            }
            finally
            {
                release.Set();
                await worker;
            }
        });
    }

    /// <summary>
    /// 索引反映中の再受付でも前後のprogress・開始/終了通知を各要求へ保持し、要求版や操作tokenを混同する誤りを検出する。
    /// </summary>
    /// <remarks>実CatalogChartInfoOwnerのacceptedWorkと専用SQLiteを使い、受付版・元要求・索引/補完の通知と処理終端を観測する。</remarks>
    [TestMethod]
    public async Task CatalogChartInfoOwner_HydrationProgressKeepsCapturedRequestWhenAnotherRequestIsAccepted()
    {
        await WithTemporarySongDb(async (tempRootPath, songDbPath) =>
        {
            string md5 = new('a', 32);
            string sha256 = new('1', 64);
            string chartPath = Path.Combine(tempRootPath, "hydration-progress.bms");
            ChartFile file = (ChartTestValues.Empty() with { Path = chartPath });
            file = file with { Md5 = md5 };
            file = file with { Sha256 = sha256 };
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
                InsertSongForSummary(songDb, chartPath, md5);
                songDb.InsertOrReplace(CreateChartDigestRow(md5, sha256), typeof(LR2SongDBExtended.chart_digest_map));
                songDb.InsertOrReplace(ChartInfoStorageMapping.ToStorage(CreateChartInfoRow(sha256, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion)), typeof(LR2SongDBExtended.chart_info));
            }
            var gateway = new BmsLibraryDbGateway(songDbPath);
            var storageRowsOwner = new CatalogOwnedCollectionOwner();
            storageRowsOwner.ReplaceCharts([file], null, replaceBmson: false);
            CatalogOwnedCollectionOwner ownedCollectionOwner = storageRowsOwner;
            var mutationOwner = new CatalogMutationOwner(storageRowsOwner, gateway);
            long operationToken = 11;
            var execution = new ConcurrentQueue<(OperationProgressRequest Request, bool Running)>();
            var snapshots = new ConcurrentQueue<ChartInfoWorkflowProgressSnapshot>();
            var captured = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim(false);
            Func<Task>? scheduledProcess = null;
            int gateUsed = 0;
            CatalogChartInfoOwner? owner = null;
            owner = new CatalogChartInfoOwner(
                propertyName =>
                {
                    if (owner is not { } currentOwner)
                    {
                        return;
                    }
                    if (propertyName == nameof(BMSLibrary.ChartInfoHydrationProgressSnapshot))
                    {
                        snapshots.Enqueue(currentOwner.HydrationProgressSnapshot);
                    }
                    if (propertyName == nameof(BMSLibrary.ChartInfoIndexVersion)
                        && Interlocked.Exchange(ref gateUsed, 1) == 0)
                    {
                        captured.TrySetResult(currentOwner.ChartInfoHydrationRequestedVersion);
                        release.Wait();
                    }
                },
                () => false,
                (_, _) => false,
                () => (_, _, _, process) =>
                {
                    scheduledProcess = process;
                    return true;
                },
                _ => { });
            owner.ProgressRequestFactory = (name, version) => new(7, operationToken, name, version);
            owner.RequestProgressReporter = (request, running) => execution.Enqueue((request, running));
            owner.ConfigureWorkflow(gateway, mutationOwner, ownedCollectionOwner, _ => { }, _ => { });
            owner.QueueDeferredHydration("first", queueFullBackfillAfterHydration: false);
            Assert.IsNotNull(scheduledProcess);
            var worker = Task.Run(scheduledProcess);
            try
            {
                Task first = await Task.WhenAny(captured.Task, worker);
                if (first == worker)
                {
                    await worker;
                    Assert.Fail("Hydrationの実処理が索引変更ゲートへ到達しませんでした。");
                }
                int firstVersion = await captured.Task;
                operationToken = 22;
                owner.QueueDeferredHydration("next", queueFullBackfillAfterHydration: false);
                int nextVersion = owner.ChartInfoHydrationRequestedVersion;
                Assert.IsTrue(nextVersion > firstVersion);
                snapshots.Clear();
                release.Set();
                await worker;

                ChartInfoWorkflowProgressSnapshot retainedFirst = snapshots.First(snapshot =>
                    snapshot.RequestVersion == firstVersion && snapshot.TotalCount == 1);
                Assert.AreEqual(0, retainedFirst.ProcessedCount);
                Assert.IsTrue(snapshots.Any(snapshot => snapshot.RequestVersion == nextVersion
                    && snapshot.TotalCount == 1 && snapshot.ProcessedCount == 0));
                Assert.AreEqual(nextVersion, owner.HydrationProgressSnapshot.RequestVersion);
                Assert.AreEqual(nextVersion, owner.ChartInfoHydrationCompletedVersion);
                Assert.IsFalse(owner.ChartInfoHydrationRunning);
                Assert.AreEqual(0, owner.ChartInfoBackfillRequestedVersion);
                Assert.AreEqual(new OperationProgressRequest(7, 11, "chart_info_hydration", firstVersion), retainedFirst.Request);
                Assert.IsTrue(execution.Contains((new(7, 11, "chart_info_hydration", firstVersion), true)));
                Assert.IsTrue(execution.Contains((new(7, 11, "chart_info_hydration", firstVersion), false)));
                Assert.IsTrue(execution.Contains((new(7, 22, "chart_info_hydration", nextVersion), true)));
                Assert.IsTrue(execution.Contains((new(7, 22, "chart_info_hydration", nextVersion), false)));
                Assert.AreEqual(firstVersion, retainedFirst.RequestVersion);
                Assert.AreEqual(1, retainedFirst.TotalCount);
                Assert.AreEqual(0, retainedFirst.ProcessedCount);
            }
            finally
            {
                release.Set();
                await worker;
            }
        });
    }

    /// <summary>
    /// ファイル変更をinline反映した通知時点で新譜面情報の索引とhash/playlist解決を利用可能にし、digest効果が先に見える順序誤りを検出する。
    /// </summary>
    /// <remarks>実BMSLibraryと専用DBを使い、保存行から索引・所有行・facade通知への接続を観測する。管理主体だけの結果ではこの接続を代用できない。</remarks>
    [TestMethod]
    public void CatalogChartInfoOwner_InlinePublicationOrdersDigestEffectsAfterSessionIndex()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "publication-order.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n#TITLE publication order before\r\n#PLAYLEVEL 7\r\n#BPM 130\r\n#00111:01\r\n",
                Encoding.ASCII);
            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
            string oldMd5 = file.Md5;
            string oldSha256 = file.Sha256;
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n#TITLE publication order after\r\n#PLAYLEVEL 7\r\n#BPM 130\r\n#00111:01\r\n",
                Encoding.ASCII);
            var library = new TestBmsLibrary(songDbPath, settings: testSettings);
            OwnedChartCollectionTestSupport.SetLibraryFilesWithoutNotification(library, [file]);
            OwnedChartCollectionTestSupport.SetLibraryBmsonSongsWithoutNotification(library, []);
            OwnedChartCollectionTestSupport.SetDuplicateChartGroupsWithoutNotification(library, []);
            PlaylistLibraryResolveIndexSnapshot initialResolve = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out _,
                out _);
            Assert.AreEqual(chartPath, initialResolve.ResolveChartForPlaylistHash(oldMd5, null).Path);
            List<string> publicationOrder = [];
            bool playlistResolvedAtDigestNotification = false;
            library.PropertyChanged += delegate (object? _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(BMSLibrary.ChartInfoIndexVersion))
                {
                    publicationOrder.Add("session_index");
                    Assert.IsTrue(library.ChartInfoIndexVersion > 0);
                    Assert.IsNotNull(library.ResolveChartInfo(library.BmsCharts.Single().Sha256, library.BmsCharts.Single().Md5));
                }
                else if (args.PropertyName == nameof(BMSLibrary.OwnedCollectionVersion))
                {
                    publicationOrder.Add("digest_effects");
                    Assert.IsNotNull(library.ResolveChartInfo(library.BmsCharts.Single().Sha256, library.BmsCharts.Single().Md5));
                    Assert.IsTrue(library.GetOwnedChartHashIndexSnapshot().ContainsMd5(library.BmsCharts.Single().Md5));
                    PlaylistLibraryResolveIndexSnapshot notificationResolve = library.GetPlaylistLibraryResolveIndexSnapshot(
                        CancellationToken.None,
                        out bool cacheHit,
                        out int staleRetries);
                    Assert.IsTrue(cacheHit);
                    Assert.AreEqual(0, staleRetries);
                    Assert.IsNull(notificationResolve.ResolveChartForPlaylistHash(oldMd5, null));
                    Assert.AreEqual(
                        chartPath,
                        notificationResolve.ResolveChartForPlaylistHash(library.BmsCharts.Single().Md5, null).Path);
                    playlistResolvedAtDigestNotification = true;
                }
            };

            ChartInfoInlineBuildResult result = OwnedChartCollectionTestSupport.InvokeBuildAndPersistInlineChartInfoForInstalledCharts(
                library,
                "publication_order",
                [(file)]);

            Assert.IsTrue(publicationOrder.IndexOf("session_index") >= 0);
            Assert.IsTrue(publicationOrder.IndexOf("digest_effects") >= 0);
            Assert.IsTrue(
                publicationOrder.IndexOf("session_index") < publicationOrder.IndexOf("digest_effects"),
                "chart-info session index must be observable before digest-dependent collection notification.");
            Assert.IsTrue(playlistResolvedAtDigestNotification);
            Assert.AreEqual(oldMd5, file.Md5);
            file = library.BmsCharts.Single();
            Assert.AreNotEqual(oldMd5, file.Md5);
            Assert.AreNotEqual(oldSha256, file.Sha256);
            Assert.IsTrue(result.DigestChanges.Count > 0);
        });
    }

    /// <summary>
    /// 通常/完了済みLR2モードとも欠落情報を補完してsongと所有行へ一回反映し、LR2完了状態だけで必要補完を省く誤りを検出する。
    /// </summary>
    /// <remarks>実BMSLibraryと専用DBを使い、保存行から索引・所有行・facade通知への接続を観測する。管理主体だけの結果ではこの接続を代用できない。</remarks>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DeferredChartInfoHydration_MissingCurrentRowBackfillsRegardlessOfCompletedLr2Status(bool operationModeLr2Db)
    {

        await WithTemporarySongDb(async delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "missing-current.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#PLAYLEVEL 13\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFile digest = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = chartPath
            };
            file = file with { Md5 = digest.Md5 };
            file = file with { Sha256 = digest.Sha256 };
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
                InsertSongForSummary(songDb, chartPath, file.Md5);
                songDb.InsertOrReplace(CreateChartDigestRow(file.Md5, file.Sha256), typeof(LR2SongDBExtended.chart_digest_map));
                if (operationModeLr2Db)
                {
                    string signature = Lr2SongDbSyncSignatureBuilder.Build(new BmsLibraryOptionsSnapshot { OperationModeLR2DB = true });
                    Lr2SongDbSyncStatusService.MarkCompleted(songDb, signature, "unit-test", 1, DateTime.UtcNow);
                }
            }
            bool originalOperationMode = testSettings.OperationModeLR2DB;
            try
            {
                testSettings.OperationModeLR2DB = operationModeLr2Db;
                var library = new TestBmsLibrary(songDbPath, null, null, null, new RecordingDialogService(), settings: testSettings)
                {
                    BmsCharts = [file]
                };
                var workers = new ConcurrentBag<Task>();
                library.StartupBackgroundTaskScheduler = (_, _, _, work) =>
                {
                    workers.Add(Task.Run(work));
                    return true;
                };
                Exception? primaryFailure = null;
                try
                {
                    ChartFile captured = library.BmsCharts.Single();
                    int initialOwnedVersion = library.OwnedCollectionVersion;
                    InvokeDeferredChartInfoHydration(library, "unit_test_missing", queueFullBackfillAfterHydration: true);

                    await TestUiDispatcherHost.AwaitNotificationAsync(
                        AwaitChartInfoHydrationAsync(library), Task.WhenAll(workers), "chart-info.hydration-completion");
                    await Task.WhenAll(workers);
                    await TestUiDispatcherHost.AwaitNotificationAsync(
                        AwaitChartInfoBackfillAsync(library), Task.WhenAll(workers), "chart-info.backfill-completion");
                    using var verify = new LR2SongDBExtended(songDbPath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
                    Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ?;", file.Sha256));
                    Assert.AreEqual(13, verify.ExecuteScalar<int>("SELECT level FROM song WHERE path = ?;", chartPath));
                    Assert.AreEqual(13, library.BmsCharts.Single().Level);
                    Assert.AreSame(captured.Token, library.BmsCharts.Single().Token);
                    Assert.AreEqual(initialOwnedVersion + 1, library.OwnedCollectionVersion);
                    Assert.IsNull(file.Level);
                }
                catch (Exception exception)
                {
                    primaryFailure = exception;
                }
                finally
                {
                    try
                    {
                        await Task.WhenAll(workers);
                    }
                    catch (Exception cleanupFailure)
                    {
                        if (primaryFailure is null) { primaryFailure = cleanupFailure; }
                        else { primaryFailure.Data["WorkerSecondaryDiagnostics"] = cleanupFailure.ToString(); }
                    }
                }
                if (primaryFailure is not null)
                {
                    ExceptionDispatchInfo.Capture(primaryFailure).Throw();
                }

            }
            finally
            {
                testSettings.OperationModeLR2DB = originalOperationMode;
            }
        });
    }

    /// <summary>
    /// 候補がない補完要求も受付版と完了版を揃えて停止し、空入力を処理中に残す誤りを検出する。
    /// </summary>
    /// <remarks>実BMSLibraryと専用DBを使い、保存行から索引・所有行・facade通知への接続を観測する。管理主体だけの結果ではこの接続を代用できない。</remarks>
    [TestMethod]
    public async Task DeferredChartInfoHydration_SkipsFullBackfillWhenNoCandidates()
    {

        await WithTemporarySongDb(async delegate (string tempRootPath, string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, null, new RecordingDialogService(), settings: testSettings);
            var workers = new ConcurrentBag<Task>();
            library.StartupBackgroundTaskScheduler = (_, _, _, work) =>
            {
                workers.Add(Task.Run(work));
                return true;
            };
            Exception? primaryFailure = null;
            try
            {
                InvokeDeferredChartInfoHydration(library, "unit_test", queueFullBackfillAfterHydration: true);

                await TestUiDispatcherHost.AwaitNotificationAsync(
                    AwaitChartInfoHydrationAsync(library), Task.WhenAll(workers), "chart-info.hydration-completion");
                await Task.WhenAll(workers);
                Assert.AreEqual(1, library.ChartInfoBackfillRequestedVersion);
                Assert.AreEqual(1, library.ChartInfoBackfillCompletedVersion);
                Assert.IsFalse(library.ChartInfoBackfillRunning);
            }
            catch (Exception exception)
            {
                primaryFailure = exception;
            }
            finally
            {
                try
                {
                    await Task.WhenAll(workers);
                }
                catch (Exception cleanupFailure)
                {
                    if (primaryFailure is null) { primaryFailure = cleanupFailure; }
                    else { primaryFailure.Data["WorkerSecondaryDiagnostics"] = cleanupFailure.ToString(); }
                }
            }
            if (primaryFailure is not null)
            {
                ExceptionDispatchInfo.Capture(primaryFailure).Throw();
            }
        });
    }

    /// <summary>
    /// 保存行の表示射影と更新時刻を保ってparser版/timeoutから現行集合を分類し、軽量読込みで情報や再試行条件を失う誤りを検出する。
    /// </summary>
    /// <remarks>実SQLiteの表示用軽量読取りと完全行読取りを照合する。共通の環境変数を一時変更するため非並列とし、元値を必ず復元する。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void LoadChartInfoHydrationData_UsesRawProjectionAndPreservesCurrentness()
    {
        string? previousMode = Environment.GetEnvironmentVariable("BMS_CHART_INFO_HYDRATION_LOAD_MODE");
        Environment.SetEnvironmentVariable("BMS_CHART_INFO_HYDRATION_LOAD_MODE", null);
        try
        {
            WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
            {
                var gateway = new BmsLibraryDbGateway(songDbPath);
                string currentMd5 = new('a', 32);
                string staleMd5 = new('b', 32);
                string currentFailureMd5 = new('c', 32);
                string staleFailureMd5 = new('d', 32);
                string currentSha = new('1', 64);
                string staleSha = new('2', 64);
                string currentFailureSha = new('3', 64);
                string staleFailureSha = new('4', 64);
                BeMusicSeeker.Models.ChartDetails currentRow = CreateChartInfoRow(currentSha, currentMd5, BmsLibraryDbGateway.CurrentChartInfoParserVersion);
                currentRow = currentRow with { level = null };
                currentRow = currentRow with { difficulty = 3 };
                currentRow = currentRow with { difficulty_defined = false };
                currentRow = currentRow with { mainbpm = 123.5 };
                currentRow = currentRow with { total = null };
                currentRow = currentRow with { total_defined = true };
                currentRow = currentRow with { density = 12.25 };
                currentRow = currentRow with { speedchange_count = 2 };
                currentRow = currentRow with { updated_at = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc) };
                BeMusicSeeker.Models.ChartDetails staleRow = CreateChartInfoRow(staleSha, staleMd5, BmsLibraryDbGateway.CurrentChartInfoParserVersion - 1);
                staleRow = staleRow with { charthash = new string('e', 64) };
                staleRow = staleRow with { distribution = "1,2,3" };
                staleRow = staleRow with { speedchange = "120.0,0.0;240.0,1.0" };
                staleRow = staleRow with { lanenotes = "1,2,3,4" };

                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
                    songDb.InsertOrReplace(ChartInfoStorageMapping.ToStorage(currentRow), typeof(LR2SongDBExtended.chart_info));
                    songDb.InsertOrReplace(ChartInfoStorageMapping.ToStorage(staleRow), typeof(LR2SongDBExtended.chart_info));
                    songDb.InsertOrReplace(ChartInfoStorageMapping.ToStorage(
                        CreateChartInfoParseFailureRow(currentFailureMd5, currentFailureSha, Path.Combine(tempRootPath, "current-failure.bms"), BmsLibraryDbGateway.CurrentChartInfoParserVersion, "parse_failed", "InvalidDataException", "bad", null)), typeof(LR2SongDBExtended.chart_info_parse_failure));
                    songDb.InsertOrReplace(ChartInfoStorageMapping.ToStorage(
                        CreateChartInfoParseFailureRow(staleFailureMd5, staleFailureSha, Path.Combine(tempRootPath, "stale-timeout.bms"), BmsLibraryDbGateway.CurrentChartInfoParserVersion, "timeout", "ChartInfoParseTimeoutException", "old timeout", 500)), typeof(LR2SongDBExtended.chart_info_parse_failure));
                }

                ChartInfoHydrationLoadResult result = gateway.LoadChartInfoHydrationData(TimeSpan.FromMilliseconds(1000));

                Assert.AreEqual("raw_string_display", result.MaterializeMode);
                Assert.AreEqual(2, result.ChartInfoRows);
                Assert.AreEqual(2, result.ParseFailureRows);
                Assert.AreEqual(4, result.RawRows);
                Assert.AreEqual(2, result.ChartInfoBySha256.Count);
                Assert.IsTrue(result.CurrentChartInfoSha256s.Contains(currentSha));
                Assert.IsFalse(result.CurrentChartInfoSha256s.Contains(staleSha));
                Assert.IsTrue(result.CurrentParseFailureMd5s.Contains(currentFailureMd5));
                Assert.IsFalse(result.CurrentParseFailureMd5s.Contains(staleFailureMd5));
                AssertChartInfoDisplayProjectionEquivalent(currentRow, result.ChartInfoBySha256[currentSha]);
                AssertChartInfoDisplayProjectionEquivalent(staleRow, result.ChartInfoBySha256[staleSha]);
                Assert.AreEqual(currentRow.updated_at, result.ChartInfoBySha256[currentSha].updated_at);
                Assert.AreEqual(staleRow.updated_at, result.ChartInfoBySha256[staleSha].updated_at);

                Dictionary<string, BeMusicSeeker.Models.ChartDetails> fullRows = gateway.LoadChartInfosBySha256([currentSha, staleSha]);

                AssertChartInfoEquivalent(currentRow, fullRows[currentSha]);
                AssertChartInfoEquivalent(staleRow, fullRows[staleSha]);
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("BMS_CHART_INFO_HYDRATION_LOAD_MODE", previousMode);
        }
    }

    /// <summary>
    /// 現行情報・現行失敗・digest欠落・旧情報を実保存行から分類し、再解析不要な行を候補へ含めたり必要行を落としたりする誤りを検出する。
    /// </summary>
    /// <remarks>専用SQLiteに異なる現行条件の保存行を投入し、候補要約の実検索を確認する。合成した要約だけでDB条件の保証を代用しない。</remarks>
    [TestMethod]
    public void GetChartInfoBackfillCandidateSummary_ClassifiesCurrentFailureAndStaleRows()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            var gateway = new BmsLibraryDbGateway(songDbPath);
            string currentMd5 = new('a', 32);
            string missingDigestMd5 = new('b', 32);
            string staleMd5 = new('c', 32);
            string failureMd5 = new('d', 32);
            string currentSha = new('1', 64);
            string staleSha = new('2', 64);
            string failureSha = new('3', 64);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
                InsertSongForSummary(songDb, Path.Combine(tempRootPath, "current.bms"), currentMd5);
                InsertSongForSummary(songDb, Path.Combine(tempRootPath, "missing-digest.bms"), missingDigestMd5);
                InsertSongForSummary(songDb, Path.Combine(tempRootPath, "stale.bms"), staleMd5);
                InsertSongForSummary(songDb, Path.Combine(tempRootPath, "failure.bms"), failureMd5);
                songDb.InsertOrReplace(CreateChartDigestRow(currentMd5, currentSha), typeof(LR2SongDBExtended.chart_digest_map));
                songDb.InsertOrReplace(CreateChartDigestRow(staleMd5, staleSha), typeof(LR2SongDBExtended.chart_digest_map));
                songDb.InsertOrReplace(CreateChartDigestRow(failureMd5, failureSha), typeof(LR2SongDBExtended.chart_digest_map));
                songDb.InsertOrReplace(ChartInfoStorageMapping.ToStorage(CreateChartInfoRow(currentSha, currentMd5, BmsLibraryDbGateway.CurrentChartInfoParserVersion)), typeof(LR2SongDBExtended.chart_info));
                songDb.InsertOrReplace(ChartInfoStorageMapping.ToStorage(CreateChartInfoRow(staleSha, staleMd5, BmsLibraryDbGateway.CurrentChartInfoParserVersion - 1)), typeof(LR2SongDBExtended.chart_info));
                songDb.InsertOrReplace(ChartInfoStorageMapping.ToStorage(
                    CreateChartInfoParseFailureRow(failureMd5, failureSha, Path.Combine(tempRootPath, "failure.bms"), BmsLibraryDbGateway.CurrentChartInfoParserVersion, "parse_failed", "InvalidDataException", "bad", null)), typeof(LR2SongDBExtended.chart_info_parse_failure));
            }

            ChartInfoBackfillCandidateSummary summary = gateway.GetChartInfoBackfillCandidateSummary(TimeSpan.FromSeconds(30));

            Assert.AreEqual(4, summary.BmsOwnerCount);
            Assert.AreEqual(0, summary.BmsonOwnerCount);
            Assert.AreEqual(1, summary.CurrentChartInfoOwnerCount);
            Assert.AreEqual(1, summary.CurrentParseFailureOwnerCount);
            Assert.AreEqual(1, summary.MissingDigestOwnerCount);
            Assert.AreEqual(0, summary.MissingChartInfoOwnerCount);
            Assert.AreEqual(1, summary.StaleChartInfoOwnerCount);
            Assert.AreEqual(2, summary.CandidateOwnerCount);
        });
    }

    /// <summary>実snapshotの未知の解析故障は今回の非採用にし、失敗事実を残して恒久失敗行・削除候補・取消へ変換しない。</summary>
    [TestMethod]
    public void ChartInfoSnapshotEvaluator_InternalFaultSkipsWithoutPersistentFailure()
    {
        string md5 = new('a', 32);
        string sha256 = new('b', 64);
        ChartFile file = ChartTestValues.Empty() with { Path = "valid.bms", Md5 = md5, Sha256 = sha256 };
        var snapshot = new ChartFileSnapshot(file.Path, Encoding.ASCII.GetBytes("#BPM 120\r\n#00111:01\r\n"), DateTime.UtcNow, md5, sha256);
        var service = new ChartInfoBuildService(null, parseBytes: (_, _, _, _, _) => throw new InvalidOperationException("injected internal fault"));
        ChartInfoBuildService.ChartInfoSnapshotBuildResult result = service.EvaluateSnapshot(snapshot, ChartInfoBuildTargetMapper.Create(file), null, false);

        Assert.IsNull(result.Row);
        Assert.IsTrue(result.ParseFailed);
        Assert.IsFalse(result.TimeoutFailed);
        Assert.IsFalse(result.ShouldPersistRow);
        Assert.IsFalse(result.SkippedPersistedFailure);
        Assert.IsNull(result.ParseFailureRow);
        Assert.IsTrue(string.IsNullOrEmpty(result.ParseFailureDeleteMd5));
    }

    /// <summary>配置先の未知の取得不能と警告先故障が重なっても正常対象を集約・保存し、既存基本値と恒久失敗表を保持する。</summary>
    /// <remarks>固有の実ファイル・SQLite・保存ownerを使う。NULを含むpathの取得失敗を使い、基本解析やDB保存の故障へ置き換えない。</remarks>
    [TestMethod]
    public void ChartInfoInlineBuildService_UnknownReadFailurePreservesSiblingAndStoredBasic()
    {
        WithTemporarySongDb((root, songDbPath) =>
        {
            List<ChartFile> charts = [];
            for (int index = 0; index < 2; index++)
            {
                string path = Path.Combine(root, index + ".bms");
                File.WriteAllText(path, "#TITLE inline" + index + "\r\n#PLAYLEVEL 9\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
                charts.Add(BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(path)) with { Level = 2 });
            }
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.UpsertSongs(charts);
            gateway.EnsureChartInfoBackfillSchema();
            var service = new ChartInfoInlineBuildService(new ChartInfoBuildService(), parserDegree: 1);
            ChartInfoInlineBuildResult result = service.BuildForExistingCharts(gateway,
                [charts[0] with { Path = charts[0].Path + "\0" }, charts[1]],
                logInstallPerformanceWarn: _ => throw new InvalidOperationException("warning sink failed"));

            Assert.AreEqual(1, result.ReadFailedCount);
            Assert.AreEqual(1, result.SuccessCount);
            Assert.AreEqual(0, result.FailurePersistedCount);
            Assert.AreEqual(1, result.StorageApplications.Count);
            Assert.AreEqual(charts[1].Path, result.StorageApplications.Single().Chart.Path);
            Assert.AreEqual(1, result.ChartInfoRows.Count);
            var owner = new CatalogMutationOwner(new CatalogOwnedCollectionOwner(), gateway);
            CatalogChartInfoStorageWriteReceipt receipt = owner.ApplyChartInfoStorageWrite(new CatalogChartInfoStorageWriteRequest(
                result.StorageApplications.Select(application => application.CreateCurrentValue()),
                new CatalogChartInfoWriteRequest(chartInfoRows: result.ChartInfoRows,
                    parseFailureRows: result.ParseFailureRows, parseFailureDeleteMd5s: result.ParseFailureDeleteMd5s)));
            Assert.IsTrue(receipt.Applied);
            using var verify = new LR2SongDBExtended(songDbPath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            Assert.AreEqual(2, verify.ExecuteScalar<int>("SELECT level FROM song WHERE path = ?;", charts[0].Path));
            Assert.AreEqual(9, verify.ExecuteScalar<int>("SELECT level FROM song WHERE path = ?;", charts[1].Path));
            Assert.AreEqual(charts[0].Md5, verify.ExecuteScalar<string>("SELECT hash FROM song WHERE path = ?;", charts[0].Path));
            Assert.AreEqual(1, verify.ExecuteScalar<int>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ?;", charts[1].Sha256));
            Assert.AreEqual(0, verify.ExecuteScalar<int>("SELECT COUNT(1) FROM chart_info_parse_failure;"));
        });
    }

    /// <summary>実snapshotの成功診断が入力失敗型を投げても、成功行と失敗履歴削除の事実を保つことを確かめる。</summary>
    [TestMethod]
    public void ChartInfoSnapshotEvaluator_DiagnosticFailurePreservesSuccessfulRow()
    {
        string md5 = new('a', 32);
        string sha256 = new('b', 64);
        byte[] bytes = Encoding.ASCII.GetBytes("#BPM 1\r\n#STOP01 90000\r\n#00009:"
            + string.Concat(Enumerable.Repeat("01", 40)) + "\r\n#00111:01\r\n");
        ChartFile file = ChartTestValues.Empty() with { Path = "diagnostic.bms", Md5 = md5, Sha256 = sha256 };
        var snapshot = new ChartFileSnapshot(file.Path, bytes, DateTime.UtcNow, md5, sha256);
        int diagnostics = 0;

        ChartInfoBuildService.ChartInfoSnapshotBuildResult result = new ChartInfoBuildService().EvaluateSnapshot(
            snapshot, ChartInfoBuildTargetMapper.Create(file), null, hasCurrentParseFailure: false,
            logInstallPerformance: _ => { diagnostics++; throw new InvalidDataException("diagnostic sink failed"); });

        Assert.AreEqual(1, diagnostics);
        Assert.IsNotNull(result.Row);
        Assert.IsTrue(result.ShouldPersistRow);
        Assert.IsFalse(result.ParseFailed);
        Assert.IsNull(result.ParseFailureRow);
        Assert.AreEqual(md5, result.ParseFailureDeleteMd5);
    }

    /// <summary>入力解析失敗と期限切れを実snapshotで発生させ、警告先の故障で元の失敗分類・永続行を失わないことを確かめる。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ChartInfoSnapshotEvaluator_WarningFailurePreservesInputFailure(bool timeout)
    {
        string md5 = new('c', 32);
        string sha256 = new('d', 64);
        byte[] bytes = Encoding.ASCII.GetBytes("#PLAYER 1\r\n#00111:01\r\n");
        ChartFile file = ChartTestValues.Empty() with { Path = "invalid.bms", Md5 = md5, Sha256 = sha256 };
        var snapshot = new ChartFileSnapshot(file.Path, bytes, DateTime.UtcNow, md5, sha256);
        int warnings = 0;

        ChartInfoBuildService.ChartInfoSnapshotBuildResult result = new ChartInfoBuildService().EvaluateSnapshot(
            snapshot, ChartInfoBuildTargetMapper.Create(file), null, hasCurrentParseFailure: false,
            parseTimeout: timeout ? TimeSpan.Zero : TimeSpan.FromSeconds(60),
            logInstallPerformanceWarn: _ => { warnings++; throw new InvalidOperationException("warning sink failed"); });

        Assert.AreEqual(1, warnings);
        Assert.IsTrue(result.ParseFailed);
        Assert.IsNull(result.Row);
        Assert.IsNotNull(result.ParseFailureRow);
        Assert.AreEqual(timeout, result.TimeoutFailed);
        Assert.AreEqual(timeout ? "timeout" : "parse_failed", result.ParseFailureRow.failure_kind);
        Assert.AreEqual(timeout ? "ChartInfoParseTimeoutException" : "InvalidDataException", result.ParseFailureRow.exception_type);
        Assert.AreEqual(md5, result.ParseFailureRow.md5);
    }

    /// <summary>BMSONの整数範囲外入力を数値変換の局所境界で形式失敗にし、内部故障と区別して永続失敗を生成する。</summary>
    [TestMethod]
    public void ChartInfoSnapshotEvaluator_BmsonNumericInputFailureRemainsPersistent()
    {
        string md5 = new('e', 32);
        string sha256 = new('f', 64);
        byte[] bytes = Encoding.UTF8.GetBytes("{\"info\":{\"level\":9223372036854775807}}");
        ChartFile file = ChartTestValues.Empty() with { Path = "invalid.bmson", Md5 = md5, Sha256 = sha256 };
        var snapshot = new ChartFileSnapshot(file.Path, bytes, DateTime.UtcNow, md5, sha256);

        ChartInfoBuildService.ChartInfoSnapshotBuildResult result = new ChartInfoBuildService().EvaluateSnapshot(
            snapshot, ChartInfoBuildTargetMapper.Create(file), null, hasCurrentParseFailure: false);

        Assert.IsTrue(result.ParseFailed);
        Assert.IsFalse(result.TimeoutFailed);
        Assert.IsNotNull(result.ParseFailureRow);
        Assert.AreEqual("JsonSerializationException", result.ParseFailureRow.exception_type);
    }

    private static TargetedFailureQueryRun ExecuteTargetedFailureQueryCase(int backgroundCount)
    {
        const string targetMd5 = "ffffffffffffffffffffffffffffffff";
        string tempRootPath = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker_ChartInfoTargetedFailureQuery_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            var setupGateway = new BmsLibraryDbGateway(songDbPath);
            setupGateway.EnsureChartInfoSchema();
            setupGateway.UpsertChartInfoParseFailures(
                Enumerable.Range(0, backgroundCount)
                    .Select(index => CreateChartInfoParseFailureRow(
                        index.ToString("x32", CultureInfo.InvariantCulture),
                        index.ToString("x64", CultureInfo.InvariantCulture),
                        Path.Combine(tempRootPath, "background-" + index.ToString(CultureInfo.InvariantCulture) + ".bms"),
                        BmsLibraryDbGateway.CurrentChartInfoParserVersion,
                        "parse_failed",
                        "InvalidDataException",
                        "background",
                        null))
                    .Append(CreateChartInfoParseFailureRow(
                        targetMd5,
                        new string('e', 64),
                        Path.Combine(tempRootPath, "target.bms"),
                        BmsLibraryDbGateway.CurrentChartInfoParserVersion,
                        "parse_failed",
                        "InvalidDataException",
                        "target",
                        null)));

            using var observation = new SqliteStatementObservation();
            var gateway = new BmsLibraryDbGateway(songDbPath, songDbFactory: observation.OpenSongDb);
            Dictionary<string, BeMusicSeeker.Models.ChartParseFailure> result = gateway.LoadCurrentChartInfoParseFailuresByMd5(
                [targetMd5],
                TimeSpan.FromSeconds(60));
            observation.ThrowIfCallbackFailed();

            IReadOnlyList<SqliteStatementObservation.SqliteObservedStatement> statements = observation.Statements;
            SqliteStatementObservation.SqliteObservedStatement[] targetedStatements = statements
                .Where(IsTargetedFailureStatement)
                .ToArray();
            Assert.AreEqual(1, targetedStatements.Length, "対象 failure query が一つにまとまりませんでした。");
            SqliteStatementObservation.SqliteObservedStatement targetedStatement = targetedStatements[0];
            Assert.AreEqual(1, targetedStatement.RowCount);
            Assert.IsTrue(targetedStatement.ProfileCount > 0, "対象 failure query の PROFILE callback を観測できませんでした。");
            Assert.IsTrue(targetedStatement.VmSteps > 0, "対象 failure query の VM_STEP を観測できませんでした。");
            Assert.AreEqual(0, targetedStatement.FullScanSteps, "対象 failure query が全表走査になりました。");
            Assert.AreEqual(1, result.Count);
            Assert.IsTrue(result.ContainsKey(targetMd5));
            return new TargetedFailureQueryRun(
                targetedStatement.RowCount,
                targetedStatement.FullScanSteps,
                targetedStatement.VmSteps,
                targetedStatement.ProfileCount);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    private static bool IsTargetedFailureStatement(SqliteStatementObservation.SqliteObservedStatement statement)
    {
        return statement != null
            && statement.Sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            && statement.Sql.Contains(
                "FROM chart_info_parse_failure",
                StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TestMutationWindow : IDisposable
    {
        public void Dispose() { }
    }

    private sealed record TargetedFailureQueryRun(
        int ReturnedRows,
        int FullScanSteps,
        int VmSteps,
        int ProfileCount);

}
