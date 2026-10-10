using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.BmsLibraryInitializationTestSupport;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;
namespace BeMusicSeeker.Tests;


[TestClass]
public sealed class BmsLibraryInitializationInstallTests
{
    [TestMethod]
    public async Task Initialize_RestoresPendingWithoutAutomaticEstimationAndAllowsManualEstimation()
    {

        string root = Path.Combine(Path.GetTempPath(), nameof(BmsLibraryInitializationInstallTests), Guid.NewGuid().ToString("N"));
        string installed = Path.Combine(root, "Library", "Song");
        string pending = Path.Combine(root, "Pending");
        Directory.CreateDirectory(installed);
        Directory.CreateDirectory(pending);
        string installedChart = Path.Combine(installed, "chart.bms");
        string pendingChart = Path.Combine(pending, "diff.bms");
        string songDb = Path.Combine(root, "song.db");
        File.WriteAllText(installedChart, CreateValidBmsText("Song"), Encoding.ASCII);
        File.WriteAllText(pendingChart, CreateValidBmsText("Song") + "#PLAYLEVEL 12\r\n", Encoding.ASCII);
        File.WriteAllBytes(Path.Combine(installed, "sound.wav"), [1]);
        File.WriteAllBytes(songDb, []);
        var backgroundWork = new ConcurrentQueue<Func<Task>>();
        var observer = new CountingInstallEstimationObserver();
        TestBmsLibrary? library = null;
        try
        {
            ExecuteSongDbFixtureTransaction(songDb, connection =>
            {
                connection.CreateTable<LR2SongDBExtended.install>();
                connection.InsertOrReplace(new LR2SongDBExtended.install { path = pending, delete_parent = false });
            });
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = false,
                ScanBmsFilesOnStartup = true,
                PendingInstallEstimateMaxParallelPackages = 1
            };
            library = new TestBmsLibrary(songDb, null, null, null, new RecordingDialogService(),
                new TestUiScheduler(() => null), () => options,
                CapturedChartFileScanner.FromFixture([installedChart],
                    new Dictionary<string, IEnumerable<string>>
                    {
                        [installed] = ["sound.wav"]
                    }, [installed]), observer)
            {
                SearchTargets = [Path.Combine(root, "Library")],
                StartupBackgroundTaskScheduler = (_, _, _, work) =>
                {
                    backgroundWork.Enqueue(work);
                    return true;
                }
            };
            library.Initialize(null, null, BMSLibrary.LibraryInitializeMode.Startup);
            Assert.IsFalse(library.GetInstallEstimationProgressSnapshot().IsActive);
            while (backgroundWork.TryDequeue(out Func<Task>? work))
            {
                await work();
            }
            Assert.AreEqual(0, observer.StartedCount, "復元と必須背景更新では評価を開始しません。");
            ChartPackage restored = library.ChartPackagesPending.Single();
            Assert.AreEqual(pending, restored.path);
            Assert.IsTrue(restored.ChartEntries.All(entry => string.IsNullOrEmpty(entry.Chart.InstallDestination)));

            library.SearchEstimatedInstallationDirectory(restored);

            Assert.AreEqual(installed, restored.ChartEntries.Single().Chart.InstallDestination);
        }
        finally
        {
            while (backgroundWork.TryDequeue(out Func<Task>? work))
            {
                await work();
            }
            library?.RequestShutdown("startup-pending-restore-test");
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class CountingInstallEstimationObserver : IInstallEstimationExecutionObserver
    {
        internal int StartedCount;

        public IDisposable BeginWorkItem(InstallEstimationWorkItemObservation observation)
        {
            Interlocked.Increment(ref StartedCount);
            return new Scope();
        }

        public void ObserveProgress(InstallEstimationProgressObservation observation) { }

        public void ObserveResultApplied(InstallEstimationAppliedObservation observation) { }

        private sealed class Scope : IDisposable
        {
            public void Dispose() { }
        }
    }

    [TestMethod]
    public void UpsertSongs_FillsLr2FolderAndParentForInstalledBmsRows()
    {

        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "Installed");
            Directory.CreateDirectory(chartDirectoryPath);
            string bmsPath = Path.Combine(chartDirectoryPath, "chart.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("Installed"), Encoding.ASCII);
            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            ChartFile file = ChartTestValues.Empty() with
            {
                Path = bmsPath,
                Folder = null
            };
            file = file with { Md5 = "cccccccccccccccccccccccccccccccc" };

            new BmsLibraryDbGateway(songDbPath).UpsertSongs([file]);

            Assert.IsNull(file.Folder, "生成列の保存は捕捉済みの共通入力を書き換えません。");
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            LR2SongDB.song stored = verify.Table<LR2SongDB.song>().Single();
            Assert.AreEqual(bmsPath, stored.path);
            Assert.AreEqual(file.Md5, stored.hash);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(chartDirectoryPath), stored.folder);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(lr2RootPath), stored.parent);
            ChartFile projected = ChartSongStorageMapping.FromBmsRow(stored);
            Assert.AreEqual("Installed", projected.Folder, "表示用FolderはLR2の保存hashから逆算しません。");
            Assert.IsNull(file.Folder);
        });
    }

    [TestMethod]
    public void UpsertSongs_PreservesShiftJisUnsupportedInstalledBmsRowsWithWarning()
    {

        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "Installed😀");
            Directory.CreateDirectory(chartDirectoryPath);
            string bmsPath = Path.Combine(chartDirectoryPath, "chart.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("Installed Emoji"), Encoding.ASCII);
            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            ChartFile file = ChartTestValues.Empty() with
            {
                Path = bmsPath,
                Folder = null
            };
            file = file with { Md5 = "dddddddddddddddddddddddddddddddd" };

            new BmsLibraryDbGateway(songDbPath).UpsertSongs([file]);

            Assert.IsNull(file.Folder);
            Assert.AreEqual(0, file.Warnings.Count, "保存による旧入力への警告書戻しを行いません。");
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            LR2SongDB.song stored = verify.Table<LR2SongDB.song>().Single();
            Assert.AreEqual(bmsPath, stored.path);
            Assert.AreEqual(file.Md5, stored.hash);
            Assert.IsNull(stored.parent);
            var currentOwner = new CatalogOwnedCollectionOwner();
            currentOwner.ReplaceCharts([ChartSongStorageMapping.FromBmsRow(stored)], []);
            ChartFile current = currentOwner.BmsRows.Single();
            Assert.IsTrue(current.Warnings.Any(warning => warning.Kind == ChartWarningKind.Lr2PathEncodingUnsupported));
            Assert.AreEqual(bmsPath, current.Path);
            Assert.AreEqual(file.Md5, current.Md5);
            Assert.AreEqual(0, file.Warnings.Count);
        });
    }

    [TestMethod]
    public void UpsertSongs_UpdatesGeneratedColumnsWithoutOverwritingUserSongColumns()
    {
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "MergeSong");
            Directory.CreateDirectory(chartDirectoryPath);
            string bmsPath = Path.Combine(chartDirectoryPath, "chart.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("Updated Title"), Encoding.ASCII);
            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                ChartFile existing = ChartTestValues.Empty() with
                {
                    Path = bmsPath,
                    Date = 100,
                    AddDate = 12345,
                    Tag = "user-tag"
                };
                existing = existing with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
                existing = existing with { Favorite = 7 };
                existing = existing with { Txt = 1 };
                songDbConnection.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(existing), typeof(LR2SongDB.song));
            }

            ChartFile updated = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bmsPath));
            updated = updated with { Date = 200 };

            new BmsLibraryDbGateway(songDbPath).UpsertSongs([updated]);

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            LR2SongDB.song row = verify.Table<LR2SongDB.song>().Single();
            Assert.AreEqual(bmsPath, row.path);
            Assert.AreEqual("Updated Title", row.title);
            Assert.AreEqual(updated.Md5, row.hash);
            Assert.AreEqual(200, row.date);
            Assert.AreEqual(7, row.favorite);
            Assert.AreEqual(1, row.txt);
            Assert.AreEqual(12345, row.adddate);
            Assert.AreEqual("user-tag", row.tag);
        });
    }

    [TestMethod]
    public void LoadInstallTable_InitializesPendingWarningsAndCounts()
    {

        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string directoryPackagePath = Path.Combine(lr2RootPath, "PendingDir");
            Directory.CreateDirectory(directoryPackagePath);
            string directoryChartPath = Path.Combine(directoryPackagePath, "dir_chart.bms");
            File.WriteAllText(directoryChartPath, "#PLAYER 1\r\n#TITLE Dir\r\n");

            string singleFileDirectoryPath = Path.Combine(lr2RootPath, "Single");
            Directory.CreateDirectory(singleFileDirectoryPath);
            string singleFileChartPath = Path.Combine(singleFileDirectoryPath, "single_chart.bms");
            File.WriteAllText(singleFileChartPath, "#PLAYER 1\r\n#TITLE Single\r\n");

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.install>();
                songDb.InsertOrReplace(new ChartPackage
                {
                    path = directoryPackagePath,
                    delete_parent = false
                }, typeof(LR2SongDBExtended.install));
                songDb.InsertOrReplace(new ChartPackage
                {
                    path = singleFileChartPath,
                    delete_parent = false
                }, typeof(LR2SongDBExtended.install));
                songDb.InsertOrReplace(new ChartPackage
                {
                    path = Path.Combine(lr2RootPath, "MissingPkg"),
                    delete_parent = false
                }, typeof(LR2SongDBExtended.install));
            }

            string installedHash = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(directoryChartPath)).Md5;
            var service = new BmsLibraryInitializationService();

            InstallTableLoadResult result = service.LoadInstallTable(
                new BmsLibraryDbGateway(songDbPath),
                [],
                chart => string.Equals(chart?.Md5, installedHash, StringComparison.OrdinalIgnoreCase));

            Assert.AreEqual(2, result.PendingPackages.Count);
            Assert.AreEqual(1, result.StaleInstallPaths.Count);
            Assert.AreEqual(1, result.InstalledWarningCount);
            Assert.AreEqual(1, result.SingleFileWarningCount);
            Assert.AreEqual(0, result.StrictWarningCount);
            Assert.IsTrue(result.LoadMs >= 0);
            Assert.IsTrue(result.WarningInitMs >= 0);
            Assert.IsTrue(result.TotalMs >= 0);
            ChartPackage installedWarningPackage = result.PendingPackages.Single(pkg => pkg.path.Equals(directoryPackagePath, StringComparison.OrdinalIgnoreCase));
            ChartPackage singleFileWarningPackage = result.PendingPackages.Single(pkg => pkg.path.Equals(singleFileChartPath, StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(installedWarningPackage.ChartEntries[0].Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
            Assert.IsTrue(singleFileWarningPackage.ChartEntries[0].Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.SingleBmsFile));
        });
    }

    /// <summary>
    /// Legacy install rows under registered roots are pruned before publication,
    /// without deleting physical sources or catalog rows. A later explicit reload
    /// applies a newly registered root to the entire pending collection.
    /// </summary>
    [TestMethod]
    public void ReloadInstallTable_ExcludesRegisteredRootsBeforePendingPublicationAndPreservesSources()
    {

        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string registeredRoot = Path.Combine(lr2RootPath, "Library");
            string nestedDirectory = Path.Combine(registeredRoot, "Song");
            string outsideDirectory = Path.Combine(lr2RootPath, "Library-pending");
            string remainingDirectory = Path.Combine(lr2RootPath, "Downloads");
            string[] packageDirectories = [registeredRoot, nestedDirectory, outsideDirectory, remainingDirectory];
            var chartContents = new Dictionary<string, string>();
            foreach (string directory in packageDirectories)
            {
                Directory.CreateDirectory(directory);
                string chartPath = Path.Combine(directory, "chart.bms");
                string content = CreateValidBmsText(Path.GetFileName(directory));
                File.WriteAllText(chartPath, content, Encoding.ASCII);
                chartContents.Add(chartPath, content);
            }
            string nestedChartPath = Path.Combine(nestedDirectory, "chart.bms");
            string nestedAlias = Path.Combine(nestedDirectory, ".");
            string[] protectedRows = [registeredRoot, nestedDirectory, nestedChartPath, nestedAlias];
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.install>();
                songDb.CreateTable<LR2SongDB.song>();
                songDb.InsertOrReplace(new LR2SongDB.song
                {
                    path = nestedChartPath,
                    hash = "11111111111111111111111111111111",
                    title = "Keep catalog"
                });
                foreach (string sourcePath in protectedRows.Concat(new[] { outsideDirectory, remainingDirectory }))
                {
                    songDb.InsertOrReplace(new ChartPackage { path = sourcePath }, typeof(LR2SongDBExtended.install));
                }
            }
            var dbGateway = new BmsLibraryDbGateway(songDbPath);
            var owner = new PackageLifecycleOwner(
                dbGateway,
                new TestUiScheduler(() => null!),
                _ => { },
                packages => new ObservableCollection<ChartPackage>(packages ?? []),
                () => { },
                _ => { });
            string rootAlias = Path.Combine(registeredRoot, ".").ToUpperInvariant()
                + Path.DirectorySeparatorChar;

            InstallTableLoadResult firstLoad = owner.ReloadInstallTable(
                new BmsLibraryInitializationService(),
                dbGateway,
                [rootAlias],
                _ => true);

            CollectionAssert.AreEquivalent(protectedRows, firstLoad.StaleInstallPaths);
            Assert.AreEqual(2, firstLoad.PendingPackages.Count);
            CollectionAssert.AreEquivalent(
                new[] { outsideDirectory, remainingDirectory },
                owner.PendingPackages.Select(package => package.path).ToArray());
            CollectionAssert.AreEquivalent(
                new[] { outsideDirectory, remainingDirectory },
                dbGateway.LoadInstallPackages().Select(package => package.path).ToArray());

            InstallTableLoadResult secondLoad = owner.ReloadInstallTable(
                new BmsLibraryInitializationService(),
                dbGateway,
                [registeredRoot, outsideDirectory],
                _ => true);

            CollectionAssert.AreEquivalent(new[] { outsideDirectory }, secondLoad.StaleInstallPaths);
            Assert.AreEqual(1, owner.PendingPackages.Count);
            Assert.AreEqual(remainingDirectory, owner.PendingPackages[0].path);
            Assert.AreEqual(remainingDirectory, dbGateway.LoadInstallPackages().Single().path);
            foreach (KeyValuePair<string, string> chart in chartContents)
            {
                Assert.AreEqual(chart.Value, File.ReadAllText(chart.Key));
            }
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            LR2SongDB.song catalogRow = verify.Table<LR2SongDB.song>().Single();
            Assert.AreEqual(nestedChartPath, catalogRow.path);
            Assert.AreEqual("Keep catalog", catalogRow.title);
        });
    }

    [TestMethod]
    public void LoadInstallTable_CanonicalPathFirstWinsAndStartupCleanupConvergesDatabase()
    {

        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string validPackagePath = Path.Combine(lr2RootPath, "CanonicalPending");
            Directory.CreateDirectory(validPackagePath);
            File.WriteAllText(
                Path.Combine(validPackagePath, "chart.bms"),
                CreateValidBmsText("Canonical pending"),
                Encoding.ASCII);
            string duplicateAliasPath = Path.Combine(validPackagePath, ".");
            string noChartPackagePath = Path.Combine(lr2RootPath, "NoChartPending");
            Directory.CreateDirectory(noChartPackagePath);
            string missingPackagePath = Path.Combine(lr2RootPath, "MissingPending");

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.install>();
                songDb.InsertOrReplace(new ChartPackage { path = validPackagePath }, typeof(LR2SongDBExtended.install));
                songDb.InsertOrReplace(new ChartPackage { path = duplicateAliasPath }, typeof(LR2SongDBExtended.install));
                songDb.InsertOrReplace(new ChartPackage { path = noChartPackagePath }, typeof(LR2SongDBExtended.install));
                songDb.InsertOrReplace(new ChartPackage { path = missingPackagePath }, typeof(LR2SongDBExtended.install));
            }

            var dbGateway = new BmsLibraryDbGateway(songDbPath);
            var packageLifecycleOwner = new PackageLifecycleOwner(
                dbGateway,
                new TestUiScheduler(() => null!),
                _ => { },
                packages => new ObservableCollection<ChartPackage>(packages ?? []),
                () => { },
                _ => { });
            InstallTableLoadResult result = packageLifecycleOwner.ReloadInstallTable(
                new BmsLibraryInitializationService(),
                dbGateway,
                [],
                _ => false);

            string canonicalValidPackagePath = LongPathFileSystem.TrimTrailingDirectorySeparators(
                LongPathFileSystem.NormalizePathForStorage(validPackagePath));
            Assert.AreEqual(1, result.PendingPackages.Count);
            Assert.AreEqual(canonicalValidPackagePath, result.PendingPackages[0].path);
            CollectionAssert.AreEquivalent(
                new[] { duplicateAliasPath, noChartPackagePath, missingPackagePath },
                result.StaleInstallPaths);

            List<ChartPackage> remainingInstallRows = dbGateway.LoadInstallPackages();
            Assert.AreEqual(1, remainingInstallRows.Count);
            Assert.AreEqual(canonicalValidPackagePath, remainingInstallRows[0].path);
            Assert.AreEqual(1, packageLifecycleOwner.PendingPackages.Count);
            Assert.AreEqual(canonicalValidPackagePath, packageLifecycleOwner.PendingPackages[0].path);
            CollectionAssert.AreEquivalent(
                result.PendingPackages[0].ChartEntries
                    .Select(entry => entry.Chart?.Path)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .ToArray(),
                packageLifecycleOwner.PendingPackages[0].ChartEntries
                    .Select(entry => entry.Chart?.Path)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .ToArray());
        });
    }

    [TestMethod]
    public void LoadInstallTable_SoleNonCanonicalSurvivorConvergesDatabaseBeforePendingPublication()
    {

        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string packagePath = Path.Combine(lr2RootPath, "SoleAliasPending");
            Directory.CreateDirectory(packagePath);
            File.WriteAllText(
                Path.Combine(packagePath, "chart.bms"),
                CreateValidBmsText("Sole alias pending"),
                Encoding.ASCII);
            string rawAliasPath = Path.Combine(packagePath, ".");
            string caseOnlyPackagePath = Path.Combine(lr2RootPath, "solealiaspending");
            string caseOnlyAliasPath = Path.Combine(caseOnlyPackagePath, ".");
            string canonicalPackagePath = LongPathFileSystem.TrimTrailingDirectorySeparators(
                LongPathFileSystem.NormalizePathForStorage(packagePath));

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.install>();
                songDb.InsertOrReplace(new ChartPackage
                {
                    path = rawAliasPath,
                    delete_parent = true
                }, typeof(LR2SongDBExtended.install));
                songDb.InsertOrReplace(new ChartPackage
                {
                    path = caseOnlyPackagePath
                }, typeof(LR2SongDBExtended.install));
                songDb.InsertOrReplace(new ChartPackage
                {
                    path = caseOnlyAliasPath
                }, typeof(LR2SongDBExtended.install));
            }

            var dbGateway = new BmsLibraryDbGateway(songDbPath);
            var packageLifecycleOwner = new PackageLifecycleOwner(
                dbGateway,
                new TestUiScheduler(() => null!),
                _ => { },
                packages => new ObservableCollection<ChartPackage>(packages ?? []),
                () => { },
                _ => { });
            InstallTableLoadResult result = packageLifecycleOwner.ReloadInstallTable(
                new BmsLibraryInitializationService(),
                dbGateway,
                [],
                _ => false);

            Assert.AreEqual(2, result.PendingPackages.Count);
            Assert.IsTrue(result.PendingPackages.Any(package =>
                string.Equals(package.path, canonicalPackagePath, StringComparison.Ordinal)));
            Assert.IsTrue(result.PendingPackages.Any(package =>
                string.Equals(package.path, caseOnlyPackagePath, StringComparison.Ordinal)));
            Assert.AreEqual(2, result.StaleInstallPaths.Count);
            Assert.IsTrue(result.StaleInstallPaths.Any(path =>
                string.Equals(path, rawAliasPath, StringComparison.Ordinal)));
            Assert.IsTrue(result.StaleInstallPaths.Any(path =>
                string.Equals(path, caseOnlyAliasPath, StringComparison.Ordinal)));
            Assert.AreEqual(2, packageLifecycleOwner.PendingPackages.Count);
            ChartPackage canonicalPendingPackage = packageLifecycleOwner.PendingPackages.Single(package =>
                string.Equals(package.path, canonicalPackagePath, StringComparison.Ordinal));
            Assert.IsTrue(canonicalPendingPackage.delete_parent);
            Assert.IsTrue(packageLifecycleOwner.PendingPackages.Any(package =>
                string.Equals(package.path, caseOnlyPackagePath, StringComparison.Ordinal)));

            List<ChartPackage> remainingInstallRows = dbGateway.LoadInstallPackages();
            Assert.AreEqual(2, remainingInstallRows.Count);
            ChartPackage canonicalInstallRow = remainingInstallRows.Single(row =>
                string.Equals(row.path, canonicalPackagePath, StringComparison.Ordinal));
            Assert.IsNotNull(remainingInstallRows.SingleOrDefault(row =>
                string.Equals(row.path, caseOnlyPackagePath, StringComparison.Ordinal)));
            Assert.IsFalse(remainingInstallRows.Any(row =>
                string.Equals(row.path, rawAliasPath, StringComparison.Ordinal)));
            Assert.IsFalse(remainingInstallRows.Any(row =>
                string.Equals(row.path, caseOnlyAliasPath, StringComparison.Ordinal)));
            Assert.IsTrue(canonicalInstallRow.delete_parent);

            var followOnPackage = new ChartPackage
            {
                path = Path.Combine(lr2RootPath, "FollowOnPackage"),
                delete_parent = false
            };
            packageLifecycleOwner.ApplyPendingPackageMutationDelta(
                new PendingPackageMutationDelta
                {
                    HasChanges = true,
                    RemainingPackages = [.. packageLifecycleOwner.PendingPackages]
                },
                packagesToAdd: [followOnPackage],
                installRowsToUpsert: [followOnPackage]);

            Assert.AreEqual(3, packageLifecycleOwner.PendingPackages.Count);
            Assert.IsTrue(packageLifecycleOwner.PendingPackages.Any(package =>
                string.Equals(package.path, followOnPackage.path, StringComparison.Ordinal)));
            remainingInstallRows = dbGateway.LoadInstallPackages();
            Assert.AreEqual(3, remainingInstallRows.Count);
            Assert.IsNotNull(remainingInstallRows.SingleOrDefault(row =>
                string.Equals(row.path, followOnPackage.path, StringComparison.Ordinal)));
        });
    }

    [TestMethod]
    public void LoadInstallTable_DatabaseFailureIsPropagated()
    {
        string missingSongDbPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_MissingInitTests_" + Guid.NewGuid().ToString("N"), "song.db");
        var service = new BmsLibraryInitializationService();

        Assert.ThrowsException<SQLite.SQLiteException>(() => service.LoadInstallTable(new BmsLibraryDbGateway(missingSongDbPath), []));
    }

    [TestMethod]
    public void LoadInstallTable_ChecksInstalledChartsWithoutMaterializingUnmatchedBmsonEntries()
    {

        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string directoryPackagePath = Path.Combine(lr2RootPath, "PendingBmsonDir");
            Directory.CreateDirectory(directoryPackagePath);
            string installedBmsonPath = CreateBmsonFile(directoryPackagePath, "installed.bmson", "Installed", "Artist");
            string unmatchedBmsonPath = Path.Combine(directoryPackagePath, "unmatched.bmson");
            File.WriteAllText(unmatchedBmsonPath, "{\"version\":\"1.0.0\",\"info\":{\"title\":\"Unmatched\",\"artist\":\"Artist\",\"mode_hint\":\"beat-7k\"},\"sound_channels\":[],\"lines\":[{\"y\":0}]}");

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.install>();
                songDb.InsertOrReplace(new ChartPackage
                {
                    path = directoryPackagePath,
                    delete_parent = false
                }, typeof(LR2SongDBExtended.install));
            }

            var service = new BmsLibraryInitializationService();

            InstallTableLoadResult result = service.LoadInstallTable(
                new BmsLibraryDbGateway(songDbPath),
                [],
                chart => string.Equals(chart?.Path, installedBmsonPath, StringComparison.OrdinalIgnoreCase));

            ChartPackage pendingPackage = result.PendingPackages.Single();
            PackageChartEntry installedEntry = pendingPackage.ChartEntries.Single(entry => string.Equals(entry.Chart.Path, installedBmsonPath, StringComparison.OrdinalIgnoreCase));
            PackageChartEntry unmatchedEntry = pendingPackage.ChartEntries.Single(entry => string.Equals(entry.Chart.Path, unmatchedBmsonPath, StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(1, result.InstalledWarningCount);
            Assert.IsNull(installedEntry.GetBmsChartForTest());
            Assert.IsTrue(installedEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
            Assert.IsNull(unmatchedEntry.GetBmsChartForTest());
        });
    }

    [TestMethod]
    public void LoadInstallTable_AddsBmsonResourceWarningWithoutMaterializingCompatibilityAdapter()
    {

        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string directoryPackagePath = Path.Combine(lr2RootPath, "PendingBmsonResource");
            Directory.CreateDirectory(directoryPackagePath);
            string bmsonPath = Path.Combine(directoryPackagePath, "chart.bmson");
            File.WriteAllText(bmsonPath, CreateBmsonJsonWithSound("missing.wav"));

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.install>();
                songDb.InsertOrReplace(new ChartPackage
                {
                    path = directoryPackagePath,
                    delete_parent = false
                }, typeof(LR2SongDBExtended.install));
            }

            var service = new BmsLibraryInitializationService();

            InstallTableLoadResult result = service.LoadInstallTable(
                new BmsLibraryDbGateway(songDbPath),
                [],
                _ => false);

            ChartPackage pendingPackage = result.PendingPackages.Single();
            PackageChartEntry entry = pendingPackage.ChartEntries.Single();
            Assert.AreEqual(1, result.StrictWarningCount);
            Assert.IsNull(entry.GetBmsChartForTest());
            Assert.AreEqual(ChartFileKind.Bmson, entry.Chart.Kind);
            Assert.IsTrue(entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
        });
    }

    [TestMethod]
    public void LoadInstallTable_ProjectsResourceHealthForAllPackageEntries()
    {

        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string directoryPackagePath = Path.Combine(lr2RootPath, "PendingBmsonResources");
            Directory.CreateDirectory(directoryPackagePath);
            string missingBmsonPath = Path.Combine(directoryPackagePath, "missing.bmson");
            string healthyBmsonPath = Path.Combine(directoryPackagePath, "healthy.bmson");
            File.WriteAllText(missingBmsonPath, CreateBmsonJsonWithSound("missing.wav"));
            File.WriteAllText(healthyBmsonPath, CreateBmsonJsonWithSound("sound.wav"));
            File.WriteAllBytes(Path.Combine(directoryPackagePath, "sound.wav"), new byte[] { 1 });

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.install>();
                songDb.InsertOrReplace(new ChartPackage
                {
                    path = directoryPackagePath,
                    delete_parent = false
                }, typeof(LR2SongDBExtended.install));
            }

            var service = new BmsLibraryInitializationService();
            InstallTableLoadResult result = service.LoadInstallTable(
                new BmsLibraryDbGateway(songDbPath),
                [],
                _ => false);

            ChartPackage pendingPackage = result.PendingPackages.Single();
            PackageChartEntry missingEntry = pendingPackage.ChartEntries.Single(entry => string.Equals(entry.Chart.Path, missingBmsonPath, StringComparison.OrdinalIgnoreCase));
            PackageChartEntry healthyEntry = pendingPackage.ChartEntries.Single(entry => string.Equals(entry.Chart.Path, healthyBmsonPath, StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(1, result.StrictWarningCount);
            Assert.IsTrue(missingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            Assert.IsTrue(missingEntry.Chart.WAVHealth.HasValue);
            Assert.AreEqual(100, healthyEntry.Chart.WAVHealth);
            Assert.IsFalse(healthyEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
        });
    }

    [TestMethod]
    public void LoadInstallTable_RestoresNestedChartsAndPrioritizesNestedWarning()
    {

        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string directoryPackagePath = Path.Combine(lr2RootPath, "PendingDir");
            string nestedDirectoryPath = Path.Combine(directoryPackagePath, "sub");
            Directory.CreateDirectory(nestedDirectoryPath);
            File.WriteAllText(Path.Combine(directoryPackagePath, "root.bms"), "#PLAYER 1\r\n#TITLE Root\r\n");
            File.WriteAllText(Path.Combine(nestedDirectoryPath, "another.bms"), "#PLAYER 1\r\n#TITLE Nested\r\n#WAVAA missing.wav\r\n#00111:AA\r\n");

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.install>();
                songDb.InsertOrReplace(new ChartPackage
                {
                    path = directoryPackagePath,
                    delete_parent = false
                }, typeof(LR2SongDBExtended.install));
            }

            var service = new BmsLibraryInitializationService();
            InstallTableLoadResult result = service.LoadInstallTable(
                new BmsLibraryDbGateway(songDbPath),
                [],
                file => false);

            Assert.AreEqual(1, result.PendingPackages.Count);
            Assert.AreEqual(2, result.PendingPackages[0].GetBmsChartsForTest().Count);
            PackageChartEntry nestedEntry = result.PendingPackages[0].ChartEntries.Single(entry => Path.GetFileName(entry.Chart.Path).Equals("another.bms", StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(nestedEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.NestedChartFileInPackage));
            Assert.IsTrue(nestedEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            Assert.AreEqual("[2] " + BeMusicSeeker.Properties.Resources.WarningDigest_NestedChart + ", " + BeMusicSeeker.Properties.Resources.WarningDigest_ResourceMissing, ChartWarningTestHelpers.BuildDigestText(nestedEntry));
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(nestedEntry), BeMusicSeeker.Properties.Resources.Warning_NestedChartFileInPackage);
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(nestedEntry), "WAV");
        });
    }

    /// <summary>通常カタログloadから限定読取へ検出保証を移し、過去の閏年も候補の保存行・mtimeとして捕捉します。</summary>
    [TestMethod]
    public void LeapYearCandidateCapture_DetectsFolderTimestampInAnyLeapYear()
    {

        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string folderPath = Path.Combine(lr2RootPath, "Songs", "LeapYearFolder");
            Directory.CreateDirectory(folderPath);
            Directory.SetLastWriteTime(folderPath, new DateTime(2024, 2, 29, 12, 0, 0));

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.InsertOrReplace(new LR2SongDB.folder
                {
                    path = folderPath + Path.DirectorySeparatorChar,
                    title = "LeapYearFolder",
                    parent = "e2977170",
                    type = 1,
                    date = null,
                    adddate = 0
                }, typeof(LR2SongDB.folder));
            }

            var fileMutationService = new RecordingFileMutationService();
            var service = new BmsLibraryInitializationService();

            IReadOnlyList<LeapYearFolderRepairCandidate> candidates = service.CaptureLeapYearFolderRepairCandidates(new BmsLibraryDbGateway(songDbPath), out _);
            Assert.AreEqual(1, candidates.Count);
            Assert.AreEqual(0, fileMutationService.TimestampCalls.Count);
            Assert.AreEqual(folderPath + Path.DirectorySeparatorChar, candidates[0].OriginalFolderPath);
            Assert.AreEqual(folderPath, candidates[0].Path);

        });
    }

    [TestMethod]
    public void LeapYearFolderRepair_RevalidatesApprovedCandidateAndPersistsAfterTimestampMutation()
    {

        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string folderPath = Path.Combine(lr2RootPath, "Songs", "LeapYearRepair");
            Directory.CreateDirectory(folderPath);
            Directory.SetLastWriteTime(folderPath, new DateTime(2024, 2, 29, 12, 0, 0));

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.InsertOrReplace(new LR2SongDB.folder
                {
                    path = folderPath + Path.DirectorySeparatorChar,
                    title = "LeapYearRepair",
                    parent = "e2977170",
                    type = 1,
                    date = null,
                    adddate = 0
                }, typeof(LR2SongDB.folder));
            }

            var service = new BmsLibraryInitializationService();
            IReadOnlyList<LeapYearFolderRepairCandidate> candidates = service.CaptureLeapYearFolderRepairCandidates(new BmsLibraryDbGateway(songDbPath), out _);
            Assert.AreEqual(1, candidates.Count);
            Assert.AreEqual(folderPath, candidates[0].Path);

            var fileMutationService = new RecordingFileMutationService
            {
                OnSetTimestamps = call => Directory.SetLastWriteTime(call.Path, call.LastWriteTime.GetValueOrDefault())
            };
            LeapYearFolderRepairResult repair = service.RepairLeapYearFolderTimestamps(
                new BmsLibraryDbGateway(songDbPath),
                candidates,
                fileMutationService,
                null,
                exception => exception.Message);

            Assert.AreEqual(1, repair.RepairedCount);
            Assert.AreEqual(folderPath, repair.RepairedPaths.Single());
            Assert.AreEqual(0, repair.Failures.Count);
            Assert.AreEqual(1, fileMutationService.TimestampCalls.Count);
            Assert.AreEqual(folderPath, fileMutationService.TimestampCalls[0].Path);
            Assert.AreEqual(fileMutationService.TimestampCalls[0].LastWriteTime, Directory.GetLastWriteTime(folderPath));
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            LR2SongDB.folder persistedFolder = verify.Table<LR2SongDB.folder>().Single();
            Assert.IsNull(persistedFolder.adddate);
            Assert.IsNull(persistedFolder.date);
        });
    }

    [TestMethod]
    public void LeapYearFolderRepair_SkipsCandidateWhenTimestampChangesAfterCapture()
    {

        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string folderPath = Path.Combine(lr2RootPath, "Songs", "LeapYearReplacement");
            Directory.CreateDirectory(folderPath);
            Directory.SetLastWriteTime(folderPath, new DateTime(2024, 2, 29, 12, 0, 0));

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.InsertOrReplace(new LR2SongDB.folder
                {
                    path = folderPath + Path.DirectorySeparatorChar,
                    title = "LeapYearReplacement",
                    parent = "e2977170",
                    type = 1,
                    date = null,
                    adddate = 0
                }, typeof(LR2SongDB.folder));
            }

            var service = new BmsLibraryInitializationService();
            IReadOnlyList<LeapYearFolderRepairCandidate> candidates = service.CaptureLeapYearFolderRepairCandidates(new BmsLibraryDbGateway(songDbPath), out _);
            Assert.AreEqual(1, candidates.Count);

            // March 1 is still inside the legacy leap-year sentinel window,
            // so checking only the current predicate would incorrectly repair
            // this replacement.
            Directory.SetLastWriteTime(folderPath, new DateTime(2024, 3, 1, 12, 0, 0));
            var fileMutationService = new RecordingFileMutationService();
            LeapYearFolderRepairResult repair = service.RepairLeapYearFolderTimestamps(
                new BmsLibraryDbGateway(songDbPath),
                candidates,
                fileMutationService,
                null,
                exception => exception.Message);

            Assert.AreEqual(0, repair.RepairedCount);
            Assert.AreEqual(0, repair.Failures.Count);
            Assert.AreEqual(0, fileMutationService.TimestampCalls.Count);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            LR2SongDB.folder persistedFolder = verify.Table<LR2SongDB.folder>().Single();
            Assert.AreEqual(0, persistedFolder.adddate);
            Assert.IsNull(persistedFolder.date);
        });
    }

    [TestMethod]
    public void LeapYearFolderRepair_MissingMutationServiceFailsWithoutPersistingCatalogUpdate()
    {

        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string folderPath = Path.Combine(lr2RootPath, "Songs", "LeapYearMissingMutationService");
            Directory.CreateDirectory(folderPath);
            Directory.SetLastWriteTime(folderPath, new DateTime(2024, 2, 29, 12, 0, 0));

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.InsertOrReplace(new LR2SongDB.folder
                {
                    path = folderPath + Path.DirectorySeparatorChar,
                    title = "LeapYearMissingMutationService",
                    parent = "e2977170",
                    type = 1,
                    date = null,
                    adddate = 0
                }, typeof(LR2SongDB.folder));
            }

            var service = new BmsLibraryInitializationService();
            IReadOnlyList<LeapYearFolderRepairCandidate> candidates = service.CaptureLeapYearFolderRepairCandidates(new BmsLibraryDbGateway(songDbPath), out _);
            LeapYearFolderRepairResult repair = service.RepairLeapYearFolderTimestamps(
                new BmsLibraryDbGateway(songDbPath),
                candidates,
                fileMutationService: null,
                targetOnlyFileMutationOptions: null,
                getDisplayedExceptionMessage: exception => exception.Message);

            Assert.AreEqual(0, repair.RepairedCount);
            Assert.AreEqual(1, repair.Failures.Count);
            Assert.AreEqual(folderPath, repair.Failures[0].Path);
            Assert.IsNotNull(repair.Failures[0].Exception);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            LR2SongDB.folder persistedFolder = verify.Table<LR2SongDB.folder>().Single();
            Assert.AreEqual(0, persistedFolder.adddate);
            Assert.IsNull(persistedFolder.date);
        });
    }

    [TestMethod]
    public void Initialize_SkipsApprovedLeapYearFolderWhenCatalogIdentityChangesBeforeAdmission()
    {

        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string folderPath = Path.Combine(lr2RootPath, "Songs", "PublicReplacement");
            Directory.CreateDirectory(folderPath);
            Directory.SetLastWriteTime(folderPath, new DateTime(2024, 2, 29, 12, 0, 0));

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.InsertOrReplace(new LR2SongDB.folder
                {
                    path = folderPath + Path.DirectorySeparatorChar,
                    title = "PublicReplacement",
                    parent = "e2977170",
                    type = 1,
                    date = null,
                    adddate = 0
                }, typeof(LR2SongDB.folder));
            }

            var dialogService = new RecordingDialogService
            {
                ResultToReturn = MessageBoxResult.Yes
            };
            dialogService.OnShow = dialogCall =>
            {
                if (dialogCall.Button == MessageBoxButton.YesNo)
                {
                    using var replacementDb = new LR2SongDBExtended(songDbPath);
                    LR2SongDB.folder replacement = replacementDb.Table<LR2SongDB.folder>().Single();
                    replacement.title = "PublicReplacementUpdated";
                    replacementDb.InsertOrReplace(replacement, typeof(LR2SongDB.folder));
                }
            };
            var fileMutationService = new RecordingFileMutationService();
            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                fileMutationService,
                dialogService,
                new TestUiScheduler(() => null!),
                () => CreateInitializationOptions(lr2RootPath));
            library.StartupBackgroundTaskScheduler = (_, _, _, _) => false;

            library.Initialize(null, null, BMSLibrary.LibraryInitializeMode.Startup);

            Assert.AreEqual(0, fileMutationService.TimestampCalls.Count);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            LR2SongDB.folder persistedFolder = verify.Table<LR2SongDB.folder>().Single();
            Assert.AreEqual(0, persistedFolder.adddate);
            Assert.IsNull(persistedFolder.date);
        });
    }

    [DataTestMethod]
    [DataRow(BMSLibrary.LibraryInitializeMode.Startup, false)]
    [DataRow(BMSLibrary.LibraryInitializeMode.FullReinitialize, false)]
    [DataRow(BMSLibrary.LibraryInitializeMode.Startup, true)]
    [DataRow(BMSLibrary.LibraryInitializeMode.FullReinitialize, true)]
    /// <summary>公開起動・全再初期化の修復と通知再入を保ち、確認内の終了では日時・保存行を変えずBusy通知も追加しません。</summary>
    public void Initialize_RepairsStableLeapYearFolderBeforeDeferredDialogAndAllowsReentry(BMSLibrary.LibraryInitializeMode mode, bool shutdownDuringConfirmation)
    {

        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string folderPath = Path.Combine(lr2RootPath, "Songs", "PublicStable");
            Directory.CreateDirectory(folderPath);
            Directory.SetLastWriteTime(folderPath, new DateTime(2024, 2, 29, 12, 0, 0));

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.InsertOrReplace(new LR2SongDB.folder
                {
                    path = folderPath + Path.DirectorySeparatorChar,
                    title = "PublicStable",
                    parent = "e2977170",
                    type = 1,
                    date = null,
                    adddate = 0
                }, typeof(LR2SongDB.folder));
            }

            var dialogService = new RecordingDialogService
            {
                ResultToReturn = MessageBoxResult.Yes
            };
            var fileMutationService = new RecordingFileMutationService();
            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                fileMutationService,
                dialogService,
                new TestUiScheduler(() => null!),
                () => CreateInitializationOptions(lr2RootPath));
            library.StartupBackgroundTaskScheduler = (_, _, _, _) => false;
            bool timestampObservedWithoutModelGuards = false;
            bool callbackAfterReleaseObserved = false;
            fileMutationService.OnSetTimestamps = timestampCall =>
            {
                Assert.IsTrue(library.OperationAdmission.IsActive);
                Assert.IsTrue(library.Lr2Synchronization.PlaylistOperationAdmission.IsActive);
                Directory.SetLastWriteTime(timestampCall.Path, timestampCall.LastWriteTime.GetValueOrDefault());
                timestampObservedWithoutModelGuards = !library.IsWriteLockHeldInitializeAll
                    && !library.IsWriteLockHeldInitializeMin
                    && !library.IsWriteLockHeldInitializeBMSFiles;
            };
            dialogService.OnShow = dialogCall =>
            {
                Assert.IsFalse(library.OperationAdmission.IsActive);
                Assert.IsFalse(library.Lr2Synchronization.PlaylistOperationAdmission.IsActive);
                if (dialogCall.Button == MessageBoxButton.YesNo)
                {
                    Assert.AreEqual(MessageBoxResult.No, dialogCall.DefaultResult);
                    if (shutdownDuringConfirmation) { library.RequestShutdown("public_confirmation_shutdown"); }
                }
                if (dialogCall.Button == MessageBoxButton.OK && dialogService.Calls.Count > 1)
                {
                    callbackAfterReleaseObserved = true;
                    Assert.IsFalse(library.IsWriteLockHeldInitializeAll);
                    Assert.IsFalse(library.IsWriteLockHeldInitializeMin);
                    Assert.IsFalse(library.IsWriteLockHeldInitializeBMSFiles);
                    library.InitializeScoresOnly(null);
                }
            };

            library.Initialize(null, null, mode);

            if (shutdownDuringConfirmation)
            {
                Assert.AreEqual(0, fileMutationService.TimestampCalls.Count);
                Assert.AreEqual(1, dialogService.Calls.Count);
                Assert.IsFalse(library.OperationAdmission.IsActive);
                Assert.IsFalse(library.Lr2Synchronization.PlaylistOperationAdmission.IsActive);
                Assert.IsFalse(callbackAfterReleaseObserved);
                Assert.AreEqual(new DateTime(2024, 2, 29, 12, 0, 0), Directory.GetLastWriteTime(folderPath));
                using LR2SongDBExtended unchanged = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
                LR2SongDB.folder saved = unchanged.Table<LR2SongDB.folder>().Single();
                Assert.AreEqual(0, saved.adddate);
                Assert.IsNull(saved.date);
                return;
            }
            Assert.AreEqual(1, fileMutationService.TimestampCalls.Count);
            Assert.IsTrue(timestampObservedWithoutModelGuards);
            Assert.IsTrue(callbackAfterReleaseObserved);
            Assert.AreEqual(fileMutationService.TimestampCalls[0].LastWriteTime, Directory.GetLastWriteTime(folderPath));
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            LR2SongDB.folder persistedFolder = verify.Table<LR2SongDB.folder>().Single();
            Assert.IsNull(persistedFolder.adddate);
            Assert.IsNull(persistedFolder.date);
        });
    }

    [TestMethod]
    public void LoadInstallTable_AssignsBmsonSingleFileWarning()
    {

        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string singleFileDirectoryPath = Path.Combine(lr2RootPath, "Pending");
            Directory.CreateDirectory(singleFileDirectoryPath);
            string singleFileChartPath = Path.Combine(singleFileDirectoryPath, "single_chart.bmson");
            File.WriteAllText(singleFileChartPath, "{\"version\":\"1.0.0\",\"info\":{\"title\":\"Single\",\"artist\":\"Artist\"},\"lines\":[{\"y\":0}]}");

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.install>();
                songDb.InsertOrReplace(new ChartPackage
                {
                    path = singleFileChartPath,
                    delete_parent = false
                }, typeof(LR2SongDBExtended.install));
            }

            var service = new BmsLibraryInitializationService();
            InstallTableLoadResult result = service.LoadInstallTable(
                new BmsLibraryDbGateway(songDbPath),
                [],
                file => false);

            Assert.AreEqual(1, result.PendingPackages.Count);
            Assert.AreEqual(1, result.SingleFileWarningCount);
            Assert.IsTrue(result.PendingPackages[0].ChartEntries[0].Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.SingleBmsonFile));
        });
    }

    /// <summary>song表もschemaもないDBを使い、folder限定の読取りとDB/物理日時の非変更を区別します。</summary>
    [TestMethod]
    public void LeapYearCandidateCapture_ReadsOnlyEligibleFoldersAndDoesNotCreateDatabaseOrSchema()
    {
        WithTemporaryLr2SongDb((lr2Root, dbPath) =>
        {
            string folder = Path.Combine(lr2Root, "Songs", "Candidate");
            Directory.CreateDirectory(folder);
            DateTime sentinel = new(2024, 2, 29, 12, 0, 0);
            Directory.SetLastWriteTime(folder, sentinel);
            using (var db = new LR2SongDBExtended(dbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.Insert(new LR2SongDB.folder { path = folder + Path.DirectorySeparatorChar, type = 1, date = -1, adddate = 0, title = "eligible" });
                db.Insert(new LR2SongDB.folder { path = folder, type = 1, date = null, adddate = -1, title = "abnormal adddate" });
                db.Insert(new LR2SongDB.folder { path = "Songs\\Candidate", type = 1, date = null, adddate = 0, title = "relative" });
                db.Insert(new LR2SongDB.folder { path = folder + "\\.", type = 2, date = null, adddate = 0, title = "other type" });
                db.Insert(new LR2SongDB.folder { path = Path.Combine(lr2Root, "missing"), type = 1, date = null, adddate = 0, title = "missing" });
                db.Insert(new LR2SongDB.folder { path = folder + "\\..\\Candidate", type = 1, date = 123, adddate = 0, title = "ordinary date" });
            }
            byte[] saved = File.ReadAllBytes(dbPath);
            var service = new BmsLibraryInitializationService();
            IReadOnlyList<LeapYearFolderRepairCandidate> candidates = service.CaptureLeapYearFolderRepairCandidates(new BmsLibraryDbGateway(dbPath), out IReadOnlyList<LeapYearFolderRepairCandidate> unreadable);
            Assert.AreEqual(1, candidates.Count);
            Assert.AreEqual(folder, candidates[0].Path);
            Assert.AreEqual(0, unreadable.Count);
            CollectionAssert.AreEqual(saved, File.ReadAllBytes(dbPath));
            Assert.AreEqual(sentinel, Directory.GetLastWriteTime(folder));
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(dbPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(0L, db.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE name IN ('song','chart_info','app_schema');"));
                Assert.AreEqual(6L, db.ExecuteScalar<long>("SELECT COUNT(1) FROM folder;"));
            }
            string absent = Path.Combine(lr2Root, "absent", "song.db");
            Assert.AreEqual(0, service.CaptureLeapYearFolderRepairCandidates(new BmsLibraryDbGateway(absent), out _).Count);
            Assert.IsFalse(File.Exists(absent));
            Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(absent)));
        });
    }

    /// <summary>Noと閉じた確認は非承認として初期化を継続し、元の日時・保存列と検出警告を保持します。</summary>
    [DataTestMethod]
    [DataRow((int)MessageBoxResult.No)]
    [DataRow((int)MessageBoxResult.Cancel)]
    [DataRow((int)MessageBoxResult.None)]
    public void Initialize_LeapYearDeclineKeepsTimestampAndWarnsAfterBothAdmissions(int choice)
    {
        WithTemporaryLr2SongDb((lr2Root, dbPath) =>
        {
            string folder = Path.Combine(lr2Root, "Songs", "Declined");
            Directory.CreateDirectory(folder);
            DateTime sentinel = new(2024, 2, 29, 12, 0, 0);
            Directory.SetLastWriteTime(folder, sentinel);
            using (var db = new LR2SongDBExtended(dbPath))
            {
                db.CreateTable<LR2SongDB.song>();
                db.CreateTable<LR2SongDB.folder>();
                db.Insert(new LR2SongDB.folder { path = folder + Path.DirectorySeparatorChar, type = 1, date = null, adddate = 0, title = "declined" });
            }
            var dialogs = new RecordingDialogService { ResultToReturn = (MessageBoxResult)choice };
            var mutation = new RecordingFileMutationService();
            var library = new TestBmsLibrary(dbPath, null, null, mutation, dialogs, new TestUiScheduler(() => null), () => CreateInitializationOptions(lr2Root));
            library.StartupBackgroundTaskScheduler = (_, _, _, _) => false;
            dialogs.OnShow = _ =>
            {
                Assert.IsFalse(library.OperationAdmission.IsActive);
                Assert.IsFalse(library.Lr2Synchronization.PlaylistOperationAdmission.IsActive);
            };
            library.InitializeStartup(null);
            Assert.AreEqual(0, mutation.TimestampCalls.Count);
            Assert.AreEqual(sentinel, Directory.GetLastWriteTime(folder));
            Assert.AreEqual(1, dialogs.Calls.Count(call => call.Button == MessageBoxButton.YesNo));
            Assert.AreEqual(1, dialogs.Calls.Count(call => call.Message == BeMusicSeeker.Properties.Resources.Warn_LR2LeapYearBugDetected));
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(dbPath).OpenSongDbReadOnly();
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().Single().adddate);
        });
    }

    /// <summary>一部失敗では兄弟成功を保持し、全失敗では検出だけを成功警告へ変えず、受付外で元対象・原因を通知します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Initialize_LeapYearPartialRepairPreservesSuccessAndNotifiesOriginalFailureAfterBothAdmissions(bool allFail)
    {
        WithTemporaryLr2SongDb((lr2Root, dbPath) =>
        {
            string first = Path.Combine(lr2Root, "Songs", "First");
            string second = Path.Combine(lr2Root, "Songs", "Second");
            DateTime sentinel = new(2024, 2, 29, 12, 0, 0);
            foreach (string folder in new[] { first, second }) { Directory.CreateDirectory(folder); Directory.SetLastWriteTime(folder, sentinel); }
            using (var db = new LR2SongDBExtended(dbPath))
            {
                db.CreateTable<LR2SongDB.song>();
                db.CreateTable<LR2SongDB.folder>();
                foreach (string folder in new[] { first, second }) { db.Insert(new LR2SongDB.folder { path = folder + Path.DirectorySeparatorChar, type = 1, date = null, adddate = 0, title = folder }); }
            }
            var original = new IOException("partial repair failure");
            var dialogs = new RecordingDialogService { ResultToReturn = MessageBoxResult.Yes };
            var mutation = new RecordingFileMutationService();
            var library = new TestBmsLibrary(dbPath, null, null, mutation, dialogs, new TestUiScheduler(() => null), () => CreateInitializationOptions(lr2Root));
            library.StartupBackgroundTaskScheduler = (_, _, _, _) => false;
            mutation.OnSetTimestamps = call =>
            {
                Assert.IsTrue(library.OperationAdmission.IsActive);
                Assert.IsTrue(library.Lr2Synchronization.PlaylistOperationAdmission.IsActive);
                if (allFail || call.Path == first) { throw original; }
                Directory.SetLastWriteTime(call.Path, call.LastWriteTime.GetValueOrDefault());
            };
            dialogs.OnShow = _ =>
            {
                Assert.IsFalse(library.OperationAdmission.IsActive);
                Assert.IsFalse(library.Lr2Synchronization.PlaylistOperationAdmission.IsActive);
            };
            library.InitializeStartup(null);
            Assert.AreEqual(2, mutation.TimestampCalls.Count);
            Assert.AreEqual(sentinel, Directory.GetLastWriteTime(first));
            if (allFail) { Assert.AreEqual(sentinel, Directory.GetLastWriteTime(second)); }
            else { Assert.AreNotEqual(sentinel, Directory.GetLastWriteTime(second)); }
            Assert.IsTrue(dialogs.Calls.Any(call => call.Message.Contains(first, StringComparison.Ordinal) && call.Message.Contains(original.Message, StringComparison.Ordinal)));
            Assert.AreEqual(allFail ? 0 : 1, dialogs.Calls.Count(call => call.Message == BeMusicSeeker.Properties.Resources.Warn_LR2LeapYearBugDetected));
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(dbPath).OpenSongDbReadOnly();
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().Single(row => row.title == first).adddate);
            LR2SongDB.folder secondRow = verify.Table<LR2SongDB.folder>().Single(row => row.title == second);
            if (allFail) { Assert.AreEqual(0, secondRow.adddate); }
            else { Assert.IsNull(secondRow.adddate); }
        });
    }

    /// <summary>実修復後の通常DB読込みが失敗しても、保存済み補正と受付外警告を保持し、元の失敗を伝播します。</summary>
    [TestMethod]
    public void Initialize_LeapYearRepairFollowedByCatalogFailureRetainsRepairAndDoesNotBecomeSuccess()
    {
        WithTemporaryLr2SongDb((lr2Root, dbPath) =>
        {
            string folder = Path.Combine(lr2Root, "Songs", "BeforeLoadFailure");
            Directory.CreateDirectory(folder);
            DateTime sentinel = new(2024, 2, 29, 12, 0, 0);
            Directory.SetLastWriteTime(folder, sentinel);
            using (var db = new LR2SongDBExtended(dbPath))
            {
                db.CreateTable<LR2SongDB.song>();
                db.CreateTable<LR2SongDB.folder>();
                db.Insert(new LR2SongDB.folder { path = folder + Path.DirectorySeparatorChar, type = 1, date = null, adddate = 0 });
            }
            var dialogs = new RecordingDialogService { ResultToReturn = MessageBoxResult.Yes };
            var mutation = new RecordingFileMutationService();
            var library = new TestBmsLibrary(dbPath, null, null, mutation, dialogs, new TestUiScheduler(() => null), () => CreateInitializationOptions(lr2Root));
            library.StartupBackgroundTaskScheduler = (_, _, _, _) => false;
            mutation.OnSetTimestamps = call =>
            {
                Assert.IsTrue(library.OperationAdmission.IsActive);
                Assert.IsTrue(library.Lr2Synchronization.PlaylistOperationAdmission.IsActive);
                Directory.SetLastWriteTime(call.Path, call.LastWriteTime.GetValueOrDefault());
                // 次の通常loadで実DBエラーを起こし、修復の部分確定を成功初期化と取り違えない。
                using var db = new LR2SongDBExtended(dbPath);
                db.DropTable<LR2SongDB.song>();
            };
            dialogs.OnShow = call =>
            {
                Assert.IsFalse(library.OperationAdmission.IsActive);
                Assert.IsFalse(library.Lr2Synchronization.PlaylistOperationAdmission.IsActive);
                Assert.IsFalse(library.IsWriteLockHeldInitializeAll);
                Assert.IsFalse(library.IsWriteLockHeldInitializeMin);
            };
            SQLite.SQLiteException failure = Assert.ThrowsException<SQLite.SQLiteException>(() => library.InitializeStartup(null));
            StringAssert.Contains(failure.Message, "song");
            Assert.AreEqual(1, mutation.TimestampCalls.Count);
            Assert.AreNotEqual(sentinel, Directory.GetLastWriteTime(folder));
            Assert.AreEqual(1, dialogs.Calls.Count(call => call.Message == BeMusicSeeker.Properties.Resources.Warn_LR2LeapYearBugDetected));
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(dbPath).OpenSongDbReadOnly();
            Assert.IsNull(verify.Table<LR2SongDB.folder>().Single().adddate);
        });
    }
    private static BmsLibraryOptionsSnapshot CreateInitializationOptions(string lr2RootPath)
    {
        return new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = true,
            LR2RootPath = lr2RootPath,
            ScanBmsFilesOnStartup = false,
            UpdateLr2IrRankingCacheOnStartup = false,
            EnableDownloadLr2IrScoreAndDetectUnsent = false,
            UseBeatorajaScoreDb = false,
            EnableReadOptimizedPragmas = false,
            PendingInstallEstimateMaxParallelPackages = 1
        };
    }

}
