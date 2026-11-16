using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Wpf;
using static BeMusicSeeker.TestSupport.Components.BmsLibraryLr2SongDbSyncTestSupport;
using static BeMusicSeeker.TestSupport.Components.Lr2SongDbSyncTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>実共有Dispatcherの排出前後でLR2同期のLevel・LevelText・通知を確認します。</summary>
/// <remarks>
/// 実DB・ファイルシステム、ロールバック、結果記録、通知時の現在値は Core の同名クラスが headless scheduler で確認します。
/// このモジュールには、実Dispatcherを必要とする通知の確認だけを置きます。
/// 共通factoryの既定は変えず、Performanceと共用する祖先確認の補助処理 <c>BmsLibraryFolderRenameRefreshTestSupport.AssertFinalFilesystemAncestors</c> も、スケジューラーを省略したときは共有Dispatcherのスケジューラーを使う意味を保ちます。
/// </remarks>
[TestClass]
public sealed class BmsLibraryLr2SongDbSyncTests
{
    private readonly BeMusicSeeker.Properties.Settings testSettings = TestSettingsFactory.CreateIsolatedSettings();

    /// <summary>
    /// 旧Incompleteの途中位置から先頭実行し、Completedへの手動forceでは有効形式の誤CRCを修復し、新解析・現行cacheともUI排出前にLevel/LevelTextを譜面由来へ戻します。利用者列と保存・終端を維持します。
    /// </summary>
    /// <remarks>実管理主体とSQLite保存を接続し、入力面の選択だけでは分からない反映と終端を確認する。</remarks>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task QueueLr2SongDbSync_RunsLr2SongDbSyncAndMarksCompletedWhenClean(bool manualResync, bool currentCache)
    {
        LibraryFileInitializationResult? initializationInput = null;
        Lr2SongDbSyncPreparedDataSurface? preparedInput = null;
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string packDirectory = Path.Combine(rootDirectory, "Pack");
            string songDirectory = Path.Combine(packDirectory, "Song");
            Directory.CreateDirectory(songDirectory);
            File.WriteAllText(Path.Combine(packDirectory, "folderinfo.txt"), "#TITLE Pack Title");
            string chartPath = Path.Combine(songDirectory, "chart.bms");
            File.WriteAllText(chartPath, "#TITLE Parsed Title\r\n#ARTIST Parsed Artist\r\n#BPM 120\r\n#PLAYLEVEL 3\r\n#DIFFICULTY 2\r\n#RANK 3\r\n#00111:01\r\n");
            ChartFileSnapshot chartSnapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            File.WriteAllText(Path.Combine(songDirectory, "readme.txt"), "text group");
            string customFolderPath = Path.Combine(rootDirectory, "custom.lr2folder");
            File.WriteAllText(customFolderPath, "#TITLE Custom Folder");

            var library = new TestBmsLibrary(scope.DatabaseScope,
                scope.SongDbPath,
                null,
                null,
                null,
                new UnexpectedBmsLibraryDialogService(),
                new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher), settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings));
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = chartPath
            };
            file = file with { Md5 = chartSnapshot.Md5 };
            file = file with { Sha256 = chartSnapshot.Sha256 };
            file = file with { Folder = "876fd4dd", Level = 12 };
            LR2SongDB.song capturedStorageRow = ChartSongStorageMapping.ToBmsRow(file);
            capturedStorageRow.parent = "11111111";
            library.BmsCharts = [file];

            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                setup.RunInTransaction(() =>
                {
                    setup.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(((ChartTestValues.Empty() with
                    {
                        Path = chartPath,
                        AddDate = 98765,
                        Level = 12,
                        Folder = "876fd4dd",
                        Tag = "keep-tag"
                    })) with
                    { Md5 = "cccccccccccccccccccccccccccccccc", Favorite = 3 }), typeof(LR2SongDB.song));
                    BmsLibraryDbGateway.EnsureChartInfoSchema(setup);
                    setup.InsertOrReplace(ChartInfoStorageMapping.ToStorage(currentCache ? ChartInfoParser.Parse(chartPath) : new BeMusicSeeker.Models.ChartDetails
                    {
                        sha256 = chartSnapshot.Sha256,
                        md5 = chartSnapshot.Md5,
                        level = 9,
                        difficulty = 3,
                        maxbpm = 180.7,
                        minbpm = 120.4,
                        mode = 7,
                        feature = 4 | 8,
                        notes = 1234,
                        parser_version = manualResync ? 21 : BmsLibraryDbGateway.CurrentChartInfoParserVersion
                    }), typeof(LR2SongDBExtended.chart_info));
                    string stalePath = ToFolderPath(Path.Combine(rootDirectory, "Removed"));
                    setup.InsertOrReplace(new LR2SongDB.folder
                    {
                        path = stalePath,
                        type = 1,
                        date = 1
                    }, typeof(LR2SongDB.folder));
                    setup.InsertOrReplace(new LR2SongDB.folder
                    {
                        path = customFolderPath,
                        type = 2,
                        date = 1
                    }, typeof(LR2SongDB.folder));
                    setup.InsertOrReplace(new LR2SongDB.folder
                    {
                        path = Path.Combine(rootDirectory, "stale.lr2folder"),
                        type = 2,
                        date = 1
                    }, typeof(LR2SongDB.folder));
                });
            }

            using (LR2SongDBExtended statusSetup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                Lr2SongDbSyncStatusService.MarkIncomplete(statusSetup, "old-run", 9, 10, "song_rows", "interrupted", DateTime.UtcNow);
                if (manualResync) { statusSetup.Execute("UPDATE lr2_song_db_sync_status SET status = 'Completed', signature = 'legacy-parser21';"); }
                statusSetup.Execute("UPDATE song SET folder = '876fd4dd', parent = '11111111';");
            }

            // Everything can report a complete result before its index observes files created by this test.
            // Use the startup scan surface so the synchronization contract is exercised against deterministic input.
            initializationInput = new(InvokeCaptureLr2SongDbSyncScanSurface(
                library,
                new BmsLibraryOptionsSnapshot { OperationModeLR2DB = true },
                [rootDirectory],
                new SongTableFileCheckResult
                {
                    Lr2ScanSurfaceAvailable = true,
                    Lr2ScanNormalFolderDirectoryPaths = [rootDirectory, packDirectory, songDirectory],
                    Lr2ScanDirectoryEntries = CreateDirectoryEntryMap(rootDirectory, packDirectory, songDirectory),
                    Lr2ScanNormalFolderDirectoryEntries = CreateDirectoryEntryMap(rootDirectory, packDirectory, songDirectory),
                    Lr2ScanFolderInfoFilePaths = [Path.Combine(packDirectory, "folderinfo.txt")],
                    Lr2ScanFolderInfoFileEntries = CreateFileEntryMap(Path.Combine(packDirectory, "folderinfo.txt")),
                    Lr2ScanTextFileDirectories = [packDirectory, songDirectory],
                    Lr2ScanLr2FolderDiscoveryDirectories = [rootDirectory],
                    Lr2ScanLr2FolderFilePaths = [customFolderPath],
                    Lr2ScanLr2FolderFileEntries = CreateFileEntryMap(customFolderPath),
                    Lr2ScanLr2FolderFileDiscoveryComplete = true
                }));

            bool stagePublicationObserved = false;
            library.PropertyChanged += (sender, args) =>
            {
                if (string.Equals(args.PropertyName, nameof(BMSLibrary.Lr2SongDbSyncStage), StringComparison.Ordinal))
                {
                    stagePublicationObserved = true;
                }
            };
            Lr2SongDbSyncStatusSnapshot snapshot = await library.QueueLr2SongDbSyncAsync("test_enabled", force: manualResync, initializationResult: initializationInput, preparedSurface: preparedInput);
            Assert.AreEqual(manualResync ? 3 : 9, library.BmsCharts.Single().Level);
            Assert.AreEqual(manualResync ? "3" : "9", library.BmsCharts.Single().LevelText);
            TestUiDispatcherHost.ProcessQueuedPresentation();

            Assert.AreEqual(Lr2SongDbSyncStatusKind.Completed, snapshot.Status);
            Assert.IsTrue(stagePublicationObserved);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            LR2SongDBExtended.lr2_song_db_sync_status row = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.IsNotNull(row);
            Assert.AreEqual("Completed", row.status);
            Assert.AreEqual(string.Empty, row.last_error);
            Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, row.stage);
            Assert.AreEqual(row.total_count, row.processed_cursor);
            Assert.IsTrue(row.total_count > 0);
            Assert.IsNotNull(row.completed_at);

            string expectedRootFolderPath = ToFolderPath(rootDirectory);
            string expectedPackFolderPath = ToFolderPath(packDirectory);
            string expectedSongFolderPath = ToFolderPath(songDirectory);
            string expectedRemovedFolderPath = ToFolderPath(Path.Combine(rootDirectory, "Removed"));
            string expectedCustomFolderPath = customFolderPath;
            string expectedStaleLr2FolderPath = Path.Combine(rootDirectory, "stale.lr2folder");
            var folderRows = verify.Table<LR2SongDB.folder>().ToList();
            LR2SongDB.folder root = folderRows.Single(folder => folder.path == expectedRootFolderPath);
            LR2SongDB.folder pack = folderRows.Single(folder => folder.path == expectedPackFolderPath);
            LR2SongDB.folder song = folderRows.Single(folder => folder.path == expectedSongFolderPath);
            LR2SongDB.folder custom = folderRows.Single(folder => folder.path == expectedCustomFolderPath);
            Assert.AreEqual(1, root.type);
            Assert.AreEqual("Pack Title", pack.title);
            Assert.AreEqual("Song", song.title);
            Assert.AreEqual(2, custom.type);
            Assert.AreEqual("Custom Folder", custom.title);
            Assert.AreEqual(0, folderRows.Count(folder => folder.path == expectedRemovedFolderPath));
            Assert.AreEqual(0, folderRows.Count(folder => folder.path == expectedStaleLr2FolderPath));
            Assert.AreEqual(1, folderRows.Count(folder => folder.path == expectedCustomFolderPath));
            string expectedSongFolderHash = Lr2StoredPathReference.Compute(song.path);
            Assert.AreEqual(song.path, verify.Query<LR2SongDB.folder>("SELECT * FROM folder WHERE parent = ? AND path = ?;", Lr2StoredPathReference.Compute(pack.path), song.path).Single().path);
            Assert.AreEqual(custom.path, verify.Query<LR2SongDB.folder>("SELECT * FROM folder WHERE parent = ? AND path = ?;", Lr2StoredPathReference.Compute(root.path), custom.path).Single().path);
            Assert.AreEqual(expectedSongFolderHash, verify.ExecuteScalar<string>("SELECT folder FROM song WHERE path = ?;", chartPath));
            Assert.IsFalse(string.IsNullOrWhiteSpace(verify.ExecuteScalar<string>("SELECT parent FROM song WHERE path = ?;", chartPath)));
            Assert.AreEqual("Parsed Title", verify.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual("Parsed Artist", verify.ExecuteScalar<string>("SELECT artist FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(file.Md5, verify.ExecuteScalar<string>("SELECT hash FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(manualResync ? 3 : 9, verify.ExecuteScalar<int>("SELECT level FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(manualResync ? 2 : 3, verify.ExecuteScalar<int>("SELECT difficulty FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(manualResync ? 120 : 180, verify.ExecuteScalar<int>("SELECT maxbpm FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(120, verify.ExecuteScalar<int>("SELECT minbpm FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(5, verify.ExecuteScalar<int>("SELECT mode FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(manualResync ? 0 : 1, verify.ExecuteScalar<int>("SELECT random FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(manualResync ? 0 : 1, verify.ExecuteScalar<int>("SELECT longnote FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(manualResync ? 1 : 1234, verify.ExecuteScalar<int>("SELECT karinotes FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(1, verify.ExecuteScalar<int>("SELECT txt FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(3, verify.ExecuteScalar<int>("SELECT favorite FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(98765, verify.ExecuteScalar<int>("SELECT adddate FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual("keep-tag", verify.ExecuteScalar<string>("SELECT tag FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(string.Empty, row.signature);
            if (manualResync)
            {
                Assert.AreEqual(3, library.BmsCharts.Single().Level);
                SongTableLoadResult reloaded = new BmsLibraryInitializationService().LoadSongTable(
                    new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath), new BmsLibraryOptionsSnapshot { OperationModeLR2DB = true },
                    new OwnedChartCollectionTestSupport.TestFileMutationService(), null, exception => exception.Message);
                Assert.AreEqual(3, reloaded.LoadedFiles.Single().Level);
            }
            Assert.AreEqual(1, library.Lr2SongDbSyncRequestedVersion);
            Assert.AreEqual(1, library.Lr2SongDbSyncCompletedVersion);
            Assert.IsFalse(library.Lr2SongDbSyncRunning);
            Assert.AreEqual(row.total_count.GetValueOrDefault(), library.Lr2SongDbSyncTotalCount);
            Assert.AreEqual(row.processed_cursor.GetValueOrDefault(), library.Lr2SongDbSyncProcessedCount);
            Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, library.Lr2SongDbSyncStage);
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Completed, library.GetLr2SongDbSyncStatusSnapshot().Status);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }
}
