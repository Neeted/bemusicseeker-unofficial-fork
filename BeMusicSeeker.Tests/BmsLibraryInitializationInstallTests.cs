using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using static BeMusicSeeker.Tests.BmsLibraryInitializationTestSupport;
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

            BMSFile updated = BMSFile.CreateBMSFileFromFile(bmsPath);
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

    [TestMethod]
    public void LoadInstallTable_DatabaseFailureIsPropagated()
    {
        string missingSongDbPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_MissingInitTests_" + Guid.NewGuid().ToString("N"), "song.db");
        var service = new BmsLibraryInitializationService();

        Assert.ThrowsException<SQLite.SQLiteException>(() => service.LoadInstallTable(new BmsLibraryDbGateway(missingSongDbPath)));
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

            var dialogService = new RecordingDialogService
            {
                ResultToReturn = MessageBoxResult.Yes
            };
            var fileMutationService = new RecordingFileMutationService();
            var service = new BmsLibraryInitializationService();

            SongTableLoadResult result = service.LoadSongTable(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                dialogService,
                fileMutationService,
                null,
                ex => ex.Message);

            Assert.IsTrue(result.LeapYearDetected);
            Assert.AreEqual(1, fileMutationService.TimestampCalls.Count);
            Assert.AreEqual(folderPath, fileMutationService.TimestampCalls[0].Path);
            Assert.IsTrue(result.UpdatedFolders.Any(folder => string.Equals(folder.path, folderPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(dialogService.Calls.Any(call => call.Button == MessageBoxButton.YesNo));
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
                file => false);

            Assert.AreEqual(1, result.PendingPackages.Count);
            Assert.AreEqual(1, result.SingleFileWarningCount);
            Assert.IsTrue(result.PendingPackages[0].ChartEntries[0].Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.SingleBmsonFile));
        });
    }

}
