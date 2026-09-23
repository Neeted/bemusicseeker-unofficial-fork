using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.BmsLibraryInitializationTestSupport;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;
namespace BeMusicSeeker.Tests;


[TestClass]
public sealed class BmsLibraryInitializationInstallTests
{
    [TestMethod]
    public void UpsertSongs_FillsLr2FolderAndParentForInstalledBmsRows()
    {
        TestResourceInitializer.EnsureJapaneseResources();
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

            var file = new TestableBmsFile
            {
                path = bmsPath,
                folder = null,
                parent = null
            };
            file.SetHash("cccccccccccccccccccccccccccccccc");

            new BmsLibraryDbGateway(songDbPath).UpsertSongs([file]);

            Assert.IsFalse(string.IsNullOrWhiteSpace(file.folder));
            Assert.IsFalse(string.IsNullOrWhiteSpace(file.parent));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(file.folder, verify.ExecuteScalar<string>("SELECT folder FROM song WHERE path = ?;", bmsPath));
            Assert.AreEqual(file.parent, verify.ExecuteScalar<string>("SELECT parent FROM song WHERE path = ?;", bmsPath));
        });
    }

    [TestMethod]
    public void UpsertSongs_PreservesShiftJisUnsupportedInstalledBmsRowsWithWarning()
    {
        TestResourceInitializer.EnsureJapaneseResources();
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

            var file = new TestableBmsFile
            {
                path = bmsPath,
                folder = null,
                parent = null
            };
            file.SetHash("dddddddddddddddddddddddddddddddd");

            new BmsLibraryDbGateway(songDbPath).UpsertSongs([file]);

            Assert.IsTrue(string.IsNullOrWhiteSpace(file.parent));
            Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.Lr2PathEncodingUnsupported));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", bmsPath));
            Assert.IsTrue(string.IsNullOrWhiteSpace(verify.ExecuteScalar<string>("SELECT parent FROM song WHERE path = ?;", bmsPath)));
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
                var existing = new TestableBmsFile
                {
                    path = bmsPath,
                    date = 100,
                    adddate = 12345,
                    tag = "user-tag"
                };
                existing.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                existing.SetFavorite(7);
                existing.SetTextGroupFlagForTest(1);
                songDbConnection.InsertOrReplace(existing, typeof(LR2SongDB.song));
            }

            var updated = BMSFile.CreateBMSFileFromFile(bmsPath);
            updated.date = 200;

            new BmsLibraryDbGateway(songDbPath).UpsertSongs([updated]);

            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.song row = verify.Table<LR2SongDB.song>().Single();
            Assert.AreEqual(bmsPath, row.path);
            Assert.AreEqual("Updated Title", row.title);
            Assert.AreEqual(updated.hash, row.hash);
            Assert.AreEqual(200, row.date);
            Assert.AreEqual(7, row.favorite);
            Assert.AreEqual(1, row.txt);
            Assert.AreEqual(12345, row.adddate);
            Assert.AreEqual("user-tag", row.tag);
        });
    }

    [TestMethod]
    public void RunInitialize_InvokesAllPhasesAndWaitsForContinuations()
    {
        var service = new BmsLibraryInitializationService();
        int phase1Count = 0;
        int phase2Count = 0;
        int phase3Count = 0;
        int continuationCount = 0;

        InitializationExecutionResult result = service.RunInitialize(
            [
                delegate
                {
                    Interlocked.Increment(ref continuationCount);
                }
            ],
            new SemaphoreSlim(2, 2),
            delegate
            {
                Interlocked.Increment(ref phase1Count);
            },
            delegate
            {
                Interlocked.Increment(ref phase2Count);
            },
            delegate
            {
                Interlocked.Increment(ref phase3Count);
            });

        Assert.AreEqual(1, phase1Count);
        Assert.AreEqual(1, phase2Count);
        Assert.AreEqual(1, phase3Count);
        Assert.AreEqual(1, continuationCount);
        Assert.IsTrue(result.Phase1MinLoadMs >= 0);
        Assert.IsTrue(result.Phase2ScanMaintMs >= 0);
        Assert.IsTrue(result.Phase3InstallMaintenanceMs >= 0);
        Assert.IsTrue(result.WaitContinuationMs >= 0);
        Assert.IsTrue(result.WaitBeforeContinuationStartMs >= 0);
        Assert.IsTrue(result.WaitForContinuationSignalMs >= 0);
        Assert.IsTrue(result.WaitForContinuationTasksMs >= 0);
        Assert.AreEqual(
            result.WaitBeforeContinuationStartMs + result.WaitForContinuationSignalMs + result.WaitForContinuationTasksMs,
            result.WaitContinuationMs);
        Assert.IsTrue(result.TotalMs >= 0);
    }

    [TestMethod]
    public void LoadInstallTable_InitializesPendingWarningsAndCounts()
    {
        TestResourceInitializer.EnsureJapaneseResources();
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

            string installedHash = BMSFile.CreateBMSFileFromFile(directoryChartPath).hash;
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
        TestResourceInitializer.EnsureJapaneseResources();
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
                (_, _) => { },
                _ => { },
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
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.song catalogRow = verify.Table<LR2SongDB.song>().Single();
            Assert.AreEqual(nestedChartPath, catalogRow.path);
            Assert.AreEqual("Keep catalog", catalogRow.title);
        });
    }

    [TestMethod]
    public void LoadInstallTable_CanonicalPathFirstWinsAndStartupCleanupConvergesDatabase()
    {
        TestResourceInitializer.EnsureJapaneseResources();
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
                (_, _) => { },
                _ => { },
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
        TestResourceInitializer.EnsureJapaneseResources();
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
                (_, _) => { },
                _ => { },
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
        TestResourceInitializer.EnsureJapaneseResources();
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
            Assert.IsNull(installedEntry.GetBmsOwnerForTest());
            Assert.IsTrue(installedEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
            Assert.IsNull(unmatchedEntry.GetBmsOwnerForTest());
        });
    }

    [TestMethod]
    public void LoadInstallTable_AddsBmsonResourceWarningWithoutMaterializingCompatibilityAdapter()
    {
        TestResourceInitializer.EnsureJapaneseResources();
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
            Assert.IsNull(entry.GetBmsOwnerForTest());
            Assert.AreEqual(ChartFileKind.Bmson, entry.Chart.Kind);
            Assert.IsTrue(entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
        });
    }

    [TestMethod]
    public void LoadInstallTable_ProjectsResourceHealthForAllPackageEntries()
    {
        TestResourceInitializer.EnsureJapaneseResources();
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
        TestResourceInitializer.EnsureJapaneseResources();
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
            Assert.AreEqual(2, result.PendingPackages[0].GetBmsOwnersForTest().Count);
            PackageChartEntry nestedEntry = result.PendingPackages[0].ChartEntries.Single(entry => Path.GetFileName(entry.Chart.Path).Equals("another.bms", StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(nestedEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.NestedChartFileInPackage));
            Assert.IsTrue(nestedEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            Assert.AreEqual("[2] " + BeMusicSeeker.Properties.Resources.WarningDigest_NestedChart + ", " + BeMusicSeeker.Properties.Resources.WarningDigest_ResourceMissing, ChartWarningTestHelpers.BuildDigestText(nestedEntry));
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(nestedEntry), BeMusicSeeker.Properties.Resources.Warning_NestedChartFileInPackage);
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(nestedEntry), "WAV");
        });
    }

    [TestMethod]
    public void LoadSongTable_DetectsLeapYearFolderTimestampInAnyLeapYear()
    {
        TestResourceInitializer.EnsureJapaneseResources();
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

            SongTableLoadResult result = service.LoadSongTable(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                null,
                fileMutationService,
                null,
                ex => ex.Message);

            Assert.IsTrue(result.LeapYearDetected);
            Assert.AreEqual(1, result.LeapYearRepairCandidates.Count);
            Assert.AreEqual(0, fileMutationService.TimestampCalls.Count);
            Assert.IsFalse(result.UpdatedFolders.Any(folder => string.Equals(folder.path, folderPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
            Assert.AreEqual(folderPath + Path.DirectorySeparatorChar, result.LeapYearRepairCandidates[0].OriginalFolderPath);
            Assert.AreEqual(folderPath, result.LeapYearRepairCandidates[0].Path);
        });
    }

    [TestMethod]
    public void LeapYearFolderRepair_RevalidatesApprovedCandidateAndPersistsAfterTimestampMutation()
    {
        TestResourceInitializer.EnsureJapaneseResources();
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
            SongTableLoadResult loadResult = service.LoadSongTable(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                null,
                null,
                null,
                exception => exception.Message);
            IReadOnlyList<LeapYearFolderRepairCandidate> candidates = loadResult.LeapYearRepairCandidates;
            Assert.AreEqual(1, candidates.Count);
            Assert.AreEqual(folderPath, candidates[0].Path);

            var fileMutationService = new RecordingFileMutationService();
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
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.folder persistedFolder = verify.Table<LR2SongDB.folder>().Single();
            Assert.IsNull(persistedFolder.adddate);
            Assert.IsNull(persistedFolder.date);
        });
    }

    [TestMethod]
    public void LeapYearFolderRepair_SkipsCandidateWhenTimestampChangesAfterCapture()
    {
        TestResourceInitializer.EnsureJapaneseResources();
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
            SongTableLoadResult loadResult = service.LoadSongTable(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                null,
                null,
                null,
                exception => exception.Message);
            IReadOnlyList<LeapYearFolderRepairCandidate> candidates = loadResult.LeapYearRepairCandidates;
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
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.folder persistedFolder = verify.Table<LR2SongDB.folder>().Single();
            Assert.AreEqual(0, persistedFolder.adddate);
            Assert.IsNull(persistedFolder.date);
        });
    }

    [TestMethod]
    public void LeapYearFolderRepair_MissingMutationServiceFailsWithoutPersistingCatalogUpdate()
    {
        TestResourceInitializer.EnsureJapaneseResources();
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
            SongTableLoadResult loadResult = service.LoadSongTable(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                null,
                null,
                null,
                exception => exception.Message);
            IReadOnlyList<LeapYearFolderRepairCandidate> candidates = loadResult.LeapYearRepairCandidates;
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
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.folder persistedFolder = verify.Table<LR2SongDB.folder>().Single();
            Assert.AreEqual(0, persistedFolder.adddate);
            Assert.IsNull(persistedFolder.date);
        });
    }

    [TestMethod]
    public void Initialize_SkipsApprovedLeapYearFolderWhenCatalogIdentityChangesBeforeAdmission()
    {
        TestResourceInitializer.EnsureJapaneseResources();
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
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.folder persistedFolder = verify.Table<LR2SongDB.folder>().Single();
            Assert.AreEqual(0, persistedFolder.adddate);
            Assert.IsNull(persistedFolder.date);
        });
    }

    [TestMethod]
    public void Initialize_RepairsStableLeapYearFolderBeforeDeferredDialogAndAllowsReentry()
    {
        TestResourceInitializer.EnsureJapaneseResources();
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
            fileMutationService.OnSetTimestamps = _ =>
            {
                timestampObservedWithoutModelGuards = !library.IsWriteLockHeldInitializeAll
                    && !library.IsWriteLockHeldInitializeMin
                    && !library.IsWriteLockHeldInitializeBMSFiles;
            };
            dialogService.OnShow = dialogCall =>
            {
                if (dialogCall.Button == MessageBoxButton.OK && dialogService.Calls.Count > 1)
                {
                    callbackAfterReleaseObserved = true;
                    Assert.IsFalse(library.IsWriteLockHeldInitializeAll);
                    Assert.IsFalse(library.IsWriteLockHeldInitializeMin);
                    Assert.IsFalse(library.IsWriteLockHeldInitializeBMSFiles);
                    library.InitializeScoresOnly(null);
                }
            };

            library.Initialize(null, null, BMSLibrary.LibraryInitializeMode.Startup);

            Assert.AreEqual(1, fileMutationService.TimestampCalls.Count);
            Assert.IsTrue(timestampObservedWithoutModelGuards);
            Assert.IsTrue(callbackAfterReleaseObserved);
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.folder persistedFolder = verify.Table<LR2SongDB.folder>().Single();
            Assert.IsNull(persistedFolder.adddate);
            Assert.IsNull(persistedFolder.date);
        });
    }

    [TestMethod]
    public void LoadInstallTable_AssignsBmsonSingleFileWarning()
    {
        TestResourceInitializer.EnsureJapaneseResources();
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
