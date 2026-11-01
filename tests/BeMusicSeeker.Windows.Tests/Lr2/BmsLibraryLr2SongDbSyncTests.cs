using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using static BeMusicSeeker.TestSupport.Components.BmsLibraryLr2SongDbSyncTestSupport;
using static BeMusicSeeker.TestSupport.Components.Lr2SongDbSyncTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryLr2SongDbSyncTests
{
    private readonly BeMusicSeeker.Properties.Settings testSettings = TestSettingsFactory.CreateIsolatedSettings();

    /// <summary>
    /// 通常出力先は譜面ルートとして保持し追加・ルート出力先と配下を除外して、用途の異なる登録範囲を混同しない。
    /// </summary>
    /// <remarks>実在/不在の登録先と設定を使い、通常出力と管理出力の探索境界を確認する。DB保存は観測しない。</remarks>
    [TestMethod]
    public void CaptureBmsDirectories_IncludesNormalOutputSearchRootsAndExcludesAdditionalAndRootOutputRoots()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string bmsRoot = Path.Combine(scope.DirectoryPath, "BMS");
            string nestedCustomOutputLikeDirectory = Path.Combine(bmsRoot, "#BeMusicSeeker");
            string normalOutputBase = Path.Combine(scope.DirectoryPath, "NormalCustomFolderOutput");
            string normalOutputChild = Path.Combine(normalOutputBase, "Table");
            string additionalOutputBase = Path.Combine(scope.DirectoryPath, "AdditionalCustomFolderOutput");
            string additionalOutputChild = Path.Combine(additionalOutputBase, "Table");
            string rootOutputBase = Path.Combine(scope.DirectoryPath, "RootCustomFolderOutput");
            string rootOutputChild = Path.Combine(rootOutputBase, "Table");
            Directory.CreateDirectory(bmsRoot);
            Directory.CreateDirectory(nestedCustomOutputLikeDirectory);
            Directory.CreateDirectory(normalOutputBase);
            Directory.CreateDirectory(normalOutputChild);
            Directory.CreateDirectory(additionalOutputBase);
            Directory.CreateDirectory(additionalOutputChild);
            Directory.CreateDirectory(rootOutputBase);
            Directory.CreateDirectory(rootOutputChild);
            testSettings.LR2CustomFolderOutputBaseDir = normalOutputBase;
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs =
                CustomFolderOutputBaseRegistry.SerializeBaseDirectories([additionalOutputBase]);
            testSettings.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            string normalChart = Path.Combine(normalOutputChild, "normal.bms");
            File.WriteAllText(normalChart, BmsLibraryInitializationTestSupport.CreateValidBmsText("NormalOutput"));
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, null, null, null,
                () => BmsLibraryOptionsSnapshot.CreateCurrent(testSettings), TestBmsFactory.MissingEverythingBridge,
                chartFileScanner: new FastDirectoryFileScanner(), rootFileEnumerator: new FastRootFileEnumerator(), settings: TestBmsFactory.CreateRootSettings([bmsRoot, nestedCustomOutputLikeDirectory, normalOutputBase, normalOutputChild, additionalOutputBase, additionalOutputChild, rootOutputBase, rootOutputChild], testSettings))
            { BmsCharts = [], BmsonCharts = [] };

            Lr2SearchRootSnapshot rootSnapshot = library.Lr2Synchronization.CaptureBmsDirectories();
            HashSet<string> directories = [.. rootSnapshot.Roots.Select(NormalizeDirectory)];

            Assert.IsTrue(directories.Contains(NormalizeDirectory(bmsRoot)));
            Assert.IsTrue(rootSnapshot.RequestedRoots.Any(path => NormalizeDirectory(path) == NormalizeDirectory(nestedCustomOutputLikeDirectory)));
            Assert.IsTrue(directories.Contains(NormalizeDirectory(bmsRoot)), "登録childは実XMLに保持し、走査では祖先に包含します。");
            Assert.IsTrue(directories.Contains(NormalizeDirectory(normalOutputBase)));
            Assert.IsTrue(rootSnapshot.RequestedRoots.Any(path => NormalizeDirectory(path) == NormalizeDirectory(normalOutputChild)));
            Assert.IsFalse(directories.Contains(NormalizeDirectory(additionalOutputBase)));
            Assert.IsFalse(directories.Contains(NormalizeDirectory(additionalOutputChild)));
            Assert.IsFalse(directories.Contains(NormalizeDirectory(rootOutputBase)));
            Assert.IsFalse(directories.Contains(NormalizeDirectory(rootOutputChild)));
            try
            {
                library.ReloadFileDiff();
                Assert.IsTrue(library.BmsCharts.Any(chart => chart.Path == normalChart), "利用者readerで除く通常出力も、実走査では取り込みます。");
            }
            finally { library.RequestShutdown("normal-output-scan-test"); }
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 存在・不在・通常・追加・ルート出力の登録を分類して件数と採用集合を報告し、範囲除外の診断誤りを検出する。
    /// </summary>
    /// <remarks>実在/不在の登録先と設定を使い、通常出力と管理出力の探索境界を確認する。DB保存は観測しない。</remarks>
    [TestMethod]
    public void CaptureBmsDirectories_ReportsCustomFolderOutputRootNormalizationCounts()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string bmsRoot = Path.Combine(scope.DirectoryPath, "BMS");
            string missingRoot = Path.Combine(scope.DirectoryPath, "Missing");
            string normalOutputBase = Path.Combine(scope.DirectoryPath, "NormalCustomFolderOutput");
            string normalOutputChild = Path.Combine(normalOutputBase, "Table");
            string additionalOutputBase = Path.Combine(scope.DirectoryPath, "AdditionalCustomFolderOutput");
            string additionalOutputChild = Path.Combine(additionalOutputBase, "Table");
            string rootOutputBase = Path.Combine(scope.DirectoryPath, "RootCustomFolderOutput");
            Directory.CreateDirectory(bmsRoot);
            Directory.CreateDirectory(normalOutputBase);
            Directory.CreateDirectory(normalOutputChild);
            Directory.CreateDirectory(additionalOutputBase);
            Directory.CreateDirectory(additionalOutputChild);
            Directory.CreateDirectory(rootOutputBase);
            testSettings.LR2CustomFolderOutputBaseDir = normalOutputBase;
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs =
                CustomFolderOutputBaseRegistry.SerializeBaseDirectories([additionalOutputBase]);
            testSettings.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: TestBmsFactory.CreateRootSettings([bmsRoot, missingRoot, normalOutputBase, normalOutputChild, additionalOutputBase, additionalOutputChild, rootOutputBase], testSettings))
            {

            };

            Lr2SearchRootSnapshot rootSnapshot = library.Lr2Synchronization.CaptureBmsDirectories();
            List<string> directories = [.. rootSnapshot.Roots];

            CollectionAssert.AreEquivalent(new[] { NormalizeDirectory(bmsRoot), NormalizeDirectory(normalOutputBase) }, directories.Select(NormalizeDirectory).ToArray());
            Assert.AreEqual(7, rootSnapshot.RequestedRootCount);
            Assert.AreEqual(4, rootSnapshot.ExistingRootCount);
            Assert.AreEqual(2, rootSnapshot.Roots.Count);
            Assert.AreEqual(2, rootSnapshot.ConfiguredCustomOutputRootCount);
            Assert.AreEqual(2, rootSnapshot.ExcludedCustomOutputRootCount);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// LR2モードの実ファイル差分取込みで譜面のルート・祖先・直近フォルダを保存し、通常走査とLR2反映の接続漏れを検出する。
    /// </summary>
    /// <remarks>実SQLiteと管理主体を接続して保存結果または保存前の拒否を確認する。局所portだけではDB反映との順序を判定できない。</remarks>
    [TestMethod]
    public void ReloadFileDiff_SyncsNormalFolderRowsWhenLr2SongDbSyncEnabled()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string packDirectory = Path.Combine(rootDirectory, "Pack");
            string songDirectory = Path.Combine(packDirectory, "Song");
            Directory.CreateDirectory(songDirectory);
            string chartPath = Path.Combine(songDirectory, "chart.bms");
            File.WriteAllText(chartPath, "#TITLE Added\r\n#ARTIST Artist\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            ChartFile file = CreateSyncTestFile(chartPath, snapshot);
            ChartFile previous = file with { Md5 = new string('a', 32), RawTitle = "Old", Title = "Old", Date = 1 };
            ChartFile removed = previous with { Path = Path.Combine(songDirectory, "removed.bms"), Md5 = new string('b', 32) };
            string addedPath = Path.Combine(songDirectory, "added.bms");
            File.WriteAllText(addedPath, "#TITLE New\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).UpsertSongs([previous, removed]);
            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                setup.CreateTable<LR2SongDB.folder>();
                Lr2SongDbSyncStatusService.MarkCompleted(setup, "legacy-completed", 1, DateTime.UtcNow);
                setup.Execute("UPDATE lr2_song_db_sync_status SET signature = 'old-parser21';");
            }
            BmsLibraryOptionsSnapshot options = new()
            {
                OperationModeLR2DB = true,
                ScanBmsFilesOnStartup = false,
                UpdateLr2IrRankingCacheOnStartup = false,
                EnableDownloadLr2IrScoreAndDetectUnsent = false,
                UseBeatorajaScoreDb = false,
                EnableReadOptimizedPragmas = false,
                PendingInstallEstimateMaxParallelPackages = 1
            };
            IChartFileScanner chartFileScanner = CapturedChartFileScanner.FromFixture(
                [chartPath, addedPath],
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    [songDirectory] = []
                },
                [rootDirectory]);
            var library = new TestBmsLibrary(scope.DatabaseScope,
                scope.SongDbPath,
                getLR2Config: null,
                _lr2ScoreDB: null,
                startupRequiredFileScanReason: null,
                optionsSnapshotProvider: () => options,
                applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge,
                chartFileScanner: chartFileScanner,
                dialogService: new BmsLibraryInitializationTestSupport.RecordingDialogService(), settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

                BmsCharts = [previous, removed]
            };

            try
            {
                library.ReloadFileDiff();

                using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
                LR2SongDB.folder[] folders = [.. verify.Table<LR2SongDB.folder>()];
                Assert.AreEqual(3, folders.Count(folder => folder.type == 1));
                Assert.IsTrue(folders.Any(folder => folder.path == ToFolderPath(rootDirectory)));
                Assert.IsTrue(folders.Any(folder => folder.path == ToFolderPath(packDirectory)));
                Assert.IsTrue(folders.Any(folder => folder.path == ToFolderPath(songDirectory)));
                Assert.AreEqual(2, verify.Table<LR2SongDB.song>().Count());
                Assert.AreEqual("Added", verify.Find<LR2SongDB.song>(chartPath).title);
                Assert.AreEqual(snapshot.Md5, verify.Find<LR2SongDB.song>(chartPath).hash);
                Assert.AreEqual("New", verify.Find<LR2SongDB.song>(addedPath).title);
                Assert.IsNull(verify.Find<LR2SongDB.song>(removed.Path));
                Assert.AreEqual("Completed", verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>("default").status);
                Assert.AreEqual("old-parser21", verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>("default").signature);
                Assert.AreEqual(0, library.Lr2SongDbSyncRequestedVersion);
            }
            finally
            {
                library.RequestShutdown("lr2-file-diff-folder-sync-test");
            }
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// LR2モード無効の実ファイル取込みは通常フォルダ行を作らず、別モードへのLR2反映混入を防ぐ。
    /// </summary>
    /// <remarks>実SQLiteと管理主体を接続して保存結果または保存前の拒否を確認する。局所portだけではDB反映との順序を判定できない。</remarks>
    [TestMethod]
    public void ReloadFileDiff_DoesNotSyncNormalFolderRowsWhenLr2ModeDisabled()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = false;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string songDirectory = Path.Combine(rootDirectory, "Pack", "Song");
            Directory.CreateDirectory(songDirectory);
            string chartPath = Path.Combine(songDirectory, "chart.bms");
            File.WriteAllText(chartPath, "#TITLE Added\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            ChartFile file = CreateSyncTestFile(chartPath, snapshot);
            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                setup.CreateTable<LR2SongDB.folder>();
            }
            BmsLibraryOptionsSnapshot options = new()
            {
                OperationModeLR2DB = false,
                ScanBmsFilesOnStartup = false,
                UpdateLr2IrRankingCacheOnStartup = false,
                EnableDownloadLr2IrScoreAndDetectUnsent = false,
                UseBeatorajaScoreDb = false,
                EnableReadOptimizedPragmas = false,
                PendingInstallEstimateMaxParallelPackages = 1
            };
            IChartFileScanner chartFileScanner = CapturedChartFileScanner.FromFixture(
                [chartPath],
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    [songDirectory] = []
                },
                [rootDirectory]);
            var library = new TestBmsLibrary(scope.DatabaseScope,
                scope.SongDbPath,
                getLR2Config: null,
                _lr2ScoreDB: null,
                startupRequiredFileScanReason: null,
                optionsSnapshotProvider: () => options,
                applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge,
                chartFileScanner: chartFileScanner,
                dialogService: new BmsLibraryInitializationTestSupport.RecordingDialogService(), settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

                BmsCharts = []
            };

            try
            {
                library.ReloadFileDiff();

                using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
                Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().Count());
            }
            finally
            {
                library.RequestShutdown("lr2-file-diff-folder-sync-disabled-test");
            }
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 実取込み後の譜面削除で該当songと通常フォルダを消し、残存譜面のフォルダ・祖先を保持して削除範囲の誤りを検出する。
    /// </summary>
    /// <remarks>実SQLiteと管理主体を接続して保存結果または保存前の拒否を確認する。局所portだけではDB反映との順序を判定できない。</remarks>
    [TestMethod]
    // 依存なし、譜面だけ削除してcustomが残る、フォルダごとcustomも消える条件を同じ実入口で比較します。
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void RemoveLibraryCharts_PrunesNormalFolderRowsForRemovedBmsWhenLr2SongDbSyncEnabled(bool customPresent, bool wholeFolder)
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string packDirectory = Path.Combine(rootDirectory, "Pack");
            string keepDirectory = Path.Combine(packDirectory, "Keep");
            string removeDirectory = Path.Combine(packDirectory, "Remove");
            Directory.CreateDirectory(keepDirectory);
            Directory.CreateDirectory(removeDirectory);
            string keepPath = Path.Combine(keepDirectory, "keep.bms");
            string removePath = Path.Combine(removeDirectory, "remove.bms");
            File.WriteAllText(keepPath, "#TITLE Keep\r\n#00111:01\r\n", Encoding.ASCII);
            File.WriteAllText(removePath, "#TITLE Remove\r\n#00111:01\r\n", Encoding.ASCII);
            string customPath = Path.Combine(removeDirectory, "external.lr2folder");
            if (customPresent) { File.WriteAllText(customPath, "#TITLE External", Encoding.ASCII); }
            ChartFile keepFile = CreateSyncTestFile(keepPath, ChartFileContentReader.ReadSnapshot(keepPath));
            ChartFile removeFile = CreateSyncTestFile(removePath, ChartFileContentReader.ReadSnapshot(removePath));
            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                setup.RunInTransaction(() =>
                {
                    setup.CreateTable<LR2SongDB.folder>();
                    setup.CreateTable<LR2SongDB.song>();
                    setup.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(keepFile), typeof(LR2SongDB.song));
                    setup.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(removeFile), typeof(LR2SongDB.song));
                });
            }
            BmsLibraryOptionsSnapshot options = new()
            {
                OperationModeLR2DB = true,
                ScanBmsFilesOnStartup = false,
                UpdateLr2IrRankingCacheOnStartup = false,
                EnableDownloadLr2IrScoreAndDetectUnsent = false,
                UseBeatorajaScoreDb = false,
                EnableReadOptimizedPragmas = false,
                PendingInstallEstimateMaxParallelPackages = 1
            };
            IChartFileScanner chartFileScanner = CapturedChartFileScanner.FromFixture(
                [keepPath, removePath],
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    [rootDirectory] = []
                },
                [rootDirectory]);
            var library = new TestBmsLibrary(scope.DatabaseScope,
                scope.SongDbPath,
                getLR2Config: null,
                _lr2ScoreDB: null,
                startupRequiredFileScanReason: null,
                optionsSnapshotProvider: () => options,
                applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge,
                chartFileScanner: chartFileScanner,
                dialogService: new BmsLibraryInitializationTestSupport.RecordingDialogService(), settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

                BmsCharts = []
            };

            try
            {
                // 先に通常のファイル走査を通して、削除対象を含む実在カタログを構築する。
                library.ReloadFileDiff();
                if (customPresent)
                {
                    using LR2SongDBExtended setupCustom = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath);
                    setupCustom.InsertOrReplace(new LR2SongDB.folder { path = customPath, type = 2, title = "External", command = "song.hash = 'keep'", parent = "keep-exact-parent", date = 321, adddate = 123 });
                    Assert.IsNotNull(setupCustom.Find<LR2SongDB.folder>(customPath), "全物理削除でも直前にcustomのDB行が存在する条件を固定します。");
                }
                ChartFile currentRemoveFile = library.BmsCharts.Single(file =>
                    string.Equals(file.Path, removePath, StringComparison.OrdinalIgnoreCase));
                LibraryChartRemovalOutcome removal = library.RemoveLibraryCharts(
                    library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile((currentRemoveFile))]),
                    sendToRecycleBin: false,
                    approvedWholeFolderDeletePaths: wholeFolder ? [removeDirectory] : []);
                Assert.IsFalse(removal.HasError);

                using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
                List<LR2SongDB.folder> folders = [.. verify.Table<LR2SongDB.folder>()];
                Assert.IsTrue(folders.Any(folder => folder.path == ToFolderPath(rootDirectory)));
                Assert.IsTrue(folders.Any(folder => folder.path == ToFolderPath(packDirectory)));
                Assert.IsTrue(folders.Any(folder => folder.path == ToFolderPath(keepDirectory)));
                Assert.AreEqual(customPresent && !wholeFolder, folders.Any(folder => folder.path == ToFolderPath(removeDirectory)));
                if (customPresent && !wholeFolder)
                {
                    Assert.IsTrue(File.Exists(customPath));
                    Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(packDirectory), folders.Single(folder => folder.path == ToFolderPath(removeDirectory)).parent);
                    LR2SongDB.folder custom = verify.Find<LR2SongDB.folder>(customPath);
                    Assert.AreEqual("External", custom.title); Assert.AreEqual("keep-exact-parent", custom.parent);
                    Assert.AreEqual(123, custom.adddate); Assert.AreEqual(321, custom.date);
                }
                if (wholeFolder) { Assert.IsFalse(Directory.Exists(removeDirectory)); }
                Assert.IsNotNull(verify.Find<LR2SongDB.song>(keepPath));
                Assert.IsNull(verify.Find<LR2SongDB.song>(removePath));
            }
            finally
            {
                library.RequestShutdown("lr2-remove-folder-prune-test");
            }
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// カタログ削除がSQLで失敗すると既存songを保持して通常フォルダ同期を進めず、未確定変更から後段へ反映する誤りを防ぐ。
    /// </summary>
    /// <remarks>実SQLiteと管理主体を接続して保存結果または保存前の拒否を確認する。局所portだけではDB反映との順序を判定できない。</remarks>
    [TestMethod]
    public void RemoveLibraryCharts_DoesNotSyncNormalFolderRowsWhenCatalogWriteFails()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            string chartPath = Path.Combine(rootDirectory, "chart.bms");
            File.WriteAllText(chartPath, "#TITLE Remove\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFile file = CreateSyncTestFile(chartPath, ChartFileContentReader.ReadSnapshot(chartPath));
            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                setup.RunInTransaction(() =>
                {
                    setup.CreateTable<LR2SongDB.folder>();
                    setup.CreateTable<LR2SongDB.song>();
                    setup.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
                    string escapedPath = chartPath.Replace("'", "''");
                    setup.Execute(
                        "CREATE TRIGGER fail_catalog_remove BEFORE DELETE ON song WHEN OLD.path = '"
                        + escapedPath
                        + "' BEGIN SELECT RAISE(ABORT, 'forced catalog mutation failure'); END;");
                });
            }

            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

                BmsCharts = [file]
            };
            LibraryChartRemovalOutcome removal = library.RemoveLibraryCharts(
                library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile((file))]),
                sendToRecycleBin: false,
                approvedWholeFolderDeletePaths: []);
            Assert.IsTrue(removal.HasError);
            Assert.IsFalse(removal.CatalogDurable);

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().Count());
            Assert.IsNotNull(verify.Find<LR2SongDB.song>(chartPath));
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 実フォルダ移動でsong実キーと通常階層を新パスへ反映し、移動先folderinfo・時刻を保持して旧行残留を検出する。
    /// </summary>
    /// <remarks>実SQLiteと管理主体を接続して保存結果または保存前の拒否を確認する。局所portだけではDB反映との順序を判定できない。</remarks>
    [TestMethod]
    public void RenameChartFolder_MovesNormalFolderRowsForMovedBmsWhenLr2SongDbSyncEnabled()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string packDirectory = Path.Combine(rootDirectory, "Pack");
            string oldDirectory = Path.Combine(packDirectory, "Old");
            string newDirectory = Path.Combine(packDirectory, "New");
            Directory.CreateDirectory(oldDirectory);
            string oldPath = Path.Combine(oldDirectory, "chart.bms");
            string newPath = Path.Combine(newDirectory, "chart.bms");
            string oldFolderInfoPath = Path.Combine(oldDirectory, "folderinfo.txt");
            DateTime newDirectoryTimestamp = new(2026, 6, 11, 1, 2, 3, DateTimeKind.Utc);
            File.WriteAllText(oldPath, "#TITLE Moved\r\n#00111:01\r\n", Encoding.ASCII);
            File.WriteAllText(oldFolderInfoPath, "#TITLE Owned Mutation New", Encoding.GetEncoding("shift_jis"));
            Directory.SetLastWriteTimeUtc(oldDirectory, newDirectoryTimestamp);
            ChartFile file = CreateSyncTestFile(oldPath, ChartFileContentReader.ReadSnapshot(oldPath));
            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                setup.RunInTransaction(() =>
                {
                    setup.CreateTable<LR2SongDB.folder>();
                    setup.CreateTable<LR2SongDB.song>();
                    setup.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
                });
            }
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

                BmsCharts = []
            };
            library.BmsCharts = [file];
            LibraryMutationSessionReceipt moveReceipt = library.RenameChartFolderWithReceipt(
                oldDirectory,
                "New",
                unregister: false,
                renameRootFolder: false);
            Assert.IsTrue(moveReceipt.DurableCommit, moveReceipt.PrimaryFailure?.ToString());

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            List<LR2SongDB.folder> folders = [.. verify.Table<LR2SongDB.folder>()];
            Assert.AreEqual(oldPath, file.Path);
            Assert.AreEqual(newPath, library.BmsCharts.Single().Path);
            Assert.IsTrue(folders.Any(folder => folder.path == ToFolderPath(rootDirectory)));
            Assert.IsTrue(folders.Any(folder => folder.path == ToFolderPath(packDirectory)));
            Assert.IsFalse(folders.Any(folder => folder.path == ToFolderPath(oldDirectory)));
            LR2SongDB.folder newFolder = folders.Single(folder => folder.path == ToFolderPath(newDirectory));
            Assert.AreEqual("Owned Mutation New", newFolder.title);
            Assert.AreEqual(
                Lr2SongRowEnricher.ToLr2UnixSeconds(Directory.GetLastWriteTimeUtc(newDirectory)),
                newFolder.date);
            Assert.IsNull(verify.Find<LR2SongDB.song>(oldPath));
            Assert.IsNotNull(verify.Find<LR2SongDB.song>(newPath));
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 受理済みLR2同期を保持したままファイル差分要求を拒否し、利用者へbusy警告を一度渡して競合する変更を開始しない。
    /// </summary>
    [TestMethod]
    public async Task ReloadFileDiff_BlocksWhileLr2SongDbSyncIsRunning()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            var dialogs = new BmsLibraryInitializationTestSupport.RecordingDialogService();
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, null, null, null, dialogs, settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

                BmsCharts = []
            };
            await using HeldLr2Operation held = await HeldLr2Operation.StartAsync(library);

            library.ReloadFileDiff();
            Assert.AreEqual(1, dialogs.Calls.Count);
            Assert.AreEqual(Resources.Warn_LibraryOperationBusy, dialogs.Calls[0].Message);
            library.RequestShutdown("lr2-file-diff-blocked-test");
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// LR2同期との競合で値のある対象の表レベル上書きを拒否し、DB・現在値・新規表示投影を保つ。Busy通知後に受付を解放しても自動保存しない。
    /// </summary>
    [TestMethod]
    public async Task PlaylistTableLevelOverwriteWorkflow_PresentsBlockedWarningWithoutLibraryDialog()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            ChartFile song = ChartTestValues.Empty() with { Path = Path.Combine(scope.DirectoryPath, "level.bms"), Md5 = new string('a', 32), Level = 4, LevelText = "4" };
            new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).UpsertSongs([song]);
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: testSettings)
            {
                BmsCharts = [song]
            };
            await using (HeldLr2Operation held = await HeldLr2Operation.StartAsync(library))
            {
                var dialogs = new PlaylistWorkspaceFixturePorts.PlaylistWorkspaceDialogService
                {
                    ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
                };
                var workflow = new PlaylistTableLevelOverwriteWorkflowOwner(dialogs, () => library);

                await workflow.OverwriteAsync(new BMSTable { entries = [new BMSTableEntry { md5 = song.Md5, level = 12 }] });

                Assert.IsNotNull(dialogs.LastMessageRequest);
                Assert.AreEqual(Resources.Warn_LibraryOperationBusy, dialogs.LastMessageRequest.MessageBoxText);
                Assert.AreEqual(Resources.MessageBoxTitle_Warning, dialogs.LastMessageRequest.Caption);
                AssertOriginalLevel();
            }
            AssertOriginalLevel();

            void AssertOriginalLevel()
            {
                using LR2SongDBExtended verify = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath);
                Assert.AreEqual(4, verify.ExecuteScalar<int>("SELECT level FROM song WHERE path = ?;", song.Path));
                ChartFile current = library.BmsCharts.Single();
                Assert.AreEqual(4.0, current.Level);
                LibraryChartRow projected = new MainChartRowProjectionOwner().CreateLibraryRow(library, current);
                Assert.AreEqual(4.0, projected.level);
                Assert.AreEqual("4", projected.Level);
            }
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// LR2無効時は最終検査のfalseを一回通知し、準備・投稿前に両受付を解放して不要状態を返します。
    /// </summary>
    [TestMethod]
    public async Task QueueLr2SongDbSync_DoesNotQueueWhenLr2ModeDisabled()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = false;
            ResetLr2FolderDiscoverySettings(testSettings);
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: testSettings);
            bool queued = false;
            library.StartupBackgroundTaskScheduler = delegate
            {
                queued = true;
                return true;
            };

            int prepared = 0;
            List<bool> admissions = [];
            Lr2SongDbSyncStatusSnapshot snapshot = await library.QueueLr2SongDbSyncAsync("test_l2_mode_disabled",
                prepareGeneratedData: (_, _) => { prepared++; return Lr2SongDbSyncPreparedDataSurface.Empty; },
                admissionResult: admissions.Add);

            CollectionAssert.AreEqual(new[] { false }, admissions);
            Assert.AreEqual(0, prepared);
            Assert.IsTrue(library.OperationAdmission.TryEnter(out IDisposable nextLibrary));
            nextLibrary.Dispose();
            Assert.IsTrue(library.Lr2Synchronization.PlaylistOperationAdmission.TryEnter(out IDisposable nextPlaylist));
            nextPlaylist.Dispose();

            Assert.AreEqual(Lr2SongDbSyncStatusKind.NotNeeded, snapshot.Status);
            Assert.AreEqual(1, library.Lr2SongDbSyncStatusVersion);
            Assert.AreEqual(Lr2SongDbSyncStatusKind.NotNeeded, library.GetLr2SongDbSyncStatusSnapshot().Status);
            Assert.IsFalse(queued);
            Assert.AreEqual(0, library.Lr2SongDbSyncRequestedVersion);
            Assert.AreEqual(0, library.Lr2SongDbSyncCompletedVersion);
            Assert.IsFalse(library.Lr2SongDbSyncRunning);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>旧複合署名付き保存済みCompletedでは通常要求の同期を省略し、完了状態・run・DBを保持して新要求と準備を開始せず、L/Pを解放します。</summary>
    /// <remarks>状態保存の本番窓口で入力を作ります。省略後の取り込み成功分類と案内はAutomaticContinuationの実owner系列が分担します。</remarks>
    [TestMethod]
    public async Task QueueLr2SongDbSync_LegacyCompletedSkipsWithoutStartingOrChangingStoredCompletion()
    {
        using var scope = TestDatabaseScope.Create();
        var options = new BmsLibraryOptionsSnapshot { OperationModeLR2DB = true };
        string signature = "lr2_song_db_sync_v2|appSchema=1|chartInfoSchema=1|chartInfoParser=21|operationModeLR2DB=1";
        DateTime completedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        string root = Path.Combine(scope.DirectoryPath, "Root"), child = Path.Combine(root, "Child");
        Directory.CreateDirectory(child);
        using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
        {
            setup.CreateTable<LR2SongDB.folder>();
            Lr2FolderFileDbSyncService.Sync(setup, new Lr2FolderFileDbSyncRequest
            {
                Items = [new Lr2FolderFileSyncItem { FilePath = Path.Combine(child, "external.lr2folder"), Definition = new Lr2FolderFileDefinition { Title = "External" }, LastWriteTimeUtc = completedAt }],
                DirectoryRowRootDirectories = [root],
                DirectoryRowGenerationScopeDirectories = [Path.GetDirectoryName(root) ?? throw new AssertFailedException("Missing boundary.")],
                DirectoryMetadataResolver = _ => new Lr2FolderDirectoryMetadata(completedAt)
            });
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(root), setup.Find<LR2SongDB.folder>(ToFolderPath(child)).parent);
            Lr2SongDbSyncStatusService.MarkCompleted(setup, "previous-completed-run", 7, completedAt);
            setup.Execute("UPDATE lr2_song_db_sync_status SET signature = ?;", signature);
        }
        var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, null, null, null,
            () => options, TestBmsFactory.MissingEverythingBridge, settings: testSettings);
        byte[] before = File.ReadAllBytes(scope.SongDbPath);
        int prepared = 0;
        try
        {
            Lr2SongDbSyncStatusSnapshot result = await library.QueueLr2SongDbSyncAsync("completed-normal-request",
                prepareGeneratedData: (_, _) => { prepared++; return Lr2SongDbSyncPreparedDataSurface.Empty; });
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Completed, result.Status);
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Completed, result.StoredStatus);
            Assert.AreEqual("previous-completed-run", result.RunId);
            Assert.AreEqual(completedAt, result.CompletedAt);
            Assert.AreEqual(0, prepared);
            Assert.AreEqual(0, library.Lr2SongDbSyncRequestedVersion);
            Assert.AreEqual(0, library.Lr2SongDbSyncCompletedVersion);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(scope.SongDbPath));
            Assert.IsTrue(library.OperationAdmission.TryEnter(out IDisposable nextLibrary));
            nextLibrary.Dispose();
            Assert.IsTrue(library.Lr2Synchronization.PlaylistOperationAdmission.TryEnter(out IDisposable nextPlaylist));
            nextPlaylist.Dispose();
        }
        finally { library.RequestShutdown("completed-normal-request-cleanup"); }
    }

    /// <summary>
    /// 差分フォルダ同期失敗を保存状態と公開状態へ不完全として反映し、前回完了を現在も有効と誤報することを防ぐ。
    /// </summary>
    /// <remarks>実SQLiteと管理主体を接続して保存結果または保存前の拒否を確認する。局所portだけではDB反映との順序を判定できない。</remarks>
    [TestMethod]
    public void FileDiffNormalFolderSyncFailureMarksLr2SongDbSyncIncomplete()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

            };
            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                Lr2SongDbSyncStatusService.MarkCompleted(
                    setup,
                    runId: "completed",
                    totalCount: 1,
                    nowUtc: DateTime.UtcNow);
            }
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
            };
            var result = new SongTableFileCheckResult
            {
                Lr2NormalFolderSyncFailed = true,
                Lr2NormalFolderSyncFailureReason = "db locked"
            };

            InvokeMarkLr2SongDbSyncIncompleteAfterFileDiffNormalFolderSyncFailure(library, options, result);

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            LR2SongDBExtended.lr2_song_db_sync_status row = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.IsNotNull(row);
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Incomplete.ToString(), row.status);
            Assert.AreEqual("lr2_normal_folder_file_diff_sync_failed", row.stage);
            StringAssert.Contains(row.last_error, "db locked");
            Lr2SongDbSyncStatusSnapshot snapshot = library.GetLr2SongDbSyncStatusSnapshot();
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Incomplete, snapshot.Status);
            Assert.AreEqual("lr2_normal_folder_file_diff_sync_failed", snapshot.Stage);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 変更セッションのフォルダ同期失敗を保存・公開状態へ原因付きで反映し、確定後の不完全状態を隠す誤りを防ぐ。
    /// </summary>
    /// <remarks>実SQLiteと管理主体を接続して保存結果または保存前の拒否を確認する。局所portだけではDB反映との順序を判定できない。</remarks>
    [TestMethod]
    public void MutationNormalFolderSyncFailureMarksLr2SongDbSyncIncomplete()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

            };
            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                Lr2SongDbSyncStatusService.MarkCompleted(
                    setup,
                    runId: "completed",
                    totalCount: 1,
                    nowUtc: DateTime.UtcNow);
            }
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
            };

            InvokeMarkLr2SongDbSyncIncompleteAfterNormalFolderSyncFailure(
                library,
                options,
                stage: "lr2_normal_folder_mutation_sync_failed",
                detail: "lr2_normal_folder_mutation_sync_failed: db locked",
                logReason: "lr2_normal_folder_mutation_sync_failed");

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            LR2SongDBExtended.lr2_song_db_sync_status row = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.IsNotNull(row);
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Incomplete.ToString(), row.status);
            Assert.AreEqual("lr2_normal_folder_mutation_sync_failed", row.stage);
            StringAssert.Contains(row.last_error, "db locked");
            Lr2SongDbSyncStatusSnapshot snapshot = library.GetLr2SongDbSyncStatusSnapshot();
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Incomplete, snapshot.Status);
            Assert.AreEqual("lr2_normal_folder_mutation_sync_failed", snapshot.Stage);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// song書込み失敗を保存・公開状態へ原因付きで反映し、前回の完了状態を残す誤りを防ぐ。
    /// </summary>
    /// <remarks>実SQLiteと管理主体を接続して保存結果または保存前の拒否を確認する。局所portだけではDB反映との順序を判定できない。</remarks>
    [TestMethod]
    public void SongDbWriteFailureMarksLr2SongDbSyncIncomplete()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

            };
            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                Lr2SongDbSyncStatusService.MarkCompleted(
                    setup,
                    runId: "completed",
                    totalCount: 1,
                    nowUtc: DateTime.UtcNow);
            }
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
            };

            InvokeMarkLr2SongDbSyncIncompleteAfterSongDbWriteFailure(
                library,
                options,
                stage: "lr2_song_db_test_write_failed",
                detail: "lr2_song_db_test_write_failed: db locked",
                logReason: "lr2_song_db_test_write_failed");

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            LR2SongDBExtended.lr2_song_db_sync_status row = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.IsNotNull(row);
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Incomplete.ToString(), row.status);
            Assert.AreEqual("lr2_song_db_test_write_failed", row.stage);
            StringAssert.Contains(row.last_error, "db locked");
            Lr2SongDbSyncStatusSnapshot snapshot = library.GetLr2SongDbSyncStatusSnapshot();
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Incomplete, snapshot.Status);
            Assert.AreEqual("lr2_song_db_test_write_failed", snapshot.Stage);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 変更権を保持中は競合権を取得させず、解放後は新要求が取得できて拒否と解放の両方を確認する。
    /// </summary>
    [TestMethod]
    public void MutationAdmission_RejectsConcurrentWritersWithoutWaiting()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: testSettings);
            BMSLibrary.Lr2SynchronizationOwner owner = GetLr2SynchronizationOwner(library);
            using LibraryFileMutationLease incumbent = owner.TryBeginMutation(
                "test_incumbent",
                showMessage: false);
            Assert.IsNotNull(incumbent);
            Assert.IsNull(owner.TryBeginMutation("test_competing", showMessage: false));

            incumbent.Dispose();
            using LibraryFileMutationLease fresh = owner.TryBeginMutation(
                "test_after_release",
                showMessage: false);
            Assert.IsNotNull(fresh);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// ファイル変更権が使用中の同期要求は現在状態を返し準備・投稿を行わず、拒否後の隠れた実行開始を防ぐ。
    /// </summary>
    [TestMethod]
    public async Task QueueLr2SongDbSync_WhenFileMutationLeaseIsActive_ReturnsCurrentStatusWithoutPreparationOrScheduling()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, null, null, null, new BmsLibraryInitializationTestSupport.RecordingDialogService(), settings: testSettings);
            BMSLibrary.Lr2SynchronizationOwner owner = GetLr2SynchronizationOwner(library);
            int prepareCount = 0;
            int scheduleCount = 0;
            library.StartupBackgroundTaskScheduler = delegate
            {
                scheduleCount++;
                return true;
            };
            Lr2SongDbSyncStatusSnapshot before = library.GetLr2SongDbSyncStatusSnapshot();
            using LibraryFileMutationLease incumbent = owner.TryBeginMutation(
                "test_queue_incumbent",
                showMessage: false);
            Assert.IsNotNull(incumbent);

            Lr2SongDbSyncStatusSnapshot result = await library.QueueLr2SongDbSyncAsync(
                "test_queue_while_file_mutation",
                force: true,
                (_, _) =>
                {
                    prepareCount++;
                    return Lr2SongDbSyncPreparedDataSurface.Empty;
                });

            Assert.AreEqual(before.Status, result.Status);
            Assert.AreEqual(0, prepareCount);
            Assert.AreEqual(0, scheduleCount);
            Assert.AreEqual(0, library.Lr2SongDbSyncRequestedVersion);
            Assert.IsFalse(library.Lr2SongDbSyncRunning);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }



    /// <summary>
    /// 受理済み変更権でplaylist定義を管理主体から実DBへ保存し、窓口からwriterへの接続漏れを検出する。
    /// </summary>
    /// <remarks>実SQLiteと管理主体を接続して保存結果または保存前の拒否を確認する。局所portだけではDB反映との順序を判定できない。</remarks>
    [TestMethod]
    public void PlaylistLr2FolderSynchronization_CommitsFolderRowsThroughOwner()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string outputDirectory = Path.Combine(scope.DirectoryPath, "Output");
            Directory.CreateDirectory(outputDirectory);
            string filePath = Path.Combine(outputDirectory, "0000.lr2folder");
            DateTime timestamp = new(2026, 7, 18, 4, 5, 6, DateTimeKind.Utc);
            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                setup.CreateTable<LR2SongDB.folder>();
            }
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: testSettings);

            Lr2FolderFileDbSyncResult result = InvokeWithMutationCapability(
                library,
                mutationCapability => library.Lr2PlaylistFolderSynchronization.SyncPlaylistLr2FolderFileRows(
                    "playlist_lr2folder_sync",
                    new Lr2FolderFileDbSyncRequest
                    {
                        Items =
                    [
                        new Lr2FolderFileSyncItem
                        {
                            FilePath = filePath,
                            LastWriteTimeUtc = timestamp,
                            Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Owner sync"])
                        }
                    ],
                        ScopeDirectories = [outputDirectory],
                        DirectoryRowScopeDirectories = [outputDirectory],
                        DirectoryRowGenerationScopeDirectories = [outputDirectory],
                        DirectoryMetadataResolver = _ => new Lr2FolderDirectoryMetadata(timestamp),
                        GeneratedAtUtc = timestamp,
                        AllowPrune = true
                    },
                    mutationCapability), playlistOperation: true);

            Assert.IsTrue(result.HasChanges);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            Assert.IsNotNull(verify.Find<LR2SongDB.folder>(filePath));
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }



    /// <summary>
    /// 実playlist生成の準備はFSだけを作成してDB行を反映せず、権解放後の別スレッド通知と購読例外隔離を確認する。
    /// </summary>
    /// <remarks>実SQLiteと管理主体を接続して保存結果または保存前の拒否を確認する。局所portだけではDB反映との順序を判定できない。</remarks>
    [TestMethod]
    public async Task PlaylistLr2FolderSynchronization_PreparationLeavesFolderRowsUnchanged()
    {
        Lr2SongDbSyncPreparedDataSurface? preparedInput = null;
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string lr2RootPath = Path.Combine(scope.DirectoryPath, "LR2");
            string bmsRoot = Path.Combine(scope.DirectoryPath, "BMS");
            string outputBase = Path.Combine(bmsRoot, "#BeMusicSeeker");
            Directory.CreateDirectory(bmsRoot);
            testSettings.LR2RootPath = lr2RootPath;
            testSettings.LR2CustomFolderOutputBaseDir = outputBase;
            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                setup.CreateTable<LR2SongDB.folder>();
            }
            var table = new BMSTable
            {
                playlist_id = 7612,
                name = "PreparationOwned",
                symbol = "PO",
                Output_dir = "PreparationOwned",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    PlaylistTestFixture.CreateEntry(
                            "cccccccccccccccccccccccccccccccc",
                            "Folder C")
                ],
                Folder_order = ["Folder C"]
            };
            Lr2ConfigOwner lr2ConfigProvider = new(TestLr2ConfigFactory.CreateLr2Config(lr2RootPath, bmsRoot));
            var scheduler = new QueuedUiScheduler();
            var library = new TestBmsLibrary(scope.DatabaseScope,
                    scope.SongDbPath,
                    lr2ConfigProvider,
                    null,
                    null,
                    null,
                    scheduler, settings: testSettings);
            var playlist = new TestBmsPlaylist(new BmsPlaylistLibraryBindings(library), scope.SongDbPath,
                () => CustomFolderOutputSettingsSnapshot.CreateCurrent(testSettings), lr2ConfigProvider, settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            var runtime = new BmsLr2SongDbSyncWorkflowRuntime(
                () => library,
                () => playlist,
                () => true);
            BMSLibrary.Lr2SynchronizationOwner owner = GetLr2SynchronizationOwner(library);
            var preparationResultObserved = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            int preparationThreadId = 0;
            int notificationThreadId = 0;
            int subscriberFailureCount = 0;
            System.ComponentModel.PropertyChangedEventHandler propertyChangedHandler = (_, args) =>
            {
                if (string.Equals(
                        args.PropertyName,
                        nameof(BMSLibrary.Lr2SongDbSyncStatusVersion),
                        StringComparison.Ordinal)
                    && string.Equals(
                        library.GetLr2SongDbSyncStatusSnapshot().Stage,
                        "playlist_result_preparation",
                        StringComparison.Ordinal)
                    && Interlocked.CompareExchange(ref subscriberFailureCount, 1, 0) == 0)
                {
                    notificationThreadId = Thread.CurrentThread.ManagedThreadId;
                    preparationResultObserved.TrySetResult(true);
                    throw new InvalidOperationException("forced LR2 progress subscriber failure");
                }
            };
            library.PropertyChanged += propertyChangedHandler;
            Task? preparation = null;
            try
            {
                bool preparationResult = false;
                preparation = Task.Factory.StartNew(
                    () =>
                    {
                        preparationThreadId = Thread.CurrentThread.ManagedThreadId;
                        using LibraryFileMutationLease lease = owner.TryBeginMutation("test_preparation_owned_playlist", showMessage: false);
                        Assert.IsNotNull(lease);
                        Assert.IsTrue(playlist.TryEnterPlaylistMutation(out IDisposable playlistLease));
                        using IDisposable acceptedPlaylist = playlistLease;
                        using LibraryFileMutationCapability playlistAuthority = playlist.CreatePlaylistMutationCapability(playlistLease);
                        Lr2SongDbSyncPreparedDataSurface prepared = runtime.PrepareGeneratedDataUnderLease(library, "test_preparation_owned_playlist", lease, false,
                            playlistCapability: playlistAuthority);
                        preparedInput = prepared;
                        preparationResult = true;
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);

                await preparation;
                Assert.AreEqual(0, subscriberFailureCount);
                // 完了継続は準備スレッド上でも動けるため、UI排出は別の実行境界で行う。
                await Task.Run(scheduler.Drain);
                await TestUiDispatcherHost.AwaitNotificationAsync(
                    preparationResultObserved.Task, Task.CompletedTask, "lr2-playlist-preparation.publication");

                Assert.IsTrue(preparationResult);
                Assert.AreEqual(1, subscriberFailureCount);
                Assert.AreNotEqual(preparationThreadId, notificationThreadId);
                Lr2SongDbSyncPreparedDataSurface preparedSurface =
                    preparedInput!;

                string outputDirectory = Path.Combine(outputBase, table.Output_dir);
                string filePath = Path.Combine(outputDirectory, "0000.lr2folder");
                Assert.IsNotNull(preparedSurface);
                Assert.IsTrue(preparedSurface.Lr2FolderFilePaths.Any(
                    path => string.Equals(path, filePath, StringComparison.OrdinalIgnoreCase)));
                Assert.IsTrue(File.Exists(filePath));
                using LibraryFileMutationLease fresh = owner.TryBeginMutation(
                    "test_after_preparation_release",
                    showMessage: false);
                Assert.IsNotNull(fresh);
                using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
                Assert.IsNull(verify.Find<LR2SongDB.folder>(filePath));
            }
            finally
            {
                try { if (preparation != null) { await preparation; } }
                finally { library.PropertyChanged -= propertyChangedHandler; }
            }
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// playlistの実生成後に前提取得が失敗しても生成FSと既存DB行を保持し、新規DB反映を始めず元原因を通知する。
    /// </summary>
    /// <remarks>実SQLiteと管理主体を接続して保存結果または保存前の拒否を確認する。局所portだけではDB反映との順序を判定できない。</remarks>
    [TestMethod]
    public async Task QueueLr2SongDbSync_LatePreflightFailureLeavesFolderRowsUnchangedAfterPlaylistMaterialization()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string lr2RootPath = Path.Combine(scope.DirectoryPath, "LR2");
            string bmsRoot = Path.Combine(scope.DirectoryPath, "BMS");
            string outputBase = Path.Combine(bmsRoot, "#BeMusicSeeker");
            Directory.CreateDirectory(bmsRoot);
            testSettings.LR2RootPath = lr2RootPath;
            testSettings.LR2CustomFolderOutputBaseDir = outputBase;
            string durableFolderPath = Path.Combine(scope.DirectoryPath, "Durable") + Path.DirectorySeparatorChar;
            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                setup.RunInTransaction(() =>
                {
                    setup.CreateTable<LR2SongDB.folder>();
                    setup.InsertOrReplace(new LR2SongDB.folder
                    {
                        path = durableFolderPath,
                        title = "Durable folder before full preflight",
                        parent = "durable-parent",
                        type = 1,
                        date = 123456,
                        adddate = 123457
                    }, typeof(LR2SongDB.folder));
                });
            }
            var table = new BMSTable
            {
                playlist_id = 7613,
                name = "LatePreflightFailure",
                symbol = "LPF",
                Output_dir = "LatePreflightFailure",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    PlaylistTestFixture.CreateEntry(
                        "dddddddddddddddddddddddddddddddd",
                        "Folder D")
                ],
                Folder_order = ["Folder D"]
            };
            Lr2ConfigOwner lr2ConfigProvider = new(TestLr2ConfigFactory.CreateLr2Config(lr2RootPath, bmsRoot));
            var options = BmsLibraryOptionsSnapshot.CreateCurrent(testSettings);
            bool failPreflight = false;
            var library = new TestBmsLibrary(scope.DatabaseScope,
                scope.SongDbPath,
                lr2ConfigProvider,
                null,
                null,
                () => options,
                applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge,
                uiScheduler: new InlineUiScheduler(), rootFileEnumerator: new CapturedRootFileEnumerator(new Dictionary<string, RootFileEnumerationEntry>())
                {
                    BeforeEnumeration = () =>
                    {
                        if (Volatile.Read(ref failPreflight)) { throw new InvalidOperationException("forced full preflight failure"); }
                    }
                }, settings: testSettings);
            var playlist = new TestBmsPlaylist(new BmsPlaylistLibraryBindings(library), scope.SongDbPath,
                () => CustomFolderOutputSettingsSnapshot.CreateCurrent(testSettings), lr2ConfigProvider, settings: testSettings)
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            var runtime = new BmsLr2SongDbSyncWorkflowRuntime(
                () => library,
                () => playlist,
                () => true);
            library.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(BMSLibrary.Lr2SongDbSyncStage)
                    && library.Lr2SongDbSyncStage == "chart_info_hydration")
                { Volatile.Write(ref failPreflight, true); }
            };

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.QueueAsync(
                "test_late_full_preflight_failure",
                force: true,
                prepareGeneratedData: true));

            Assert.IsTrue(Volatile.Read(ref failPreflight));
            Lr2SongDbSyncStatusSnapshot status = library.GetLr2SongDbSyncStatusSnapshot();
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Failed, status.Status);
            StringAssert.Contains(status.LastError, "forced full preflight failure");
            string generatedFilePath = Path.Combine(outputBase, table.Output_dir, "0000.lr2folder");
            Assert.IsTrue(File.Exists(generatedFilePath), "実生成後の後段失敗でも生成済みFSを保持します。");
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            LR2SongDB.folder durableFolder = verify.Find<LR2SongDB.folder>(durableFolderPath);
            Assert.IsNotNull(durableFolder);
            Assert.AreEqual("Durable folder before full preflight", durableFolder.title);
            Assert.AreEqual("durable-parent", durableFolder.parent);
            Assert.AreEqual(123456, durableFolder.date);
            Assert.AreEqual(123457, durableFolder.adddate);
            Assert.IsNull(verify.Find<LR2SongDB.folder>(generatedFilePath));
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// playlist反映の情報取得失敗は元例外を返して保存・公開原因へ反映し、利用者向け段階表示との接続を確認する。
    /// </summary>
    /// <remarks>実SQLiteと管理主体を接続して保存結果または保存前の拒否を確認する。局所portだけではDB反映との順序を判定できない。</remarks>
    [TestMethod]
    public void PlaylistLr2FolderSynchronization_PublishesFailureStatusAndRethrowsOriginalException()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string outputDirectory = Path.Combine(scope.DirectoryPath, "Output");
            Directory.CreateDirectory(outputDirectory);
            string nestedDirectory = Path.Combine(outputDirectory, "Nested");
            Directory.CreateDirectory(nestedDirectory);
            string filePath = Path.Combine(nestedDirectory, "0000.lr2folder");
            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                setup.CreateTable<LR2SongDB.folder>();
            }
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: testSettings);
            Exception originalException = new InvalidOperationException("forced playlist folder failure");

            InvalidOperationException exception = Assert.ThrowsExactly<InvalidOperationException>(
                () => InvokeWithMutationCapability(
                    library,
                    mutationCapability => library.Lr2PlaylistFolderSynchronization.SyncPlaylistLr2FolderFileRows(
                        "playlist_lr2folder_sync",
                        new Lr2FolderFileDbSyncRequest
                        {
                            Items =
                        [
                            new Lr2FolderFileSyncItem
                            {
                                FilePath = filePath,
                                LastWriteTimeUtc = DateTime.UtcNow,
                                Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Failure"])
                            }
                        ],
                            ScopeDirectories = [outputDirectory],
                            DirectoryRowScopeDirectories = [outputDirectory],
                            DirectoryRowGenerationScopeDirectories = [outputDirectory],
                            DirectoryMetadataResolver = _ => throw originalException,
                            AllowPrune = true
                        },
                        mutationCapability), playlistOperation: true));

            Assert.AreSame(originalException, exception);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            LR2SongDBExtended.lr2_song_db_sync_status row = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.IsNotNull(row);
            Assert.AreEqual("lr2_playlist_lr2folder_sync_failed", row.stage);
            StringAssert.Contains(row.last_error, "forced playlist folder failure");
            Lr2SongDbSyncStatusSnapshot snapshot = library.GetLr2SongDbSyncStatusSnapshot();
            Lr2SongDbSyncRuntimeStatus presentation = Lr2SongDbSyncStatusMapper.Create(snapshot, DateTime.MinValue);
            Assert.AreEqual(Resources.Lr2_song_db_sync_stage_folder_saving, presentation.ProgressText);
            StringAssert.Contains(presentation.Detail, Resources.Lr2_song_db_sync_stage_folder_saving + ": " + originalException.Message);
            Assert.AreEqual(row.last_error, snapshot.LastError);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 二行目の保存失敗で一行目も戻して失敗段階を保存し、playlist反映の部分確定を防ぐ。
    /// </summary>
    /// <remarks>実SQLiteと管理主体を接続して保存結果または保存前の拒否を確認する。局所portだけではDB反映との順序を判定できない。</remarks>
    [TestMethod]
    public void PlaylistLr2FolderSynchronization_RollsBackWriterFailureAfterPartialPlan()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string outputDirectory = Path.Combine(scope.DirectoryPath, "Output");
            Directory.CreateDirectory(outputDirectory);
            string firstFilePath = Path.Combine(outputDirectory, "0001.lr2folder");
            string failingFilePath = Path.Combine(outputDirectory, "0002.lr2folder");
            DateTime timestamp = new(2026, 7, 18, 4, 5, 6, DateTimeKind.Utc);
            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                setup.RunInTransaction(() =>
                {
                    setup.CreateTable<LR2SongDB.folder>();
                    setup.Execute(
                        "CREATE TRIGGER fail_lr2_folder_insert AFTER INSERT ON folder WHEN NEW.path = '"
                        + failingFilePath.Replace("'", "''")
                        + "' BEGIN SELECT RAISE(ABORT, 'forced folder insert failure'); END;");
                });
            }
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: testSettings);

            SQLite.SQLiteException exception = Assert.ThrowsExactly<SQLite.SQLiteException>(
                () => InvokeWithMutationCapability(
                    library,
                    mutationCapability => library.Lr2PlaylistFolderSynchronization.SyncPlaylistLr2FolderFileRows(
                        "playlist_lr2folder_sync",
                        new Lr2FolderFileDbSyncRequest
                        {
                            Items =
                        [
                            new Lr2FolderFileSyncItem
                            {
                                FilePath = firstFilePath,
                                LastWriteTimeUtc = timestamp,
                                Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE First"])
                            },
                            new Lr2FolderFileSyncItem
                            {
                                FilePath = failingFilePath,
                                LastWriteTimeUtc = timestamp,
                                Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Failing"])
                            }
                        ],
                            ScopeDirectories = [outputDirectory],
                            DirectoryRowScopeDirectories = [outputDirectory],
                            DirectoryRowGenerationScopeDirectories = [outputDirectory],
                            DirectoryMetadataResolver = _ => new Lr2FolderDirectoryMetadata(timestamp),
                            GeneratedAtUtc = timestamp,
                            AllowPrune = true
                        },
                        mutationCapability), playlistOperation: true));

            Assert.IsFalse(string.IsNullOrWhiteSpace(exception.Message));
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().Count());
            LR2SongDBExtended.lr2_song_db_sync_status row = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.IsNotNull(row);
            Assert.AreEqual("lr2_playlist_lr2folder_sync_failed", row.stage);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 実同期権を保持中の再要求はRunningを返して二重投稿せず、受理済み作業の重複開始を防ぐ。
    /// </summary>
    [TestMethod]
    public async Task QueueLr2SongDbSync_DoesNotQueueAgainWhileRunning()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, null, null, null, new BmsLibraryInitializationTestSupport.RecordingDialogService(), settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

            };
            bool queued = false;
            library.StartupBackgroundTaskScheduler = delegate
            {
                queued = true;
                return true;
            };
            await using HeldLr2Operation held = await HeldLr2Operation.StartAsync(library);

            Lr2SongDbSyncStatusSnapshot snapshot = await library.QueueLr2SongDbSyncAsync("test_running");

            Assert.AreEqual(Lr2SongDbSyncStatusKind.Running, snapshot.Status);
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Running, library.GetLr2SongDbSyncStatusSnapshot().Status);
            Assert.IsFalse(queued);
            Assert.AreEqual(1, library.Lr2SongDbSyncRequestedVersion);
            Assert.IsTrue(library.Lr2SongDbSyncRunning);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// ライブラリ・playlistの競合/受付閉鎖は、直接要求と通常の手動同期ownerの両方で準備・保存前に拒否します。実受付結果のfalse通知・部分権限解放・自動再実行0を確認します。
    /// </summary>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task QueueLr2SongDbSync_CompetingAdmissionSkipsBeforePreparationAndReleasesPartialLease(bool playlistBusy, bool closed)
    {
        using var scope = TestDatabaseScope.Create();
        var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, null, null, null,
            () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = true }, TestBmsFactory.MissingEverythingBridge, settings: testSettings);
        ChartFileOperationSynchronizer gate = playlistBusy ? library.Lr2Synchronization.PlaylistOperationAdmission : library.OperationAdmission;
        IDisposable? incumbent = null;
        if (closed) { gate.CloseAdmission(); }
        else { Assert.IsTrue(gate.TryEnter(out incumbent)); }
        int prepared = 0;
        int requestedBefore = library.Lr2SongDbSyncRequestedVersion;
        int completedBefore = library.Lr2SongDbSyncCompletedVersion;
        byte[] before = File.ReadAllBytes(scope.SongDbPath);
        bool? admitted = null;
        try
        {
            await library.QueueLr2SongDbSyncAsync("competing-whole", force: true,
                prepareGeneratedData: (_, _) => { prepared++; return Lr2SongDbSyncPreparedDataSurface.Empty; },
                admissionResult: value => admitted = value);
            Assert.AreEqual(false, admitted);
            admitted = null;
            var runtime = new BmsLr2SongDbSyncWorkflowRuntime(() => library,
                () => { prepared++; throw new AssertFailedException("拒否後は生成入力を取得しません。"); }, () => true);
            var owner = new Lr2SongDbSyncWorkflowOwner(runtime);
            Assert.IsFalse(await owner.RequestManualResyncAsync(value => admitted = value));
            Assert.AreEqual(false, admitted);
            Assert.AreEqual(0, prepared);
            Assert.AreEqual(requestedBefore, library.Lr2SongDbSyncRequestedVersion);
            Assert.AreEqual(completedBefore, library.Lr2SongDbSyncCompletedVersion);
            Assert.IsFalse(library.Lr2SongDbSyncRunning);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(scope.SongDbPath));
            if (playlistBusy)
            {
                Assert.IsTrue(library.OperationAdmission.TryEnter(out IDisposable released));
                released.Dispose();
            }
            incumbent?.Dispose();
            Assert.AreEqual(0, prepared, "解除は予約済み再実行を起こしません。");
            Assert.AreEqual(!closed, gate.TryEnter(out IDisposable next));
            next?.Dispose();
        }
        finally { incumbent?.Dispose(); library.RequestShutdown("whole-admission-cleanup"); }
    }

    /// <summary>通常の手動同期ownerへ実受付を通知する処理が失敗しても、元例外を伝播し、準備/保存前にL/Pの両方を解放します。</summary>
    /// <remarks>通常起動の状態評価で状態表が準備された後の手動同期を接続します。実runtimeと実DBの同じ受付点を通し、通知失敗を受理成功として隠す実装とPの解放漏れを検出します。</remarks>
    [TestMethod]
    public async Task QueueLr2SongDbSync_AdmissionNotificationFailureReleasesBothLeasesBeforePreparation()
    {
        using var scope = TestDatabaseScope.Create();
        var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, null, null, null,
            () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = true }, TestBmsFactory.MissingEverythingBridge, settings: testSettings);
        int prepared = 0;
        int requestedBefore = library.Lr2SongDbSyncRequestedVersion;
        int completedBefore = library.Lr2SongDbSyncCompletedVersion;
        using (LR2SongDBExtended initialized = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
        { BmsLibraryDbGateway.EnsureLr2SongDbSyncStatusSchema(initialized); }
        byte[] before = File.ReadAllBytes(scope.SongDbPath);
        var failure = new InvalidOperationException("acceptance presentation failed");
        bool? admitted = null;
        var runtime = new BmsLr2SongDbSyncWorkflowRuntime(() => library, () => { prepared++; throw new AssertFailedException("通知失敗後は準備へ進みません。"); }, () => true);
        var owner = new Lr2SongDbSyncWorkflowOwner(runtime);
        try
        {
            InvalidOperationException actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                owner.RequestManualResyncAsync(value => { admitted = value; throw failure; }));
            Assert.AreSame(failure, actual);
            Assert.AreEqual(true, admitted);
            Assert.AreEqual(0, prepared);
            Assert.AreEqual(requestedBefore, library.Lr2SongDbSyncRequestedVersion);
            Assert.AreEqual(completedBefore, library.Lr2SongDbSyncCompletedVersion);
            Assert.IsFalse(library.Lr2SongDbSyncRunning);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(scope.SongDbPath));
            Assert.IsTrue(library.OperationAdmission.TryEnter(out IDisposable nextLibrary));
            nextLibrary.Dispose();
            Assert.IsTrue(library.Lr2Synchronization.PlaylistOperationAdmission.TryEnter(out IDisposable nextPlaylist));
            nextPlaylist.Dispose();
        }
        finally { library.RequestShutdown("admission-notification-failure-cleanup"); }
    }

    /// <summary>
    /// 実差分反映後の通知でカタログ・解析・資源索引が更新済みとなり、旧スナップショットを保持して変更権の再取得と購読例外隔離を確認する。
    /// </summary>
    /// <remarks>実SQLiteと管理主体を接続して保存結果または保存前の拒否を確認する。局所portだけではDB反映との順序を判定できない。</remarks>
    [TestMethod]
    public void ReloadFileDiff_PublishesCatalogAfterLeaseReleaseAndIsolatesTerminalSubscriber()
    {
        using var scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
        Directory.CreateDirectory(rootDirectory);
        string catalogChartRoot = Path.Combine(
            ChartInfoComponentTestSupport.FindRepoRoot(),
            "BeMusicSeeker.Tests",
            "TestData",
            "chart_info_real",
            "charts",
            "00");
        string catalogChartPath = Path.Combine(
            catalogChartRoot,
            "0011a110d2d54f455a1dbb9a03598aaa1cc092238800ca9b1c1260a8bb78db36.bms");
        testSettings.OperationModeLR2DB = false;
        var options = new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = false,
        };
        IChartFileScanner chartFileScanner = CapturedChartFileScanner.FromFixture(
            [catalogChartPath],
            new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [catalogChartRoot] = [],
                [rootDirectory] = []
            },
            [catalogChartRoot, rootDirectory]);
        var library = new TestBmsLibrary(scope.DatabaseScope,
            scope.SongDbPath,
            getLR2Config: null,
            _lr2ScoreDB: null,
            startupRequiredFileScanReason: null,
            optionsSnapshotProvider: () => options,
            applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge,
            chartFileScanner: chartFileScanner, settings: TestBmsFactory.CreateRootSettings([catalogChartRoot, rootDirectory], testSettings))
        {

            BmsCharts = []
        };
        OwnedChartHashIndexVersionedSnapshot oldHashIndex = library.GetOwnedChartHashIndexSnapshot();
        InstalledChartLookupIndexSnapshot oldInstalledLookup =
            library.CreateInstalledChartLookupSnapshotForDiagnostics();
        PlaylistLibraryResolveIndexSnapshot oldPlaylistResolve = library.GetPlaylistLibraryResolveIndexSnapshot(
            CancellationToken.None,
            out bool oldPlaylistCacheHit,
            out int oldPlaylistStaleRetries);
        LibraryResourceIndexOwner resourceIndexOwner = LibraryResourceIndexTestSupport.GetOwner(library);
        LibraryResourceIndexSnapshot oldResourceIndex = resourceIndexOwner.CaptureSnapshot();
        int oldChartInfoIndexVersion = library.ChartInfoIndexVersion;
        BMSLibrary.Lr2SynchronizationOwner synchronizationOwner =
            GetLr2SynchronizationOwner(library);
        bool catalogNotificationObserved = false;
        bool catalogNotificationReadbackObserved = false;
        bool catalogNotificationResourceObserved = false;
        bool catalogNotificationResourceHealthObserved = false;
        bool chartInfoNotificationObserved = false;
        bool chartInfoNotificationReadbackObserved = false;
        bool initializedWriterWasHeld = false;
        bool mutationLeaseWasAvailable = false;
        int subscriberFailureCount = 0;
        OwnedChartHashIndexVersionedSnapshot notificationHashIndex = null!;
        InstalledChartLookupIndexSnapshot notificationInstalledLookup = null!;
        PlaylistLibraryResolveIndexSnapshot notificationPlaylistResolve = null!;
        LibraryResourceIndexSnapshot notificationResourceIndex = default;
        ResourceHealthIndexSnapshot notificationResourceHealth = null!;
        System.ComponentModel.PropertyChangedEventHandler catalogSubscriber = (_, args) =>
        {
            if (!string.Equals(
                    args.PropertyName,
                    nameof(BMSLibrary.CatalogRevision),
                    StringComparison.Ordinal))
            {
                return;
            }
            catalogNotificationObserved = true;
            initializedWriterWasHeld |= library.IsWriteLockHeldInitializeAll;
            ChartFile? notificationFile = library.BmsCharts.FirstOrDefault(file =>
                string.Equals(file?.Path, catalogChartPath, StringComparison.OrdinalIgnoreCase));
            if (notificationFile != null)
            {
                notificationHashIndex = library.GetOwnedChartHashIndexSnapshot();
                notificationInstalledLookup = library.CreateInstalledChartLookupSnapshotForDiagnostics();
                notificationPlaylistResolve = library.GetPlaylistLibraryResolveIndexSnapshot(
                    CancellationToken.None,
                    out bool _notificationPlaylistCacheHit,
                    out int notificationPlaylistStaleRetries);
                catalogNotificationReadbackObserved = notificationHashIndex.ContainsMd5(notificationFile.Md5)
                    && notificationInstalledLookup.ContainsPrimaryHash(notificationFile.Md5)
                    && notificationPlaylistResolve.ResolveChartForPlaylistHash(
                        notificationFile.Md5,
                        notificationFile.Sha256) is LibraryChartRef notificationChart
                    && string.Equals(notificationChart.Path, catalogChartPath, StringComparison.OrdinalIgnoreCase)
                    && notificationPlaylistStaleRetries == 0;
                notificationResourceIndex = resourceIndexOwner.CaptureSnapshot();
                catalogNotificationResourceObserved = notificationResourceIndex.Generation > oldResourceIndex.Generation
                    && notificationResourceIndex.DirectoryLookupCache.GetEntryOrNull(catalogChartRoot) != null;
                notificationResourceHealth = library.GetResourceHealthIndexSnapshotForView(
                    "test_reload_file_diff_notification");
                catalogNotificationResourceHealthObserved = notificationResourceHealth.TargetCount > 0;
            }
            using LibraryFileMutationLease reentryLease = synchronizationOwner.TryBeginMutation(
                "test_reload_file_diff_post_lease_reentry",
                showMessage: false);
            mutationLeaseWasAvailable |= reentryLease != null;
        };
        System.ComponentModel.PropertyChangedEventHandler chartInfoSubscriber = (_, args) =>
        {
            if (!string.Equals(
                    args.PropertyName,
                    nameof(BMSLibrary.ChartInfoIndexVersion),
                    StringComparison.Ordinal))
            {
                return;
            }
            chartInfoNotificationObserved = true;
            ChartFile? notificationFile = library.BmsCharts.FirstOrDefault(file =>
                string.Equals(file?.Path, catalogChartPath, StringComparison.OrdinalIgnoreCase));
            chartInfoNotificationReadbackObserved = notificationFile != null
                && library.ChartInfoIndexVersion > oldChartInfoIndexVersion
                && library.ResolveChartInfo(notificationFile.Sha256, notificationFile.Md5) != null;
        };
        System.ComponentModel.PropertyChangedEventHandler throwingSubscriber = (_, args) =>
        {
            if (string.Equals(
                    args.PropertyName,
                    nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion),
                    StringComparison.Ordinal)
                && Interlocked.CompareExchange(ref subscriberFailureCount, 1, 0) == 0)
            {
                throw new InvalidOperationException("forced reload catalog subscriber failure");
            }
        };
        library.PropertyChanged += catalogSubscriber;
        library.PropertyChanged += chartInfoSubscriber;
        library.PropertyChanged += throwingSubscriber;
        try
        {
            library.ReloadFileDiff();
        }
        finally
        {
            library.PropertyChanged -= throwingSubscriber;
            library.PropertyChanged -= chartInfoSubscriber;
            library.PropertyChanged -= catalogSubscriber;
        }
        Assert.IsTrue(catalogNotificationObserved);
        Assert.IsTrue(catalogNotificationReadbackObserved);
        Assert.IsTrue(catalogNotificationResourceObserved);
        Assert.IsTrue(catalogNotificationResourceHealthObserved);
        Assert.IsTrue(chartInfoNotificationObserved);
        Assert.IsTrue(chartInfoNotificationReadbackObserved);
        Assert.IsFalse(initializedWriterWasHeld);
        Assert.IsTrue(mutationLeaseWasAvailable);
        Assert.AreEqual(1, subscriberFailureCount);

        ChartFile? catalogFile = library.BmsCharts.FirstOrDefault(file =>
            string.Equals(file?.Path, catalogChartPath, StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(catalogFile);
        ChartFile loadedCatalogFile = catalogFile!;
        Assert.IsFalse(string.IsNullOrWhiteSpace(loadedCatalogFile.RawTitle));
        OwnedChartHashIndexVersionedSnapshot afterHashIndex = library.GetOwnedChartHashIndexSnapshot();
        OwnedChartHashIndexVersionedSnapshot afterHashIndexReadback = library.GetOwnedChartHashIndexSnapshot();
        InstalledChartLookupIndexSnapshot afterInstalledLookup =
            library.CreateInstalledChartLookupSnapshotForDiagnostics();
        InstalledChartLookupIndexSnapshot afterInstalledLookupReadback =
            library.CreateInstalledChartLookupSnapshotForDiagnostics();
        PlaylistLibraryResolveIndexSnapshot afterPlaylistResolve = library.GetPlaylistLibraryResolveIndexSnapshot(
            CancellationToken.None,
            out bool afterPlaylistCacheHit,
            out int afterPlaylistStaleRetries);
        PlaylistLibraryResolveIndexSnapshot afterPlaylistResolveReadback = library.GetPlaylistLibraryResolveIndexSnapshot(
            CancellationToken.None,
            out bool afterPlaylistReadbackCacheHit,
            out int afterPlaylistReadbackStaleRetries);
        LibraryResourceIndexSnapshot afterResourceIndex = resourceIndexOwner.CaptureSnapshot();
        LibraryResourceIndexSnapshot afterResourceIndexReadback = resourceIndexOwner.CaptureSnapshot();
        ResourceHealthIndexSnapshot afterResourceHealth =
            library.GetResourceHealthIndexSnapshotForView("test_reload_file_diff_after");
        ResourceHealthIndexSnapshot afterResourceHealthReadback =
            library.GetResourceHealthIndexSnapshotForView("test_reload_file_diff_after_readback");
        Assert.IsTrue(afterHashIndex.ContainsMd5(loadedCatalogFile.Md5));
        Assert.IsTrue(afterHashIndex.ContainsSha256(loadedCatalogFile.Sha256));
        Assert.AreEqual(afterHashIndex.Version, afterHashIndexReadback.Version);
        Assert.IsTrue(afterInstalledLookup.ContainsPrimaryHash(loadedCatalogFile.Md5));
        Assert.IsTrue(afterInstalledLookup.GetDistinctDirectoriesByPrimaryHash(loadedCatalogFile.Md5)
            .Contains(catalogChartRoot, StringComparer.OrdinalIgnoreCase));
        Assert.AreEqual(afterInstalledLookup.HashCount, afterInstalledLookupReadback.HashCount);
        Assert.IsTrue(afterPlaylistCacheHit);
        Assert.IsTrue(afterPlaylistReadbackCacheHit);
        Assert.AreEqual(0, afterPlaylistStaleRetries);
        Assert.AreEqual(0, afterPlaylistReadbackStaleRetries);
        Assert.AreSame(afterPlaylistResolve, afterPlaylistResolveReadback);
        Assert.AreEqual(catalogChartPath, afterPlaylistResolve.ResolveChartForPlaylistHash(
            loadedCatalogFile.Md5,
            loadedCatalogFile.Sha256)!.Path);
        Assert.IsTrue(afterResourceIndex.Generation > oldResourceIndex.Generation);
        Assert.IsNotNull(afterResourceIndex.DirectoryLookupCache.GetEntryOrNull(catalogChartRoot));
        Assert.AreEqual(afterResourceIndex.Generation, afterResourceIndexReadback.Generation);
        Assert.AreSame(
            afterResourceIndex.DirectoryLookupCache,
            afterResourceIndexReadback.DirectoryLookupCache);
        Assert.IsTrue(afterResourceHealth.TargetCount > 0);
        Assert.AreSame(afterResourceHealth, afterResourceHealthReadback);
        Assert.IsFalse(oldHashIndex.ContainsMd5(loadedCatalogFile.Md5));
        Assert.IsFalse(oldHashIndex.ContainsSha256(loadedCatalogFile.Sha256));
        Assert.IsFalse(oldInstalledLookup.ContainsPrimaryHash(loadedCatalogFile.Md5));
        Assert.IsNull(oldPlaylistResolve.ResolveChartForPlaylistHash(
            loadedCatalogFile.Md5,
            loadedCatalogFile.Sha256));
        Assert.IsFalse(oldPlaylistCacheHit);
        Assert.AreEqual(0, oldPlaylistStaleRetries);
        Assert.IsNull(oldResourceIndex.DirectoryLookupCache.GetEntryOrNull(catalogChartRoot));
        Assert.IsTrue(notificationHashIndex.ContainsMd5(loadedCatalogFile.Md5));
        Assert.IsTrue(notificationInstalledLookup.ContainsPrimaryHash(loadedCatalogFile.Md5));
        Assert.IsTrue(notificationPlaylistResolve.ResolveChartForPlaylistHash(
            loadedCatalogFile.Md5,
            loadedCatalogFile.Sha256) is LibraryChartRef);
        Assert.AreEqual(afterResourceIndex.Generation, notificationResourceIndex.Generation);
        Assert.AreEqual(afterResourceHealth.Version, notificationResourceHealth.Version);
        using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
        Assert.IsTrue(verify.Table<LR2SongDB.song>().Any(row =>
            string.Equals(row?.path, catalogChartPath, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// 同時刻で保持する外部定義のタイトル・追加時刻は変えず、欠落した親階層だけを通常行として補修する。
    /// </summary>
    /// <remarks>実SQLiteと管理主体を接続して保存結果または保存前の拒否を確認する。局所portだけではDB反映との順序を判定できない。</remarks>
    [TestMethod]
    public void ReloadFileDiff_RepairsMissingParentRowForPreservedExternalLr2Folder()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string categoryDirectory = Path.Combine(rootDirectory, "#minbp");
            string tableDirectory = Path.Combine(categoryDirectory, "InsaneTable");
            Directory.CreateDirectory(tableDirectory);
            string lr2FolderPath = Path.Combine(tableDirectory, "0000.lr2folder");
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);
            File.WriteAllText(lr2FolderPath, "#TITLE Should Not Be Reparsed", Encoding.GetEncoding("shift_jis"));
            File.SetLastWriteTimeUtc(lr2FolderPath, timestamp);
            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                setup.RunInTransaction(() =>
                {
                    setup.CreateTable<LR2SongDB.folder>();
                    setup.InsertOrReplace(new LR2SongDB.folder
                    {
                        path = Lr2FolderPath.ToFolderPath(categoryDirectory),
                        title = "#minbp",
                        type = 1,
                        parent = Lr2SongFolderParentNormalizer.ComputeDirectoryHash(rootDirectory),
                        date = Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp),
                        adddate = 12345
                    }, typeof(LR2SongDB.folder));
                    setup.InsertOrReplace(new LR2SongDB.folder
                    {
                        path = lr2FolderPath,
                        title = "Preserved External Folder",
                        type = 2,
                        parent = Lr2SongFolderParentNormalizer.ComputeDirectoryHash(tableDirectory),
                        date = Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp),
                        adddate = 23456
                    }, typeof(LR2SongDB.folder));
                });
            }
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, TestBmsFactory.AvailableEverythingBridge, settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

                BmsCharts = []
            };
            library.ReloadFileDiff();

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            var rows = verify.Table<LR2SongDB.folder>().ToList();
            LR2SongDB.folder rootRow = rows.Single(row => row.path == Lr2FolderPath.ToFolderPath(rootDirectory));
            Assert.AreEqual(1, rootRow.type);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, rootRow.parent);
            LR2SongDB.folder categoryRow = rows.Single(row => row.path == Lr2FolderPath.ToFolderPath(categoryDirectory));
            Assert.AreEqual(1, categoryRow.type);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(rootDirectory), categoryRow.parent);
            LR2SongDB.folder tableRow = rows.Single(row => row.path == Lr2FolderPath.ToFolderPath(tableDirectory));
            Assert.AreEqual(1, tableRow.type);
            Assert.AreEqual("InsaneTable", tableRow.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(categoryDirectory), tableRow.parent);
            LR2SongDB.folder lr2Folder = rows.Single(row => row.path == lr2FolderPath);
            Assert.AreEqual("Preserved External Folder", lr2Folder.title);
            Assert.AreEqual(23456, lr2Folder.adddate);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(tableDirectory), lr2Folder.parent);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 登録ルートが深い階層でもROOT親と子階層CRCを正しく保存し、物理ボリューム側まで親生成を広げる誤りを防ぐ。
    /// </summary>
    /// <remarks>実SQLiteと管理主体を接続して保存結果または保存前の拒否を確認する。局所portだけではDB反映との順序を判定できない。</remarks>
    [TestMethod]
    public void ReloadFileDiff_GeneratesSearchRootChildParentRowUnderSearchRoot()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string bmsContainerDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string searchRootDirectory = Path.Combine(bmsContainerDirectory, "#minbp");
            string tableDirectory = Path.Combine(searchRootDirectory, "InsaneTable");
            Directory.CreateDirectory(tableDirectory);
            string lr2FolderPath = Path.Combine(tableDirectory, "0000.lr2folder");
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);
            File.WriteAllText(lr2FolderPath, "#TITLE External Folder", Encoding.GetEncoding("shift_jis"));
            File.SetLastWriteTimeUtc(lr2FolderPath, timestamp);
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, TestBmsFactory.AvailableEverythingBridge, settings: TestBmsFactory.CreateRootSettings([searchRootDirectory], testSettings))
            {

                BmsCharts = []
            };
            library.ReloadFileDiff();

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            var rows = verify.Table<LR2SongDB.folder>().ToList();
            LR2SongDB.folder searchRootRow = rows.Single(row => row.path == Lr2FolderPath.ToFolderPath(searchRootDirectory));
            Assert.AreEqual(1, searchRootRow.type);
            Assert.AreEqual("#minbp", searchRootRow.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, searchRootRow.parent);
            LR2SongDB.folder tableRow = rows.Single(row => row.path == Lr2FolderPath.ToFolderPath(tableDirectory));
            Assert.AreEqual(1, tableRow.type);
            Assert.AreEqual("InsaneTable", tableRow.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(searchRootDirectory), tableRow.parent);
            LR2SongDB.folder lr2Folder = rows.Single(row => row.path == lr2FolderPath);
            Assert.AreEqual(2, lr2Folder.type);
            Assert.AreEqual("External Folder", lr2Folder.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(tableDirectory), lr2Folder.parent);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 差分走査では外部定義の古い行を消し、管理対象出力の既存行は別の修復主体へ残す。管理境界を越えた削除を検出する。
    /// </summary>
    /// <remarks>実管理主体とSQLite保存を接続し、入力面の選択だけでは分からない反映と終端を確認する。</remarks>
    [TestMethod]
    public void ReloadFileDiff_PrunesExternalRowsAndLeavesManagedOutputRowsToCustomFolderRepair()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            PlaylistPersistenceRepository.EnsureSchema(scope.DatabaseScope, scope.SongDbPath);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string tableDirectory = Path.Combine(rootDirectory, "ExternalTable");
            string outputBase = Path.Combine(rootDirectory, "#BeMusicSeekerOutput");
            string managedDirectory = Path.Combine(outputBase, "ManagedTable");
            Directory.CreateDirectory(tableDirectory);
            Directory.CreateDirectory(managedDirectory);
            testSettings.LR2CustomFolderOutputBaseDir = outputBase;
            string currentPath = Path.Combine(tableDirectory, "current.lr2folder");
            string stalePath = Path.Combine(tableDirectory, "stale.lr2folder");
            string managedCurrentPath = Path.Combine(managedDirectory, "0000.lr2folder");
            string managedStalePath = Path.Combine(managedDirectory, "stale_external.lr2folder");
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);
            File.WriteAllText(currentPath, "#TITLE Current", Encoding.GetEncoding("shift_jis"));
            File.WriteAllText(managedCurrentPath, "#TITLE Managed Physical", Encoding.GetEncoding("shift_jis"));
            File.SetLastWriteTimeUtc(currentPath, timestamp);
            File.SetLastWriteTimeUtc(managedCurrentPath, timestamp);
            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                setup.RunInTransaction(() =>
                {
                    setup.CreateTable<LR2SongDB.folder>();
                    setup.InsertOrReplace(new BMSTable
                    {
                        playlist_id = 9101,
                        name = "ManagedTable",
                        symbol = "M",
                        Output_dir = "ManagedTable"
                    }, typeof(LR2SongDBExtended.playlist));
                    setup.InsertOrReplace(new LR2SongDB.folder
                    {
                        path = stalePath,
                        title = "Stale",
                        type = 2,
                        date = 1
                    }, typeof(LR2SongDB.folder));
                    setup.InsertOrReplace(new LR2SongDB.folder
                    {
                        path = managedCurrentPath,
                        title = "Managed Existing",
                        type = 2,
                        date = 1
                    }, typeof(LR2SongDB.folder));
                    setup.InsertOrReplace(new LR2SongDB.folder
                    {
                        path = managedStalePath,
                        title = "Managed Stale",
                        type = 2,
                        date = 1
                    }, typeof(LR2SongDB.folder));
                });
            }
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, TestBmsFactory.AvailableEverythingBridge, settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

                BmsCharts = []
            };
            library.ReloadFileDiff();
            Lr2SongDbSyncInput scanInput = library.Lr2Synchronization.CreateLr2SongDbSyncInput();

            CollectionAssert.AreEqual(new[] { currentPath }, scanInput.Lr2FolderFilePaths.ToArray());
            Assert.IsFalse(scanInput.Lr2FolderFileEntries.ContainsKey(managedCurrentPath));

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(1, verify.Table<LR2SongDB.folder>().Count(row => row.path == currentPath));
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().Count(row => row.path == stalePath));
            LR2SongDB.folder managedCurrent = verify.Table<LR2SongDB.folder>().Single(row => row.path == managedCurrentPath);
            Assert.AreEqual("Managed Existing", managedCurrent.title);
            Assert.AreEqual(1, verify.Table<LR2SongDB.folder>().Count(row => row.path == managedStalePath));
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 差分走査では設定済みの管理出力を同期候補から除外し、外部定義を捕捉しながら管理出力の既存DB行を保持する。
    /// </summary>
    /// <remarks>実管理主体とSQLite保存を接続し、入力面の選択だけでは分からない反映と終端を確認する。</remarks>
    [TestMethod]
    public void ReloadFileDiff_ExcludesManagedOutputAndPreservesExistingManagedRow()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            PlaylistPersistenceRepository.EnsureSchema(scope.DatabaseScope, scope.SongDbPath);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string externalDirectory = Path.Combine(rootDirectory, "ExternalTable");
            string outputBase = Path.Combine(rootDirectory, "#BeMusicSeekerOutput");
            string managedDirectory = Path.Combine(outputBase, "ManagedTable");
            Directory.CreateDirectory(externalDirectory);
            Directory.CreateDirectory(managedDirectory);
            testSettings.LR2CustomFolderOutputBaseDir = outputBase;
            string externalPath = Path.Combine(externalDirectory, "current.lr2folder");
            string managedPath = Path.Combine(managedDirectory, "0000.lr2folder");
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);
            File.WriteAllText(externalPath, "#TITLE Current", Encoding.GetEncoding("shift_jis"));
            File.WriteAllText(managedPath, "#TITLE Managed Physical", Encoding.GetEncoding("shift_jis"));
            File.SetLastWriteTimeUtc(externalPath, timestamp);
            File.SetLastWriteTimeUtc(managedPath, timestamp);
            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                setup.RunInTransaction(() =>
                {
                    setup.CreateTable<LR2SongDB.folder>();
                    setup.InsertOrReplace(new BMSTable
                    {
                        playlist_id = 9102,
                        name = "ManagedTable",
                        symbol = "M",
                        Output_dir = "ManagedTable"
                    }, typeof(LR2SongDBExtended.playlist));
                    setup.InsertOrReplace(new LR2SongDB.folder
                    {
                        path = managedPath,
                        title = "Managed Existing",
                        type = 2,
                        date = 1
                    }, typeof(LR2SongDB.folder));
                });
            }
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, TestBmsFactory.AvailableEverythingBridge, settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

                BmsCharts = []
            };
            library.ReloadFileDiff();
            Lr2SongDbSyncInput scanInput = library.Lr2Synchronization.CreateLr2SongDbSyncInput();

            CollectionAssert.AreEqual(new[] { externalPath }, scanInput.Lr2FolderFilePaths.ToArray());
            Assert.IsFalse(scanInput.Lr2FolderFileEntries.ContainsKey(managedPath));
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            LR2SongDB.folder managedCurrent = verify.Table<LR2SongDB.folder>().Single(row => row.path == managedPath);
            Assert.AreEqual("Managed Existing", managedCurrent.title);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 完全な外部定義走査で消失した定義行を除き、現存定義だけをDBへ残す。古い行の取り残しを検出する。
    /// </summary>
    /// <remarks>実管理主体とSQLite保存を接続し、入力面の選択だけでは分からない反映と終端を確認する。</remarks>
    [TestMethod]
    public void ReloadFileDiff_PrunesExternalRowsInScope()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string tableDirectory = Path.Combine(rootDirectory, "ExternalTable");
            Directory.CreateDirectory(tableDirectory);
            string currentPath = Path.Combine(tableDirectory, "current.lr2folder");
            string stalePath = Path.Combine(tableDirectory, "stale.lr2folder");
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);
            File.WriteAllText(currentPath, "#TITLE Current", Encoding.GetEncoding("shift_jis"));
            File.SetLastWriteTimeUtc(currentPath, timestamp);
            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                setup.RunInTransaction(() =>
                {
                    setup.CreateTable<LR2SongDB.folder>();
                    setup.InsertOrReplace(new LR2SongDB.folder
                    {
                        path = stalePath,
                        title = "Stale",
                        type = 2,
                        date = 1
                    }, typeof(LR2SongDB.folder));
                });
            }
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, TestBmsFactory.AvailableEverythingBridge, settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

                BmsCharts = []
            };
            library.ReloadFileDiff();

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(1, verify.Table<LR2SongDB.folder>().Count(row => row.path == currentPath));
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().Count(row => row.path == stalePath));
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 管理playlistがない場合も実在する外部lr2folderを同期し、管理出力扱いで一律除外する誤りを検出する。
    /// </summary>
    /// <remarks>実管理主体とSQLite保存を接続し、入力面の選択だけでは分からない反映と終端を確認する。</remarks>
    [TestMethod]
    public void ReloadFileDiff_KeepsPhysicalLr2FoldersWhenNoManagedPlaylistScopeExists()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string outputBase = Path.Combine(rootDirectory, "#BeMusicSeekerOutput");
            string outputDirectory = Path.Combine(outputBase, "ManagedTable");
            string lr2FolderPath = Path.Combine(outputDirectory, "0000.lr2folder");
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllText(lr2FolderPath, "#TITLE Should Not Read", Encoding.GetEncoding("shift_jis"));
            testSettings.LR2CustomFolderOutputBaseDir = outputBase;
            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                setup.CreateTable<LR2SongDBExtended.playlist>();
            }
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, TestBmsFactory.AvailableEverythingBridge, settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

                BmsCharts = []
            };
            library.ReloadFileDiff();
            Lr2SongDbSyncInput scanInput = library.Lr2Synchronization.CreateLr2SongDbSyncInput();

            CollectionAssert.Contains(scanInput.Lr2FolderFilePaths.ToList(), lr2FolderPath);
            Assert.IsTrue(scanInput.Lr2FolderFileDiscoveryComplete);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 組込定義の入力ディレクトリがなくても準備は既存DB行を消さず、準備段階で反映する誤りを検出する。
    /// </summary>
    [TestMethod]
    public void SyncLr2BuiltinCustomFolderRows_PreparesPhysicalSurfaceWithoutDbMutationWhenSourceDirectoryMissing()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            testSettings.LR2RootPath = Path.Combine(scope.DirectoryPath, "MissingLR2");
            const string staleBuiltinPath = @"LR2files\CustomFolder\favorite.lr2folder";
            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                setup.RunInTransaction(() =>
                {
                    setup.CreateTable<LR2SongDB.folder>();
                    setup.InsertOrReplace(new LR2SongDB.folder
                    {
                        path = staleBuiltinPath,
                        title = "Favorite",
                        parent = Lr2SongFolderParentNormalizer.RootParentHash,
                        type = 2,
                        date = 1
                    }, typeof(LR2SongDB.folder));
                });
            }
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: testSettings);

            Lr2SongDbSyncPreparedDataSurface prepared = InvokeWithMutationCapability(
                library,
                mutationCapability => library.Lr2Synchronization.SyncLr2BuiltinCustomFolderRows(
                    "test_builtin_missing_source",
                    mutationCapability));

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            Assert.IsNotNull(verify.Table<LR2SongDB.folder>().ToList().SingleOrDefault(row => row.path == staleBuiltinPath));
            Assert.AreEqual(0, prepared.Lr2FolderFilePaths.Count);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 同期保持中は強制要求も拒否し、受理版を進めない。forceによる競合受付の迂回を検出する。
    /// </summary>
    [TestMethod]
    public async Task TryBeginLr2SongDbSyncRequest_DoesNotAdvanceVersionWhenAlreadyRunning()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, null, null, null, new BmsLibraryInitializationTestSupport.RecordingDialogService(), settings: testSettings);

            await using HeldLr2Operation held = await HeldLr2Operation.StartAsync(library);
            int firstVersion = library.Lr2SongDbSyncRequestedVersion;
            Assert.AreEqual(1, firstVersion);
            await library.QueueLr2SongDbSyncAsync("test_request_version_duplicate", force: true);
            int secondVersion = library.Lr2SongDbSyncRequestedVersion;
            Assert.AreEqual(firstVersion, secondVersion);
            Assert.AreEqual(1, library.Lr2SongDbSyncRequestedVersion);
            Assert.IsTrue(library.Lr2SongDbSyncRunning);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 走査面へ準備済み定義の面を合流して古い候補を外し、準備済み定義のtitle・type・親CRCと完了状態を実DBへ保存する。
    /// </summary>
    /// <remarks>実管理主体とSQLite保存を接続し、入力面の選択だけでは分からない反映と終端を確認する。</remarks>
    [TestMethod]
    public async Task QueueLr2SongDbSync_MergesPreparedLr2FolderSurfaceAfterPreparedOutput()
    {
        LibraryFileInitializationResult? initializationInput = null;
        Lr2SongDbSyncPreparedDataSurface? preparedInput = null;
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string outputBase = Path.Combine(scope.DirectoryPath, "#BeMusicSeekerOutput");
            string lr2FolderPath = Path.Combine(outputBase, "prepared.lr2folder");
            string staleLr2FolderPath = Path.Combine(outputBase, "stale.lr2folder");
            Directory.CreateDirectory(rootDirectory);
            string folderInfoPath = Path.Combine(rootDirectory, "folderinfo.txt");
            File.WriteAllText(folderInfoPath, "#TITLE Surface Root", Encoding.GetEncoding("shift_jis"));
            testSettings.LR2CustomFolderOutputBaseDir = outputBase;
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

                BmsCharts = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
            };
            initializationInput = new(InvokeCaptureLr2SongDbSyncScanSurface(library, options, [rootDirectory], new SongTableFileCheckResult
            {
                Lr2ScanSurfaceAvailable = true,
                Lr2ScanNormalFolderDirectoryPaths = [rootDirectory],
                Lr2ScanNormalFolderDirectoryEntries = CreateDirectoryEntryMap(rootDirectory),
                Lr2ScanFolderInfoFilePaths = [folderInfoPath],
                Lr2ScanFolderInfoFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [folderInfoPath] = new RootFileEnumerationEntry(folderInfoPath, new DateTime(2026, 6, 7, 1, 0, 0, DateTimeKind.Utc))
                },
                Lr2ScanTextFileDirectories = [rootDirectory],
                Lr2ScanLr2FolderDiscoveryDirectories = [rootDirectory, outputBase],
                Lr2ScanLr2FolderFilePaths = [staleLr2FolderPath],
                Lr2ScanLr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [staleLr2FolderPath] = new RootFileEnumerationEntry(staleLr2FolderPath, new DateTime(2026, 6, 7, 0, 30, 0, DateTimeKind.Utc))
                },
                Lr2ScanLr2FolderFileDiscoveryComplete = true
            }));

            Directory.CreateDirectory(outputBase);
            File.WriteAllText(lr2FolderPath, "#TITLE Prepared Folder", Encoding.GetEncoding("shift_jis"));
            preparedInput = CreatePreparedLr2FolderSurface(
                        outputBase,
                        lr2FolderPath,
                        CreateDirectoryEntryMap(rootDirectory, outputBase));
            Lr2SongDbSyncInput input = InvokeCreateLr2SongDbSyncInput(library, initializationInput, preparedInput);
            CollectionAssert.Contains(input.FolderInfoFilePaths.ToList(), folderInfoPath);
            CollectionAssert.Contains(input.TextFileDirectories.ToList(), rootDirectory);
            CollectionAssert.Contains(input.Lr2FolderDiscoveryDirectories.ToList(), outputBase);
            CollectionAssert.Contains(input.Lr2FolderFilePaths.ToList(), lr2FolderPath);
            CollectionAssert.DoesNotContain(input.Lr2FolderFilePaths.ToList(), staleLr2FolderPath);
            Assert.IsTrue(input.Lr2FolderFileDiscoveryComplete);


            await library.QueueLr2SongDbSyncAsync(
                "test_prepare_lr2folder_surface",
                force: true,
                prepareGeneratedData: (_, _) =>
                {
                    Directory.CreateDirectory(outputBase);
                    File.WriteAllText(lr2FolderPath, "#TITLE Prepared Folder", Encoding.GetEncoding("shift_jis"));
                    return CreatePreparedLr2FolderSurface(
                        outputBase,
                        lr2FolderPath,
                        CreateDirectoryEntryMap(rootDirectory, outputBase));
                }, initializationResult: initializationInput, preparedSurface: preparedInput);

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            LR2SongDB.folder lr2Folder = verify.Table<LR2SongDB.folder>().ToList().Single(row => row.path == lr2FolderPath);
            Assert.AreEqual(2, lr2Folder.type);
            Assert.AreEqual("Prepared Folder", lr2Folder.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(outputBase), lr2Folder.parent);
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().Count(row => row.path == lr2FolderPath && row.title == "stale"));
            LR2SongDBExtended.lr2_song_db_sync_status status = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.IsNotNull(status);
            Assert.AreEqual("Completed", status.status);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 走査後に物理ディレクトリ時刻が変わっても、同期入力は走査で捕捉した時刻を使用する。再読取りによる入力混在を検出する。
    /// </summary>
    [TestMethod]
    public void CreateLr2SongDbSyncInput_ReusesScanSurfaceDirectoryEntries()
    {
        LibraryFileInitializationResult? initializationInput = null;
        Lr2SongDbSyncPreparedDataSurface? preparedInput = null;
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            DateTime surfaceTimestamp = new(2026, 6, 7, 4, 0, 0, DateTimeKind.Utc);
            DateTime liveTimestamp = new(2026, 6, 8, 4, 0, 0, DateTimeKind.Utc);
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

                BmsCharts = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
            };

            initializationInput = new(InvokeCaptureLr2SongDbSyncScanSurface(library, options, [rootDirectory], new SongTableFileCheckResult
            {
                Lr2ScanSurfaceAvailable = true,
                Lr2ScanNormalFolderDirectoryPaths = [rootDirectory],
                Lr2ScanNormalFolderDirectoryEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [rootDirectory] = new RootFileEnumerationEntry(rootDirectory, surfaceTimestamp)
                },
                Lr2ScanTextFileDirectories = [],
                Lr2ScanLr2FolderDiscoveryDirectories = [rootDirectory],
                Lr2ScanLr2FolderFilePaths = [],
                Lr2ScanLr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                Lr2ScanLr2FolderFileDiscoveryComplete = true
            }));
            Directory.SetLastWriteTimeUtc(rootDirectory, liveTimestamp);

            Lr2SongDbSyncInput input = InvokeCreateLr2SongDbSyncInput(library, initializationInput, preparedInput);
            IReadOnlyDictionary<string, RootFileEnumerationEntry> entries = input.DirectoryEntries;

            string rootKey = Lr2FolderPath.NormalizeDirectoryPath(rootDirectory);
            Assert.IsTrue(entries.TryGetValue(rootKey, out RootFileEnumerationEntry? entry));
            Assert.AreEqual(surfaceTimestamp, entry!.LastWriteTimeUtc);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 定義の親ディレクトリも捕捉時刻を保持し、同期入力作成時の物理時刻へ置き換えない。
    /// </summary>
    [TestMethod]
    public void CreateLr2SongDbSyncInput_ReusesScanSurfaceDirectoryEntriesForLr2FolderParents()
    {
        LibraryFileInitializationResult? initializationInput = null;
        Lr2SongDbSyncPreparedDataSurface? preparedInput = null;
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string tableDirectory = Path.Combine(rootDirectory, "Table");
            string lr2FolderPath = Path.Combine(tableDirectory, "select.lr2folder");
            Directory.CreateDirectory(tableDirectory);
            File.WriteAllText(lr2FolderPath, "#TITLE Table", Encoding.GetEncoding("shift_jis"));
            DateTime surfaceTimestamp = new(2026, 6, 7, 4, 0, 0, DateTimeKind.Utc);
            DateTime liveTimestamp = new(2026, 6, 8, 4, 0, 0, DateTimeKind.Utc);
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

                BmsCharts = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
            };

            initializationInput = new(InvokeCaptureLr2SongDbSyncScanSurface(library, options, [rootDirectory], new SongTableFileCheckResult
            {
                Lr2ScanSurfaceAvailable = true,
                Lr2ScanNormalFolderDirectoryPaths = [rootDirectory],
                Lr2ScanDirectoryEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [tableDirectory] = new RootFileEnumerationEntry(tableDirectory, surfaceTimestamp)
                },
                Lr2ScanNormalFolderDirectoryEntries = CreateDirectoryEntryMap(rootDirectory),
                Lr2ScanTextFileDirectories = [],
                Lr2ScanLr2FolderDiscoveryDirectories = [rootDirectory],
                Lr2ScanLr2FolderFilePaths = [lr2FolderPath],
                Lr2ScanLr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [lr2FolderPath] = new RootFileEnumerationEntry(lr2FolderPath, surfaceTimestamp)
                },
                Lr2ScanLr2FolderFileDiscoveryComplete = true
            }));
            Directory.SetLastWriteTimeUtc(tableDirectory, liveTimestamp);

            Lr2SongDbSyncInput input = InvokeCreateLr2SongDbSyncInput(library, initializationInput, preparedInput);
            IReadOnlyDictionary<string, RootFileEnumerationEntry> entries = input.DirectoryEntries;

            string tableKey = Lr2FolderPath.NormalizeDirectoryPath(tableDirectory);
            Assert.IsTrue(entries.TryGetValue(tableKey, out RootFileEnumerationEntry? entry));
            Assert.AreEqual(surfaceTimestamp, entry!.LastWriteTimeUtc);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 走査面がなくても準備済み定義のパスと列挙完了を入力へ渡し、準備結果を欠落させない。
    /// </summary>
    [TestMethod]
    public void PreparedSurface_UsesPreparedSurfaceWithoutCapturedScanSurface()
    {
        LibraryFileInitializationResult? initializationInput = null;
        Lr2SongDbSyncPreparedDataSurface? preparedInput = null;
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string outputBase = Path.Combine(scope.DirectoryPath, "#BeMusicSeekerOutput");
            string lr2FolderPath = Path.Combine(outputBase, "prepared.lr2folder");
            Directory.CreateDirectory(rootDirectory);
            testSettings.LR2CustomFolderOutputBaseDir = outputBase;
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
                LR2RootPath = string.Empty,
                LR2CustomFolderOutputBaseDir = string.Empty,
                LR2CustomFolderAdditionalOutputBaseDirs = [],
                LR2CustomFolderOutputBaseDirRootType = string.Empty
            };
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, null, null, "test", () => options, settings: TestBmsFactory.CreateRootSettings([], testSettings))
            {

                BmsCharts = []
            };

            Directory.CreateDirectory(outputBase);
            File.WriteAllText(lr2FolderPath, "#TITLE Prepared Folder", Encoding.GetEncoding("shift_jis"));
            preparedInput = CreatePreparedLr2FolderSurface(
                        outputBase,
                        lr2FolderPath,
                        CreateDirectoryEntryMap(rootDirectory, outputBase));
            Lr2SongDbSyncInput input = InvokeCreateLr2SongDbSyncInput(library, initializationInput, preparedInput);

            Assert.AreEqual(0, input.Lr2FolderDiscoveryDirectories.Count);
            CollectionAssert.Contains(input.Lr2FolderFilePaths.ToList(), lr2FolderPath);
            Assert.IsTrue(input.Lr2FolderFileDiscoveryComplete);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 準備後の物理時刻変更に影響されず、準備面のディレクトリ時刻を入力へ引き継ぐ。
    /// </summary>
    [TestMethod]
    public void PreparedSurface_UsesPreparedDirectoryEntries()
    {
        LibraryFileInitializationResult? initializationInput = null;
        Lr2SongDbSyncPreparedDataSurface? preparedInput = null;
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string outputBase = Path.Combine(scope.DirectoryPath, "#BeMusicSeekerOutput");
            string lr2FolderPath = Path.Combine(outputBase, "prepared.lr2folder");
            Directory.CreateDirectory(rootDirectory);
            Directory.CreateDirectory(outputBase);
            File.WriteAllText(lr2FolderPath, "#TITLE Prepared Folder", Encoding.GetEncoding("shift_jis"));
            testSettings.LR2CustomFolderOutputBaseDir = outputBase;
            DateTime preparedTimestamp = new(2026, 6, 8, 1, 0, 0, DateTimeKind.Utc);
            DateTime liveTimestamp = preparedTimestamp.AddHours(2);
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
                LR2RootPath = string.Empty,
                LR2CustomFolderOutputBaseDir = string.Empty,
                LR2CustomFolderAdditionalOutputBaseDirs = [],
                LR2CustomFolderOutputBaseDirRootType = string.Empty
            };
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, null, null, "test", () => options, settings: TestBmsFactory.CreateRootSettings([], testSettings))
            {

                BmsCharts = []
            };

            preparedInput = CreatePreparedLr2FolderSurface(
                    outputBase,
                    lr2FolderPath,
                    new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                    {
                        [rootDirectory] = new RootFileEnumerationEntry(rootDirectory, preparedTimestamp),
                        [outputBase] = new RootFileEnumerationEntry(outputBase, preparedTimestamp)
                    });
            Directory.SetLastWriteTimeUtc(outputBase, liveTimestamp);

            Lr2SongDbSyncInput input = InvokeCreateLr2SongDbSyncInput(library, initializationInput, preparedInput);
            IReadOnlyDictionary<string, RootFileEnumerationEntry> entries = input.DirectoryEntries;

            string outputKey = Lr2FolderPath.NormalizeDirectoryPath(outputBase);
            Assert.IsTrue(entries.TryGetValue(outputKey, out RootFileEnumerationEntry? entry));
            Assert.AreEqual(preparedTimestamp, entry!.LastWriteTimeUtc);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 定義ファイルがない準備面でもディレクトリ情報を残し、ファイル件数だけでメタデータを捨てない。
    /// </summary>
    [TestMethod]
    public void PreparedSurface_KeepsMetadataOnlySurface()
    {
        LibraryFileInitializationResult? initializationInput = null;
        Lr2SongDbSyncPreparedDataSurface? preparedInput = null;
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            DateTime preparedTimestamp = new(2026, 6, 8, 3, 0, 0, DateTimeKind.Utc);
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
                LR2RootPath = string.Empty,
                LR2CustomFolderOutputBaseDir = string.Empty,
                LR2CustomFolderAdditionalOutputBaseDirs = [],
                LR2CustomFolderOutputBaseDirRootType = string.Empty
            };
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, null, null, "test", () => options, settings: TestBmsFactory.CreateRootSettings([], testSettings))
            {

                BmsCharts = []
            };

            preparedInput = new Lr2SongDbSyncPreparedDataSurface(
                    [],
                    [],
                    new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                    {
                        [rootDirectory] = new RootFileEnumerationEntry(rootDirectory, preparedTimestamp)
                    },
                    [],
                    new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                    [],
                    discoveryComplete: true);

            Lr2SongDbSyncInput input = InvokeCreateLr2SongDbSyncInput(library, initializationInput, preparedInput);
            IReadOnlyDictionary<string, RootFileEnumerationEntry> entries = input.DirectoryEntries;

            string rootKey = Lr2FolderPath.NormalizeDirectoryPath(rootDirectory);
            Assert.IsTrue(entries.TryGetValue(rootKey, out RootFileEnumerationEntry? entry));
            Assert.AreEqual(preparedTimestamp, entry!.LastWriteTimeUtc);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 同じfolderinfoを含む準備面は古い走査情報へ上書きし、新しい時刻とテキスト対象を入力へ引き継ぐ。
    /// </summary>
    [TestMethod]
    public void PreparedSurface_OverlaysPreparedFolderInfoSurface()
    {
        LibraryFileInitializationResult? initializationInput = null;
        Lr2SongDbSyncPreparedDataSurface? preparedInput = null;
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string outputBase = Path.Combine(scope.DirectoryPath, "#BeMusicSeekerOutput");
            string lr2FolderPath = Path.Combine(outputBase, "prepared.lr2folder");
            string folderInfoPath = Path.Combine(outputBase, "folderinfo.txt");
            Directory.CreateDirectory(rootDirectory);
            Directory.CreateDirectory(outputBase);
            File.WriteAllText(lr2FolderPath, "#TITLE Prepared Folder", Encoding.GetEncoding("shift_jis"));
            File.WriteAllText(folderInfoPath, "#TITLE Prepared Info", Encoding.GetEncoding("shift_jis"));
            testSettings.LR2CustomFolderOutputBaseDir = outputBase;
            DateTime oldTimestamp = new(2026, 6, 8, 1, 0, 0, DateTimeKind.Utc);
            DateTime preparedTimestamp = oldTimestamp.AddHours(1);
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

                BmsCharts = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
            };
            initializationInput = new(InvokeCaptureLr2SongDbSyncScanSurface(library, options, [rootDirectory], new SongTableFileCheckResult
            {
                Lr2ScanSurfaceAvailable = true,
                Lr2ScanNormalFolderDirectoryPaths = [rootDirectory],
                Lr2ScanDirectoryEntries = CreateDirectoryEntryMap(rootDirectory, outputBase),
                Lr2ScanNormalFolderDirectoryEntries = CreateDirectoryEntryMap(rootDirectory),
                Lr2ScanFolderInfoFilePaths = [folderInfoPath],
                Lr2ScanFolderInfoFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [folderInfoPath] = new RootFileEnumerationEntry(folderInfoPath, oldTimestamp)
                },
                Lr2ScanTextFileDirectories = [],
                Lr2ScanLr2FolderDiscoveryDirectories = [rootDirectory, outputBase],
                Lr2ScanLr2FolderFilePaths = [lr2FolderPath],
                Lr2ScanLr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [lr2FolderPath] = new RootFileEnumerationEntry(lr2FolderPath, oldTimestamp)
                },
                Lr2ScanLr2FolderFileDiscoveryComplete = true
            }));

            preparedInput = CreatePreparedLr2FolderSurface(
                    outputBase,
                    lr2FolderPath,
                    CreateDirectoryEntryMap(outputBase),
                    [folderInfoPath],
                    new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                    {
                        [folderInfoPath] = new RootFileEnumerationEntry(folderInfoPath, preparedTimestamp)
                    },
                    [outputBase]);

            Lr2SongDbSyncInput input = InvokeCreateLr2SongDbSyncInput(library, initializationInput, preparedInput);
            IReadOnlyDictionary<string, RootFileEnumerationEntry> entries = input.FolderInfoFileEntries;
            IReadOnlyCollection<string> textFileDirectories = input.TextFileDirectories;

            Assert.IsTrue(entries.TryGetValue(folderInfoPath, out RootFileEnumerationEntry? entry));
            Assert.AreEqual(preparedTimestamp, entry!.LastWriteTimeUtc);
            CollectionAssert.Contains(textFileDirectories.ToList(), Lr2FolderPath.NormalizeDirectoryPath(outputBase));
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 準備範囲内の古いテキスト対象だけを置換し、別範囲の捕捉対象を保持する。範囲外まで消す誤りを検出する。
    /// </summary>
    [TestMethod]
    public void CreateLr2SongDbSyncInput_ReplacesOnlyPreparedScopeTextFileDirectories()
    {
        LibraryFileInitializationResult? initializationInput = null;
        Lr2SongDbSyncPreparedDataSurface? preparedInput = null;
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string outputBase = Path.Combine(scope.DirectoryPath, "#BeMusicSeekerOutput");
            string preparedDirectory = Path.Combine(outputBase, "Table");
            string preparedPrefixSibling = Path.Combine(outputBase, "TableOther");
            string stalePreparedChildDirectory = Path.Combine(preparedDirectory, "OldText");
            string lr2FolderPath = Path.Combine(preparedDirectory, "0000.lr2folder");
            Directory.CreateDirectory(rootDirectory);
            Directory.CreateDirectory(preparedDirectory);
            Directory.CreateDirectory(preparedPrefixSibling);
            Directory.CreateDirectory(stalePreparedChildDirectory);
            File.WriteAllText(lr2FolderPath, "#TITLE Prepared Folder", Encoding.GetEncoding("shift_jis"));
            testSettings.LR2CustomFolderOutputBaseDir = outputBase;
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

                BmsCharts = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
            };
            initializationInput = new(InvokeCaptureLr2SongDbSyncScanSurface(library, options, [rootDirectory], new SongTableFileCheckResult
            {
                Lr2ScanSurfaceAvailable = true,
                Lr2ScanNormalFolderDirectoryPaths = [rootDirectory],
                Lr2ScanDirectoryEntries = CreateDirectoryEntryMap(rootDirectory, outputBase, preparedDirectory, preparedPrefixSibling, stalePreparedChildDirectory),
                Lr2ScanNormalFolderDirectoryEntries = CreateDirectoryEntryMap(rootDirectory),
                Lr2ScanFolderInfoFilePaths = [],
                Lr2ScanFolderInfoFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                Lr2ScanTextFileDirectories = [rootDirectory, stalePreparedChildDirectory, preparedPrefixSibling],
                Lr2ScanLr2FolderDiscoveryDirectories = [rootDirectory, outputBase],
                Lr2ScanLr2FolderFilePaths = [Path.Combine(preparedDirectory, "stale.lr2folder")],
                Lr2ScanLr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                Lr2ScanLr2FolderFileDiscoveryComplete = true
            }));

            preparedInput = CreatePreparedLr2FolderSurface(
                    preparedDirectory,
                    lr2FolderPath,
                    CreateDirectoryEntryMap(
                        rootDirectory,
                        outputBase,
                        preparedDirectory,
                        preparedPrefixSibling,
                        stalePreparedChildDirectory),
                    textFileDirectories: [preparedDirectory]);

            Lr2SongDbSyncInput input = InvokeCreateLr2SongDbSyncInput(library, initializationInput, preparedInput);
            var textFileDirectories = input.TextFileDirectories.ToList();

            CollectionAssert.Contains(textFileDirectories, Lr2FolderPath.NormalizeDirectoryPath(rootDirectory));
            CollectionAssert.Contains(textFileDirectories, Lr2FolderPath.NormalizeDirectoryPath(preparedDirectory));
            CollectionAssert.Contains(textFileDirectories, Lr2FolderPath.NormalizeDirectoryPath(preparedPrefixSibling));
            CollectionAssert.DoesNotContain(textFileDirectories, Lr2FolderPath.NormalizeDirectoryPath(stalePreparedChildDirectory));
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 管理出力の準備済み定義を合流後の入力へ含め、消失した同範囲の旧定義を除く。
    /// </summary>
    [TestMethod]
    public void CreateLr2SongDbSyncInput_IncludesPreparedManagedOutputAfterScanSurfaceMerge()
    {
        LibraryFileInitializationResult? initializationInput = null;
        Lr2SongDbSyncPreparedDataSurface? preparedInput = null;
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            PlaylistPersistenceRepository.EnsureSchema(scope.DatabaseScope, scope.SongDbPath);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string outputBase = Path.Combine(rootDirectory, "#BeMusicSeekerOutput");
            string outputDirectory = Path.Combine(outputBase, "ManagedTable");
            string managedPath = Path.Combine(outputDirectory, "0000.lr2folder");
            string stalePath = Path.Combine(outputDirectory, "stale.lr2folder");
            Directory.CreateDirectory(rootDirectory);
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllText(managedPath, "#TITLE Managed", Encoding.GetEncoding("shift_jis"));
            File.WriteAllText(stalePath, "#TITLE Stale", Encoding.GetEncoding("shift_jis"));
            testSettings.LR2CustomFolderOutputBaseDir = outputBase;
            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                setup.InsertOrReplace(new BMSTable
                {
                    playlist_id = 9303,
                    name = "ManagedTable",
                    symbol = "MT",
                    Output_dir = "ManagedTable",
                    ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                        & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                        & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
                }, typeof(LR2SongDBExtended.playlist));
            }
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

                BmsCharts = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
            };
            initializationInput = new(InvokeCaptureLr2SongDbSyncScanSurface(library, options, [rootDirectory], new SongTableFileCheckResult
            {
                Lr2ScanSurfaceAvailable = true,
                Lr2ScanNormalFolderDirectoryPaths = [rootDirectory],
                Lr2ScanDirectoryEntries = CreateDirectoryEntryMap(rootDirectory, outputBase, outputDirectory),
                Lr2ScanNormalFolderDirectoryEntries = CreateDirectoryEntryMap(rootDirectory),
                Lr2ScanFolderInfoFilePaths = [],
                Lr2ScanFolderInfoFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                Lr2ScanTextFileDirectories = [rootDirectory],
                Lr2ScanLr2FolderDiscoveryDirectories = [rootDirectory, outputBase],
                Lr2ScanLr2FolderFilePaths = [stalePath],
                Lr2ScanLr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                Lr2ScanLr2FolderFileDiscoveryComplete = true
            }));
            preparedInput = CreatePreparedLr2FolderSurface(
                    outputDirectory,
                    managedPath,
                    CreateDirectoryEntryMap(rootDirectory, outputBase, outputDirectory),
                    managedDefinition: new Lr2FolderFileDefinition { Title = "Managed" });

            Lr2SongDbSyncInput input = InvokeCreateLr2SongDbSyncInput(library, initializationInput, preparedInput);
            var lr2FolderFilePaths = input.Lr2FolderFilePaths.ToList();

            CollectionAssert.Contains(lr2FolderFilePaths, managedPath);
            CollectionAssert.DoesNotContain(lr2FolderFilePaths, stalePath);
            Assert.IsTrue(input.Lr2FolderFileDiscoveryComplete);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 出力base内でも準備範囲外の外部定義は残し、base全体を準備結果で置換しない。
    /// </summary>
    [TestMethod]
    public void CreateLr2SongDbSyncInput_KeepsExternalLr2FolderInOutputBaseOutsidePreparedScope()
    {
        LibraryFileInitializationResult? initializationInput = null;
        Lr2SongDbSyncPreparedDataSurface? preparedInput = null;
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string outputBase = Path.Combine(scope.DirectoryPath, "Output");
            string appOutputDir = Path.Combine(outputBase, "Table");
            string preparedLr2FolderPath = Path.Combine(appOutputDir, "0000.lr2folder");
            string externalLr2FolderPath = Path.Combine(outputBase, "external.lr2folder");
            Directory.CreateDirectory(rootDirectory);
            Directory.CreateDirectory(appOutputDir);
            File.WriteAllText(externalLr2FolderPath, "#TITLE External Folder", Encoding.GetEncoding("shift_jis"));
            testSettings.LR2CustomFolderOutputBaseDir = outputBase;
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

                BmsCharts = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
            };
            initializationInput = new(InvokeCaptureLr2SongDbSyncScanSurface(library, options, [rootDirectory], new SongTableFileCheckResult
            {
                Lr2ScanSurfaceAvailable = true,
                Lr2ScanNormalFolderDirectoryPaths = [rootDirectory],
                Lr2ScanNormalFolderDirectoryEntries = CreateDirectoryEntryMap(rootDirectory),
                Lr2ScanDirectoryEntries = CreateDirectoryEntryMap(rootDirectory, outputBase, appOutputDir),
                Lr2ScanLr2FolderDiscoveryDirectories = [rootDirectory, outputBase],
                Lr2ScanLr2FolderFilePaths = [externalLr2FolderPath],
                Lr2ScanLr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [externalLr2FolderPath] = new RootFileEnumerationEntry(
                        externalLr2FolderPath,
                        File.GetLastWriteTimeUtc(externalLr2FolderPath))
                },
                Lr2ScanLr2FolderFileDiscoveryComplete = true
            }));

            File.WriteAllText(preparedLr2FolderPath, "#TITLE Prepared Folder", Encoding.GetEncoding("shift_jis"));
            preparedInput = CreatePreparedLr2FolderSurface(
                        appOutputDir,
                        preparedLr2FolderPath,
                        CreateDirectoryEntryMap(rootDirectory, outputBase, appOutputDir));

            Lr2SongDbSyncInput input = InvokeCreateLr2SongDbSyncInput(library, initializationInput, preparedInput);
            var lr2FolderFilePaths = input.Lr2FolderFilePaths.ToList();

            CollectionAssert.Contains(lr2FolderFilePaths, preparedLr2FolderPath);
            CollectionAssert.Contains(lr2FolderFilePaths, externalLr2FolderPath);
            Assert.IsTrue(input.Lr2FolderFileDiscoveryComplete);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 走査捕捉時に管理playlistの出力候補だけを除き、同じbaseの外部定義を残す。
    /// </summary>
    [TestMethod]
    public void CaptureLr2SongDbSyncScanSurface_ExcludesManagedOutputCandidates()
    {
        LibraryFileInitializationResult? initializationInput = null;
        Lr2SongDbSyncPreparedDataSurface? preparedInput = null;
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            PlaylistPersistenceRepository.EnsureSchema(scope.DatabaseScope, scope.SongDbPath);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string outputBase = Path.Combine(rootDirectory, "#BeMusicSeekerOutput");
            string managedDirectory = Path.Combine(outputBase, "ManagedTable");
            string managedPath = Path.Combine(managedDirectory, "0000.lr2folder");
            string externalOutputPath = Path.Combine(outputBase, "external.lr2folder");
            Directory.CreateDirectory(managedDirectory);
            File.WriteAllText(managedPath, "#TITLE Managed", Encoding.GetEncoding("shift_jis"));
            File.WriteAllText(externalOutputPath, "#TITLE External", Encoding.GetEncoding("shift_jis"));
            testSettings.LR2CustomFolderOutputBaseDir = outputBase;
            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                setup.InsertOrReplace(new BMSTable
                {
                    playlist_id = 9102,
                    name = "ManagedTable",
                    symbol = "M",
                    Output_dir = "ManagedTable"
                }, typeof(LR2SongDBExtended.playlist));
            }
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

                BmsCharts = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
            };
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);

            initializationInput = new(InvokeCaptureLr2SongDbSyncScanSurface(library, options, [rootDirectory], new SongTableFileCheckResult
            {
                Lr2ScanSurfaceAvailable = true,
                Lr2ScanNormalFolderDirectoryPaths = [rootDirectory],
                Lr2ScanNormalFolderDirectoryEntries = CreateDirectoryEntryMap(rootDirectory),
                Lr2ScanDirectoryEntries = CreateDirectoryEntryMap(rootDirectory, outputBase, managedDirectory),
                Lr2ScanFolderInfoFilePaths = [],
                Lr2ScanFolderInfoFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                Lr2ScanTextFileDirectories = [rootDirectory],
                Lr2ScanLr2FolderDiscoveryDirectories = [rootDirectory, outputBase],
                Lr2ScanLr2FolderFilePaths = [managedPath, externalOutputPath],
                Lr2ScanLr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [managedPath] = new RootFileEnumerationEntry(managedPath, timestamp),
                    [externalOutputPath] = new RootFileEnumerationEntry(externalOutputPath, timestamp)
                },
                Lr2ScanLr2FolderFileDiscoveryComplete = true
            }));

            Lr2SongDbSyncInput input = InvokeCreateLr2SongDbSyncInput(library, initializationInput, preparedInput);
            var lr2FolderFilePaths = input.Lr2FolderFilePaths.ToList();

            CollectionAssert.DoesNotContain(lr2FolderFilePaths, managedPath);
            CollectionAssert.Contains(lr2FolderFilePaths, externalOutputPath);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// playlist由来の管理範囲は出力ディレクトリを返し、個別定義や削除対象パスを混入させない。
    /// </summary>
    [TestMethod]
    public void CreateLr2SongDbSyncAppManagedOutputScope_ReturnsManagedOutputDirectoriesOnly()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            testSettings.EnableDownloadLr2IrScoreAndDetectUnsent = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            PlaylistPersistenceRepository.EnsureSchema(scope.DatabaseScope, scope.SongDbPath);
            string outputBase = Path.Combine(scope.DirectoryPath, "Output");
            testSettings.LR2CustomFolderOutputBaseDir = outputBase;
            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                setup.RunInTransaction(() =>
                {
                    setup.InsertOrReplace(new BMSTable
                    {
                        playlist_id = 9201,
                        name = "CountParity",
                        symbol = "CP",
                        Output_dir = "CountParity",
                        ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                            & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                            & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
                            & ~LR2SongDBExtended.playlist.CustomFolderType.LevelFolder
                            & ~LR2SongDBExtended.playlist.CustomFolderType.OtherFolder
                    }, typeof(LR2SongDBExtended.playlist));
                    setup.Execute(
                        "INSERT INTO playlist_entry (playlist_id, md5, title, folder, level, is_removed) VALUES (?, ?, ?, ?, ?, ?);",
                        9201,
                        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                        "Active A",
                        "Folder A",
                        1.2,
                        0);
                    setup.Execute(
                        "INSERT INTO playlist_entry (playlist_id, md5, title, folder, level, is_removed) VALUES (?, ?, ?, ?, ?, ?);",
                        9201,
                        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                        "Active B",
                        "Folder B",
                        null,
                        0);
                    setup.Execute(
                        "INSERT INTO playlist_entry (playlist_id, md5, title, folder, level, is_removed) VALUES (?, ?, ?, ?, ?, ?);",
                        9201,
                        "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
                        "Active Null Folder",
                        null,
                        null,
                        0);
                    setup.Execute(
                        "INSERT INTO playlist_entry (playlist_id, md5, title, folder, level, is_removed) VALUES (?, ?, ?, ?, ?, ?);",
                        9201,
                        "ffffffffffffffffffffffffffffffff",
                        "Active Empty Folder",
                        string.Empty,
                        null,
                        0);
                    setup.Execute(
                        "INSERT INTO playlist_entry (playlist_id, md5, title, folder, level, is_removed) VALUES (?, ?, ?, ?, ?, ?);",
                        9201,
                        "cccccccccccccccccccccccccccccccc",
                        "Removed Level",
                        "Removed Folder",
                        12.0,
                        1);
                    setup.Execute(
                        "INSERT INTO playlist_entry (playlist_id, md5, title, folder, level, is_removed) VALUES (?, ?, ?, ?, ?, ?);",
                        9201,
                        "dddddddddddddddddddddddddddddddd",
                        "Removed Null",
                        "Removed Null Folder",
                        null,
                        1);
                });
            }
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: TestBmsFactory.CreateRootSettings([scope.DirectoryPath], testSettings))
            {

                BmsCharts = []
            };

            Lr2SongDbSyncAppManagedOutputScope outputScope = InvokeCreateLr2SongDbSyncAppManagedOutputScope(library);
            var directories = outputScope.Directories.ToList();
            var filePaths = outputScope.FilePaths.ToList();
            var pruneExcludedPaths = outputScope.PruneExcludedPaths.ToList();

            string outputDirectory = Path.Combine(outputBase, "CountParity");
            CollectionAssert.Contains(directories, outputDirectory);
            Assert.AreEqual(0, filePaths.Count);
            Assert.AreEqual(0, pruneExcludedPaths.Count);
            Assert.IsTrue(outputScope.IsComplete);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 出力抑制設定がある表も出力ディレクトリを管理境界に使い、生成ファイルの有無へ範囲を狭めない。
    /// </summary>
    [TestMethod]
    public void CreateLr2SongDbSyncAppManagedOutputScope_UsesOutputDirectoryAsManagedBoundary()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            PlaylistPersistenceRepository.EnsureSchema(scope.DatabaseScope, scope.SongDbPath);
            string bmsRoot = Path.Combine(scope.DirectoryPath, "BMS");
            string outputBase = Path.Combine(bmsRoot, "#BeMusicSeeker");
            testSettings.LR2CustomFolderOutputBaseDir = outputBase;
            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                setup.RunInTransaction(() =>
                {
                    setup.InsertOrReplace(new BMSTable
                    {
                        playlist_id = 9202,
                        name = "ManagedHierarchy",
                        symbol = "MH",
                        Output_dir = "ManagedHierarchy",
                        ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                            & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                            & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
                            & ~LR2SongDBExtended.playlist.CustomFolderType.ClearFolder
                    }, typeof(LR2SongDBExtended.playlist));
                    setup.Execute(
                        "INSERT INTO playlist_entry (playlist_id, md5, title, folder, level, is_removed) VALUES (?, ?, ?, ?, ?, ?);",
                        9202,
                        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                        "Active A",
                        "st0",
                        1.0,
                        0);
                });
            }
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: TestBmsFactory.CreateRootSettings([bmsRoot], testSettings))
            {

                BmsCharts = []
            };

            Lr2SongDbSyncAppManagedOutputScope outputScope = InvokeCreateLr2SongDbSyncAppManagedOutputScope(library);
            var directories = outputScope.Directories.ToList();
            var pruneExcludedPaths = outputScope.PruneExcludedPaths.ToList();

            string outputDirectory = Path.Combine(outputBase, "ManagedHierarchy");
            CollectionAssert.Contains(directories, outputDirectory);
            Assert.AreEqual(0, pruneExcludedPaths.Count);
            Assert.IsTrue(outputScope.IsComplete);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 準備済み入力の対象folderinfoだけを同期入力へ渡し、無関係な物理ファイルを再走査で混入させない。
    /// </summary>
    [TestMethod]
    public void CreateLr2SongDbSyncInputFromPreparedSurface_UsesTargetFolderInfoOnly()
    {
        LibraryFileInitializationResult? initializationInput = null;
        Lr2SongDbSyncPreparedDataSurface? preparedInput = null;
        using var scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
        string packDirectory = Path.Combine(rootDirectory, "Pack");
        string songDirectory = Path.Combine(packDirectory, "Song");
        string unrelatedDirectory = Path.Combine(rootDirectory, "Other");
        Directory.CreateDirectory(songDirectory);
        Directory.CreateDirectory(unrelatedDirectory);
        string chartPath = Path.Combine(songDirectory, "chart.bms");
        string packFolderInfoPath = Path.Combine(packDirectory, "folderinfo.txt");
        string songTextPath = Path.Combine(songDirectory, "readme.txt");
        string unrelatedFolderInfoPath = Path.Combine(unrelatedDirectory, "folderinfo.txt");
        File.WriteAllText(chartPath, "#TITLE Test");
        File.WriteAllText(packFolderInfoPath, "#TITLE Pack", Encoding.GetEncoding("shift_jis"));
        File.WriteAllText(songTextPath, "notes", Encoding.UTF8);
        File.WriteAllText(unrelatedFolderInfoPath, "#TITLE Other", Encoding.GetEncoding("shift_jis"));
        var options = new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = true,
            LR2RootPath = string.Empty,
            LR2CustomFolderOutputBaseDir = string.Empty,
            LR2CustomFolderAdditionalOutputBaseDirs = [],
            LR2CustomFolderOutputBaseDirRootType = string.Empty,
            EnableDownloadLr2IrScoreAndDetectUnsent = false
        };
        var library = new TestBmsLibrary(scope.DatabaseScope,
            scope.SongDbPath,
            null,
            null,
            "test",
            () => options, settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
        {

            BmsCharts =
            [
                ChartTestValues.Empty() with {
                    Path = chartPath
                }
            ]
        };

        // Candidate production is outside this test. Supply the complete scan
        // directory surface and the prepared metadata surface, then verify only
        // that the target folderinfo is consumed.
        initializationInput = new(InvokeCaptureLr2SongDbSyncScanSurface(
            library,
            options,
            [rootDirectory],
            CreateCompleteLr2ScanSurface(
                [rootDirectory, packDirectory, songDirectory],
                [rootDirectory],
                [],
                [rootDirectory, packDirectory, songDirectory])));
        preparedInput = new Lr2SongDbSyncPreparedDataSurface(
                [],
                [],
                new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                CreateDirectoryEntryMap(rootDirectory, packDirectory, songDirectory),
                [packFolderInfoPath],
                CreateFileEntryMap(packFolderInfoPath),
                [packDirectory, songDirectory],
                discoveryComplete: true);

        Lr2SongDbSyncInput input = InvokeCreateLr2SongDbSyncInput(library, initializationInput, preparedInput);
        var folderInfoFilePaths = input.FolderInfoFilePaths.ToList();
        var textFileDirectories = input.TextFileDirectories.ToList();

        CollectionAssert.Contains(folderInfoFilePaths, packFolderInfoPath);
        CollectionAssert.DoesNotContain(folderInfoFilePaths, unrelatedFolderInfoPath);
        CollectionAssert.Contains(textFileDirectories, songDirectory);
        CollectionAssert.DoesNotContain(textFileDirectories, unrelatedDirectory);
    }

    /// <summary>
    /// 捕捉面では管理出力ディレクトリ内の定義を除き、外部の兄弟ディレクトリを残す。
    /// </summary>
    [TestMethod]
    public void CreateLr2SongDbSyncInputFromCapturedSurface_RejectsMissingManagedDefinitionsAfterExcludingManagedScope()
    {
        LibraryFileInitializationResult? initializationInput = null;
        Lr2SongDbSyncPreparedDataSurface? preparedInput = null;
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            testSettings.EnableDownloadLr2IrScoreAndDetectUnsent = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            PlaylistPersistenceRepository.EnsureSchema(scope.DatabaseScope, scope.SongDbPath);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string outputBase = Path.Combine(rootDirectory, "#BeMusicSeekerOutput");
            string outputDirectory = Path.Combine(outputBase, "ManagedTable");
            string managedPath = Path.Combine(outputDirectory, "0000.lr2folder");
            string externalPath = Path.Combine(outputDirectory, "external.lr2folder");
            string unmanagedSiblingDirectory = Path.Combine(outputBase, "ExternalTable");
            string unmanagedSiblingPath = Path.Combine(unmanagedSiblingDirectory, "external.lr2folder");
            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(unmanagedSiblingDirectory);
            File.WriteAllText(managedPath, "#TITLE Managed", Encoding.GetEncoding("shift_jis"));
            File.WriteAllText(externalPath, "#TITLE External", Encoding.GetEncoding("shift_jis"));
            File.WriteAllText(unmanagedSiblingPath, "#TITLE External Sibling", Encoding.GetEncoding("shift_jis"));
            testSettings.LR2CustomFolderOutputBaseDir = outputBase;
            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                setup.RunInTransaction(() =>
                {
                    setup.InsertOrReplace(new BMSTable
                    {
                        playlist_id = 9301,
                        name = "ManagedTable",
                        symbol = "MT",
                        Output_dir = "ManagedTable",
                        ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                            & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                            & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
                    }, typeof(LR2SongDBExtended.playlist));
                    setup.Execute(
                        "INSERT INTO playlist_entry (playlist_id, md5, title, folder, is_removed) VALUES (?, ?, ?, ?, ?);",
                        9301,
                        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                        "Active",
                        "Folder A",
                        0);
                });
            }
            var options = BmsLibraryOptionsSnapshot.CreateCurrent(testSettings);
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

                BmsCharts = []
            };

            initializationInput = new(InvokeCaptureLr2SongDbSyncScanSurface(
                library,
                options,
                [rootDirectory],
                CreateCompleteLr2ScanSurface(
                    [rootDirectory],
                    [rootDirectory, outputBase],
                    [managedPath, externalPath, unmanagedSiblingPath],
                    [rootDirectory, outputBase, outputDirectory, unmanagedSiblingDirectory])));

            Lr2SongDbSyncInput input = InvokeCreateLr2SongDbSyncInput(library, initializationInput, preparedInput);
            var lr2FolderFilePaths = (initializationInput.ScanSurface ?? throw new AssertFailedException("Missing captured surface.")).Lr2FolderFilePaths.ToList();
            var pruneDirectories = input.Lr2FolderPruneDirectories.ToList();

            CollectionAssert.DoesNotContain(lr2FolderFilePaths, managedPath);
            CollectionAssert.DoesNotContain(lr2FolderFilePaths, externalPath);
            CollectionAssert.Contains(lr2FolderFilePaths, unmanagedSiblingPath);
            CollectionAssert.Contains(pruneDirectories, outputBase);
            Assert.IsFalse(input.Lr2FolderFileDiscoveryComplete, "管理定義未提供を外部兄弟だけの完全入力へ読み替えません。");
            CollectionAssert.AreEqual(new[] { unmanagedSiblingPath }, input.Lr2FolderFilePaths.ToArray());
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 管理出力内でも受理済み準備で作った定義は入力へ戻し、同範囲の旧捕捉ファイルは残さない。
    /// </summary>
    [TestMethod]
    public void CreateLr2SongDbSyncInputFromPreparedSurface_IncludesPreparedManagedOutputFiles()
    {
        LibraryFileInitializationResult? initializationInput = null;
        Lr2SongDbSyncPreparedDataSurface? preparedInput = null;
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            testSettings.EnableDownloadLr2IrScoreAndDetectUnsent = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            PlaylistPersistenceRepository.EnsureSchema(scope.DatabaseScope, scope.SongDbPath);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string outputBase = Path.Combine(rootDirectory, "#BeMusicSeekerOutput");
            string outputDirectory = Path.Combine(outputBase, "ManagedTable");
            string managedPath = Path.Combine(outputDirectory, "0000.lr2folder");
            string externalPath = Path.Combine(outputDirectory, "external.lr2folder");
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllText(managedPath, "#TITLE Managed", Encoding.GetEncoding("shift_jis"));
            File.WriteAllText(externalPath, "#TITLE External", Encoding.GetEncoding("shift_jis"));
            testSettings.LR2CustomFolderOutputBaseDir = outputBase;
            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                setup.RunInTransaction(() =>
                {
                    setup.InsertOrReplace(new BMSTable
                    {
                        playlist_id = 9302,
                        name = "ManagedTable",
                        symbol = "MT",
                        Output_dir = "ManagedTable",
                        ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                            & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                            & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
                    }, typeof(LR2SongDBExtended.playlist));
                    setup.Execute(
                        "INSERT INTO playlist_entry (playlist_id, md5, title, folder, is_removed) VALUES (?, ?, ?, ?, ?);",
                        9302,
                        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                        "Active",
                        "Folder A",
                        0);
                });
            }
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

                BmsCharts = []
            };
            var options = BmsLibraryOptionsSnapshot.CreateCurrent(testSettings);
            initializationInput = new(InvokeCaptureLr2SongDbSyncScanSurface(
                library,
                options,
                [rootDirectory],
                CreateCompleteLr2ScanSurface(
                    [rootDirectory],
                    [rootDirectory, outputBase],
                    [managedPath, externalPath],
                    [rootDirectory, outputBase, outputDirectory])));
            preparedInput = CreatePreparedLr2FolderSurface(
                    outputDirectory,
                    managedPath,
                    CreateDirectoryEntryMap(rootDirectory, outputBase, outputDirectory),
                    managedDefinition: new Lr2FolderFileDefinition { Title = "Managed" });

            Lr2SongDbSyncInput input = InvokeCreateLr2SongDbSyncInput(library, initializationInput, preparedInput);
            var lr2FolderFilePaths = input.Lr2FolderFilePaths.ToList();

            CollectionAssert.Contains(lr2FolderFilePaths, managedPath);
            CollectionAssert.DoesNotContain(lr2FolderFilePaths, externalPath);
            Assert.IsTrue(input.Lr2FolderFileDiscoveryComplete);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 管理playlistが空なら捕捉済みの実定義を入力へ残し、出力baseだけを理由に除外しない。
    /// </summary>
    [TestMethod]
    public void CreateLr2SongDbSyncInputFromCapturedSurface_KeepsPhysicalLr2FoldersWhenNoManagedPlaylistScopeExists()
    {
        LibraryFileInitializationResult? initializationInput = null;
        Lr2SongDbSyncPreparedDataSurface? preparedInput = null;
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string outputBase = Path.Combine(rootDirectory, "#BeMusicSeekerOutput");
            string outputDirectory = Path.Combine(outputBase, "ManagedTable");
            string lr2FolderPath = Path.Combine(outputDirectory, "0000.lr2folder");
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllText(lr2FolderPath, "#TITLE Should Not Read", Encoding.GetEncoding("shift_jis"));
            testSettings.LR2CustomFolderOutputBaseDir = outputBase;
            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                setup.CreateTable<LR2SongDBExtended.playlist>();
            }
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

                BmsCharts = []
            };

            var options = BmsLibraryOptionsSnapshot.CreateCurrent(testSettings);
            initializationInput = new(InvokeCaptureLr2SongDbSyncScanSurface(
                library,
                options,
                [rootDirectory],
                CreateCompleteLr2ScanSurface(
                    [rootDirectory],
                    [rootDirectory, outputBase],
                    [lr2FolderPath],
                    [rootDirectory, outputBase, outputDirectory])));

            Lr2SongDbSyncInput input = InvokeCreateLr2SongDbSyncInput(library, initializationInput, preparedInput);
            var lr2FolderFilePaths = input.Lr2FolderFilePaths.ToList();

            CollectionAssert.Contains(lr2FolderFilePaths, lr2FolderPath);
            Assert.IsTrue(input.Lr2FolderFileDiscoveryComplete);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 管理playlistが空の場合、走査面にある定義と完全性を同期入力へ引き継ぐ。
    /// </summary>
    [TestMethod]
    public void CreateLr2SongDbSyncInput_KeepsScanSurfaceLr2FoldersWhenNoManagedPlaylistScopeExists()
    {
        LibraryFileInitializationResult? initializationInput = null;
        Lr2SongDbSyncPreparedDataSurface? preparedInput = null;
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string outputBase = Path.Combine(rootDirectory, "#BeMusicSeekerOutput");
            string outputDirectory = Path.Combine(outputBase, "ManagedTable");
            string lr2FolderPath = Path.Combine(outputDirectory, "0000.lr2folder");
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllText(lr2FolderPath, "#TITLE Should Not Read", Encoding.GetEncoding("shift_jis"));
            testSettings.LR2CustomFolderOutputBaseDir = outputBase;
            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                setup.CreateTable<LR2SongDBExtended.playlist>();
            }
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

                BmsCharts = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
            };
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);
            initializationInput = new(InvokeCaptureLr2SongDbSyncScanSurface(library, options, [rootDirectory], new SongTableFileCheckResult
            {
                Lr2ScanSurfaceAvailable = true,
                Lr2ScanNormalFolderDirectoryPaths = [rootDirectory],
                Lr2ScanNormalFolderDirectoryEntries = CreateDirectoryEntryMap(rootDirectory),
                Lr2ScanDirectoryEntries = CreateDirectoryEntryMap(rootDirectory, outputBase, outputDirectory),
                Lr2ScanFolderInfoFilePaths = [],
                Lr2ScanFolderInfoFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                Lr2ScanTextFileDirectories = [rootDirectory],
                Lr2ScanLr2FolderDiscoveryDirectories = [rootDirectory, outputBase],
                Lr2ScanLr2FolderFilePaths = [lr2FolderPath],
                Lr2ScanLr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [lr2FolderPath] = new RootFileEnumerationEntry(lr2FolderPath, timestamp)
                },
                Lr2ScanLr2FolderFileDiscoveryComplete = true
            }));

            Lr2SongDbSyncInput input = InvokeCreateLr2SongDbSyncInput(library, initializationInput, preparedInput);
            var lr2FolderFilePaths = input.Lr2FolderFilePaths.ToList();

            CollectionAssert.Contains(lr2FolderFilePaths, lr2FolderPath);
            Assert.IsTrue(input.Lr2FolderFileDiscoveryComplete);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

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
                null,
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
                    null, new OwnedChartCollectionTestSupport.TestFileMutationService(), null, exception => exception.Message);
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

    /// <summary>
    /// 難度未指定の現譜面は古い別譜面行を難度推測へ使わず、既存行を残して現在の難度を保存する。
    /// </summary>
    /// <remarks>実管理主体とSQLite保存を接続し、入力面の選択だけでは分からない反映と終端を確認する。</remarks>
    [TestMethod]
    public async Task QueueLr2SongDbSync_DefaultsDifficultyWithoutUsingStaleRowsAsAnchor()
    {
        LibraryFileInitializationResult? initializationInput = null;
        Lr2SongDbSyncPreparedDataSurface? preparedInput = null;
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string songDirectory = Path.Combine(rootDirectory, "Song");
            Directory.CreateDirectory(songDirectory);
            string chartPath = Path.Combine(songDirectory, "chart.bms");
            File.WriteAllText(chartPath, "#TITLE Undefined Difficulty\r\n#PLAYLEVEL 1\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFileSnapshot chartSnapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            ChartFile file = CreateSyncTestFile(chartPath, chartSnapshot);
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

                BmsCharts = [file]
            };
            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                setup.RunInTransaction(() =>
                {
                    setup.CreateTable<LR2SongDB.song>();
                    setup.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
                    setup.Execute(
                        "INSERT INTO song (path, folder, mode, karinotes, difficulty) VALUES (?, ?, ?, ?, ?);",
                        Path.Combine(songDirectory, "stale.bms"),
                        Lr2SongFolderParentNormalizer.ComputeDirectoryHash(songDirectory),
                        5,
                        0,
                        4);
                });
            }
            var options = BmsLibraryOptionsSnapshot.CreateCurrent(testSettings);
            initializationInput = new(InvokeCaptureLr2SongDbSyncScanSurface(
                library,
                options,
                [rootDirectory],
                CreateCompleteLr2ScanSurface(
                    [rootDirectory],
                    [rootDirectory],
                    [],
                    [rootDirectory, songDirectory])));

            await library.QueueLr2SongDbSyncAsync("test_difficulty_normalization", initializationResult: initializationInput, preparedSurface: preparedInput);

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(1, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(1, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM song WHERE path LIKE '%stale.bms';"));
            Assert.AreEqual(2, verify.ExecuteScalar<int>("SELECT difficulty FROM song WHERE path = ?;", chartPath));
            LR2SongDBExtended.lr2_song_db_sync_status row = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.AreEqual("Completed", row.status);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 同じ保存入力で完了済みなら再同期の受理版を進めず、状態再公開だけを行う。不要な再実行を検出する。
    /// </summary>
    /// <remarks>実管理主体とSQLite保存を接続し、入力面の選択だけでは分からない反映と終端を確認する。</remarks>
    [TestMethod]
    public async Task QueueLr2SongDbSync_DoesNotQueueSecondSyncWhenCompletedStatusIsCurrent()
    {
        LibraryFileInitializationResult? initializationInput = null;
        Lr2SongDbSyncPreparedDataSurface? preparedInput = null;
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string songDirectory = Path.Combine(rootDirectory, "Song");
            Directory.CreateDirectory(songDirectory);
            string chartPath = Path.Combine(songDirectory, "chart.bms");
            File.WriteAllText(chartPath, "#TITLE Noop Sync\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFileSnapshot chartSnapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = chartPath
            };
            file = file with { Md5 = chartSnapshot.Md5 };
            file = file with { Sha256 = chartSnapshot.Sha256 };
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

                BmsCharts = [file]
            };
            var options = BmsLibraryOptionsSnapshot.CreateCurrent(testSettings);
            initializationInput = new(InvokeCaptureLr2SongDbSyncScanSurface(
                library,
                options,
                [rootDirectory],
                CreateCompleteLr2ScanSurface(
                    [rootDirectory],
                    [rootDirectory],
                    [],
                    [rootDirectory, songDirectory])));

            Lr2SongDbSyncStatusSnapshot first = await library.QueueLr2SongDbSyncAsync("test_first", initializationResult: initializationInput, preparedSurface: preparedInput);
            int requestedVersionAfterFirst = library.Lr2SongDbSyncRequestedVersion;
            int completedVersionAfterFirst = library.Lr2SongDbSyncCompletedVersion;
            int statusVersionAfterFirst = library.Lr2SongDbSyncStatusVersion;
            Lr2SongDbSyncStatusSnapshot second = await library.QueueLr2SongDbSyncAsync("test_second", initializationResult: initializationInput, preparedSurface: preparedInput);

            Assert.AreEqual(Lr2SongDbSyncStatusKind.Completed, first.Status);
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Completed, second.Status);
            Assert.AreEqual(1, library.Lr2SongDbSyncCompletedVersion);
            Assert.AreEqual(requestedVersionAfterFirst, library.Lr2SongDbSyncRequestedVersion);
            Assert.AreEqual(completedVersionAfterFirst, library.Lr2SongDbSyncCompletedVersion);
            Assert.AreEqual(statusVersionAfterFirst + 1, library.Lr2SongDbSyncStatusVersion);
            Assert.IsFalse(library.Lr2SongDbSyncRunning);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            LR2SongDBExtended.lr2_song_db_sync_status row = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.AreEqual("Completed", row.status);
            Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, row.stage);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 別ライブラリへコピーした完了済みDBが現在の入力に一致すれば、新規同期を始めず保存済み完了状態を採用する。
    /// </summary>
    /// <remarks>実管理主体とSQLite保存を接続し、入力面の選択だけでは分からない反映と終端を確認する。</remarks>
    [TestMethod]
    public async Task QueueLr2SongDbSync_DoesNotQueueWhenCompletedCopiedSongDbIsCurrent()
    {
        LibraryFileInitializationResult? initializationInput = null;
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string songDirectory = Path.Combine(rootDirectory, "Song");
            Directory.CreateDirectory(songDirectory);
            string chartPath = Path.Combine(songDirectory, "chart.bms");
            File.WriteAllText(chartPath, "#TITLE Copied Noop Sync\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFileSnapshot chartSnapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            ChartFile file = CreateSyncTestFile(chartPath, chartSnapshot);
            var firstLibrary = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

                BmsCharts = [file]
            };
            var options = BmsLibraryOptionsSnapshot.CreateCurrent(testSettings);
            initializationInput = new(InvokeCaptureLr2SongDbSyncScanSurface(
                firstLibrary,
                options,
                [rootDirectory],
                CreateCompleteLr2ScanSurface(
                    [rootDirectory],
                    [rootDirectory],
                    [],
                    [rootDirectory, songDirectory])));

            Lr2SongDbSyncStatusSnapshot first = await firstLibrary.QueueLr2SongDbSyncAsync("test_first_for_copy", initializationResult: initializationInput);
            string copiedDirectory = Path.Combine(scope.DirectoryPath, "Copied");
            Directory.CreateDirectory(copiedDirectory);
            string copiedSongDbPath = Path.Combine(copiedDirectory, "song.db");
            File.Copy(scope.SongDbPath, copiedSongDbPath, overwrite: true);
            var copiedLibrary = new TestBmsLibrary(firstLibrary.DatabaseScope, copiedSongDbPath, settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

                BmsCharts = [file]
            };
            initializationInput = new(InvokeCaptureLr2SongDbSyncScanSurface(
                copiedLibrary,
                options,
                [rootDirectory],
                CreateCompleteLr2ScanSurface(
                    [rootDirectory],
                    [rootDirectory],
                    [],
                    [rootDirectory, songDirectory])));

            Lr2SongDbSyncStatusSnapshot second = await copiedLibrary.QueueLr2SongDbSyncAsync("test_copied_song_db", initializationResult: initializationInput);

            Assert.AreEqual(Lr2SongDbSyncStatusKind.Completed, first.Status);
            Assert.AreEqual(1, firstLibrary.Lr2SongDbSyncCompletedVersion);
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Completed, second.Status);
            Assert.AreEqual(0, copiedLibrary.Lr2SongDbSyncRequestedVersion);
            Assert.AreEqual(0, copiedLibrary.Lr2SongDbSyncCompletedVersion);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(copiedLibrary.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), copiedSongDbPath).OpenSongDbReadOnly();
            LR2SongDBExtended.lr2_song_db_sync_status row = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.AreEqual("Completed", row.status);
            Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, row.stage);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// rootも譜面も空の受理済み世代は件数0で保存・完了し、実行中や失敗の状態を残さない。
    /// </summary>
    /// <remarks>実管理主体とSQLite保存を接続し、入力面の選択だけでは分からない反映と終端を確認する。</remarks>
    [TestMethod]
    public async Task QueueLr2SongDbSync_WithNoRootsCompletesEmptyGeneration()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: TestBmsFactory.CreateRootSettings([], testSettings))
            {

            };

            await library.QueueLr2SongDbSyncAsync("test_no_roots");

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            LR2SongDBExtended.lr2_song_db_sync_status row = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.IsNotNull(row);
            Assert.AreEqual("Completed", row.status);
            Assert.AreEqual(string.Empty, row.last_error);
            Assert.AreEqual(0, row.processed_cursor);
            Assert.AreEqual(0, row.total_count);
            Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, row.stage);
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().ToList().Count);
            Assert.AreEqual(1, library.Lr2SongDbSyncRequestedVersion);
            Assert.AreEqual(1, library.Lr2SongDbSyncCompletedVersion);
            Assert.AreEqual(0, library.Lr2SongDbSyncFailedVersion);
            Assert.IsFalse(library.Lr2SongDbSyncRunning);
            Assert.AreEqual(string.Empty, library.Lr2SongDbSyncFailureMessage);
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Completed, library.GetLr2SongDbSyncStatusSnapshot().Status);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 検索rootが空でも所持譜面のsong行を解析・保存し、root件数だけで同期全体を省略しない。
    /// </summary>
    /// <remarks>実管理主体とSQLite保存を接続し、入力面の選択だけでは分からない反映と終端を確認する。</remarks>
    [TestMethod]
    public async Task QueueLr2SongDbSync_WithNoRootsStillSyncsSongRows()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string songDirectory = Path.Combine(scope.DirectoryPath, "Loose");
            Directory.CreateDirectory(songDirectory);
            string chartPath = Path.Combine(songDirectory, "chart.bms");
            File.WriteAllText(chartPath, "#TITLE test");
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = chartPath
            };
            file = file with { Md5 = "dddddddddddddddddddddddddddddddd" };
            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                setup.RunInTransaction(() =>
                {
                    setup.CreateTable<LR2SongDB.song>();
                    setup.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
                });
            }
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: TestBmsFactory.CreateRootSettings([], testSettings))
            {

                BmsCharts = [file]
            };

            await library.QueueLr2SongDbSyncAsync("test_no_roots_with_song");

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            LR2SongDBExtended.lr2_song_db_sync_status row = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.IsNotNull(row);
            Assert.AreEqual("Completed", row.status);
            Assert.AreEqual(string.Empty, row.last_error);
            Assert.AreEqual(1, row.processed_cursor);
            Assert.AreEqual(1, row.total_count);
            Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, row.stage);
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().ToList().Count);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(songDirectory), verify.ExecuteScalar<string>("SELECT folder FROM song WHERE path = ?;", chartPath));
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>Catalogで捕捉した確定Big5・UTF-8を実同期へ渡し、DB・現在一覧・利用者列・健康と無視状態を保持します。</summary>
    /// <remarks>内容差し替え時は最初の非採用に加え、通常DB読込みから次の明示同期まで旧指定の非復活を確認します。採否条件網羅と単回読取りはservice系列へ分担します。</remarks>
    [TestMethod]
    [DataRow("big5", false)]
    [DataRow("utf-8", false)]
    [DataRow("big5", true)]
    public async Task QueueLr2SongDbSync_PreservesFixedEncodingAndExpiresOnlyChangedContent(string encoding, bool changeContent)
    {
        using var scope = TestDatabaseScope.Create();
        TestBmsLibrary? library = null;
        TestBmsLibrary? reloadedLibrary = null;
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string path = Path.Combine(scope.DirectoryPath, "fixed-sync.bms");
            const string title = "中文標題";
            const string subtitle = "副標題";
            const string artist = "作者";
            const string subartist = "編曲";
            const string genre = "音樂";
            File.WriteAllText(path, $"#PLAYER 1\r\n#TITLE {title}\r\n#SUBTITLE {subtitle}\r\n#ARTIST {artist}\r\n#SUBARTIST {subartist}\r\n#GENRE {genre}\r\n#BPM 120\r\n#WAV01 missing.wav\r\n#00111:01\r\n", Encoding.GetEncoding(encoding));
            ChartFileSnapshot original = ChartFileContentReader.ReadSnapshot(path);
            var gateway = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath);
            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                setup.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureMaintenanceSchema(setup);
                setup.InsertOrReplace(new LR2SongDB.song
                {
                    path = path,
                    hash = original.Md5,
                    title = title,
                    subtitle = subtitle,
                    artist = artist,
                    subartist = subartist,
                    genre = genre,
                    favorite = 7,
                    tag = "利用者タグ",
                    adddate = 1234
                });
                setup.InsertOrReplace(new LR2SongDBExtended.maintenance
                {
                    path = path,
                    hash = original.Md5.ToUpperInvariant(),
                    encoding = encoding,
                    is_encoding_fixed = true,
                    wav_files_defined = 99,
                    wav_files_existing = 88,
                    is_files_warning_ignored = true
                });
            }

            ChartFile LoadCurrent()
            {
                var initialization = new BmsLibraryInitializationService();
                var options = new BmsLibraryOptionsSnapshot { OperationModeLR2DB = true };
                SongTableLoadResult songs = initialization.LoadSongTable(gateway, options,
                    null, new OwnedChartCollectionTestSupport.TestFileMutationService(), null, exception => exception.Message);
                MaintenanceTableHydrationResult maintenance = initialization.LoadMaintenanceTable(gateway, options);
                return ChartFileProjection.WithMaintenance(songs.LoadedFiles.Single(), maintenance.MaintenanceMap[path]);
            }

            library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: TestBmsFactory.CreateRootSettings([], testSettings))
            { BmsCharts = [LoadCurrent()], BmsonCharts = [] };
            if (changeContent)
            {
                File.WriteAllText(path, "#PLAYER 1\r\n#TITLE 更新題名\r\n#SUBTITLE 更新副題\r\n#ARTIST 更新作者\r\n#SUBARTIST 更新編曲\r\n#GENRE 更新ジャンル\r\n#BPM 120\r\n#WAV01 missing.wav\r\n#00111:01\r\n", new UTF8Encoding(true));
            }

            Lr2SongDbSyncStatusSnapshot result = await library.QueueLr2SongDbSyncAsync("fixed-encoding-sync", force: true);
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Completed, result.Status);
            string expectedTitle = changeContent ? "更新題名" : title;
            string expectedSubtitle = changeContent ? "更新副題" : subtitle;
            string expectedArtist = changeContent ? "更新作者" : artist;
            string expectedSubartist = changeContent ? "更新編曲" : subartist;
            string expectedGenre = changeContent ? "更新ジャンル" : genre;
            using (LR2SongDBExtended read = gateway.OpenSongDbReadOnly())
            {
                LR2SongDB.song saved = read.Table<LR2SongDB.song>().Single();
                Assert.AreEqual(expectedTitle, saved.title);
                Assert.AreEqual(expectedSubtitle, saved.subtitle);
                Assert.AreEqual(expectedArtist, saved.artist);
                Assert.AreEqual(expectedSubartist, saved.subartist);
                Assert.AreEqual(expectedGenre, saved.genre);
                Assert.AreEqual(7, saved.favorite);
                Assert.AreEqual("利用者タグ", saved.tag);
                Assert.AreEqual(1234, saved.adddate);
                LR2SongDBExtended.maintenance maintenance = read.Table<LR2SongDBExtended.maintenance>().Single();
                Assert.AreEqual(ChartFileContentReader.ReadSnapshot(path).Md5, maintenance.hash, true);
                Assert.AreEqual(changeContent ? null : encoding, maintenance.encoding);
                Assert.AreEqual(!changeContent, maintenance.is_encoding_fixed);
                Assert.AreEqual(99, maintenance.wav_files_defined);
                Assert.AreEqual(88, maintenance.wav_files_existing);
                Assert.IsTrue(maintenance.is_files_warning_ignored);
            }

            ChartFile current = changeContent ? LoadCurrent() : library.BmsCharts.Single();
            Assert.AreEqual(expectedTitle, current.RawTitle);
            Assert.AreEqual(expectedSubtitle, current.RawSubtitle);
            Assert.AreEqual(expectedArtist, current.RawArtist);
            Assert.AreEqual(expectedSubartist, current.Subartist);
            Assert.AreEqual(expectedGenre, current.Genre);
            Assert.AreEqual(expectedTitle + " " + expectedSubtitle, current.Title);
            Assert.AreEqual(expectedArtist + " " + expectedSubartist, current.Artist);
            Assert.AreEqual(changeContent ? null : encoding, current.EncodingName);
            Assert.AreEqual(!changeContent, current.ResourceHealthMaintenanceSnapshot.EncodingFixed);
            Assert.AreEqual(99, current.ResourceHealthMaintenanceSnapshot.WavFilesDefined);
            Assert.AreEqual(88, current.ResourceHealthMaintenanceSnapshot.WavFilesExisting);
            Assert.IsTrue(current.ResourceHealthWarningsIgnored);
            if (changeContent)
            {
                library.RequestShutdown("fixed-encoding-first-sync-finished");
                reloadedLibrary = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: TestBmsFactory.CreateRootSettings([], testSettings))
                { BmsCharts = [current], BmsonCharts = [] };
                Lr2SongDbSyncStatusSnapshot next = await reloadedLibrary.QueueLr2SongDbSyncAsync("fixed-encoding-after-reload", force: true);
                Assert.AreEqual(Lr2SongDbSyncStatusKind.Completed, next.Status);
                using LR2SongDBExtended read = gateway.OpenSongDbReadOnly();
                Assert.AreEqual("更新題名", read.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", path));
                Assert.IsNull(read.ExecuteScalar<string>("SELECT encoding FROM maintenance WHERE path = ?;", path));
                Assert.AreEqual(0, read.ExecuteScalar<int>("SELECT is_encoding_fixed FROM maintenance WHERE path = ?;", path));
                Assert.AreEqual("更新題名 更新副題", reloadedLibrary.BmsCharts.Single().Title);
            }
        }
        finally
        {
            reloadedLibrary?.RequestShutdown("fixed-encoding-reload-test-finished");
            library?.RequestShutdown("fixed-encoding-sync-test-finished");
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 末尾に不完全なバイトを持つ読取可能な譜面からメタデータを保存し、既存NULL列を無条件に保持しない。
    /// </summary>
    /// <remarks>実管理主体とSQLite保存を接続し、入力面の選択だけでは分からない反映と終端を確認する。</remarks>
    [TestMethod]
    public async Task QueueLr2SongDbSync_ParsesReadableSnapshotContentInsteadOfPreservingNullSongColumns()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string songDirectory = Path.Combine(scope.DirectoryPath, "ReadableSnapshot");
            Directory.CreateDirectory(songDirectory);
            string chartPath = Path.Combine(songDirectory, "chart.bms");
            byte[] directiveBytes = Encoding.ASCII.GetBytes(
                "#TITLE Parsed Readable Snapshot\r\n#ARTIST Parsed Artist\r\n#PLAYLEVEL 7\r\n#RANK 3\r\n#00118:01\r\n");
            File.WriteAllBytes(chartPath, [.. directiveBytes, 0x82]);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            ChartFile file = CreateSyncTestFile(chartPath, snapshot);
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: TestBmsFactory.CreateRootSettings([], testSettings))
            {

                BmsCharts = [file]
            };
            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                setup.RunInTransaction(() =>
                {
                    setup.CreateTable<LR2SongDB.song>();
                    setup.Execute(
                        "INSERT INTO song (hash, path, level, difficulty, mode, judge) VALUES (?, ?, NULL, NULL, NULL, NULL);",
                        "dddddddddddddddddddddddddddddddd",
                        chartPath);
                });
            }

            await library.QueueLr2SongDbSyncAsync("test_readable_snapshot_song_parse");

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            Assert.AreEqual("Parsed Readable Snapshot", verify.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual("Parsed Artist", verify.ExecuteScalar<string>("SELECT artist FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(7, verify.ExecuteScalar<int>("SELECT COALESCE(level, -999) FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(2, verify.ExecuteScalar<int>("SELECT COALESCE(difficulty, -999) FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(7, verify.ExecuteScalar<int>("SELECT COALESCE(mode, -999) FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(3, verify.ExecuteScalar<int>("SELECT COALESCE(judge, -999) FROM song WHERE path = ?;", chartPath));
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// chart_info未保存の譜面は同期内部で解析してsong列へ反映し、別backfillの予約へ回さず同じ同期を完了する。
    /// </summary>
    /// <remarks>実管理主体とSQLite保存を接続し、入力面の選択だけでは分からない反映と終端を確認する。</remarks>
    [TestMethod]
    public async Task QueueLr2SongDbSync_BuildsMissingChartInfoInsideSongRows()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string songDirectory = Path.Combine(scope.DirectoryPath, "ChartInfoBuild");
            Directory.CreateDirectory(songDirectory);
            string chartPath = Path.Combine(songDirectory, "chart.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n#TITLE generated chart info\r\n#ARTIST tester\r\n#BPM 150\r\n#PLAYLEVEL 12\r\n#RANK 3\r\n#WAV01 kick.wav\r\n#00111:01\r\n");
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            ChartFile file = CreateSyncTestFile(chartPath, snapshot);
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: TestBmsFactory.CreateRootSettings([], testSettings))
            {

                BmsCharts = [file]
            };
            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                setup.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
            }

            await library.QueueLr2SongDbSyncAsync("test_chart_info_build");

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ? AND md5 = ?;", snapshot.Sha256, snapshot.Md5));
            Assert.AreEqual(12, verify.ExecuteScalar<int>("SELECT level FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(1, verify.ExecuteScalar<int>("SELECT karinotes FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(0, library.ChartInfoBackfillRequestedVersion);
            Assert.AreEqual(library.ChartInfoBackfillRequestedVersion, library.ChartInfoBackfillCompletedVersion);
            Assert.IsFalse(library.ChartInfoBackfillRunning);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 旧parser版のchart_infoを現入力から更新し、保存済みの古いレベルをsongへ流用しない。
    /// </summary>
    /// <remarks>実管理主体とSQLite保存を接続し、入力面の選択だけでは分からない反映と終端を確認する。</remarks>
    [TestMethod]
    public async Task QueueLr2SongDbSync_RebuildsStaleChartInfoInsideSongRows()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string songDirectory = Path.Combine(scope.DirectoryPath, "StaleChartInfoBuild");
            Directory.CreateDirectory(songDirectory);
            string chartPath = Path.Combine(songDirectory, "chart.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n#TITLE stale chart info\r\n#BPM 130\r\n#PLAYLEVEL 10\r\n#WAV01 kick.wav\r\n#00111:01\r\n");
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            ChartFile file = CreateSyncTestFile(chartPath, snapshot);
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: TestBmsFactory.CreateRootSettings([], testSettings))
            {

                BmsCharts = [file]
            };
            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                setup.RunInTransaction(() =>
                {
                    setup.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
                    BmsLibraryDbGateway.EnsureChartInfoSchema(setup);
                    setup.InsertOrReplace(ChartInfoStorageMapping.ToStorage(CreateChartInfo(snapshot.Sha256, snapshot.Md5, level: 99, parserVersion: BmsLibraryDbGateway.CurrentChartInfoParserVersion - 1)), typeof(LR2SongDBExtended.chart_info));
                });
            }

            await library.QueueLr2SongDbSyncAsync("test_stale_chart_info_build");

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            BeMusicSeeker.Models.ChartDetails chartInfo = verify.Query<BeMusicSeeker.Models.ChartDetails>("SELECT * FROM chart_info WHERE sha256 = ?;", snapshot.Sha256).Single();
            Assert.AreEqual(BmsLibraryDbGateway.CurrentChartInfoParserVersion, chartInfo.parser_version);
            Assert.AreEqual(10, verify.ExecuteScalar<int>("SELECT level FROM song WHERE path = ?;", chartPath));
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 同期解析した長すぎるリソースパス警告を現在の所持譜面へ反映し、既存のリソース健全性件数を保持する。
    /// </summary>
    /// <remarks>実管理主体とSQLite保存を接続し、入力面の選択だけでは分からない反映と終端を確認する。</remarks>
    [TestMethod]
    public async Task QueueLr2SongDbSync_ProjectsLr2CompatibilityWarningsToLiveRows()
    {

        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string songDirectory = Path.Combine(scope.DirectoryPath, "LiveLr2Compatibility");
            Directory.CreateDirectory(songDirectory);
            string chartPath = Path.Combine(songDirectory, "chart.bms");
            WriteBasicBms(chartPath, "live lr2 compatibility", Path.ChangeExtension(CreateLr2TooLongResourcePath(), ".flac"));
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            ChartFile file = CreateSyncTestFile(chartPath, snapshot);
            file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(new LR2SongDBExtended.maintenance { path = file.Path, hash = file.Md5, wav_files_defined = 99, wav_files_existing = 88 }));
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: TestBmsFactory.CreateRootSettings([], testSettings))
            {

                BmsCharts = [file]
            };

            await library.QueueLr2SongDbSyncAsync("test_live_lr2_compatibility");

            ChartFile current = library.BmsCharts.Single();
            Assert.IsFalse(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.Lr2ResourcePathTooLong));
            Assert.IsTrue(current.Warnings.Any(warning => warning.Kind == ChartWarningKind.Lr2ResourcePathTooLong));
            Assert.AreEqual(99, current.ResourceHealthMaintenanceSnapshot.WavFilesDefined);
            Assert.AreEqual(88, current.ResourceHealthMaintenanceSnapshot.WavFilesExisting);
            int flags = current.ResourceHealthMaintenanceSnapshot.Lr2WarningFlags.GetValueOrDefault();
            Assert.IsTrue((flags & (int)Lr2CompatibilityWarningFlags.ResourcePathTooLong) != 0);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// CP932非対応の譜面パスでも同期を完了し、folder親情報を未設定にして利用者列を保持する。通常と強制の双方で解析失敗扱いへ変えない。
    /// </summary>
    /// <remarks>実管理主体とSQLite保存を接続し、入力面の選択だけでは分からない反映と終端を確認する。</remarks>
    [TestMethod]
    public async Task QueueLr2SongDbSync_CompletesWhenChartDirectoryPathIsUnsupported()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string normalDirectory = Path.Combine(rootDirectory, "Normal");
            string unsupportedDirectory = Path.Combine(rootDirectory, "Unsupported_ê");
            string normalChartPath = Path.Combine(normalDirectory, "normal.bms");
            string unsupportedChartPath = Path.Combine(unsupportedDirectory, "unsupported.bms");
            ChartFile normalFile = CreateReadableSyncChart(normalChartPath, "normal sibling");
            ChartFile unsupportedFile = CreateReadableSyncChart(unsupportedChartPath, "unsupported path");
            Assert.IsTrue(Lr2CompatibilityEvaluator.EvaluateChartPath(unsupportedChartPath).WarningFlags
                .HasFlag(Lr2CompatibilityWarningFlags.PathEncodingUnsupported));

            (TestBmsLibrary library, LibraryFileInitializationResult initializationInput) = CreateFolderProjectionSyncLibrary(
                scope,
                rootDirectory,
                [normalFile, unsupportedFile],
                [],
                [rootDirectory, normalDirectory, unsupportedDirectory]);
            const int normalFolderAddDate = 654321;
            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                setup.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(normalDirectory),
                    type = 1,
                    adddate = normalFolderAddDate
                }, typeof(LR2SongDB.folder));
            }

            long parseFailureCountBefore;
            using (LR2SongDBExtended setup = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly())
            {
                parseFailureCountBefore = setup.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info_parse_failure;");
            }

            await library.QueueLr2SongDbSyncAsync("unsupported_chart_directory", initializationResult: initializationInput);

            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly())
            {
                LR2SongDBExtended.lr2_song_db_sync_status status = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(
                    Lr2SongDbSyncStatusService.DefaultStatusName);
                Assert.IsNotNull(status);
                Assert.AreEqual("Completed", status.status);

                LR2SongDB.folder[] folders = [.. verify.Table<LR2SongDB.folder>()];
                Assert.IsTrue(folders.Any(folder => folder.path == ToFolderPath(rootDirectory)));
                Assert.IsTrue(folders.Any(folder => folder.path == ToFolderPath(normalDirectory)));
                Assert.IsFalse(folders.Any(folder => folder.path == ToFolderPath(unsupportedDirectory)));
                Assert.AreEqual(normalFolderAddDate, folders.Single(folder => folder.path == ToFolderPath(normalDirectory)).adddate);

                LR2SongDB.song song = verify.Query<LR2SongDB.song>(
                    "SELECT * FROM song WHERE path = ?;",
                    unsupportedChartPath).Single();
                Assert.AreEqual(unsupportedChartPath, song.path);
                Assert.IsNull(song.folder);
                Assert.IsNull(song.parent);
                Assert.AreEqual(7, song.favorite);
                Assert.AreEqual("keep-unsupported-tag", song.tag);
                Assert.AreEqual(123456, song.adddate);
                Assert.AreEqual(parseFailureCountBefore, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info_parse_failure;"));
            }

            Assert.IsTrue(library.BmsCharts.Single(current => current.Path == unsupportedChartPath).Warnings.Any(warning => warning.Kind == ChartWarningKind.Lr2PathEncodingUnsupported));

            await library.QueueLr2SongDbSyncAsync("unsupported_chart_directory_force", initializationResult: initializationInput, force: true);

            using LR2SongDBExtended forcedVerify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            LR2SongDBExtended.lr2_song_db_sync_status forcedStatus = forcedVerify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(
                Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.IsNotNull(forcedStatus);
            Assert.AreEqual("Completed", forcedStatus.status);
            LR2SongDB.song forcedSong = forcedVerify.Query<LR2SongDB.song>(
                "SELECT * FROM song WHERE path = ?;",
                unsupportedChartPath).Single();
            Assert.AreEqual(7, forcedSong.favorite);
            Assert.AreEqual("keep-unsupported-tag", forcedSong.tag);
            Assert.AreEqual(123456, forcedSong.adddate);
            Assert.IsNull(forcedSong.folder);
            Assert.IsNull(forcedSong.parent);
            Assert.AreEqual(normalFolderAddDate, forcedVerify.Table<LR2SongDB.folder>()
                .Single(folder => folder.path == ToFolderPath(normalDirectory)).adddate);
            Assert.AreEqual(parseFailureCountBefore, forcedVerify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info_parse_failure;"));
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 非対応文字を持つ定義名だけを除き、同じディレクトリの有効定義と親行を保存して完了する。
    /// </summary>
    /// <remarks>実管理主体とSQLite保存を接続し、入力面の選択だけでは分からない反映と終端を確認する。</remarks>
    [TestMethod]
    public async Task QueueLr2SongDbSync_SkipsUnsupportedLr2FolderFileNameAndContinues()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string normalDirectory = Path.Combine(rootDirectory, "Normal");
            Directory.CreateDirectory(normalDirectory);
            string normalFolderFilePath = Path.Combine(normalDirectory, "normal.lr2folder");
            string unsupportedFolderFilePath = Path.Combine(normalDirectory, "custom_ê.lr2folder");
            File.WriteAllText(normalFolderFilePath, "#TITLE Normal", Encoding.ASCII);
            File.WriteAllText(unsupportedFolderFilePath, "#TITLE Unsupported filename", Encoding.ASCII);
            Assert.IsTrue(Lr2CompatibilityEvaluator.EvaluateChartPath(unsupportedFolderFilePath).WarningFlags
                .HasFlag(Lr2CompatibilityWarningFlags.PathEncodingUnsupported));

            (TestBmsLibrary library, LibraryFileInitializationResult initializationInput) = CreateFolderProjectionSyncLibrary(
                scope,
                rootDirectory,
                [],
                [normalFolderFilePath, unsupportedFolderFilePath],
                [rootDirectory, normalDirectory]);

            await library.QueueLr2SongDbSyncAsync("unsupported_lr2folder_filename", initializationResult: initializationInput);

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            LR2SongDBExtended.lr2_song_db_sync_status status = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(
                Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.IsNotNull(status);
            Assert.AreEqual("Completed", status.status);
            LR2SongDB.folder[] folders = [.. verify.Table<LR2SongDB.folder>()];
            Assert.IsTrue(folders.Any(folder => folder.path == ToFolderPath(rootDirectory)));
            Assert.IsTrue(folders.Any(folder => folder.path == ToFolderPath(normalDirectory)));
            Assert.IsTrue(folders.Any(folder => folder.path == normalFolderFilePath));
            Assert.IsFalse(folders.Any(folder => folder.path == unsupportedFolderFilePath));
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 非対応文字を持つ親ディレクトリと定義を除き、有効な兄弟の同期を完了する。
    /// </summary>
    /// <remarks>実管理主体とSQLite保存を接続し、入力面の選択だけでは分からない反映と終端を確認する。</remarks>
    [TestMethod]
    public async Task QueueLr2SongDbSync_SkipsUnsupportedLr2FolderParentAndContinues()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string normalDirectory = Path.Combine(rootDirectory, "Normal");
            string unsupportedDirectory = Path.Combine(rootDirectory, "Unsupported_ê");
            Directory.CreateDirectory(normalDirectory);
            Directory.CreateDirectory(unsupportedDirectory);
            string normalFolderFilePath = Path.Combine(normalDirectory, "normal.lr2folder");
            string unsupportedFolderFilePath = Path.Combine(unsupportedDirectory, "custom.lr2folder");
            File.WriteAllText(normalFolderFilePath, "#TITLE Normal", Encoding.ASCII);
            File.WriteAllText(unsupportedFolderFilePath, "#TITLE Unsupported parent", Encoding.ASCII);
            Assert.IsTrue(Lr2CompatibilityEvaluator.EvaluateChartPath(unsupportedFolderFilePath).WarningFlags
                .HasFlag(Lr2CompatibilityWarningFlags.PathEncodingUnsupported));

            (TestBmsLibrary library, LibraryFileInitializationResult initializationInput) = CreateFolderProjectionSyncLibrary(
                scope,
                rootDirectory,
                [],
                [normalFolderFilePath, unsupportedFolderFilePath],
                [rootDirectory, normalDirectory, unsupportedDirectory]);

            await library.QueueLr2SongDbSyncAsync("unsupported_lr2folder_parent", initializationResult: initializationInput);

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            LR2SongDBExtended.lr2_song_db_sync_status status = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(
                Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.IsNotNull(status);
            Assert.AreEqual("Completed", status.status);
            LR2SongDB.folder[] folders = [.. verify.Table<LR2SongDB.folder>()];
            Assert.IsTrue(folders.Any(folder => folder.path == ToFolderPath(rootDirectory)));
            Assert.IsTrue(folders.Any(folder => folder.path == ToFolderPath(normalDirectory)));
            Assert.IsTrue(folders.Any(folder => folder.path == normalFolderFilePath));
            Assert.IsFalse(folders.Any(folder => folder.path == ToFolderPath(unsupportedDirectory)));
            Assert.IsFalse(folders.Any(folder => folder.path == unsupportedFolderFilePath));
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 譜面0件でも捕捉した外部定義のタイトル・command・maxを保存し、古い定義行を除いて同期を完了する。
    /// </summary>
    /// <remarks>実管理主体とSQLite保存を接続し、入力面の選択だけでは分からない反映と終端を確認する。</remarks>
    [TestMethod]
    public async Task QueueLr2SongDbSync_SyncsCapturedLr2FolderWithoutCharts()
    {
        LibraryFileInitializationResult? initializationInput = null;
        Lr2SongDbSyncPreparedDataSurface? preparedInput = null;
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string nestedDirectory = Path.Combine(rootDirectory, "Custom");
            Directory.CreateDirectory(nestedDirectory);
            string lr2FolderPath = Path.Combine(nestedDirectory, "table.lr2folder");
            File.WriteAllText(lr2FolderPath, "#TITLE 入れ子表\r\n#COMMAND song.level = 12\r\n#MAXTRACKS 64", Encoding.GetEncoding("shift_jis"));
            string outsideLr2FolderPath = Path.Combine(scope.DirectoryPath, "outside.lr2folder");
            var options = BmsLibraryOptionsSnapshot.CreateCurrent(testSettings);
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

                BmsCharts = []
            };
            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                setup.RunInTransaction(() =>
                {
                    setup.InsertOrReplace(new LR2SongDB.folder
                    {
                        path = Path.Combine(rootDirectory, "stale.lr2folder"),
                        type = 2,
                        date = 1
                    }, typeof(LR2SongDB.folder));
                    setup.InsertOrReplace(new LR2SongDB.folder
                    {
                        path = outsideLr2FolderPath,
                        type = 2,
                        date = 1
                    }, typeof(LR2SongDB.folder));
                });
            }

            initializationInput = new(InvokeCaptureLr2SongDbSyncScanSurface(
                library,
                options,
                [rootDirectory],
                CreateCompleteLr2ScanSurface(
                    [rootDirectory],
                    [rootDirectory],
                    [lr2FolderPath],
                    [rootDirectory, nestedDirectory])));

            await library.QueueLr2SongDbSyncAsync("test_lr2folder_only", initializationResult: initializationInput, preparedSurface: preparedInput);

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            LR2SongDBExtended.lr2_song_db_sync_status row = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.IsNotNull(row);
            Assert.AreEqual("Completed", row.status);
            Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, row.stage);
            Assert.AreEqual(row.total_count, row.processed_cursor);
            LR2SongDB.folder lr2Folder = verify.Table<LR2SongDB.folder>().ToList().Single(folder => folder.path == lr2FolderPath);
            Assert.AreEqual(2, lr2Folder.type);
            Assert.AreEqual("入れ子表", lr2Folder.title);
            Assert.AreEqual("song.level = 12", lr2Folder.command);
            Assert.AreEqual(64, lr2Folder.max);
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().ToList().Count(folder => folder.path == Path.Combine(rootDirectory, "stale.lr2folder")));
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().ToList().Count(folder => folder.path == outsideLr2FolderPath));
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 検索root外の通常出力baseから捕捉した外部定義を保存し、同範囲の消失定義を除く。
    /// </summary>
    /// <remarks>実管理主体とSQLite保存を接続し、入力面の選択だけでは分からない反映と終端を確認する。</remarks>
    [TestMethod]
    public async Task QueueLr2SongDbSync_SyncsCapturedLr2FolderFromCustomFolderOutputBase()
    {
        LibraryFileInitializationResult? initializationInput = null;
        Lr2SongDbSyncPreparedDataSurface? preparedInput = null;
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            string outputBase = Path.Combine(scope.DirectoryPath, "CustomOutput");
            Directory.CreateDirectory(outputBase);
            string lr2FolderPath = Path.Combine(outputBase, "0000.lr2folder");
            string stalePath = Path.Combine(outputBase, "stale.lr2folder");
            File.WriteAllText(lr2FolderPath, "#TITLE Output Folder", Encoding.GetEncoding("shift_jis"));
            testSettings.LR2CustomFolderOutputBaseDir = outputBase;
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

                BmsCharts = []
            };
            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                setup.InsertOrReplace(new LR2SongDB.folder
                {
                    path = stalePath,
                    type = 2,
                    title = "Keep Stale",
                    date = 1
                }, typeof(LR2SongDB.folder));
            }

            var options = BmsLibraryOptionsSnapshot.CreateCurrent(testSettings);
            initializationInput = new(InvokeCaptureLr2SongDbSyncScanSurface(
                library,
                options,
                [rootDirectory],
                CreateCompleteLr2ScanSurface(
                    [rootDirectory],
                    [rootDirectory, outputBase],
                    [lr2FolderPath],
                    [rootDirectory, outputBase])));

            await library.QueueLr2SongDbSyncAsync("test_custom_folder_output_base", initializationResult: initializationInput, preparedSurface: preparedInput);

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            LR2SongDB.folder lr2Folder = verify.Table<LR2SongDB.folder>().ToList().Single(folder => folder.path == lr2FolderPath);
            Assert.AreEqual(2, lr2Folder.type);
            Assert.AreEqual("Output Folder", lr2Folder.title);
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().ToList().Count(folder => folder.path == stalePath));
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// root型出力baseの定義はROOT親として保存し、通常出力の親計算へ混ぜない。
    /// </summary>
    /// <remarks>実管理主体とSQLite保存を接続し、入力面の選択だけでは分からない反映と終端を確認する。</remarks>
    [TestMethod]
    public async Task QueueLr2SongDbSync_SyncsCapturedRootCustomFolderOutputAsRootRow()
    {
        LibraryFileInitializationResult? initializationInput = null;
        Lr2SongDbSyncPreparedDataSurface? preparedInput = null;
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            string outputBase = Path.Combine(scope.DirectoryPath, "RootCustomOutput");
            Directory.CreateDirectory(outputBase);
            string lr2FolderPath = Path.Combine(outputBase, "root.lr2folder");
            File.WriteAllText(lr2FolderPath, "#TITLE Root Output", Encoding.GetEncoding("shift_jis"));
            testSettings.LR2CustomFolderOutputBaseDirRootType = outputBase;
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

                BmsCharts = []
            };

            var options = BmsLibraryOptionsSnapshot.CreateCurrent(testSettings);
            initializationInput = new(InvokeCaptureLr2SongDbSyncScanSurface(
                library,
                options,
                [rootDirectory],
                CreateCompleteLr2ScanSurface(
                    [rootDirectory],
                    [rootDirectory, outputBase],
                    [lr2FolderPath],
                    [rootDirectory, outputBase])));

            await library.QueueLr2SongDbSyncAsync("test_root_custom_folder_output_base", initializationResult: initializationInput, preparedSurface: preparedInput);

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            LR2SongDB.folder lr2Folder = verify.Table<LR2SongDB.folder>().ToList().Single(folder => folder.path == lr2FolderPath);
            Assert.AreEqual(2, lr2Folder.type);
            Assert.AreEqual("Root Output", lr2Folder.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, lr2Folder.parent);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 検索root配下の通常出力ではbase・表ディレクトリ・定義の三階層を保存し、各親CRCを正しく接続する。
    /// </summary>
    /// <remarks>実管理主体とSQLite保存を接続し、入力面の選択だけでは分からない反映と終端を確認する。</remarks>
    [TestMethod]
    public async Task QueueLr2SongDbSync_GeneratesNormalCustomFolderOutputBaseParentRow()
    {
        LibraryFileInitializationResult? initializationInput = null;
        Lr2SongDbSyncPreparedDataSurface? preparedInput = null;
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string bmsRoot = Path.Combine(scope.DirectoryPath, "BMS");
            string outputBase = Path.Combine(bmsRoot, "#BeMusicSeeker");
            string tableDirectory = Path.Combine(outputBase, "Table");
            Directory.CreateDirectory(tableDirectory);
            string lr2FolderPath = Path.Combine(tableDirectory, "0000.lr2folder");
            File.WriteAllText(lr2FolderPath, "#TITLE Normal Output", Encoding.GetEncoding("shift_jis"));
            testSettings.LR2CustomFolderOutputBaseDir = outputBase;
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
                LR2CustomFolderOutputBaseDir = outputBase,
                LR2CustomFolderAdditionalOutputBaseDirs = [],
                LR2CustomFolderOutputBaseDirRootType = string.Empty
            };
            var library = new TestBmsLibrary(scope.DatabaseScope,
                scope.SongDbPath,
                null,
                null,
                null,
                () => options,
                TestBmsFactory.AvailableEverythingBridge, settings: TestBmsFactory.CreateRootSettings([bmsRoot], testSettings))
            {

                BmsCharts = []
            };
            initializationInput = new(InvokeCaptureLr2SongDbSyncScanSurface(library, options, [bmsRoot], new SongTableFileCheckResult
            {
                Lr2ScanSurfaceAvailable = true,
                Lr2ScanDirectoryEntries = CreateDirectoryEntryMap(bmsRoot, outputBase, tableDirectory),
                Lr2ScanNormalFolderDirectoryEntries = CreateDirectoryEntryMap(bmsRoot, outputBase, tableDirectory),
                Lr2ScanLr2FolderDiscoveryDirectories = [bmsRoot, outputBase],
                Lr2ScanFolderInfoFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                Lr2ScanLr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [lr2FolderPath] = new RootFileEnumerationEntry(lr2FolderPath, File.GetLastWriteTimeUtc(lr2FolderPath))
                },
                Lr2ScanLr2FolderFileDiscoveryComplete = true
            }));

            await library.QueueLr2SongDbSyncAsync("test_normal_custom_folder_output_parent", initializationResult: initializationInput, preparedSurface: preparedInput);

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            var rows = verify.Table<LR2SongDB.folder>().ToList();
            string rowSummary = string.Join(" | ", rows.Select(row => $"{row.type}:{row.parent}:{row.path}").Take(20));
            LR2SongDB.folder? outputBaseRow = rows.SingleOrDefault(folder => folder.path == Lr2FolderPath.ToFolderPath(outputBase));
            Assert.IsNotNull(outputBaseRow, rowSummary);
            LR2SongDB.folder outputBaseFolder = outputBaseRow!;
            Assert.AreEqual(1, outputBaseFolder.type);
            Assert.AreEqual("#BeMusicSeeker", outputBaseFolder.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, outputBaseFolder.parent, rowSummary);
            LR2SongDB.folder tableRow = rows.Single(folder => folder.path == Lr2FolderPath.ToFolderPath(tableDirectory));
            Assert.AreEqual(1, tableRow.type);
            Assert.AreEqual("Table", tableRow.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(outputBase), tableRow.parent);
            LR2SongDB.folder lr2Folder = rows.Single(folder => folder.path == lr2FolderPath);
            Assert.AreEqual(2, lr2Folder.type);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(tableDirectory), lr2Folder.parent);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 受理済み準備面の管理定義と外部兄弟を同じ権限で直接同期し、別予約を作らず親行と完了状態まで保存する。
    /// </summary>
    /// <remarks>実管理主体とSQLite保存を接続し、入力面の選択だけでは分からない反映と終端を確認する。</remarks>
    [TestMethod]
    public async Task QueueLr2SongDbSync_DirectPreparedInputSyncsManagedFolderAndSibling()
    {
        LibraryFileInitializationResult? initializationInput = null;
        Lr2SongDbSyncPreparedDataSurface? preparedInput = null;
        using var scope = TestDatabaseScope.Create();
        Task? scheduledSync = null;
        TestBmsLibrary? directLibrary = null;
        TestBmsPlaylist? directPlaylist = null;
        try
        {
            PlaylistPersistenceRepository.EnsureSchema(scope.DatabaseScope, scope.SongDbPath);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            string additionalBase = Path.Combine(scope.DirectoryPath, "Additional");
            string managedDirectory = Path.Combine(additionalBase, "ManagedTable");
            string unmanagedSiblingDirectory = Path.Combine(additionalBase, "ExternalTable");
            string managedPath = Path.Combine(managedDirectory, "0000.lr2folder");
            string unmanagedSiblingPath = Path.Combine(unmanagedSiblingDirectory, "external.lr2folder");
            Directory.CreateDirectory(managedDirectory);
            Directory.CreateDirectory(unmanagedSiblingDirectory);
            File.WriteAllText(unmanagedSiblingPath, "#TITLE External Sibling", Encoding.GetEncoding("shift_jis"));
            testSettings.LR2CustomFolderAdditionalOutputBaseDirs =
                CustomFolderOutputBaseRegistry.SerializeBaseDirectories([additionalBase]);
            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                setup.InsertOrReplace(new BMSTable
                {
                    playlist_id = 9401,
                    name = "Managed",
                    symbol = "MT",
                    Output_dir = "ManagedTable",
                    custom_folder_output_base_name = "Additional",
                    ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                        & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                        & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
                }, typeof(LR2SongDBExtended.playlist));
            }
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
                LR2CustomFolderAdditionalOutputBaseDirs = [additionalBase]
            };
            TestBmsLibrary library = directLibrary = new TestBmsLibrary(scope.DatabaseScope,
                scope.SongDbPath,
                getLR2Config: null,
                _lr2ScoreDB: null,
                startupRequiredFileScanReason: null,
                optionsSnapshotProvider: () => options, settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

                BmsCharts = []
            };
            library.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                scheduledSync = Task.Run(work);
                return true;
            };

            initializationInput = new(InvokeCaptureLr2SongDbSyncScanSurface(
                library,
                options,
                [rootDirectory],
                CreateCompleteLr2ScanSurface(
                    [rootDirectory],
                    [rootDirectory, additionalBase],
                    [managedPath, unmanagedSiblingPath],
                    [rootDirectory, additionalBase, managedDirectory, unmanagedSiblingDirectory])));
            var outputSettings = new CustomFolderOutputSettingsSnapshot
            {
                OperationModeLR2DB = true,
                LR2CustomFolderAdditionalOutputBaseDirs = testSettings.LR2CustomFolderAdditionalOutputBaseDirs
            };
            TestBmsPlaylist playlist = directPlaylist = new TestBmsPlaylist(new BmsPlaylistLibraryBindings(library), scope.SongDbPath,
                () => outputSettings, null, settings: testSettings)
            { BMSTables = new ObservableCollection<BMSTable>(new PlaylistPersistenceRepository(scope.DatabaseScope, scope.SongDbPath).LoadPlaylistHeaders()) };
            var runtime = new BmsLr2SongDbSyncWorkflowRuntime(() => library, () => playlist, () => true);
            using (LibraryFileMutationLease lease = library.Lr2Synchronization.TryBeginMutation("test_direct_prepared", showMessage: false))
            {
                Assert.IsNotNull(lease);
                Assert.IsTrue(playlist.TryEnterPlaylistMutation(out IDisposable playlistLease));
                using IDisposable playlistOperation = playlistLease;
                using LibraryFileMutationCapability playlistCapability = playlist.CreatePlaylistMutationCapability(playlistLease);
                preparedInput = runtime.PrepareGeneratedDataUnderLease(library, "test_direct_prepared", lease, false, playlistCapability: playlistCapability);
            }
            Assert.IsNotNull(preparedInput.ManagedFolderDefinitions);
            Assert.IsTrue(preparedInput.ManagedFolderDefinitions.ContainsKey(managedPath));

            await library.QueueLr2SongDbSyncAsync("test_additional_output_base_external_sync", force: true,
                initializationResult: initializationInput, preparedSurface: preparedInput);
            Assert.IsNull(scheduledSync, "受理済み出力の必須同期は同じ権限で直接awaitし、別の起動予約を作りません。");

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            var rows = verify.Table<LR2SongDB.folder>().ToList();
            string rowSummary = string.Join(" | ", rows.Select(row => $"{row.type}:{row.parent}:{row.path}").Take(20));
            LR2SongDB.folder? additionalBaseRow = rows.SingleOrDefault(row => row.path == Lr2FolderPath.ToFolderPath(additionalBase));
            Assert.IsNotNull(additionalBaseRow, rowSummary);
            LR2SongDB.folder additionalBaseFolder = additionalBaseRow!;
            Assert.AreEqual(1, additionalBaseFolder.type);
            Assert.AreEqual("Additional", additionalBaseFolder.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, additionalBaseFolder.parent, rowSummary);
            LR2SongDB.folder? unmanagedSiblingDirectoryRow = rows.SingleOrDefault(row => row.path == Lr2FolderPath.ToFolderPath(unmanagedSiblingDirectory));
            Assert.IsNotNull(unmanagedSiblingDirectoryRow, rowSummary);
            LR2SongDB.folder unmanagedSiblingDirectoryRowValue = unmanagedSiblingDirectoryRow!;
            Assert.AreEqual(1, unmanagedSiblingDirectoryRowValue.type);
            Assert.AreEqual("ExternalTable", unmanagedSiblingDirectoryRowValue.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(additionalBase), unmanagedSiblingDirectoryRowValue.parent);
            LR2SongDB.folder? unmanagedSiblingRow = rows.SingleOrDefault(row => row.path == unmanagedSiblingPath);
            Assert.IsNotNull(unmanagedSiblingRow, rowSummary);
            LR2SongDB.folder unmanagedSiblingFile = unmanagedSiblingRow!;
            Assert.AreEqual(2, unmanagedSiblingFile.type);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(unmanagedSiblingDirectory), unmanagedSiblingFile.parent);
            LR2SongDB.folder? managedRow = rows.SingleOrDefault(row => row.path == managedPath);
            Assert.IsNotNull(managedRow, rowSummary);
            LR2SongDB.folder managedFile = managedRow!;
            Assert.AreEqual(2, managedFile.type);
            Assert.AreEqual("Managed ALL", managedFile.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(managedDirectory), managedFile.parent);
            LR2SongDBExtended.lr2_song_db_sync_status status = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.IsNotNull(status);
            Assert.AreEqual("Completed", status.status);
        }
        finally
        {
            try
            {
                if (scheduledSync != null) { await scheduledSync; }
            }
            finally
            {
                directPlaylist?.RequestShutdown("direct-prepared-fixture");
                directLibrary?.RequestShutdown("direct-prepared-fixture");
                ResetTouchedSettings(testSettings);
            }
        }
    }

    /// <summary>
    /// 追加出力baseの登録解除後も旧baseの外部定義行は残し、設定変更による無関係な削除を防ぐ。
    /// </summary>
    /// <remarks>実管理主体とSQLite保存を接続し、入力面の選択だけでは分からない反映と終端を確認する。</remarks>
    [TestMethod]
    public async Task SyncExternalLr2FolderRowsForCustomFolderOutputBaseChange_PreservesRemovedAdditionalBaseRows()
    {
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string oldAdditionalBase = Path.Combine(scope.DirectoryPath, "OldAdditional");
            string oldExternalPath = Path.Combine(oldAdditionalBase, "ExternalTable", "external.lr2folder");
            Directory.CreateDirectory(Path.GetDirectoryName(oldExternalPath)!);
            File.WriteAllText(oldExternalPath, "#TITLE Old External", Encoding.GetEncoding("shift_jis"));
            using (LR2SongDBExtended setup = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath))
            {
                setup.RunInTransaction(() =>
                {
                    setup.CreateTable<LR2SongDB.folder>();
                    setup.InsertOrReplace(new LR2SongDB.folder
                    {
                        path = oldExternalPath,
                        type = 2,
                        title = "Old External",
                        date = 1
                    }, typeof(LR2SongDB.folder));
                });
            }
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: TestBmsFactory.CreateRootSettings([], testSettings))
            {

                BmsCharts = []
            };

            await library.Lr2Synchronization.SyncExternalLr2FolderRowsForCustomFolderOutputBaseChangeAsync("test_removed_additional_output_base_preserve");

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(1, verify.Table<LR2SongDB.folder>().Count(row => row.path == oldExternalPath));
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 検索root外のroot型出力では表ディレクトリをROOTへ接続し、定義をそのディレクトリの子として保存する。
    /// </summary>
    /// <remarks>実管理主体とSQLite保存を接続し、入力面の選択だけでは分からない反映と終端を確認する。</remarks>
    [TestMethod]
    public async Task QueueLr2SongDbSync_GeneratesRootCustomFolderOutputParentRow()
    {
        LibraryFileInitializationResult? initializationInput = null;
        Lr2SongDbSyncPreparedDataSurface? preparedInput = null;
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string bmsRoot = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(bmsRoot);
            string outputBase = Path.Combine(scope.DirectoryPath, "RootCustomOutput");
            string tableDirectory = Path.Combine(outputBase, "Table");
            Directory.CreateDirectory(tableDirectory);
            string lr2FolderPath = Path.Combine(tableDirectory, "0000.lr2folder");
            File.WriteAllText(lr2FolderPath, "#TITLE Root Output", Encoding.GetEncoding("shift_jis"));
            testSettings.LR2CustomFolderOutputBaseDirRootType = outputBase;
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: TestBmsFactory.CreateRootSettings([bmsRoot], testSettings))
            {

                BmsCharts = []
            };

            var options = BmsLibraryOptionsSnapshot.CreateCurrent(testSettings);
            initializationInput = new(InvokeCaptureLr2SongDbSyncScanSurface(
                library,
                options,
                [bmsRoot],
                CreateCompleteLr2ScanSurface(
                    [bmsRoot],
                    [bmsRoot, outputBase],
                    [lr2FolderPath],
                    [bmsRoot, outputBase, tableDirectory])));

            await library.QueueLr2SongDbSyncAsync("test_root_custom_folder_output_parent", initializationResult: initializationInput, preparedSurface: preparedInput);

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            var rows = verify.Table<LR2SongDB.folder>().ToList();
            string rowSummary = string.Join(" | ", rows.Select(row => $"{row.type}:{row.parent}:{row.path}").Take(20));
            LR2SongDB.folder parentRow = rows.Single(folder => folder.path == Lr2FolderPath.ToFolderPath(tableDirectory));
            Assert.AreEqual(1, parentRow.type);
            Assert.AreEqual("Table", parentRow.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, parentRow.parent, rowSummary);
            Assert.IsTrue(parentRow.date.GetValueOrDefault() > 0);
            LR2SongDB.folder lr2Folder = verify.Table<LR2SongDB.folder>().ToList().Single(folder => folder.path == lr2FolderPath);
            Assert.AreEqual(2, lr2Folder.type);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(tableDirectory), lr2Folder.parent);
            LR2SongDBExtended.lr2_song_db_sync_status status = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.IsNotNull(status);
            Assert.AreEqual("Completed", status.status);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 有効な組込Favorite定義はLR2基準の相対パス・ROOT親で保存し、外部定義の絶対パス扱いへ変えない。
    /// </summary>
    /// <remarks>実管理主体とSQLite保存を接続し、入力面の選択だけでは分からない反映と終端を確認する。</remarks>
    [TestMethod]
    public async Task QueueLr2SongDbSync_SyncsCapturedEnabledLr2BuiltinCustomFolderAsRelativeRootRow()
    {
        LibraryFileInitializationResult? initializationInput = null;
        Lr2SongDbSyncPreparedDataSurface? preparedInput = null;
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string bmsRoot = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(bmsRoot);
            string lr2Root = Path.Combine(scope.DirectoryPath, "LR2beta3");
            string customFolderDirectory = Path.Combine(lr2Root, "LR2files", "CustomFolder");
            Directory.CreateDirectory(customFolderDirectory);
            string lr2FolderPath = Path.Combine(customFolderDirectory, "favorite.lr2folder");
            File.WriteAllText(lr2FolderPath, "#TITLE Favorite", Encoding.GetEncoding("shift_jis"));
            testSettings.LR2RootPath = lr2Root;
            LR2Config config = CreateLr2Config(lr2Root, customFolderMask: 0x2, titleFlashHours: 24, bmsRoot);
            var configOwner = new BeMusicSeeker.Models.LR2.Lr2ConfigOwner(config);
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, configOwner, settings: TestBmsFactory.CreateRootSettings([bmsRoot], testSettings))
            {

                BmsCharts = []
            };

            var options = BmsLibraryOptionsSnapshot.CreateCurrent(testSettings);
            initializationInput = new(InvokeCaptureLr2SongDbSyncScanSurface(
                library,
                options,
                [bmsRoot],
                CreateCompleteLr2ScanSurface(
                    [bmsRoot],
                    [bmsRoot, customFolderDirectory],
                    [lr2FolderPath],
                    [bmsRoot, customFolderDirectory])));

            await library.QueueLr2SongDbSyncAsync("test_lr2_builtin_custom_folder", initializationResult: initializationInput, preparedSurface: preparedInput);

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            LR2SongDB.folder lr2Folder = verify.Table<LR2SongDB.folder>().ToList().Single(folder => folder.path == @"LR2files\CustomFolder\favorite.lr2folder");
            Assert.AreEqual(2, lr2Folder.type);
            Assert.AreEqual("Favorite", lr2Folder.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, lr2Folder.parent);
            LR2SongDBExtended.lr2_song_db_sync_status status = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.IsNotNull(status);
            Assert.AreEqual("Completed", status.status);
            Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, status.stage);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 組込RANDOMの二段カテゴリはfolderinfoのタイトルと相対親CRCを保存し、その下の定義を正しく接続する。
    /// </summary>
    /// <remarks>実管理主体とSQLite保存を接続し、入力面の選択だけでは分からない反映と終端を確認する。</remarks>
    [TestMethod]
    public async Task QueueLr2SongDbSync_GeneratesBuiltinCustomFolderCategoryRow()
    {
        LibraryFileInitializationResult? initializationInput = null;
        Lr2SongDbSyncPreparedDataSurface? preparedInput = null;
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string bmsRoot = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(bmsRoot);
            string lr2Root = Path.Combine(scope.DirectoryPath, "LR2beta3");
            string randomDirectory = Path.Combine(lr2Root, "LR2files", "CustomFolder", "RANDOM");
            string subDirectory = Path.Combine(randomDirectory, "Sub");
            Directory.CreateDirectory(subDirectory);
            File.WriteAllText(Path.Combine(randomDirectory, "folderinfo.txt"), "#TITLE Random Folder Info", Encoding.GetEncoding("shift_jis"));
            File.WriteAllText(Path.Combine(subDirectory, "folderinfo.txt"), "#TITLE Sub Folder Info", Encoding.GetEncoding("shift_jis"));
            string lr2FolderPath = Path.Combine(subDirectory, "random.lr2folder");
            File.WriteAllText(lr2FolderPath, "#TITLE Random", Encoding.GetEncoding("shift_jis"));
            testSettings.LR2RootPath = lr2Root;
            LR2Config config = CreateLr2Config(lr2Root, customFolderMask: 0x1, titleFlashHours: 24, bmsRoot);
            var configOwner = new BeMusicSeeker.Models.LR2.Lr2ConfigOwner(config);
            var options = BmsLibraryOptionsSnapshot.CreateCurrent(testSettings);
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, configOwner, null, null, () => options, settings: TestBmsFactory.CreateRootSettings([bmsRoot], testSettings))
            {

                BmsCharts = []
            };

            initializationInput = new(InvokeCaptureLr2SongDbSyncScanSurface(
                library,
                options,
                [bmsRoot],
                CreateCompleteLr2ScanSurface(
                    [bmsRoot],
                    [bmsRoot, Path.Combine(lr2Root, "LR2files", "CustomFolder")],
                    [lr2FolderPath],
                    [
                        bmsRoot,
                        Path.Combine(lr2Root, "LR2files", "CustomFolder"),
                        randomDirectory,
                        subDirectory
                    ],
                    [
                        Path.Combine(randomDirectory, "folderinfo.txt"),
                        Path.Combine(subDirectory, "folderinfo.txt")
                    ])));

            await library.QueueLr2SongDbSyncAsync("test_builtin_custom_folder_category", initializationResult: initializationInput, preparedSurface: preparedInput);

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            LR2SongDB.folder category = verify.Table<LR2SongDB.folder>().ToList().Single(folder => folder.path == @"LR2files\CustomFolder\RANDOM\");
            Assert.AreEqual(2, category.type);
            Assert.AreEqual("Random Folder Info", category.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, category.parent);
            Assert.IsTrue(category.date.GetValueOrDefault() > 0);
            LR2SongDB.folder subCategory = verify.Table<LR2SongDB.folder>().ToList().Single(folder => folder.path == @"LR2files\CustomFolder\RANDOM\Sub\");
            Assert.AreEqual(2, subCategory.type);
            Assert.AreEqual("Sub Folder Info", subCategory.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(@"LR2files\CustomFolder\RANDOM"), subCategory.parent);
            Assert.IsTrue(subCategory.date.GetValueOrDefault() > 0);
            LR2SongDB.folder lr2Folder = verify.Table<LR2SongDB.folder>().ToList().Single(folder => folder.path == @"LR2files\CustomFolder\RANDOM\Sub\random.lr2folder");
            Assert.AreEqual(2, lr2Folder.type);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(@"LR2files\CustomFolder\RANDOM\Sub"), lr2Folder.parent);
            LR2SongDBExtended.lr2_song_db_sync_status status = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.IsNotNull(status);
            Assert.AreEqual("Completed", status.status);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// customfolder maskが0でも準備済みcourse定義は外部互換type6として保存し、通常組込定義の有効判定で捨てない。
    /// </summary>
    /// <remarks>実管理主体とSQLite保存を接続し、入力面の選択だけでは分からない反映と終端を確認する。</remarks>
    [TestMethod]
    public async Task QueueLr2SongDbSync_SyncsPreparedBuiltinCourseFolderAsTypeSixRegardlessOfCustomFolderMask()
    {
        LibraryFileInitializationResult? initializationInput = null;
        Lr2SongDbSyncPreparedDataSurface? preparedInput = null;
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string bmsRoot = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(bmsRoot);
            string lr2Root = Path.Combine(scope.DirectoryPath, "LR2beta3");
            string customFolderDirectory = Path.Combine(lr2Root, "LR2files", "CustomFolder");
            Directory.CreateDirectory(customFolderDirectory);
            string lr2FolderPath = Path.Combine(customFolderDirectory, "course1.lr2folder");
            File.WriteAllText(lr2FolderPath, "#TITLE Course", Encoding.GetEncoding("shift_jis"));
            testSettings.LR2RootPath = lr2Root;
            LR2Config config = CreateLr2Config(lr2Root, customFolderMask: 0, titleFlashHours: 24, bmsRoot);
            var configOwner = new BeMusicSeeker.Models.LR2.Lr2ConfigOwner(config);
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, configOwner, settings: TestBmsFactory.CreateRootSettings([bmsRoot], testSettings))
            {

                BmsCharts = []
            };

            var options = BmsLibraryOptionsSnapshot.CreateCurrent(testSettings);
            initializationInput = new(InvokeCaptureLr2SongDbSyncScanSurface(
                library,
                options,
                [bmsRoot],
                CreateCompleteLr2ScanSurface(
                    [bmsRoot],
                    [bmsRoot, customFolderDirectory],
                    [lr2FolderPath],
                    [bmsRoot, lr2Root, customFolderDirectory])));
            preparedInput = CreatePreparedLr2FolderSurface(
                    lr2Root,
                    lr2FolderPath,
                    CreateDirectoryEntryMap(bmsRoot, lr2Root, customFolderDirectory));
            await library.QueueLr2SongDbSyncAsync("test_lr2_builtin_course_folder", initializationResult: initializationInput, preparedSurface: preparedInput);

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            LR2SongDB.folder lr2Folder = verify.Table<LR2SongDB.folder>().ToList().Single(folder => folder.path == @"LR2files\CustomFolder\course1.lr2folder");
            Assert.AreEqual(6, lr2Folder.type);
            Assert.AreEqual("Course", lr2Folder.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, lr2Folder.parent);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 明示した準備済みNewSong定義をmask0でもtype3・ROOT親で保存し、通常の外部定義へ誤分類したり除外したりしない。
    /// </summary>
    /// <remarks>実管理主体とSQLite保存を接続し、入力面の選択だけでは分からない反映と終端を確認する。</remarks>
    [TestMethod]
    public async Task QueueLr2SongDbSync_SavesExplicitPreparedNewSongWithBuiltinTypeAndRootParentDespiteMaskZero()
    {
        LibraryFileInitializationResult? initializationInput = null;
        Lr2SongDbSyncPreparedDataSurface? preparedInput = null;
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string bmsRoot = Path.Combine(scope.DirectoryPath, "BMS");
            string packDirectory = Path.Combine(bmsRoot, "Pack");
            Directory.CreateDirectory(packDirectory);
            string chartPath = Path.Combine(packDirectory, "chart.bms");
            File.WriteAllText(chartPath, "#TITLE Recent\r\n#ARTIST Artist\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            ChartFile file = CreateSyncTestFile(chartPath, snapshot);

            string lr2Root = Path.Combine(scope.DirectoryPath, "LR2beta3");
            string customFolderDirectory = Path.Combine(lr2Root, "LR2files", "CustomFolder");
            Directory.CreateDirectory(customFolderDirectory);
            string lr2FolderPath = Path.Combine(customFolderDirectory, "newsong.lr2folder");
            File.WriteAllText(lr2FolderPath, "#TITLE New Song", Encoding.GetEncoding("shift_jis"));
            testSettings.LR2RootPath = lr2Root;
            LR2Config config = CreateLr2Config(lr2Root, customFolderMask: 0, titleFlashHours: 24, bmsRoot);
            var configOwner = new BeMusicSeeker.Models.LR2.Lr2ConfigOwner(config);
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, configOwner, settings: TestBmsFactory.CreateRootSettings([bmsRoot], testSettings))
            {

                BmsCharts = [file]
            };

            var options = BmsLibraryOptionsSnapshot.CreateCurrent(testSettings);
            initializationInput = new(InvokeCaptureLr2SongDbSyncScanSurface(
                library,
                options,
                [bmsRoot],
                CreateCompleteLr2ScanSurface(
                    [bmsRoot],
                    [bmsRoot, customFolderDirectory],
                    [lr2FolderPath],
                    [bmsRoot, packDirectory, lr2Root, customFolderDirectory])));
            preparedInput = CreatePreparedLr2FolderSurface(
                    lr2Root,
                    lr2FolderPath,
                    CreateDirectoryEntryMap(bmsRoot, packDirectory, lr2Root, customFolderDirectory));
            await library.QueueLr2SongDbSyncAsync("test_lr2_builtin_newsong_folder", initializationResult: initializationInput, preparedSurface: preparedInput);

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            LR2SongDB.folder lr2Folder = verify.Table<LR2SongDB.folder>().ToList().Single(folder => folder.path == @"LR2files\CustomFolder\newsong.lr2folder");
            Assert.AreEqual(3, lr2Folder.type);
            Assert.AreEqual("New Song", lr2Folder.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, lr2Folder.parent);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    /// <summary>
    /// 通常rootで捕捉したRIVAL定義は絶対パスと所在親CRCで保存し、組込相対定義へ誤分類しない。
    /// </summary>
    /// <remarks>実管理主体とSQLite保存を接続し、入力面の選択だけでは分からない反映と終端を確認する。</remarks>
    [TestMethod]
    public async Task QueueLr2SongDbSync_SyncsCapturedRivalFolderFromNormalScanRootAsExternalFolder()
    {
        LibraryFileInitializationResult? initializationInput = null;
        Lr2SongDbSyncPreparedDataSurface? preparedInput = null;
        using var scope = TestDatabaseScope.Create();
        try
        {
            testSettings.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings(testSettings);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string rivalDirectory = Path.Combine(rootDirectory, "__RIVAL__");
            Directory.CreateDirectory(rivalDirectory);
            string lr2FolderPath = Path.Combine(rivalDirectory, "rival.lr2folder");
            File.WriteAllText(lr2FolderPath, "#TITLE Rival External", Encoding.GetEncoding("shift_jis"));
            var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
            {

                BmsCharts = []
            };

            var options = BmsLibraryOptionsSnapshot.CreateCurrent(testSettings);
            initializationInput = new(InvokeCaptureLr2SongDbSyncScanSurface(
                library,
                options,
                [rootDirectory],
                CreateCompleteLr2ScanSurface(
                    [rootDirectory],
                    [rootDirectory],
                    [lr2FolderPath],
                    [rootDirectory, rivalDirectory])));

            await library.QueueLr2SongDbSyncAsync("test_external_rival_folder", initializationResult: initializationInput, preparedSurface: preparedInput);

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(scope.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), scope.SongDbPath).OpenSongDbReadOnly();
            LR2SongDB.folder lr2Folder = verify.Table<LR2SongDB.folder>().ToList().Single(folder => folder.path == lr2FolderPath);
            Assert.AreEqual(2, lr2Folder.type);
            Assert.AreEqual("Rival External", lr2Folder.title);
            Assert.AreEqual(
                Lr2SongFolderParentNormalizer.ComputeDirectoryHash(rivalDirectory),
                lr2Folder.parent);
            LR2SongDBExtended.lr2_song_db_sync_status status = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.IsNotNull(status);
            Assert.AreEqual("Completed", status.status);
            Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, status.stage);
        }
        finally
        {
            ResetTouchedSettings(testSettings);
        }
    }

    private static T InvokeWithMutationCapability<T>(
        BMSLibrary library,
        Func<LibraryFileMutationCapability, T> action, bool playlistOperation = false)
    {
        BMSLibrary.Lr2SynchronizationOwner owner = GetLr2SynchronizationOwner(library);
        ChartFileOperationSynchronizer admission = playlistOperation ? owner.PlaylistOperationAdmission : owner.OperationAdmission;
        Assert.IsTrue(admission.TryEnter(out IDisposable lease));
        using (lease)
        using (LibraryFileMutationCapability capability = admission.CreateMutationCapability(lease))
        { return action(capability); }
    }

    // 実準備を明示barrierで保持し、Busy観測後も受付と開始済みTaskの終端を所有する。
    private sealed class HeldLr2Operation : IAsyncDisposable
    {
        private readonly BMSLibrary library;
        private readonly ManualResetEventSlim release;
        private readonly Task<Lr2SongDbSyncStatusSnapshot> operation;

        private HeldLr2Operation(BMSLibrary library, ManualResetEventSlim release, Task<Lr2SongDbSyncStatusSnapshot> operation)
        { this.library = library; this.release = release; this.operation = operation; }

        internal static async Task<HeldLr2Operation> StartAsync(BMSLibrary library)
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new ManualResetEventSlim();
            Task<Lr2SongDbSyncStatusSnapshot> operation = library.QueueLr2SongDbSyncAsync("test_request_version", force: true,
                (_, _) => { entered.TrySetResult(); release.Wait(); return Lr2SongDbSyncPreparedDataSurface.Empty; });
            await Task.WhenAny(entered.Task, operation);
            if (!entered.Task.IsCompleted)
            {
                try { await operation; Assert.Fail("LR2の実準備が開始されませんでした。"); }
                finally { release.Set(); release.Dispose(); }
            }
            return new HeldLr2Operation(library, release, operation);
        }

        public async ValueTask DisposeAsync()
        {
            library.RequestShutdown("held-lr2-test-cleanup");
            release.Set();
            try { await operation; }
            finally { release.Dispose(); }
        }
    }

    /// <summary>確定levelだけの終端投影でも同tokenのLevel/Textを変更し、詳細索引・互換警告の通知なしで共通refreshへ変更譜面を登録します。</summary>
    [TestMethod]
    public void CurrentProjection_LevelOnlyCommitReachesCommonRefreshPublisher()
    {
        using var scope = TestDatabaseScope.Create();
        var library = new TestBmsLibrary(scope.DatabaseScope, scope.SongDbPath, settings: testSettings)
        {
            BmsCharts = [ChartTestValues.Empty() with { Path = Path.Combine(scope.DirectoryPath, "level-only.bms"), Md5 = new string('a', 32), Level = 12, LevelText = "12" }]
        };
        try
        {
            ChartFile current = library.BmsCharts.Single();
            int handledVersion = library.NormalLibraryRefreshNotificationVersion;
            int detailVersion = library.ChartInfoIndexVersion;
            ILr2SynchronizationProjectionPort projection = ReadSynchronizationDependency<ILr2SynchronizationProjectionPort>(library.Lr2Synchronization, "projection");
            int changed = projection.ApplyLr2SongDbSyncCurrentProjection(
                [new Lr2SongDbSyncCurrentFact(current with { Level = 3, LevelText = "3" }, true, null)], "level-only", false, false);
            Assert.AreEqual(1, changed);
            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledVersion);
            ChartFile notified = batch.ChangedCharts.Single();
            Assert.AreSame(current.Token, notified.Token);
            Assert.AreEqual(3, notified.Level);
            Assert.AreEqual("3", notified.LevelText);
            Assert.AreEqual(3, library.BmsCharts.Single().Level);
            Assert.AreEqual(detailVersion, library.ChartInfoIndexVersion);
            Assert.IsFalse(batch.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged));
        }
        finally { library.RequestShutdown("level-only-test-cleanup"); }
    }

    private static ChartFile CreateReadableSyncChart(string chartPath, string title)
    {
        string chartDirectory = Path.GetDirectoryName(chartPath)
            ?? throw new ArgumentException("譜面パスには親ディレクトリが必要です。", nameof(chartPath));
        Directory.CreateDirectory(chartDirectory);
        File.WriteAllText(
            chartPath,
            "#PLAYER 1\r\n#TITLE " + title + "\r\n#ARTIST tester\r\n#BPM 150\r\n#PLAYLEVEL 12\r\n#RANK 3\r\n#WAV01 kick.wav\r\n#00111:01\r\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
        ChartFile file = CreateSyncTestFile(chartPath, snapshot);
        file = file with { RawTitle = title };
        file = file with { Folder = "stale-folder" };
        file = file with { Favorite = 7 };
        file = file with { Tag = "keep-unsupported-tag" };
        file = file with { AddDate = 123456 };
        return file;
    }

    private (TestBmsLibrary Library, LibraryFileInitializationResult Input) CreateFolderProjectionSyncLibrary(
        TestDatabaseScope scope,
        string rootDirectory,
        IReadOnlyList<ChartFile> chartFiles,
        IReadOnlyList<string> lr2FolderFilePaths,
        IReadOnlyList<string> directoryPaths)
    {
        foreach (string directoryPath in directoryPaths)
        {
            Directory.CreateDirectory(directoryPath);
        }

        var library = new TestBmsLibrary(scope.DatabaseScope,
            scope.SongDbPath,
            getLR2Config: null,
            _lr2ScoreDB: null,
            startupRequiredFileScanReason: null,
            optionsSnapshotProvider: () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = true },
            applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge, settings: TestBmsFactory.CreateRootSettings([rootDirectory], testSettings))
        {

            BmsCharts = [.. chartFiles]
        };
        BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(library.DatabaseScope,
            scope.SongDbPath,
            setup =>
            {
                setup.CreateTable<LR2SongDB.folder>();
                BmsLibraryDbGateway.EnsureChartInfoSchema(setup);
                foreach (ChartFile chartFile in chartFiles)
                {
                    setup.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(chartFile), typeof(LR2SongDB.song));
                }
            });

        string[] capturedDirectories = [.. directoryPaths];
        string[] capturedFolderFiles = [.. lr2FolderFilePaths];
        var initializationInput = new LibraryFileInitializationResult(InvokeCaptureLr2SongDbSyncScanSurface(
            library,
            new BmsLibraryOptionsSnapshot { OperationModeLR2DB = true },
            [rootDirectory],
            new SongTableFileCheckResult
            {
                Lr2ScanSurfaceAvailable = true,
                Lr2ScanNormalFolderDirectoryPaths = capturedDirectories,
                Lr2ScanNormalFolderDirectoryEntries = CreateDirectoryEntryMap(capturedDirectories),
                Lr2ScanDirectoryEntries = CreateDirectoryEntryMap(capturedDirectories),
                Lr2ScanFolderInfoFilePaths = [],
                Lr2ScanFolderInfoFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                Lr2ScanTextFileDirectories = [],
                Lr2ScanLr2FolderDiscoveryDirectories = [rootDirectory],
                Lr2ScanLr2FolderFilePaths = capturedFolderFiles,
                Lr2ScanLr2FolderFileEntries = CreateFileEntryMap(capturedFolderFiles),
                Lr2ScanLr2FolderFileDiscoveryComplete = true
            }));
        return (library, initializationInput);
    }

    private static Lr2SongDbSyncInput InvokeCreateLr2SongDbSyncInput(BMSLibrary library,
        LibraryFileInitializationResult? initializationInput = null, Lr2SongDbSyncPreparedDataSurface? preparedInput = null)
    {
        return library.Lr2Synchronization.CreateLr2SongDbSyncInput(initializationResult: initializationInput, preparedSurface: preparedInput);
    }

    private static Lr2SongDbSyncAppManagedOutputScope InvokeCreateLr2SongDbSyncAppManagedOutputScope(BMSLibrary library)
    {
        return library.Lr2Synchronization.CreateLr2SongDbSyncAppManagedOutputScope();
    }

    private static void InvokeMarkLr2SongDbSyncIncompleteAfterFileDiffNormalFolderSyncFailure(
        BMSLibrary library,
        BmsLibraryOptionsSnapshot options,
        SongTableFileCheckResult result)
    {
        library.Lr2Synchronization.MarkLr2SongDbSyncIncompleteAfterFileDiffNormalFolderSyncFailure(options, result);
    }

    private static void InvokeMarkLr2SongDbSyncIncompleteAfterNormalFolderSyncFailure(
        BMSLibrary library,
        BmsLibraryOptionsSnapshot options,
        string stage,
        string detail,
        string logReason)
    {
        GetLr2SynchronizationOwner(library).MarkLr2SongDbSyncIncompleteAfterNormalFolderSyncFailure(
            options,
            stage,
            detail,
            logReason);
    }

    private static void InvokeMarkLr2SongDbSyncIncompleteAfterSongDbWriteFailure(
        BMSLibrary library,
        BmsLibraryOptionsSnapshot options,
        string stage,
        string detail,
        string logReason)
    {
        GetLr2SynchronizationOwner(library).MarkLr2SongDbSyncIncompleteAfterSongDbWriteFailure(
            options,
            stage,
            detail,
            logReason);
    }

    private static string NormalizeDirectory(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static LR2Config CreateLr2Config(string lr2RootPath, int customFolderMask, int titleFlashHours, params string[] bmsRoots)
    {
        string configDirectory = Path.Combine(lr2RootPath, "LR2files", "Config");
        Directory.CreateDirectory(configDirectory);
        string pathElements = string.Join(
            string.Empty,
            (bmsRoots ?? [])
                .Where(root => !string.IsNullOrWhiteSpace(root))
                .Select(root => "<path>" + EscapeXml(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar) + "</path>"));
        string configPath = Path.Combine(configDirectory, "config.xml");
        File.WriteAllText(
            configPath,
            "<config><system><customfolder>" + customFolderMask + "</customfolder><titleflash>" + titleFlashHours + "</titleflash></system><jukebox>" + pathElements + "</jukebox></config>",
            Encoding.UTF8);
        return new LR2Config(configPath);
    }

    private static string EscapeXml(string value)
    {
        return System.Security.SecurityElement.Escape(value) ?? string.Empty;
    }

}
