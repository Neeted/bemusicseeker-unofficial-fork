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
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.ChartInfoMetadataTestSupport;
using static BeMusicSeeker.TestSupport.Components.ChartInfoComponentTestSupport;
namespace BeMusicSeeker.Tests;

/// <summary>
/// 譜面情報の対象検索・補完・inline評価・候補分類を検査します。
/// </summary>
[TestClass]
public sealed class ChartInfoInlineHydrationTests
{
    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>実inline保存・公開後の負PLAYLEVELをASCII表記で詳細行へ渡し、その無変更文字列を実保存ownerが受理します。実F2操作と取消はworkspace画面テストが分担します。</summary>
    [TestMethod]
    public void InlinePublication_NegativeLevelCanBeCommittedFromDetailRowWithoutEditing()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
            WithTemporarySongDb((root, dbPath) =>
            {
                string path = Path.Combine(root, "negative-level.bms");
                File.WriteAllText(path, "#PLAYER 1\r\n#TITLE negative\r\n#PLAYLEVEL -1\r\n#BPM 130\r\n#00111:01\r\n", Encoding.ASCII);
                ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(path));
                var library = new TestBmsLibrary(databaseScope, dbPath, settings: testSettings);
                OwnedChartCollectionTestSupport.SetLibraryFilesWithoutNotification(library, [file]);
                OwnedChartCollectionTestSupport.SetLibraryBmsonSongsWithoutNotification(library, []);
                OwnedChartCollectionTestSupport.SetDuplicateChartGroupsWithoutNotification(library, []);
                OwnedChartCollectionTestSupport.InvokeBuildAndPersistInlineChartInfoForInstalledCharts(library, "negative_level", [file]);
                ChartFile current = library.BmsCharts.Single();
                Assert.AreEqual("-1", current.LevelText);

                PlaylistPersistenceRepository.EnsureSchema(databaseScope, dbPath);
                var collectionLock = new BeMusicSeeker.Models.Utils.ReaderWriterLockSlimWrapper();
                try
                {
                    var persistence = new PlaylistAggregatePersistenceOwner(new PlaylistPersistenceRepository(databaseScope, dbPath), collectionLock, new TestUiScheduler(() => null));
                    var entry = new BMSTableEntry { title = "negative", md5 = current.Md5, folder = "" };
                    var table = new BMSTable { playlist_id = 9321, name = "Negative", entries = [entry] };
                    persistence.MarkActiveTables([table]);
                    persistence.ReplaceTablesWithEntries([table]);
                    PlaylistDetailRow row = new BeMusicSeeker.ViewModels.PlaylistDetailSourceRow(entry, current).CreateViewRow();
                    Assert.AreEqual("-1", row.Level);
                    persistence.CommitEntry(entry, "Level", row.Level, null, _ => { }, () => collectionLock.GetReaderGuard());
                    Assert.AreEqual(-1d, entry.level);
                    using LR2SongDBExtended db = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), dbPath).OpenSongDbReadOnly();
                    Assert.AreEqual(-1d, db.Table<BMSTableEntry>().Single(e => e.playlist_id == 9321).level);
                }
                finally { collectionLock.Dispose(); }
            });
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private readonly BeMusicSeeker.Properties.Settings testSettings = TestSettingsFactory.CreateIsolatedSettings();

    /// <summary>
    /// 実保存行から索引を補完し、SHA一致をMD5代表より優先する。補完前後の解決と無関係SHAの直接解決を確かめる。
    /// </summary>
    /// <remarks>実BMSLibraryと専用DBを使い、保存行から索引・所有行・facade通知への接続を観測する。管理主体だけの結果ではこの接続を代用できない。</remarks>
    [TestMethod]
    public async Task DeferredChartInfoHydration_BuildsSessionIndexAndUsesSha256BeforeMd5()
    {

        await WithTemporarySongDb(async delegate (string tempRootPath, string songDbPath)
        {
            var gateway = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath);
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
            var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, null, new RecordingDialogService(), settings: testSettings);
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
            var gateway = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath);
            BeMusicSeeker.Models.ChartDetails bmsRow = CreateChartInfoRow(bmsSha, file.Md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion);
            BeMusicSeeker.Models.ChartDetails bmsonRow = CreateChartInfoRow(bmsonSha, bmsonSong.Md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion);
            gateway.UpsertChartInfos([bmsRow, bmsonRow]);
            var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, null, new RecordingDialogService(), settings: testSettings)
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
    [TestMethod]
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
            using (LR2SongDBExtended songDb = databaseScope.OpenSongWrite(songDbPath))
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
                    string signature = "lr2_song_db_sync_v2|appSchema=1|chartInfoSchema=1|chartInfoParser=21|operationModeLR2DB=1";
                    using LR2SongDBExtended songDb = databaseScope.OpenSongWrite(songDbPath);
                    Lr2SongDbSyncStatusService.MarkCompleted(songDb, "unit-test", 1, DateTime.UtcNow);
                    songDb.Execute("UPDATE lr2_song_db_sync_status SET signature = ?;", signature);
                }
                var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, null, new RecordingDialogService(), settings: testSettings)
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
            var library = new TestBmsLibrary(databaseScope, songDbPath, settings: testSettings);
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
                else if (args.PropertyName == nameof(BMSLibrary.CatalogRevision))
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
    /// 欠落情報と旧21を通常補完し、旧署名Completedでも全体同期を始めず、上書きlevel12を譜面由来3へDB・current・再読込みで戻します。
    /// </summary>
    /// <remarks>実BMSLibraryと専用DBを使い、保存行から索引・所有行・facade通知への接続を観測する。管理主体だけの結果ではこの接続を代用できない。</remarks>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task DeferredChartInfoHydration_MissingCurrentRowBackfillsRegardlessOfCompletedLr2Status(bool operationModeLr2Db, bool staleRow)
    {

        await WithTemporarySongDb(async delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "missing-current.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#PLAYLEVEL " + (staleRow ? "3" : "13") + "\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFile digest = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = chartPath
            };
            file = file with { Md5 = digest.Md5 };
            file = file with { Sha256 = digest.Sha256, Level = staleRow ? 12 : null };
            using (LR2SongDBExtended songDb = databaseScope.OpenSongWrite(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
                InsertSongForSummary(songDb, chartPath, file.Md5);
                if (staleRow)
                {
                    songDb.Execute("UPDATE song SET level = 12 WHERE path = ?;", chartPath);
                    songDb.InsertOrReplace(ChartInfoStorageMapping.ToStorage(CreateChartInfoRow(file.Sha256, file.Md5, 21)));
                }
                songDb.InsertOrReplace(CreateChartDigestRow(file.Md5, file.Sha256), typeof(LR2SongDBExtended.chart_digest_map));
                if (operationModeLr2Db)
                {
                    string signature = "lr2_song_db_sync_v2|appSchema=1|chartInfoSchema=1|chartInfoParser=21|operationModeLR2DB=1";
                    Lr2SongDbSyncStatusService.MarkCompleted(songDb, "unit-test", 1, DateTime.UtcNow);
                    songDb.Execute("UPDATE lr2_song_db_sync_status SET signature = ?;", signature);
                }
            }
            bool originalOperationMode = testSettings.OperationModeLR2DB;
            try
            {
                testSettings.OperationModeLR2DB = operationModeLr2Db;
                var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, null, new RecordingDialogService(), settings: testSettings)
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
                    int initialOwnedVersion = library.CatalogRevision;
                    InvokeDeferredChartInfoHydration(library, "unit_test_missing", queueFullBackfillAfterHydration: true);

                    await TestUiDispatcherHost.AwaitNotificationAsync(
                        AwaitChartInfoHydrationAsync(library), Task.WhenAll(workers), "chart-info.hydration-completion");
                    await Task.WhenAll(workers);
                    await TestUiDispatcherHost.AwaitNotificationAsync(
                        AwaitChartInfoBackfillAsync(library), Task.WhenAll(workers), "chart-info.backfill-completion");
                    using LR2SongDBExtended verify = databaseScope.OpenSongReadOnly(songDbPath);
                    Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ?;", file.Sha256));
                    Assert.AreEqual(staleRow ? 3 : 13, verify.ExecuteScalar<int>("SELECT level FROM song WHERE path = ?;", chartPath));
                    Assert.AreEqual(staleRow ? 3 : 13, library.BmsCharts.Single().Level);
                    Assert.AreSame(captured.Token, library.BmsCharts.Single().Token);
                    Assert.AreEqual(initialOwnedVersion + 1, library.CatalogRevision);
                    Assert.AreEqual(staleRow ? 12 : (int?)null, file.Level);
                    Assert.AreEqual(22, verify.ExecuteScalar<int>("SELECT parser_version FROM chart_info WHERE sha256 = ?;", file.Sha256));
                    Assert.AreEqual(0, library.Lr2SongDbSyncRequestedVersion);
                    SongTableLoadResult reloaded = new BmsLibraryInitializationService().LoadSongTable(
                        new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath), new BmsLibraryOptionsSnapshot { OperationModeLR2DB = operationModeLr2Db },
                        null, new OwnedChartCollectionTestSupport.TestFileMutationService(), null, exception => exception.Message);
                    Assert.AreEqual(staleRow ? 3 : 13, reloaded.LoadedFiles.Single().Level);
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
            var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, null, new RecordingDialogService(), settings: testSettings);
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

}
