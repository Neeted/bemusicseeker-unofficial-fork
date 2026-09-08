using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Xml.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Livet;
using Microsoft.VisualBasic.FileIO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryPackageInstallServiceTests
{

    [TestMethod]
    public void RemovePendingPackages_DeletesManagedTemporaryPackageSource()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string packageDirectoryPath = TempDirectoryPublisher.Get("pending-remove-test");
            File.WriteAllText(Path.Combine(packageDirectoryPath, "chart.bms"), "#TITLE test");
            var pendingPackage = new ChartPackage
            {
                path = packageDirectoryPath
            };
            var library = new TestBmsLibrary(songDbPath)
            {
                ChartPackagesPending = CreatePackageCollection([pendingPackage])
            };

            library.RemovePendingPackages([pendingPackage]);

            Assert.AreEqual(0, library.ChartPackagesPending.Count);
            Assert.IsFalse(Directory.Exists(packageDirectoryPath));
        });
    }

    [TestMethod]
    public void RemovePendingPackages_DoesNotDeleteUserOwnedPackageSource()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string packageDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker", "session-manual-user-package-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(packageDirectoryPath);
            File.WriteAllText(Path.Combine(packageDirectoryPath, "chart.bms"), "#TITLE test");
            try
            {
                var pendingPackage = new ChartPackage
                {
                    path = packageDirectoryPath
                };
                var library = new TestBmsLibrary(songDbPath)
                {
                    ChartPackagesPending = CreatePackageCollection([pendingPackage])
                };

                library.RemovePendingPackages([pendingPackage]);

                Assert.AreEqual(0, library.ChartPackagesPending.Count);
                Assert.IsTrue(Directory.Exists(packageDirectoryPath));
            }
            finally
            {
                if (Directory.Exists(packageDirectoryPath))
                {
                    Directory.Delete(packageDirectoryPath, recursive: true);
                }
            }
        });
    }

    [TestMethod]
    public void RenamePendingBmsFormatChartFileExtensions_PublishesAfterLeaseReleaseAndIsolatesSubscriberFailure()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "pending-invalid-extension");
            string sourceChartPath = CreateBmsFile(sourceDirectoryPath, "chart.bms", "#TITLE Pending rename");
            string destinationChartPath = Path.Combine(sourceDirectoryPath, "chart.bme");
            BMSFile sourceChart = BMSFile.CreateBMSFileFromFile(sourceChartPath);
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([sourceChart]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;
            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                new RealFileMutationService(),
                new RecordingDialogService());
            library.ChartPackagesPending = CreatePackageCollection([pendingPackage]);

            int publicationCount = 0;
            bool publicationObservedFilesystem = false;
            bool publicationObservedReleasedLease = false;
            library.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(BMSLibrary.ChartPackagesPending))
                {
                    return;
                }
                publicationCount++;
                publicationObservedFilesystem = !File.Exists(sourceChartPath)
                    && File.Exists(destinationChartPath)
                    && library.ChartPackagesPending.Count == 0;
                using LibraryFileMutationLease probe = library.TryBeginLibraryFileMutation(
                    "pending_invalid_extension_publication_probe");
                publicationObservedReleasedLease = probe != null;
                throw new InvalidOperationException("pending collection subscriber failed");
            };

            library.RenamePendingBmsFormatChartFileExtensions(
                [ChartFileProjection.FromBmsFile(sourceChart)],
                ".bme");

            Assert.AreEqual(1, publicationCount);
            Assert.IsTrue(publicationObservedFilesystem);
            Assert.IsTrue(publicationObservedReleasedLease);
            Assert.AreEqual(0, library.ChartPackagesPending.Count);
        });
    }

    [TestMethod]
    public void RenamePendingBmsFormatChartFileExtensions_FlushesEarlierEffectWhenPackageApplyFails()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string firstDirectoryPath = Path.Combine(tempRootPath, "pending-invalid-extension-first");
            string secondDirectoryPath = Path.Combine(tempRootPath, "pending-invalid-extension-second");
            string thirdDirectoryPath = Path.Combine(tempRootPath, "pending-invalid-extension-third");
            string firstChartPath = CreateBmsFile(firstDirectoryPath, "first.bms", "#TITLE First failure");
            string secondChartPath = CreateBmsFile(secondDirectoryPath, "second.bms", "#TITLE Second failure");
            string thirdChartPath = CreateBmsFile(thirdDirectoryPath, "third.bms", "#TITLE Durable rename");
            string thirdDestinationChartPath = Path.Combine(thirdDirectoryPath, "third.bme");
            BMSFile firstChart = BMSFile.CreateBMSFileFromFile(firstChartPath);
            BMSFile secondChart = BMSFile.CreateBMSFileFromFile(secondChartPath);
            BMSFile thirdChart = BMSFile.CreateBMSFileFromFile(thirdChartPath);
            ChartPackage firstPackage = ChartPackageTestExtensions.CreatePackage([firstChart]);
            firstPackage.path = firstDirectoryPath;
            firstPackage.delete_parent = false;
            ChartPackage secondPackage = ChartPackageTestExtensions.CreatePackage([secondChart]);
            secondPackage.path = secondDirectoryPath;
            secondPackage.delete_parent = false;
            ChartPackage thirdPackage = ChartPackageTestExtensions.CreatePackage([thirdChart]);
            thirdPackage.path = thirdDirectoryPath;
            thirdPackage.delete_parent = false;
            var dialogService = new ThrowOnFirstPendingRenameDialogService();
            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                new FailFirstMoveFileMutationService(2),
                dialogService);
            library.ChartPackagesPending = CreatePackageCollection([firstPackage, secondPackage, thirdPackage]);

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.Execute("DROP TABLE install");
            }

            SQLite.SQLiteException primaryFailure = Assert.ThrowsException<SQLite.SQLiteException>(() =>
                library.RenamePendingBmsFormatChartFileExtensions(
                    [
                        ChartFileProjection.FromBmsFile(firstChart),
                        ChartFileProjection.FromBmsFile(secondChart),
                        ChartFileProjection.FromBmsFile(thirdChart)
                    ],
                    ".bme"));

            Assert.IsInstanceOfType<SQLite.SQLiteException>(primaryFailure);
            StringAssert.Contains(primaryFailure.Message, "install");
            Assert.AreEqual(2, dialogService.CallCount);
            Assert.IsTrue(dialogService.LaterFailureWasObserved);
            Assert.IsTrue(File.Exists(firstChartPath));
            Assert.IsTrue(File.Exists(secondChartPath));
            Assert.IsTrue(File.Exists(thirdDestinationChartPath));
            Assert.AreEqual(3, library.ChartPackagesPending.Count);
        });
    }

    [TestMethod]
    public void RenameBMSFilesExtensions_FlushesEarlierFailureAfterCatalogApplyFailure()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "normal-invalid-extension");
            string firstChartPath = CreateBmsFile(sourceDirectoryPath, "first.bms", "#TITLE First failure");
            string secondChartPath = CreateBmsFile(sourceDirectoryPath, "second.bms", "#TITLE Durable rename");
            string secondDestinationChartPath = Path.Combine(sourceDirectoryPath, "second.bme");
            BMSFile firstChart = BMSFile.CreateBMSFileFromFile(firstChartPath);
            BMSFile secondChart = BMSFile.CreateBMSFileFromFile(secondChartPath);
            var dialogService = new BmsLibraryInitializationTestSupport.RecordingDialogService();
            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                new FailFirstMoveFileMutationService(1),
                dialogService);
            OwnedChartCollectionTestSupport.SetLibraryFilesWithoutNotification(library, [firstChart, secondChart]);

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.Execute("DROP TABLE song");
            }

            bool failureDialogObservedReleasedLease = false;
            dialogService.OnShow = _ =>
            {
                using LibraryFileMutationLease probe = library.TryBeginLibraryFileMutation(
                    "normal_invalid_extension_failure_notification_probe",
                    showMessage: false);
                failureDialogObservedReleasedLease = probe != null;
            };

            SQLite.SQLiteException primaryFailure = Assert.ThrowsException<SQLite.SQLiteException>(() =>
                library.RenameBMSFilesExtensions(
                    [
                        ChartFileProjection.FromBmsFile(firstChart),
                        ChartFileProjection.FromBmsFile(secondChart)
                    ],
                    ".bme",
                    unregister: false));

            Assert.IsInstanceOfType<SQLite.SQLiteException>(primaryFailure);
            StringAssert.Contains(primaryFailure.Message, "song");
            Assert.AreEqual(1, dialogService.Calls.Count);
            Assert.IsTrue(failureDialogObservedReleasedLease);
            Assert.IsTrue(File.Exists(firstChartPath));
            Assert.IsFalse(File.Exists(secondChartPath));
            Assert.IsTrue(File.Exists(secondDestinationChartPath));
        });
    }

    [TestMethod]
    public void RenameBMSFilesExtensions_PropagatesDurableAfterCommitFailure()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "normal-invalid-extension-durable-failure");
            string lr2RootPath = Path.Combine(tempRootPath, "LR2beta3");
            string sourceChartPath = CreateBmsFile(sourceDirectoryPath, "chart.bms", "#TITLE Durable failure");
            string destinationChartPath = Path.Combine(sourceDirectoryPath, "chart.bme");
            BMSFile chart = BMSFile.CreateBMSFileFromFile(sourceChartPath);
            LR2Config lr2Config = BmsPlaylistTestSupport.CreateLr2Config(lr2RootPath, sourceDirectoryPath);
            var library = new TestBmsLibrary(
                songDbPath,
                () => lr2Config,
                null,
                new RealFileMutationService(),
                new RecordingDialogService(),
                new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                () => new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true
                });
            library.SearchTargets = [sourceDirectoryPath];
            OwnedChartCollectionTestSupport.SetLibraryFilesWithoutNotification(library, [chart]);

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.Execute("DROP TABLE folder");
                songDb.Execute("CREATE TABLE folder (x TEXT)");
                songDb.Commit();
            }

            SQLite.SQLiteException durableFailure = Assert.ThrowsException<SQLite.SQLiteException>(() =>
                library.RenameBMSFilesExtensions(
                    [ChartFileProjection.FromBmsFile(chart)],
                    ".bme",
                    unregister: false));

            Assert.IsNotNull(durableFailure);
            Assert.IsFalse(File.Exists(sourceChartPath));
            Assert.IsTrue(File.Exists(destinationChartPath));
            Assert.AreEqual(0, library.BMSFiles.Count(file => string.Equals(file.path, sourceChartPath, StringComparison.OrdinalIgnoreCase)));
            Assert.AreEqual(1, library.BMSFiles.Count(file => string.Equals(file.path, destinationChartPath, StringComparison.OrdinalIgnoreCase)));
            using var verifySongDb = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ?;", sourceChartPath));
            Assert.AreEqual(1, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ?;", destinationChartPath));
        });
    }

    [TestMethod]
    public void GetPendingPackagesContainingOnlyInstalledCharts_UsesPackageChartEntries()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryPackageInstallService();
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "package");
            Directory.CreateDirectory(packageDirectoryPath);
            string bmsonPath = Path.Combine(packageDirectoryPath, "chart.bmson");
            File.WriteAllText(bmsonPath, "{\"info\":{\"title\":\"Song\",\"mode_hint\":\"beat-7k\"},\"sound_channels\":[]}");
            var package = new ChartPackage
            {
                path = packageDirectoryPath
            };

            List<ChartPackage> result = service.GetPendingPackagesContainingOnlyInstalledCharts(
                [package],
                chart => chart?.Kind == ChartFileKind.Bmson && string.Equals(chart.Path, bmsonPath, StringComparison.OrdinalIgnoreCase));

            Assert.AreEqual(1, result.Count);
            Assert.AreSame(package, result[0]);
        });
    }

    [TestMethod]
    public void GetPendingPackagesContainingOnlyInstalledCharts_MatchesInstalledBmsonByChartEntryHash()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string packageDirectoryPath = Path.Combine(tempRootPath, "package");
            Directory.CreateDirectory(packageDirectoryPath);
            string bmsonPath = Path.Combine(packageDirectoryPath, "chart.bmson");
            File.WriteAllText(bmsonPath, "{\"info\":{\"title\":\"Song\",\"mode_hint\":\"beat-7k\"},\"sound_channels\":[]}");
            LR2SongDBExtended.bmson_song installedBmson = BmsonSongParser.Parse(bmsonPath);
            var pendingPackage = new ChartPackage
            {
                path = packageDirectoryPath
            };
            var library = new TestBmsLibrary(songDbPath)
            {
                BmsonSongs = [installedBmson],
                ChartPackagesPending = CreatePackageCollection([pendingPackage])
            };

            List<ChartPackage> result = library.GetPendingPackagesContainingOnlyInstalledCharts();

            Assert.AreEqual(1, result.Count);
            Assert.AreSame(pendingPackage, result[0]);
        });
    }

    /// <summary>
    /// R5-01: 混在 package の既所持譜面は、source cleanup 設定が OFF なら
    /// 保持し、ON なら実 FS/DB で確認できる所持コピーを根拠に削除します。
    /// </summary>
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, true)]
    public void InstallPendingPackagesToEstimatedDestinations_MixedPackageSourceCleanupHonorsSetting(
        bool deletePendingPackageSourceAfterInstall,
        bool expectSourceCleanup)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string destinationDirectoryPath = Path.Combine(tempRootPath, "r5-01-installed");
            string sourceDirectoryPath = Path.Combine(tempRootPath, "r5-01-pending");
            string installedChartPath = CreateBmsFile(destinationDirectoryPath, "owned.bms", "#TITLE R5-01 Owned");
            string sourceOwnedChartPath = CreateBmsFile(sourceDirectoryPath, "owned.bms", "#TITLE R5-01 Owned");
            string sourceNewChartPath = CreateBmsFile(sourceDirectoryPath, "new.bms", "#TITLE R5-01 New");
            BMSFile installedChart = BMSFile.CreateBMSFileFromFile(installedChartPath);
            BMSFile sourceOwnedChart = BMSFile.CreateBMSFileFromFile(sourceOwnedChartPath);
            BMSFile sourceNewChart = BMSFile.CreateBMSFileFromFile(sourceNewChartPath);
            Assert.AreEqual(installedChart.hash, sourceOwnedChart.hash);
            using (var seedSongDb = new LR2SongDBExtended(songDbPath))
            {
                seedSongDb.InsertOrReplace(
                    installedChart.CreateSongRowPersistenceCopy(),
                    typeof(LR2SongDB.song));
            }

            ChartPackage package = ChartPackageTestExtensions.CreatePackage(
                ChartPackageTestExtensions.CreateEntryWithInstallDestination(sourceOwnedChart, destinationDirectoryPath),
                ChartPackageTestExtensions.CreateEntryWithInstallDestination(sourceNewChart, destinationDirectoryPath));
            package.path = sourceDirectoryPath;
            package.delete_parent = false;

            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                new RealFileMutationService(),
                new RecordingDialogService(),
                new TestUiScheduler(() => null),
                () => new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = false,
                    BMSInstallDir = destinationDirectoryPath,
                    FolderNameFormat = "%TITLE%",
                    DeletePendingPackageSourceAfterInstall = deletePendingPackageSourceAfterInstall,
                    EnableSmartComponentOverwrite = false,
                    KeepSmartOverwriteProtectedFilesByRenaming = false
                })
            {
                BMSFiles = [installedChart],
                BmsonSongs = [],
                ChartPackagesPending = CreatePackageCollection([package]),
                ChartPackagesInstalled = CreatePackageCollection([])
            };

            PendingInstallBatchResult result = library.InstallPendingPackagesToEstimatedDestinationsWithReceipt([package]);

            Assert.IsTrue(result.HasDurableCommit);
            Assert.IsFalse(result.FailedPackages.Contains(package));
            Assert.IsTrue(File.Exists(installedChartPath));
            Assert.IsTrue(File.Exists(Path.Combine(destinationDirectoryPath, "new.bms")));
            Assert.AreEqual(expectSourceCleanup, !File.Exists(sourceOwnedChartPath));
            Assert.AreEqual(expectSourceCleanup, !Directory.Exists(sourceDirectoryPath));
            using var verifySongDb = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1, verifySongDb.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM song WHERE path = ?;",
                installedChartPath));
            Assert.AreEqual(1, verifySongDb.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM song WHERE path = ?;",
                Path.Combine(destinationDirectoryPath, "new.bms")));
        });
    }

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public void EstimatedCleanupKeepsNormalAdviceButDefersMixedAbnormalAdviceToTerminal(bool reportAtTerminal, bool cleanupFails)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb((dbPath, root) =>
        {
            string installedDirectory = Path.Combine(root, "installed");
            string firstSource = Path.Combine(root, "pending-first");
            string secondSource = Path.Combine(root, "pending-second");
            BMSFile installed = BMSFile.CreateBMSFileFromFile(CreateBmsFile(installedDirectory, "chart.bms", "#TITLE AlreadyInstalled"));
            BMSFile firstChart = BMSFile.CreateBMSFileFromFile(CreateBmsFile(firstSource, "chart.bms", "#TITLE AlreadyInstalled"));
            BMSFile secondChart = BMSFile.CreateBMSFileFromFile(CreateBmsFile(secondSource, "chart.bms", "#TITLE AlreadyInstalled"));
            var first = ChartPackageTestExtensions.CreatePackage(
                ChartPackageTestExtensions.CreateEntryWithInstallDestination(firstChart, installedDirectory));
            var second = ChartPackageTestExtensions.CreatePackage(
                ChartPackageTestExtensions.CreateEntryWithInstallDestination(secondChart, installedDirectory));
            first.path = firstSource;
            second.path = secondSource;
            first.delete_parent = second.delete_parent = false;
            var dialogs = new FileDbReportRecordingDialogs();
            IFileMutationService files = cleanupFails ? new FailingDestinationDeleteFileMutationService(secondSource)
                : new RealFileMutationService();
            var library = new TestBmsLibrary(dbPath, null, null, files, dialogs,
                new TestUiScheduler(() => null),
                () => new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = false,
                    BMSInstallDir = installedDirectory,
                    FolderNameFormat = "%TITLE%",
                    DeletePendingPackageSourceAfterInstall = true
                })
            {
                BMSFiles = [installed],
                ChartPackagesPending = CreatePackageCollection([first, second]),
                ChartPackagesInstalled = CreatePackageCollection([])
            };
            PendingInstallBatchResult result = library.InstallPendingPackagesToEstimatedDestinationsWithReceipt(
                [first, second], reportAtTerminal: reportAtTerminal);
            Assert.AreEqual(2, result.CleanupOnlySucceeded);
            Assert.AreEqual(2, result.MutationReceipt.Receipts.Count);
            Assert.IsTrue(result.MutationReceipt.Receipts.All(receipt => receipt.DurableCommit));
            Assert.AreEqual(cleanupFails, result.CompletedWithCleanupFailure);
            Assert.AreEqual(reportAtTerminal && cleanupFails ? 0 : 1, dialogs.ModelMessages);
            Assert.IsFalse(Directory.Exists(firstSource));
            Assert.AreEqual(cleanupFails, Directory.Exists(secondSource));
            Assert.AreEqual(0, library.ChartPackagesPending.Count);
        });
    }

    /// <summary>
    /// 全譜面の推定先が空の先行パッケージが、後続パッケージの共通譜面を
    /// 既所持として誤除外しないことを、実ファイルと song.db で検証します。
    /// </summary>
    [TestMethod]
    public void InstallPendingPackagesToEstimatedDestinations_EmptyDestinationPackageDoesNotReserveHashForLaterPackage()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string destinationDirectoryPath = Path.Combine(tempRootPath, "installed-r2-02");
            string firstSourceDirectoryPath = Path.Combine(tempRootPath, "pending-empty-destination");
            string secondSourceDirectoryPath = Path.Combine(tempRootPath, "pending-valid-destination");
            string firstChartPath = CreateBmsFile(firstSourceDirectoryPath, "shared.bms", "#TITLE Shared");
            string secondSharedChartPath = CreateBmsFile(secondSourceDirectoryPath, "shared.bms", "#TITLE Shared");
            string secondUniqueChartPath = CreateBmsFile(secondSourceDirectoryPath, "unique.bms", "#TITLE Unique");
            BMSFile firstChart = BMSFile.CreateBMSFileFromFile(firstChartPath);
            BMSFile secondSharedChart = BMSFile.CreateBMSFileFromFile(secondSharedChartPath);
            BMSFile secondUniqueChart = BMSFile.CreateBMSFileFromFile(secondUniqueChartPath);
            Assert.AreEqual(firstChart.hash, secondSharedChart.hash);

            ChartPackage firstPackage = ChartPackageTestExtensions.CreatePackage([firstChart]);
            firstPackage.path = firstSourceDirectoryPath;
            firstPackage.delete_parent = false;
            firstPackage.ChartEntries.Single().SetWarning(
                ChartWarningKind.InstallEstimationAmbiguous,
                "keep warning");
            string firstWarningDigest = ChartWarningTestHelpers.BuildDigestText(
                firstPackage.ChartEntries.Single());
            ChartPackage secondPackage = ChartPackageTestExtensions.CreatePackage([
                ChartPackageTestExtensions.CreateEntryWithInstallDestination(secondSharedChart, destinationDirectoryPath),
                ChartPackageTestExtensions.CreateEntryWithInstallDestination(secondUniqueChart, destinationDirectoryPath)]);
            secondPackage.path = secondSourceDirectoryPath;
            secondPackage.delete_parent = false;

            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                new RealFileMutationService(),
                new RecordingDialogService(),
                new TestUiScheduler(() => null),
                () => new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = false,
                    BMSInstallDir = destinationDirectoryPath,
                    FolderNameFormat = "%TITLE%",
                    DeletePendingPackageSourceAfterInstall = false,
                    EnableSmartComponentOverwrite = false,
                    KeepSmartOverwriteProtectedFilesByRenaming = false
                })
            {
                BMSFiles = [],
                BmsonSongs = [],
                ChartPackagesPending = CreatePackageCollection([firstPackage, secondPackage]),
                ChartPackagesInstalled = CreatePackageCollection([])
            };

            PendingInstallBatchResult result = library.InstallPendingPackagesToEstimatedDestinationsWithReceipt(
                [firstPackage, secondPackage]);

            Assert.IsTrue(File.Exists(Path.Combine(destinationDirectoryPath, "shared.bms")));
            Assert.IsTrue(File.Exists(Path.Combine(destinationDirectoryPath, "unique.bms")));
            Assert.IsTrue(File.Exists(firstChartPath));
            Assert.AreEqual(1, library.ChartPackagesPending.Count);
            Assert.AreSame(firstPackage, library.ChartPackagesPending.Single());
            Assert.AreEqual(string.Empty, firstPackage.ChartEntries.Single().Chart.InstallDestination);
            Assert.AreEqual(
                firstWarningDigest,
                ChartWarningTestHelpers.BuildDigestText(firstPackage.ChartEntries.Single()));

            string installedSharedPath = Path.Combine(destinationDirectoryPath, "shared.bms");
            string installedUniquePath = Path.Combine(destinationDirectoryPath, "unique.bms");
            Assert.IsTrue(library.BMSFiles.Any(file => string.Equals(file.path, installedSharedPath, StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(library.BMSFiles.Any(file => string.Equals(file.path, installedUniquePath, StringComparison.OrdinalIgnoreCase)));
            using var verifySongDb = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ?;", installedSharedPath));
            Assert.AreEqual(1, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ?;", installedUniquePath));
            Assert.IsTrue(result.MutationReceipt.HasDurableCommit);
            Assert.IsFalse(result.FailedPackages.Contains(secondPackage));
        });
    }

    /// <summary>
    /// 推定先移動は package を入力順に一件ずつ durable receipt まで確定し、
    /// manual recovery に到達した package の後ろを実行しません。
    /// </summary>
    [TestMethod]
    public void InstallPendingPackagesToEstimatedDestinations_AppliesDurablePrefixBeforeManualRecoveryStopsSuffix()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string installRootPath = Path.Combine(tempRootPath, "estimated-prefix-installed");
            string firstSourceDirectoryPath = Path.Combine(tempRootPath, "estimated-prefix-first");
            string secondSourceDirectoryPath = Path.Combine(tempRootPath, "estimated-prefix-second");
            string thirdSourceDirectoryPath = Path.Combine(tempRootPath, "estimated-prefix-third");
            string firstDestinationDirectoryPath = Path.Combine(installRootPath, "D1");
            string secondDestinationDirectoryPath = Path.Combine(installRootPath, "D2");
            string thirdDestinationDirectoryPath = firstDestinationDirectoryPath;
            string firstChartPath = CreateBmsFile(firstSourceDirectoryPath, "first.bms", "#TITLE Estimated Prefix First");
            string secondChartPath = CreateBmsFile(secondSourceDirectoryPath, "second.bms", "#TITLE Estimated Prefix Second");
            string thirdChartPath = CreateBmsFile(thirdSourceDirectoryPath, "third.bms", "#TITLE Estimated Prefix Third");
            string secondDestinationChartPath = Path.Combine(secondDestinationDirectoryPath, "second.bms");

            using (var seedSongDb = new LR2SongDBExtended(songDbPath))
            {
                string escapedSecondDestinationChartPath = secondDestinationChartPath.Replace("'", "''");
                seedSongDb.Execute(
                    "CREATE TRIGGER fail_second_estimated_install_target BEFORE INSERT ON song WHEN NEW.path = '"
                    + escapedSecondDestinationChartPath
                    + "' BEGIN SELECT RAISE(ABORT, 'forced second estimated-install target failure'); END;");
            }

            ChartPackage firstPackage = ChartPackageTestExtensions.CreatePackage(
                ChartPackageTestExtensions.CreateEntryWithInstallDestination(
                    BMSFile.CreateBMSFileFromFile(firstChartPath),
                    firstDestinationDirectoryPath));
            ChartPackage secondPackage = ChartPackageTestExtensions.CreatePackage(
                ChartPackageTestExtensions.CreateEntryWithInstallDestination(
                    BMSFile.CreateBMSFileFromFile(secondChartPath),
                    secondDestinationDirectoryPath));
            ChartPackage thirdPackage = ChartPackageTestExtensions.CreatePackage(
                ChartPackageTestExtensions.CreateEntryWithInstallDestination(
                    BMSFile.CreateBMSFileFromFile(thirdChartPath),
                    thirdDestinationDirectoryPath));
            firstPackage.path = firstSourceDirectoryPath;
            secondPackage.path = secondSourceDirectoryPath;
            thirdPackage.path = thirdSourceDirectoryPath;
            firstPackage.delete_parent = secondPackage.delete_parent = thirdPackage.delete_parent = false;

            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                new FailingDestinationDeleteFileMutationService(secondDestinationChartPath),
                new RecordingDialogService(),
                new TestUiScheduler(() => null),
                () => new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = false,
                    BMSInstallDir = installRootPath,
                    FolderNameFormat = "%TITLE%",
                    DeletePendingPackageSourceAfterInstall = false,
                    EnableSmartComponentOverwrite = false,
                    KeepSmartOverwriteProtectedFilesByRenaming = false
                })
            {
                BMSFiles = [],
                BmsonSongs = [],
                ChartPackagesPending = CreatePackageCollection([firstPackage, secondPackage, thirdPackage]),
                ChartPackagesInstalled = CreatePackageCollection([])
            };

            PendingInstallBatchResult result = library.InstallPendingPackagesToEstimatedDestinationsWithReceipt(
                [firstPackage, secondPackage, thirdPackage]);

            Assert.IsTrue(result.HasDurableCommit);
            Assert.IsTrue(result.ManualRecoveryRequired);
            Assert.AreEqual(2, result.MutationReceipt.Receipts.Count);
            Assert.AreEqual(FileDbMutationTerminalState.Completed, result.MutationReceipt.Receipts[0].TerminalState);
            Assert.AreEqual(FileDbMutationTerminalState.ManualRecoveryRequired, result.MutationReceipt.Receipts[1].TerminalState);
            Assert.AreEqual(1, result.PendingPackagesToRemove.Count);
            Assert.IsTrue(result.PendingPackagesToRemove.Contains(firstPackage));
            Assert.IsTrue(result.FailedPackages.Contains(secondPackage));
            Assert.IsFalse(result.FailedPackages.Contains(thirdPackage));
            Assert.IsTrue(library.ChartPackagesInstalled.Any(package =>
                string.Equals(package.path, firstDestinationDirectoryPath, StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(library.ChartPackagesPending.Any(package => ReferenceEquals(package, secondPackage)));
            Assert.IsTrue(library.ChartPackagesPending.Any(package => ReferenceEquals(package, thirdPackage)));
            Assert.IsTrue(File.Exists(Path.Combine(firstDestinationDirectoryPath, "first.bms")));
            Assert.IsTrue(Directory.Exists(secondDestinationDirectoryPath));
            Assert.IsFalse(File.Exists(Path.Combine(thirdDestinationDirectoryPath, "third.bms")));

            using var verifySongDb = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(
                1,
                verifySongDb.ExecuteScalar<int>(
                    "SELECT COUNT(1) FROM song WHERE path = ?;",
                    Path.Combine(firstDestinationDirectoryPath, "first.bms")));
            Assert.AreEqual(
                0,
                verifySongDb.ExecuteScalar<int>(
                    "SELECT COUNT(1) FROM song WHERE path = ?;",
                    secondDestinationChartPath));
            Assert.AreEqual(firstSourceDirectoryPath, firstPackage.path, ignoreCase: true);
            Assert.AreEqual(secondSourceDirectoryPath, secondPackage.path, ignoreCase: true);
            Assert.AreEqual(thirdSourceDirectoryPath, thirdPackage.path, ignoreCase: true);
        });
    }

    /// <summary>
    /// DB precommit failure は hash を durable guard に追加せず、後続の同一 hash
    /// package が入力順に再試行できることを実FSと song.dbで検証します。
    /// </summary>
    [TestMethod]
    public void InstallPendingPackagesToEstimatedDestinations_DoesNotReserveHashAfterPrecommitFailure()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string destinationDirectoryPath = Path.Combine(tempRootPath, "estimated-retry-installed");
            string firstSourceDirectoryPath = Path.Combine(tempRootPath, "estimated-retry-first");
            string secondSourceDirectoryPath = Path.Combine(tempRootPath, "estimated-retry-second");
            string firstChartPath = CreateBmsFile(firstSourceDirectoryPath, "first.bms", "#TITLE Estimated Retry");
            string secondChartPath = CreateBmsFile(secondSourceDirectoryPath, "second.bms", "#TITLE Estimated Retry");
            BMSFile firstChart = BMSFile.CreateBMSFileFromFile(firstChartPath);
            BMSFile secondChart = BMSFile.CreateBMSFileFromFile(secondChartPath);
            Assert.AreEqual(firstChart.hash, secondChart.hash);
            string firstDestinationChartPath = Path.Combine(destinationDirectoryPath, "first.bms");
            string secondDestinationChartPath = Path.Combine(destinationDirectoryPath, "second.bms");

            using (var seedSongDb = new LR2SongDBExtended(songDbPath))
            {
                string escapedFirstDestinationChartPath = firstDestinationChartPath.Replace("'", "''");
                seedSongDb.Execute(
                    "CREATE TRIGGER fail_first_estimated_retry_target BEFORE INSERT ON song WHEN NEW.path = '"
                    + escapedFirstDestinationChartPath
                    + "' BEGIN SELECT RAISE(ABORT, 'forced first estimated-retry target failure'); END;");
            }

            ChartPackage firstPackage = ChartPackageTestExtensions.CreatePackage(
                ChartPackageTestExtensions.CreateEntryWithInstallDestination(firstChart, destinationDirectoryPath));
            ChartPackage secondPackage = ChartPackageTestExtensions.CreatePackage(
                ChartPackageTestExtensions.CreateEntryWithInstallDestination(secondChart, destinationDirectoryPath));
            firstPackage.path = firstSourceDirectoryPath;
            secondPackage.path = secondSourceDirectoryPath;
            firstPackage.delete_parent = secondPackage.delete_parent = false;

            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                new RealFileMutationService(),
                new RecordingDialogService(),
                new TestUiScheduler(() => null),
                () => new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = false,
                    BMSInstallDir = destinationDirectoryPath,
                    FolderNameFormat = "%TITLE%",
                    DeletePendingPackageSourceAfterInstall = false,
                    EnableSmartComponentOverwrite = false,
                    KeepSmartOverwriteProtectedFilesByRenaming = false
                })
            {
                BMSFiles = [],
                BmsonSongs = [],
                ChartPackagesPending = CreatePackageCollection([firstPackage, secondPackage]),
                ChartPackagesInstalled = CreatePackageCollection([])
            };

            PendingInstallBatchResult result = library.InstallPendingPackagesToEstimatedDestinationsWithReceipt(
                [firstPackage, secondPackage]);

            Assert.IsTrue(result.HasDurableCommit);
            Assert.IsFalse(result.ManualRecoveryRequired);
            Assert.AreEqual(2, result.MutationReceipt.Receipts.Count);
            Assert.AreEqual(FileDbMutationTerminalState.Failed, result.MutationReceipt.Receipts[0].TerminalState);
            Assert.AreEqual(FileDbMutationTerminalState.Completed, result.MutationReceipt.Receipts[1].TerminalState);
            Assert.AreEqual(1, result.FailedPackages.Count);
            Assert.AreSame(firstPackage, result.FailedPackages[0]);
            Assert.AreEqual(1, result.PendingPackagesToRemove.Count);
            Assert.IsTrue(result.PendingPackagesToRemove.Contains(secondPackage));
            Assert.IsTrue(library.ChartPackagesPending.Any(package => ReferenceEquals(package, firstPackage)));
            Assert.IsFalse(library.ChartPackagesPending.Any(package => ReferenceEquals(package, secondPackage)));
            Assert.IsFalse(File.Exists(firstDestinationChartPath));
            Assert.IsTrue(File.Exists(secondDestinationChartPath));
            Assert.IsTrue(File.Exists(firstChartPath));
            Assert.IsTrue(library.BMSFiles.Any(file => string.Equals(file.path, secondDestinationChartPath, StringComparison.OrdinalIgnoreCase)));
            Assert.AreEqual(string.Empty, secondPackage.ChartEntries.Single().Chart.InstallDestination);

            using var verifySongDb = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ?;", firstDestinationChartPath));
            Assert.AreEqual(1, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ?;", secondDestinationChartPath));
        });
    }

    /// <summary>
    /// 実 filesystem executor の durable finalizer failure は、durable factsを
    /// 保持したまま estimated batch の後続 package を停止させます。
    /// </summary>
    [TestMethod]
    public void ExecuteEstimatedInstallBatchPlan_StopsAfterDurableFinalizationFailure()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string firstSourceDirectoryPath = Path.Combine(tempDirectoryPath, "estimated-finalizer-first");
            string secondSourceDirectoryPath = Path.Combine(tempDirectoryPath, "estimated-finalizer-second");
            string thirdSourceDirectoryPath = Path.Combine(tempDirectoryPath, "estimated-finalizer-third");
            string firstDestinationDirectoryPath = Path.Combine(tempDirectoryPath, "estimated-finalizer-installed-first");
            string secondDestinationDirectoryPath = Path.Combine(tempDirectoryPath, "estimated-finalizer-installed-second");
            string thirdDestinationDirectoryPath = Path.Combine(tempDirectoryPath, "estimated-finalizer-installed-third");
            string firstChartPath = CreateBmsFile(firstSourceDirectoryPath, "first.bms", "#TITLE Estimated Finalizer First");
            string secondChartPath = CreateBmsFile(secondSourceDirectoryPath, "second.bms", "#TITLE Estimated Finalizer Second");
            string thirdChartPath = CreateBmsFile(thirdSourceDirectoryPath, "third.bms", "#TITLE Estimated Finalizer Third");
            ChartPackage firstPackage = ChartPackageTestExtensions.CreatePackage(
                ChartPackageTestExtensions.CreateEntryWithInstallDestination(
                    BMSFile.CreateBMSFileFromFile(firstChartPath),
                    firstDestinationDirectoryPath));
            ChartPackage secondPackage = ChartPackageTestExtensions.CreatePackage(
                ChartPackageTestExtensions.CreateEntryWithInstallDestination(
                    BMSFile.CreateBMSFileFromFile(secondChartPath),
                    secondDestinationDirectoryPath));
            ChartPackage thirdPackage = ChartPackageTestExtensions.CreatePackage(
                ChartPackageTestExtensions.CreateEntryWithInstallDestination(
                    BMSFile.CreateBMSFileFromFile(thirdChartPath),
                    thirdDestinationDirectoryPath));
            firstPackage.path = firstSourceDirectoryPath;
            secondPackage.path = secondSourceDirectoryPath;
            thirdPackage.path = thirdSourceDirectoryPath;
            firstPackage.delete_parent = secondPackage.delete_parent = thirdPackage.delete_parent = false;

            var service = new BmsLibraryPackageInstallService();
            PendingInstallBatchPlan plan = service.BuildEstimatedInstallBatchPlan(
                [firstPackage, secondPackage, thirdPackage],
                [firstPackage, secondPackage, thirdPackage],
                CreateInstalledChartLookup([]));
            var finalizationFailure = new InvalidOperationException("estimated-finalizer-failure");
            int callbackCount = 0;

            PendingInstallBatchResult result = service.ExecuteEstimatedInstallBatchPlan(
                plan,
                deletePendingPackageSourceAfterInstall: false,
                installPackage: item =>
                {
                    callbackCount++;
                    return service.InstallPackagesWithFileMutationReceipts(
                        [item.InstallWorkPackage],
                        item.DestinationDirectory,
                        (package, destinationDirectory, _, hashSnapshot, _, excludedComponentPaths, applyDurableCommit) =>
                            service.MovePackageFilesWithReceipt(
                                package,
                                destinationDirectory,
                                new BmsLibraryOptionsSnapshot
                                {
                                    EnableSmartComponentOverwrite = false,
                                    KeepSmartOverwriteProtectedFilesByRenaming = false
                                },
                                (_, _) => destinationDirectory,
                                exception => exception.Message,
                                new RealFileMutationService(),
                                null,
                                new FileMutationOptions(ReadOnlyNormalizationScope.TargetOnly),
                                new FileMutationOptions(ReadOnlyNormalizationScope.RecursiveDirectoryTree),
                                 _ => { },
                                 applyDurableCommit,
                                 sourceCleanupPolicy: PackageSourceCleanupPolicy.PreserveUnconsumedContents,
                                 showMessageBoxOnInstallFail: false,
                                 existingHashes: hashSnapshot,
                                excludedComponentPaths: excludedComponentPaths),
                        _ => FileDbMutationCommitResult.Durable(),
                        _ =>
                        {
                            if (ReferenceEquals(item.OriginalPackage, secondPackage))
                            {
                                throw finalizationFailure;
                            }
                        },
                        _ => { },
                        _ => { },
                        sourceCleanupPolicy: PackageSourceCleanupPolicy.PreserveUnconsumedContents,
                        existingHashes: plan.MoveGuardLookup);
                },
                createInstalledDisplayPackage: null,
                cleanupPendingPackageSource: null);

            Assert.AreEqual(2, callbackCount);
            Assert.IsTrue(result.HasDurableCommit);
            Assert.IsTrue(result.HasDurableFinalizationFailure);
            Assert.AreEqual(2, result.MutationReceipt.Receipts.Count);
            Assert.AreEqual(FileDbMutationTerminalState.Completed, result.MutationReceipt.Receipts[0].TerminalState);
            Assert.AreEqual(FileDbMutationTerminalState.Completed, result.MutationReceipt.Receipts[1].TerminalState);
            Assert.AreSame(finalizationFailure, result.MutationReceipt.FinalizationFailure);
            Assert.AreEqual(1, result.PendingPackagesToRemove.Count);
            Assert.IsTrue(result.PendingPackagesToRemove.Contains(firstPackage));
            Assert.AreEqual(1, result.FailedPackages.Count);
            Assert.AreSame(secondPackage, result.FailedPackages[0]);
            Assert.IsTrue(File.Exists(Path.Combine(firstDestinationDirectoryPath, "first.bms")));
            Assert.IsTrue(File.Exists(Path.Combine(secondDestinationDirectoryPath, "second.bms")));
            Assert.IsFalse(File.Exists(Path.Combine(thirdDestinationDirectoryPath, "third.bms")));
            Assert.IsTrue(File.Exists(thirdChartPath));
        });
    }

    /// <summary>
    /// 先行導入後の cleanup-only 候補もその場で receipt を確定し、
    /// durable finalization failure で後続候補を停止します。
    /// </summary>
    [TestMethod]
    public void ExecuteEstimatedInstallBatchPlan_StopsAfterCleanupOnlyDurableFinalizationFailure()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string firstSourceDirectoryPath = Path.Combine(tempRootPath, "estimated-cleanup-finalizer-first");
            string secondSourceDirectoryPath = Path.Combine(tempRootPath, "estimated-cleanup-finalizer-second");
            string thirdSourceDirectoryPath = Path.Combine(tempRootPath, "estimated-cleanup-finalizer-third");
            string firstDestinationDirectoryPath = Path.Combine(tempRootPath, "estimated-cleanup-finalizer-installed-first");
            string secondDestinationDirectoryPath = Path.Combine(tempRootPath, "estimated-cleanup-finalizer-installed-second");
            string thirdDestinationDirectoryPath = Path.Combine(tempRootPath, "estimated-cleanup-finalizer-installed-third");
            string firstChartPath = CreateBmsFile(firstSourceDirectoryPath, "first.bms", "#TITLE Estimated Cleanup Finalizer Shared");
            string secondChartPath = CreateBmsFile(secondSourceDirectoryPath, "second.bms", "#TITLE Estimated Cleanup Finalizer Shared");
            string thirdChartPath = CreateBmsFile(thirdSourceDirectoryPath, "third.bms", "#TITLE Estimated Cleanup Finalizer Suffix");
            BMSFile firstChart = BMSFile.CreateBMSFileFromFile(firstChartPath);
            BMSFile secondChart = BMSFile.CreateBMSFileFromFile(secondChartPath);
            Assert.AreEqual(firstChart.hash, secondChart.hash);
            ChartPackage firstPackage = ChartPackageTestExtensions.CreatePackage(
                ChartPackageTestExtensions.CreateEntryWithInstallDestination(
                    firstChart,
                    firstDestinationDirectoryPath));
            ChartPackage cleanupOnlyPackage = ChartPackageTestExtensions.CreatePackage(
                ChartPackageTestExtensions.CreateEntryWithInstallDestination(
                    secondChart,
                    firstDestinationDirectoryPath));
            ChartPackage suffixPackage = ChartPackageTestExtensions.CreatePackage(
                ChartPackageTestExtensions.CreateEntryWithInstallDestination(
                    BMSFile.CreateBMSFileFromFile(thirdChartPath),
                    thirdDestinationDirectoryPath));
            firstPackage.path = firstSourceDirectoryPath;
            cleanupOnlyPackage.path = secondSourceDirectoryPath;
            suffixPackage.path = thirdSourceDirectoryPath;
            firstPackage.delete_parent = cleanupOnlyPackage.delete_parent = suffixPackage.delete_parent = false;

            using (var seedSongDb = new LR2SongDBExtended(songDbPath))
            {
                seedSongDb.CreateTable<LR2SongDBExtended.install>();
                seedSongDb.InsertOrReplace(firstPackage, typeof(LR2SongDBExtended.install));
                seedSongDb.InsertOrReplace(cleanupOnlyPackage, typeof(LR2SongDBExtended.install));
                seedSongDb.InsertOrReplace(suffixPackage, typeof(LR2SongDBExtended.install));
            }
            var songDbGateway = new BmsLibraryDbGateway(songDbPath);
            var service = new BmsLibraryPackageInstallService();
            PendingInstallBatchPlan plan = service.BuildEstimatedInstallBatchPlan(
                [firstPackage, cleanupOnlyPackage, suffixPackage],
                [firstPackage, cleanupOnlyPackage, suffixPackage],
                CreateInstalledChartLookup([]));
            var finalizationFailure = new InvalidOperationException("estimated-cleanup-finalizer-failure");
            var fileMutationService = new RealFileMutationService();
            int installCallbackCount = 0;
            int cleanupCallbackCount = 0;

            PendingInstallBatchResult result = service.ExecuteEstimatedInstallBatchPlan(
                plan,
                deletePendingPackageSourceAfterInstall: true,
                installPackage: item =>
                {
                    installCallbackCount++;
                    PackageInstallExecutionResult packageResult = service.InstallPackagesWithFileMutationReceipts(
                        [item.InstallWorkPackage],
                        item.DestinationDirectory,
                        (package, destinationDirectory, sourceCleanupPolicy, hashSnapshot, _, excludedComponentPaths, applyDurableCommit) =>
                            service.MovePackageFilesWithReceipt(
                                package,
                                destinationDirectory,
                                new BmsLibraryOptionsSnapshot
                                {
                                    EnableSmartComponentOverwrite = false,
                                    KeepSmartOverwriteProtectedFilesByRenaming = false
                                },
                                (_, _) => destinationDirectory,
                                exception => exception.Message,
                                fileMutationService,
                                null,
                                new FileMutationOptions(ReadOnlyNormalizationScope.TargetOnly),
                                new FileMutationOptions(ReadOnlyNormalizationScope.RecursiveDirectoryTree),
                                _ => { },
                                applyDurableCommit,
                                showMessageBoxOnInstallFail: false,
                                sourceCleanupPolicy: sourceCleanupPolicy,
                                existingHashes: hashSnapshot,
                                excludedComponentPaths: excludedComponentPaths),
                        packageResult =>
                        {
                            songDbGateway.ApplyInstallTableMutation(
                                [packageResult.InstallPathToDelete],
                                []);
                            return FileDbMutationCommitResult.Durable();
                        },
                        _ => { },
                        _ => { },
                        _ => { },
                        sourceCleanupPolicy: PackageSourceCleanupPolicy.PreserveUnconsumedContents,
                        existingHashes: plan.MoveGuardLookup);
                    return packageResult;
                },
                createInstalledDisplayPackage: null,
                cleanupPendingPackageSource: null,
                cleanupPendingPackageSourceWithReceipt: cleanupPackage =>
                {
                    cleanupCallbackCount++;
                    var cleanupPlan = new FileDbMutationPlan(
                        Guid.NewGuid(),
                        [],
                        [],
                        [new FileDbMutationCleanupPathPlan(cleanupPackage.path, recursive: true)],
                        recursiveSourceCleanup: false);
                    return new FileDbMutationExecutor(
                        cleanupPlan,
                        fileMutationService,
                        new FileMutationOptions(ReadOnlyNormalizationScope.TargetOnly),
                        new FileMutationOptions(ReadOnlyNormalizationScope.RecursiveDirectoryTree))
                        .Execute(() =>
                        {
                            songDbGateway.ApplyInstallTableMutation(
                                [cleanupPackage.path],
                                []);
                            return FileDbMutationCommitResult.Durable(
                                () => throw finalizationFailure);
                        });
                },
                countComponentMoveTargets: (package, _, _) =>
                    string.Equals(package?.path, cleanupOnlyPackage.path, StringComparison.OrdinalIgnoreCase) ? 0 : 1);

            Assert.AreEqual(1, installCallbackCount);
            Assert.AreEqual(1, cleanupCallbackCount);
            Assert.AreEqual(1, plan.CleanupOnlyCandidateCount);
            Assert.AreEqual(1, result.CleanupOnlyFailed);
            Assert.IsTrue(result.HasDurableCommit);
            Assert.IsTrue(result.HasDurableFinalizationFailure);
            Assert.AreEqual(2, result.MutationReceipt.Receipts.Count);
            Assert.AreEqual(FileDbMutationTerminalState.Completed, result.MutationReceipt.Receipts[0].TerminalState);
            Assert.AreEqual(FileDbMutationTerminalState.DurableFinalizationFailed, result.MutationReceipt.Receipts[1].TerminalState);
            Assert.AreSame(finalizationFailure, result.MutationReceipt.Receipts[1].FinalizationFailure);
            Assert.AreEqual(1, result.PendingPackagesToRemove.Count);
            Assert.IsTrue(result.PendingPackagesToRemove.Contains(firstPackage));
            Assert.IsFalse(result.PendingPackagesToRemove.Contains(cleanupOnlyPackage));
            Assert.IsFalse(result.PendingPackagesToRemove.Contains(suffixPackage));
            Assert.IsFalse(result.FailedPackages.Contains(cleanupOnlyPackage));
            Assert.IsFalse(result.FailedPackages.Contains(suffixPackage));
            Assert.IsTrue(File.Exists(Path.Combine(firstDestinationDirectoryPath, "first.bms")));
            Assert.IsFalse(File.Exists(Path.Combine(secondDestinationDirectoryPath, "second.bms")));
            Assert.IsFalse(File.Exists(Path.Combine(thirdDestinationDirectoryPath, "third.bms")));
            Assert.IsTrue(File.Exists(thirdChartPath));
            using var verifySongDb = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0, verifySongDb.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM install WHERE path = ?;",
                firstSourceDirectoryPath));
            Assert.AreEqual(0, verifySongDb.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM install WHERE path = ?;",
                secondSourceDirectoryPath));
            Assert.AreEqual(1, verifySongDb.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM install WHERE path = ?;",
                thirdSourceDirectoryPath));
        });
    }

    [TestMethod]
    public void InstallPendingPackagesToEstimatedDestinations_ResourceOnlyBmsonWorksWithoutBmsFiles()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string destinationDirectoryPath = Path.Combine(tempRootPath, "installed");
            string pendingDirectoryPath = Path.Combine(tempRootPath, "pending");
            Directory.CreateDirectory(destinationDirectoryPath);
            Directory.CreateDirectory(pendingDirectoryPath);
            string installedBmsonPath = Path.Combine(destinationDirectoryPath, "chart.bmson");
            string pendingBmsonPath = Path.Combine(pendingDirectoryPath, "chart.bmson");
            string pendingResourcePath = Path.Combine(pendingDirectoryPath, "sound.wav");
            File.WriteAllText(installedBmsonPath, "{}");
            File.WriteAllText(pendingBmsonPath, "{}");
            File.WriteAllText(pendingResourcePath, "resource");
            var installedBmson = new LR2SongDBExtended.bmson_song
            {
                path = installedBmsonPath,
                folder = destinationDirectoryPath,
                title = "Installed",
                md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                sha256 = new string('b', 64)
            };
            var pendingBmson = new LR2SongDBExtended.bmson_song
            {
                path = pendingBmsonPath,
                folder = pendingDirectoryPath,
                title = "Pending",
                md5 = installedBmson.md5,
                sha256 = installedBmson.sha256
            };
            PackageChartEntry pendingEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(pendingBmson, includeWarningSnapshot: false, includeResourceReferences: false));
            pendingEntry.ApplyInstallDestination(destinationDirectoryPath, "Installed", "Artist");
            ChartPackage pendingPackage = ChartPackage.FromChartEntries([pendingEntry]);
            pendingPackage.path = pendingDirectoryPath;
            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = null,
                BmsonSongs = [installedBmson],
                ChartPackagesPending = CreatePackageCollection([pendingPackage]),
                ChartPackagesInstalled = CreatePackageCollection([])
            };
            bool packageEntryNotificationObserved = false;
            Exception packageEntryInspectionFailure = null;
            pendingEntry.PropertyChanged += (_, _) =>
            {
                packageEntryNotificationObserved = true;
                try
                {
                    Assert.IsFalse(library.IsWriteLockHeldPendingInstallCharts);
                    Assert.IsFalse(library.IsWriteLockHeldInitializeBMSFiles);
                    AssertLibraryWriterCanBeAcquired(
                        library,
                        "rwlockBMSFilesInitializedAll");
                    AssertLibraryWriterCanBeAcquired(
                        library,
                        "rwlockPendingInstallCharts");
                    AssertLibraryWriterCanBeAcquired(
                        library,
                        "rwlockBMSFiles");
                }
                catch (Exception exception)
                {
                    packageEntryInspectionFailure = exception;
                }
            };

            library.InstallPendingPackagesToEstimatedDestinations([pendingPackage]);

            Assert.IsTrue(packageEntryNotificationObserved);
            Assert.IsNull(
                packageEntryInspectionFailure,
                packageEntryInspectionFailure?.ToString());
            Assert.AreEqual(0, library.ChartPackagesPending.Count);
            Assert.AreEqual(1, library.ChartPackagesInstalled.Count);
            ChartPackage displayPackage = library.ChartPackagesInstalled.Single();
            Assert.AreEqual(destinationDirectoryPath, displayPackage.path);
            ChartFile displayChart = displayPackage.ChartEntries.Single().Chart;
            Assert.AreSame(installedBmson, displayChart.GetBmsonStorageOwner());
            Assert.IsTrue(File.Exists(Path.Combine(destinationDirectoryPath, "sound.wav")));
        });
    }

    /// <summary>
    /// 先行 package の durable 導入後に source cleanup だけが失敗しても、
    /// 後続 package が同じ推定導入 loop で固有譜面または resource-only として
    /// 再評価され、batch の cleanup failure を保持することを検証します。
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void InstallPendingPackagesToEstimatedDestinations_ReevaluatesResourceOnlyAfterEarlierCommit(
        bool includeUniqueChart)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string destinationDirectoryPath = Path.Combine(tempRootPath, "estimated-resource-only-after-commit");
            string firstPendingDirectoryPath = Path.Combine(tempRootPath, "estimated-resource-only-first");
            string secondPendingDirectoryPath = Path.Combine(tempRootPath, "estimated-resource-only-second");
            Directory.CreateDirectory(destinationDirectoryPath);
            Directory.CreateDirectory(firstPendingDirectoryPath);
            Directory.CreateDirectory(secondPendingDirectoryPath);
            string firstBmsonPath = Path.Combine(firstPendingDirectoryPath, "chart.bmson");
            string secondBmsonPath = Path.Combine(secondPendingDirectoryPath, "chart.bmson");
            string secondUniqueBmsonPath = Path.Combine(secondPendingDirectoryPath, "unique.bmson");
            string secondResourcePath = Path.Combine(secondPendingDirectoryPath, "sound.wav");
            if (includeUniqueChart)
            {
                File.WriteAllText(firstBmsonPath, "{}");
                File.WriteAllText(secondBmsonPath, "{}");
                File.WriteAllText(secondUniqueBmsonPath, CreateBmsonJsonWithSound("unique.wav"));
            }
            else
            {
                string sharedBmson = CreateBmsonJsonWithSound("sound.wav");
                File.WriteAllText(firstBmsonPath, sharedBmson);
                File.WriteAllText(secondBmsonPath, sharedBmson);
                File.WriteAllText(secondResourcePath, "resource");
            }

            ChartFile firstSourceChart = ChartFileProjection.FromBmsonSong(
                BmsonSongParser.Parse(firstBmsonPath),
                includeWarningSnapshot: false,
                includeResourceReferences: false);
            ChartFile uniqueSourceChart = includeUniqueChart
                ? ChartFileProjection.FromBmsonSong(
                    BmsonSongParser.Parse(secondUniqueBmsonPath),
                    includeWarningSnapshot: false,
                    includeResourceReferences: false)
                : null;
            Assert.IsFalse(string.IsNullOrWhiteSpace(firstSourceChart?.Md5));
            if (includeUniqueChart)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(uniqueSourceChart?.Md5));
            }
            string sharedMd5 = firstSourceChart.Md5;
            string sharedSha256 = firstSourceChart.Sha256;
            var firstBmson = new LR2SongDBExtended.bmson_song
            {
                path = firstBmsonPath,
                folder = firstPendingDirectoryPath,
                title = "Shared",
                artist = "Artist",
                md5 = sharedMd5,
                sha256 = sharedSha256
            };
            var secondBmson = new LR2SongDBExtended.bmson_song
            {
                path = secondBmsonPath,
                folder = secondPendingDirectoryPath,
                title = "Shared",
                artist = "Artist",
                md5 = sharedMd5,
                sha256 = sharedSha256
            };
            var secondUniqueBmson = new LR2SongDBExtended.bmson_song
            {
                path = secondUniqueBmsonPath,
                folder = secondPendingDirectoryPath,
                title = "Unique",
                artist = "Artist",
                md5 = uniqueSourceChart?.Md5 ?? "abcdefabcdefabcdefabcdefabcdefab",
                sha256 = uniqueSourceChart?.Sha256 ?? "abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd"
            };
            PackageChartEntry firstEntry = PackageChartEntry.FromChart(
                ChartFileProjection.FromBmsonSong(
                    firstBmson,
                    includeWarningSnapshot: false,
                    includeResourceReferences: false));
            firstEntry.ApplyInstallDestination(destinationDirectoryPath, "Shared", "Artist");
            PackageChartEntry secondEntry = PackageChartEntry.FromChart(
                ChartFileProjection.FromBmsonSong(
                    secondBmson,
                    includeWarningSnapshot: false,
                    includeResourceReferences: false));
            secondEntry.ApplyInstallDestination(destinationDirectoryPath, "Shared", "Artist");
            PackageChartEntry secondUniqueEntry = PackageChartEntry.FromChart(
                ChartFileProjection.FromBmsonSong(
                    secondUniqueBmson,
                    includeWarningSnapshot: false,
                    includeResourceReferences: false));
            secondUniqueEntry.ApplyInstallDestination(destinationDirectoryPath, "Unique", "Artist");
            ChartPackage firstPackage = ChartPackage.FromChartEntries([firstEntry]);
            firstPackage.path = firstPendingDirectoryPath;
            firstPackage.delete_parent = false;
            ChartPackage secondPackage = ChartPackage.FromChartEntries(
                includeUniqueChart
                    ? [secondEntry, secondUniqueEntry]
                    : [secondEntry]);
            secondPackage.path = secondPendingDirectoryPath;
            secondPackage.delete_parent = false;

            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                new FailingDestinationDeleteFileMutationService(firstBmsonPath),
                new RecordingDialogService(),
                new TestUiScheduler(() => null),
                () => new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = false,
                    BMSInstallDir = destinationDirectoryPath,
                    FolderNameFormat = "%TITLE%",
                    DeletePendingPackageSourceAfterInstall = true,
                    EnableSmartComponentOverwrite = false,
                    KeepSmartOverwriteProtectedFilesByRenaming = false
                })
            {
                BMSFiles = [],
                BmsonSongs = [],
                ChartPackagesPending = CreatePackageCollection([firstPackage, secondPackage]),
                ChartPackagesInstalled = CreatePackageCollection([])
            };

            PendingInstallBatchResult result = library.InstallPendingPackagesToEstimatedDestinationsWithReceipt(
                [firstPackage, secondPackage]);

            string installedBmsonPath = Path.Combine(destinationDirectoryPath, "chart.bmson");
            Assert.IsTrue(File.Exists(installedBmsonPath));
            if (includeUniqueChart)
            {
                Assert.IsTrue(File.Exists(Path.Combine(destinationDirectoryPath, "unique.bmson")));
            }
            else
            {
                Assert.IsTrue(File.Exists(Path.Combine(destinationDirectoryPath, "sound.wav")));
            }
            Assert.IsTrue(File.Exists(firstBmsonPath));
            if (includeUniqueChart)
            {
                Assert.IsFalse(Directory.Exists(secondPendingDirectoryPath));
            }
            else
            {
                // The shared chart has an independent durable copy from the
                // earlier package, so a resource-only install may consume its
                // remaining source after the same verified cleanup proof.
                Assert.IsFalse(Directory.Exists(secondPendingDirectoryPath));
                Assert.IsFalse(File.Exists(secondBmsonPath));
            }
            Assert.AreEqual(0, library.ChartPackagesPending.Count);
            Assert.IsTrue(result.PendingPackagesToRemove.Contains(firstPackage));
            Assert.IsTrue(result.PendingPackagesToRemove.Contains(secondPackage));
            Assert.IsTrue(result.HasDurableCommit);
            Assert.IsTrue(result.CompletedWithCleanupFailure);
            Assert.IsFalse(result.HasDurableFinalizationFailure);
            Assert.AreEqual(2, result.MutationReceipt.Receipts.Count);
            Assert.AreEqual(
                FileDbMutationTerminalState.CompletedWithCleanupFailure,
                result.MutationReceipt.Receipts[0].TerminalState);
            Assert.IsTrue(result.MutationReceipt.Receipts[0].DurableCommit);
            Assert.IsNull(result.MutationReceipt.Receipts[0].FinalizationFailure);
            Assert.IsNotNull(result.MutationReceipt.Receipts[0].CleanupFailure);
            Assert.AreEqual(FileDbMutationTerminalState.Completed, result.MutationReceipt.Receipts[1].TerminalState);
            using var verifySongDb = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1, verifySongDb.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM bmson_song WHERE path = ?;",
                installedBmsonPath));
            if (includeUniqueChart)
            {
                Assert.AreEqual(1, verifySongDb.ExecuteScalar<int>(
                    "SELECT COUNT(1) FROM bmson_song WHERE path = ?;",
                    Path.Combine(destinationDirectoryPath, "unique.bmson")));
            }
        });
    }

    [TestMethod]
    public void OverwritePendingInstalledOnlyPackagesResources_ReleasesOuterWritersBeforeEstimatedInstallPublication()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string destinationDirectoryPath = Path.Combine(tempRootPath, "installed-overwrite");
            string pendingDirectoryPath = Path.Combine(tempRootPath, "pending-overwrite");
            Directory.CreateDirectory(destinationDirectoryPath);
            Directory.CreateDirectory(pendingDirectoryPath);
            string installedBmsonPath = Path.Combine(destinationDirectoryPath, "chart.bmson");
            string pendingBmsonPath = Path.Combine(pendingDirectoryPath, "chart.bmson");
            string pendingResourcePath = Path.Combine(pendingDirectoryPath, "sound.wav");
            File.WriteAllText(installedBmsonPath, "{}");
            File.WriteAllText(pendingBmsonPath, "{}");
            File.WriteAllText(pendingResourcePath, "resource");
            var installedBmson = new LR2SongDBExtended.bmson_song
            {
                path = installedBmsonPath,
                folder = destinationDirectoryPath,
                title = "Installed",
                md5 = "cccccccccccccccccccccccccccccccc",
                sha256 = new string('d', 64)
            };
            var pendingBmson = new LR2SongDBExtended.bmson_song
            {
                path = pendingBmsonPath,
                folder = pendingDirectoryPath,
                title = "Pending",
                md5 = installedBmson.md5,
                sha256 = installedBmson.sha256
            };
            PackageChartEntry pendingEntry = PackageChartEntry.FromChart(
                ChartFileProjection.FromBmsonSong(
                    pendingBmson,
                    includeWarningSnapshot: false,
                    includeResourceReferences: false));
            ChartPackage pendingPackage = ChartPackage.FromChartEntries([pendingEntry]);
            pendingPackage.path = pendingDirectoryPath;
            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = null,
                BmsonSongs = [installedBmson],
                ChartPackagesPending = CreatePackageCollection([pendingPackage]),
                ChartPackagesInstalled = CreatePackageCollection([])
            };
            bool packageEntryNotificationObserved = false;
            pendingEntry.PropertyChanged += (_, _) =>
            {
                Assert.IsFalse(library.IsWriteLockHeldPendingInstallCharts);
                Assert.IsFalse(library.IsWriteLockHeldInitializeBMSFiles);
                packageEntryNotificationObserved = true;
            };

            PendingInstalledOnlyResourceOverwriteResult result =
                library.OverwritePendingInstalledOnlyPackagesResources([pendingPackage]);

            Assert.AreEqual(1, result.SucceededInstall);
            Assert.AreEqual(0, result.Failed);
            Assert.IsTrue(packageEntryNotificationObserved);
            Assert.AreEqual(0, library.ChartPackagesPending.Count);
            Assert.AreEqual(1, library.ChartPackagesInstalled.Count);
            Assert.IsTrue(File.Exists(Path.Combine(destinationDirectoryPath, "sound.wav")));
        });
    }

    [TestMethod]
    public void OverwritePendingInstalledOnlyPackagesResources_CleanupOnlyUsesOuterLeaseAndExcludesReentry()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string destinationDirectoryPath = Path.Combine(tempRootPath, "installed-cleanup-only");
            string pendingDirectoryPath = Path.Combine(tempRootPath, "pending-cleanup-only");
            Directory.CreateDirectory(destinationDirectoryPath);
            Directory.CreateDirectory(pendingDirectoryPath);
            string installedBmsonPath = Path.Combine(destinationDirectoryPath, "chart.bmson");
            string pendingBmsonPath = Path.Combine(pendingDirectoryPath, "chart.bmson");
            File.WriteAllText(installedBmsonPath, "{}");
            File.WriteAllText(pendingBmsonPath, "{}");
            var installedBmson = new LR2SongDBExtended.bmson_song
            {
                path = installedBmsonPath,
                folder = destinationDirectoryPath,
                title = "Installed cleanup-only",
                md5 = "11111111111111111111111111111111",
                sha256 = new string('2', 64)
            };
            var pendingBmson = new LR2SongDBExtended.bmson_song
            {
                path = pendingBmsonPath,
                folder = pendingDirectoryPath,
                title = "Pending cleanup-only",
                md5 = installedBmson.md5,
                sha256 = installedBmson.sha256
            };
            PackageChartEntry pendingEntry = PackageChartEntry.FromChart(
                ChartFileProjection.FromBmsonSong(
                    pendingBmson,
                    includeWarningSnapshot: false,
                    includeResourceReferences: false));
            ChartPackage pendingPackage = ChartPackage.FromChartEntries([pendingEntry]);
            pendingPackage.path = pendingDirectoryPath;
            pendingPackage.delete_parent = false;
            ChartPackage reentryPackage = new()
            {
                path = Path.Combine(tempRootPath, "reentry-cleanup-only")
            };
            var fileMutationService = new ReentrantCleanupFileMutationService();
            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                fileMutationService,
                new RecordingDialogService(),
                new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                () => new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                    DeletePendingPackageSourceAfterInstall = true,
                    FolderNameFormat = "%TITLE%",
                    BMSInstallDir = Path.Combine(tempRootPath, "auto-install")
                });
            library.BMSFiles = null;
            library.BmsonSongs = [installedBmson];
            library.ChartPackagesPending = CreatePackageCollection([pendingPackage]);
            fileMutationService.Configure(library, reentryPackage);

            PendingInstalledOnlyResourceOverwriteResult result;
            try
            {
                result = library.OverwritePendingInstalledOnlyPackagesResources([pendingPackage]);
            }
            finally
            {
                fileMutationService.WaitForReentryCompletion();
            }

            Assert.IsNull(fileMutationService.ReentryFailure, fileMutationService.ReentryFailure?.ToString());
            Assert.IsTrue(
                fileMutationService.ReentryCompletedDuringCleanup,
                "Cleanup-only processing did not expose the outer lease's reentry boundary.");
            Assert.IsNotNull(fileMutationService.ReentryResult);
            Assert.AreEqual(0, fileMutationService.ReentryResult.Requested);
            Assert.AreEqual(1, result.SucceededCleanupOnly);
            Assert.AreEqual(0, result.SucceededInstall);
            Assert.AreEqual(0, result.Failed);
            Assert.AreEqual(0, library.ChartPackagesPending.Count);
            Assert.IsFalse(Directory.Exists(pendingDirectoryPath));
        });
    }

    [TestMethod]
    public void InstallPendingPackagesToEstimatedDestinations_SubscriberFailureDoesNotReclassifyCommittedInstall()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string destinationDirectoryPath = Path.Combine(tempRootPath, "installed-subscriber-failure");
            string pendingDirectoryPath = Path.Combine(tempRootPath, "pending-subscriber-failure");
            Directory.CreateDirectory(destinationDirectoryPath);
            Directory.CreateDirectory(pendingDirectoryPath);
            string installedBmsonPath = Path.Combine(destinationDirectoryPath, "chart.bmson");
            string pendingBmsonPath = Path.Combine(pendingDirectoryPath, "chart.bmson");
            string pendingResourcePath = Path.Combine(pendingDirectoryPath, "sound.wav");
            File.WriteAllText(installedBmsonPath, "{}");
            File.WriteAllText(pendingBmsonPath, "{}");
            File.WriteAllText(pendingResourcePath, "resource");
            var installedBmson = new LR2SongDBExtended.bmson_song
            {
                path = installedBmsonPath,
                folder = destinationDirectoryPath,
                title = "Installed",
                md5 = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
                sha256 = new string('f', 64)
            };
            var pendingBmson = new LR2SongDBExtended.bmson_song
            {
                path = pendingBmsonPath,
                folder = pendingDirectoryPath,
                title = "Pending",
                md5 = installedBmson.md5,
                sha256 = installedBmson.sha256
            };
            PackageChartEntry pendingEntry = PackageChartEntry.FromChart(
                ChartFileProjection.FromBmsonSong(
                    pendingBmson,
                    includeWarningSnapshot: false,
                    includeResourceReferences: false));
            pendingEntry.ApplyInstallDestination(destinationDirectoryPath, "Installed", "Artist");
            ChartPackage pendingPackage = ChartPackage.FromChartEntries([pendingEntry]);
            pendingPackage.path = pendingDirectoryPath;
            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = null,
                BmsonSongs = [installedBmson],
                ChartPackagesPending = CreatePackageCollection([pendingPackage]),
                ChartPackagesInstalled = CreatePackageCollection([])
            };
            Exception packageEntryInspectionFailure = null;
            pendingEntry.PropertyChanged += (_, _) =>
            {
                try
                {
                    AssertLibraryWriterCanBeAcquired(
                        library,
                        "rwlockBMSFilesInitializedAll");
                    AssertLibraryWriterCanBeAcquired(
                        library,
                        "rwlockPendingInstallCharts");
                    AssertLibraryWriterCanBeAcquired(
                        library,
                        "rwlockBMSFiles");
                }
                catch (Exception exception)
                {
                    packageEntryInspectionFailure = exception;
                }
                if (File.Exists(Path.Combine(destinationDirectoryPath, "sound.wav")))
                {
                    throw new InvalidOperationException("subscriber publication failed");
                }
            };

            library.InstallPendingPackagesToEstimatedDestinations([pendingPackage]);

            Assert.IsNull(
                packageEntryInspectionFailure,
                packageEntryInspectionFailure?.ToString());
            Assert.AreEqual(0, library.ChartPackagesPending.Count);
            Assert.AreEqual(1, library.ChartPackagesInstalled.Count);
            Assert.IsTrue(File.Exists(Path.Combine(destinationDirectoryPath, "sound.wav")));
        });
    }

    [TestMethod]
    public void InstallPendingPackagesToEstimatedDestinations_ReleasesEstimateGateBeforeFilesystemExecutor()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string destinationDirectoryPath = Path.Combine(tempRootPath, "installed-estimate-gate");
            string pendingDirectoryPath = Path.Combine(tempRootPath, "pending-estimate-gate");
            Directory.CreateDirectory(destinationDirectoryPath);
            Directory.CreateDirectory(pendingDirectoryPath);
            string chartPath = CreateBmsFile(pendingDirectoryPath, "chart.bms", "#TITLE Pending");
            BMSFile chart = BMSFile.CreateBMSFileFromFile(chartPath);
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([chart]);
            pendingPackage.path = pendingDirectoryPath;
            pendingPackage.ChartEntries.Single().ApplyInstallDestination(destinationDirectoryPath, "Installed", "Artist");

            // The empty package only exercises the existing public estimate command's
            // admission path. It must be able to re-enter while the outer package's
            // filesystem executor is active; no package data needs to be mutated.
            ChartPackage reentryPackage = new()
            {
                path = Path.Combine(tempRootPath, "pending-estimate-gate-reentry")
            };
            var fileMutationService = new ReentrantEstimateFileMutationService();
            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                fileMutationService,
                new RecordingDialogService());
            library.BMSFiles = null;
            library.ChartPackagesPending = CreatePackageCollection([pendingPackage, reentryPackage]);
            fileMutationService.Configure(library, reentryPackage);

            try
            {
                library.InstallPendingPackagesToEstimatedDestinations([pendingPackage]);
            }
            finally
            {
                fileMutationService.WaitForReentryCompletion();
            }

            Assert.IsNull(
                fileMutationService.ReentryFailure,
                fileMutationService.ReentryFailure?.ToString());
            Assert.IsTrue(
                fileMutationService.ReentryCompletedDuringFilesystem,
                "The estimate command remained serialized through the filesystem executor.");
        });
    }

    [TestMethod]
    public void InstallPendingPackagesToEstimatedDestinations_LeavesPackageUnchangedWhenItIsNotPending()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string pendingDirectoryPath = Path.Combine(tempRootPath, "pending-not-selected");
            Directory.CreateDirectory(pendingDirectoryPath);
            string chartPath = Path.Combine(pendingDirectoryPath, "chart.bms");
            File.WriteAllText(chartPath, "#TITLE pending");
            ChartPackage requestedPackage = ChartPackageTestExtensions.CreatePackage(
                [CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath)]);
            requestedPackage.path = pendingDirectoryPath;
            requestedPackage.delete_parent = false;

            var library = new TestBmsLibrary(songDbPath)
            {
                ChartPackagesPending = CreatePackageCollection([])
            };

            library.InstallPendingPackagesToEstimatedDestinations([requestedPackage]);

            Assert.AreEqual(0, library.ChartPackagesPending.Count);
            Assert.IsTrue(File.Exists(chartPath));
            Assert.AreEqual(pendingDirectoryPath, requestedPackage.path);
            Assert.AreEqual(string.Empty, requestedPackage.ChartEntries.Single().Chart.InstallDestination);
        });
    }

    [TestMethod]
    public void InstallPendingPackagesToEstimatedDestinations_NullStillThrowsWhenLr2SyncIsRunning()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            var library = new TestBmsLibrary(songDbPath);
            library.Lr2Synchronization.Running = true;
            try
            {
                Assert.ThrowsException<ArgumentNullException>(
                    () => library.InstallPendingPackagesToEstimatedDestinations(null));
            }
            finally
            {
                library.Lr2Synchronization.Running = false;
            }
        });
    }

    /// <summary>
    /// DnD's library entry point protects configuration roots even before chart
    /// indexing, for both standalone settings and LR2's configuration source.
    /// </summary>
    [DataTestMethod]
    [DataRow(false, "root")]
    [DataRow(false, "directory")]
    [DataRow(false, "file")]
    [DataRow(true, "directory")]
    public void InstallChartPackagesAuto_ExcludesRegisteredRootsWithoutChartIndex(
        bool useLr2,
        string sourceKind)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string registeredRoot = Path.Combine(tempRootPath, "Library");
            string rootChartPath = CreateBmsFile(registeredRoot, "root.bms", "#TITLE Root");
            string nestedDirectory = Path.Combine(registeredRoot, "Song");
            string nestedChartPath = CreateBmsFile(nestedDirectory, "chart.bms", "#TITLE Nested");
            File.WriteAllText(Path.Combine(nestedDirectory, "readme.txt"), "Keep the single-file drop separate.");
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.install>();
            }

            LR2Config? config = null;
            if (useLr2)
            {
                string configDirectory = Path.Combine(tempRootPath, "LR2files", "Config");
                Directory.CreateDirectory(configDirectory);
                string configPath = Path.Combine(configDirectory, "config.xml");
                new XDocument(new XElement("config",
                    new XElement("jukebox", new XElement("path", registeredRoot))))
                    .Save(configPath);
                config = new LR2Config(configPath);
            }
            var library = new TestBmsLibrary(
                songDbPath,
                () => config!,
                null!,
                new RealFileMutationService(),
                new RecordingDialogService(),
                new TestUiScheduler(() => null!),
                () => new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = useLr2,
                    KeepInstallablePackagesPending = true
                });
            library.BMSFiles = [];
            library.SearchTargets = useLr2 ? [] : [registeredRoot];
            string sourcePath = sourceKind switch
            {
                "root" => registeredRoot,
                "directory" => nestedDirectory,
                _ => nestedChartPath
            };

            List<ChartPackage> installed = library.InstallChartPackagesAuto([sourcePath]);

            Assert.AreEqual(0, installed.Count);
            Assert.AreEqual(0, library.ChartPackagesPending.Count);
            Assert.AreEqual(0, library.ChartPackagesInstalled.Count);
            Assert.AreEqual(0, new BmsLibraryDbGateway(songDbPath).LoadInstallPackages().Count);
            Assert.IsTrue(File.Exists(rootChartPath));
            Assert.IsTrue(File.Exists(nestedChartPath));
        });
    }

    [TestMethod]
    public void InstallChartPackagesAuto_WhenAnotherFileMutationOwnsAdmission_FailsInsteadOfPublishingEmptySuccess()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string sourceDirectory = Path.Combine(tempRootPath, "busy-auto-install-source");
            Directory.CreateDirectory(sourceDirectory);
            var library = new TestBmsLibrary(songDbPath);
            using LibraryFileMutationLease incumbent = library.TryBeginLibraryFileMutation(
                "test_busy_auto_install");
            Assert.IsNotNull(incumbent);

            Assert.ThrowsException<InvalidOperationException>(
                () => library.InstallChartPackagesAuto([sourceDirectory]));
        });
    }

    [TestMethod]
    public void PendingEstimatedInstallMutationLease_ReleasesEveryGuardOnAcquireAndDisposeFailure()
    {
        var acquiredBeforeFailure = new TrackingDisposable();

        Assert.ThrowsException<InvalidOperationException>(() => PendingEstimatedInstallMutationLease.Acquire(
            () => acquiredBeforeFailure,
            () => throw new InvalidOperationException("acquire-failed")));
        Assert.AreEqual(1, acquiredBeforeFailure.DisposeCount);

        var throwingCleanupGuard = new TrackingDisposable(throwOnDispose: true);
        AggregateException acquisitionAndCleanupFailure = Assert.ThrowsException<AggregateException>(
            () => PendingEstimatedInstallMutationLease.Acquire(
                () => throwingCleanupGuard,
                () => throw new InvalidOperationException("acquire-failed-with-cleanup-error")));
        Assert.IsTrue(acquisitionAndCleanupFailure.Flatten().InnerExceptions.Any(
            exception => exception.Message == "acquire-failed-with-cleanup-error"));
        Assert.IsTrue(acquisitionAndCleanupFailure.Flatten().InnerExceptions.Any(
            exception => exception.Message == "dispose-failed"));
        Assert.AreEqual(1, throwingCleanupGuard.DisposeCount);

        var throwingGuard = new TrackingDisposable(throwOnDispose: true);
        var releasedGuard = new TrackingDisposable();
        PendingEstimatedInstallMutationLease lease = PendingEstimatedInstallMutationLease.Acquire(
            () => throwingGuard,
            () => releasedGuard);

        Assert.ThrowsException<AggregateException>(() => lease.Dispose());
        Assert.AreEqual(1, throwingGuard.DisposeCount);
        Assert.AreEqual(1, releasedGuard.DisposeCount);
    }

    [TestMethod]
    public void BuildComponentMovePlan_SkipsExcludedPaths()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryPackageInstallService();
            string sourceDirectoryPath = Path.Combine(tempDirectoryPath, "src");
            Directory.CreateDirectory(sourceDirectoryPath);
            string keepFilePath = Path.Combine(sourceDirectoryPath, "keep.txt");
            string skipFilePath = Path.Combine(sourceDirectoryPath, "skip.txt");
            File.WriteAllText(keepFilePath, "keep");
            File.WriteAllText(skipFilePath, "skip");

            ComponentMovePlanBuildResult result = service.BuildComponentMovePlan(
                [sourceDirectoryPath],
                Path.Combine(tempDirectoryPath, "dst"),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { skipFilePath });

            Assert.AreEqual(1, result.PlanItems.Count);
            Assert.AreEqual(1, result.SkippedByExclusion);
            Assert.AreEqual(keepFilePath, result.PlanItems[0].SourcePath);
        });
    }

    [TestMethod]
    public void DecideComponentMove_PrefersOverwriteWhenSourceIsNewer()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryPackageInstallService();
            string sourceFilePath = Path.Combine(tempDirectoryPath, "source.txt");
            string destinationFilePath = Path.Combine(tempDirectoryPath, "destination.txt");
            File.WriteAllText(sourceFilePath, "source");
            File.WriteAllText(destinationFilePath, "dest");
            File.SetLastWriteTimeUtc(destinationFilePath, DateTime.UtcNow.AddMinutes(-10));
            File.SetLastWriteTimeUtc(sourceFilePath, DateTime.UtcNow);

            ComponentMoveDecision decision = service.DecideComponentMove(sourceFilePath, destinationFilePath);

            Assert.AreEqual(ComponentMoveDecision.Overwrite, decision);
        });
    }

    [TestMethod]
    public void BuildPendingPackageMutationDelta_RemovesMatchedChartPathsAndDeletesEmptyPackages()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryPackageInstallService();
        TestableBmsFile keepFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\Pkg1\\keep.bms");
        TestableBmsFile removeFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "C:\\Pending\\Pkg1\\remove.bms");
        TestableBmsFile removeWholePackageFile = CreateFile("cccccccccccccccccccccccccccccccc", "C:\\Pending\\Pkg2\\only.bms");
        var keepPackage = ChartPackageTestExtensions.CreatePackage([keepFile, removeFile]);
        keepPackage.path = "C:\\Pending\\Pkg1";
        keepPackage.delete_parent = false;
        var removePackage = ChartPackageTestExtensions.CreatePackage([removeWholePackageFile]);
        removePackage.path = "C:\\Pending\\Pkg2";
        removePackage.delete_parent = false;

        PendingPackageMutationDelta delta = service.BuildPendingPackageMutationDelta(
            [keepPackage, removePackage],
            chartPathsToRemove: [removeFile.path, removeWholePackageFile.path]);

        Assert.IsTrue(delta.HasChanges);
        Assert.AreEqual(1, delta.RemainingPackages.Count);
        Assert.AreSame(keepPackage, delta.RemainingPackages[0]);
        CollectionAssert.AreEqual(new[] { keepFile, removeFile }, keepPackage.GetBmsOwnersForTest());
        Assert.AreEqual(1, delta.EntryMutations.Count);
        Assert.AreSame(keepPackage, delta.EntryMutations[0].Package);
        CollectionAssert.AreEqual(new[] { keepFile }, delta.EntryMutations[0].RemainingEntries.Select(entry => entry.Chart.GetBmsStorageOwner()).Where(owner => owner != null).ToArray());
        CollectionAssert.AreEquivalent(new[] { "C:\\Pending\\Pkg2" }, delta.InstallPathsToDelete);
    }

    [TestMethod]
    public void BuildPendingPackageMutationDelta_RemovesAdapterlessBmsonByChartPathWithoutMaterializing()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryPackageInstallService();
        var keepEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Pending\\Pkg\\keep.bmson",
            md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            sha256 = new string('a', 64)
        }));
        var removeEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Pending\\Pkg\\remove.bmson",
            md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            sha256 = new string('b', 64)
        }));
        ChartPackage package = ChartPackage.FromChartEntries([keepEntry, removeEntry]);
        package.path = "C:\\Pending\\Pkg";

        PendingPackageMutationDelta delta = service.BuildPendingPackageMutationDelta(
            [package],
            chartPathsToRemove: [removeEntry.Chart.Path]);

        Assert.IsTrue(delta.HasChanges);
        Assert.AreEqual(1, delta.RemainingPackages.Count);
        Assert.AreSame(package, delta.RemainingPackages[0]);
        Assert.IsNull(keepEntry.GetBmsOwnerForTest());
        Assert.IsNull(removeEntry.GetBmsOwnerForTest());
        Assert.AreEqual(2, package.ChartEntries.Count);
        Assert.AreEqual(1, delta.EntryMutations.Count);
        Assert.AreSame(package, delta.EntryMutations[0].Package);
        Assert.AreEqual(keepEntry.Chart.Path, delta.EntryMutations[0].RemainingEntries.Single().Chart.Path);
        Assert.IsNull(package.ChartEntries[0].GetBmsOwnerForTest());
        Assert.AreEqual(0, delta.InstallPathsToDelete.Count);
    }

    [TestMethod]
    public void BuildEstimatedInstallBatchPlan_FiltersCandidatesWithoutReservingUncommittedHashes()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryPackageInstallService();
        string destinationDirectory = "C:\\Installed\\Target";
        TestableBmsFile installedFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Lib\\a.bms");
        TestableBmsFile cleanupInstalledFile = CreateFile("cccccccccccccccccccccccccccccccc", "C:\\Lib\\c.bms");
        TestableBmsFile alreadyInstalledInPackage = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\Pkg1\\a.bms");
        TestableBmsFile newFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "C:\\Pending\\Pkg1\\b.bms");
        TestableBmsFile cleanupOnlyFile = CreateFile("cccccccccccccccccccccccccccccccc", "C:\\Pending\\Pkg2\\c.bms");
        var mixedPackage = ChartPackageTestExtensions.CreatePackage(
            ChartPackageTestExtensions.CreateEntryWithInstallDestination(alreadyInstalledInPackage, destinationDirectory),
            ChartPackageTestExtensions.CreateEntryWithInstallDestination(newFile, destinationDirectory));
        mixedPackage.path = "C:\\Pending\\Pkg1";
        mixedPackage.delete_parent = false;
        var cleanupOnlyPackage = ChartPackageTestExtensions.CreatePackage(
            ChartPackageTestExtensions.CreateEntryWithInstallDestination(cleanupOnlyFile, destinationDirectory));
        cleanupOnlyPackage.path = "C:\\Pending\\Pkg2";
        cleanupOnlyPackage.delete_parent = false;

        PendingInstallBatchPlan plan = service.BuildEstimatedInstallBatchPlan(
            [mixedPackage, cleanupOnlyPackage],
            [mixedPackage, cleanupOnlyPackage],
            CreateInstalledChartLookup([installedFile, cleanupInstalledFile]));

        Assert.AreEqual(2, plan.SelectedPendingPackages.Count);
        Assert.AreEqual(0, plan.CleanupOnlyCandidates.Count);
        Assert.IsTrue(plan.MoveGuardLookup.ContainsPrimaryHash(installedFile.hash));
        Assert.IsFalse(plan.MoveGuardLookup.ContainsPrimaryHash(newFile.hash));
        PackageChartEntry alreadyInstalledEntry = mixedPackage.ChartEntries.Single(entry => ReferenceEquals(entry.Chart.GetBmsStorageOwner(), alreadyInstalledInPackage));
        Assert.AreEqual(string.Empty, ChartWarningTestHelpers.BuildDigestText(alreadyInstalledEntry));
        Assert.IsTrue(plan.FilterMs >= 0);
        Assert.IsTrue(plan.PlanBuildMs >= 0);
        Assert.AreEqual(2, plan.SelectedPendingCount);
        Assert.AreEqual(0, plan.CleanupOnlyCandidateCount);
    }

    [TestMethod]
    public void BuildEstimatedInstallBatchPlan_DoesNotTreatMd5MismatchAsInstalledWhenSha256Matches()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryPackageInstallService();
        string destinationDirectory = "C:\\Installed\\Target";
        TestableBmsFile installedFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Lib\\a.bms");
        installedFile.SetSha256(new string('b', 64));
        TestableBmsFile pendingFile = CreateFile("cccccccccccccccccccccccccccccccc", "C:\\Pending\\Pkg1\\a.bms");
        pendingFile.SetSha256(new string('b', 64));
        var pendingPackage = ChartPackageTestExtensions.CreatePackage(
            ChartPackageTestExtensions.CreateEntryWithInstallDestination(pendingFile, destinationDirectory));
        pendingPackage.path = "C:\\Pending\\Pkg1";
        pendingPackage.delete_parent = false;

        PendingInstallBatchPlan plan = service.BuildEstimatedInstallBatchPlan(
            [pendingPackage],
            [pendingPackage],
            CreateInstalledChartLookup([installedFile]));

        PendingInstallBatchItem capturedItem = null;
        PendingInstallBatchResult result = service.ExecuteEstimatedInstallBatchPlan(
            plan,
            deletePendingPackageSourceAfterInstall: false,
            installPackage: item =>
            {
                capturedItem = item;
                return CreateSuccessfulPackageInstallResult(item.InstallWorkPackage);
            },
            createInstalledDisplayPackage: null,
            cleanupPendingPackageSource: null);

        Assert.IsNotNull(capturedItem);
        Assert.AreEqual(1, capturedItem.InstallWorkPackage.GetBmsOwnersForTest().Count);
        Assert.AreSame(pendingFile, capturedItem.InstallWorkPackage.GetBmsOwnersForTest()[0]);
        Assert.AreEqual(1, result.PendingPackagesToRemove.Count);
        Assert.AreEqual(string.Empty, pendingFile.Warnings.BuildDigestText());
    }

    [TestMethod]
    public void ExecuteEstimatedInstallBatchPlan_SuppressesDuplicateHashWithinOnePackage()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryPackageInstallService();
        string destinationDirectory = "C:\\Installed\\Target";
        TestableBmsFile firstFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\Pkg1\\first.bms");
        TestableBmsFile duplicateFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\Pkg1\\duplicate.bms");
        ChartPackage package = ChartPackageTestExtensions.CreatePackage(
            ChartPackageTestExtensions.CreateEntryWithInstallDestination(firstFile, destinationDirectory),
            ChartPackageTestExtensions.CreateEntryWithInstallDestination(duplicateFile, destinationDirectory));
        package.path = "C:\\Pending\\Pkg1";

        PendingInstallBatchPlan plan = service.BuildEstimatedInstallBatchPlan(
            [package],
            [package],
            CreateInstalledChartLookup([]));
        PendingInstallBatchItem capturedItem = null;
        PendingInstallBatchResult result = service.ExecuteEstimatedInstallBatchPlan(
            plan,
            deletePendingPackageSourceAfterInstall: false,
            installPackage: item =>
            {
                capturedItem = item;
                return CreateSuccessfulPackageInstallResult(item.InstallWorkPackage);
            },
            createInstalledDisplayPackage: null,
            cleanupPendingPackageSource: null);

        Assert.IsNotNull(capturedItem);
        Assert.AreEqual(1, capturedItem.InstallWorkPackage.ChartEntries.Count);
        Assert.AreSame(firstFile, capturedItem.InstallWorkPackage.GetBmsOwnersForTest().Single());
        Assert.IsTrue(capturedItem.ExcludedComponentPaths.Contains(duplicateFile.path));
        Assert.IsFalse(package.ChartEntries[1].Chart.Warnings.Any(
            warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
        Assert.AreEqual(1, result.PendingPackagesToRemove.Count);
    }

    [TestMethod]
    public void ExecuteEstimatedInstallBatchPlan_SkipsMismatchedDestinationsWithoutAddingHashFacts()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryPackageInstallService();
        TestableBmsFile firstFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\Pkg1\\first.bms");
        TestableBmsFile secondFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "C:\\Pending\\Pkg1\\second.bms");
        ChartPackage package = ChartPackageTestExtensions.CreatePackage(
            ChartPackageTestExtensions.CreateEntryWithInstallDestination(firstFile, "C:\\Installed\\First"),
            ChartPackageTestExtensions.CreateEntryWithInstallDestination(secondFile, "C:\\Installed\\Second"));
        package.path = "C:\\Pending\\Pkg1";

        PendingInstallBatchPlan plan = service.BuildEstimatedInstallBatchPlan(
            [package],
            [package],
            CreateInstalledChartLookup([]));
        int callbackCount = 0;
        PendingInstallBatchResult result = service.ExecuteEstimatedInstallBatchPlan(
            plan,
            deletePendingPackageSourceAfterInstall: false,
            installPackage: _ =>
            {
                callbackCount++;
                return CreateSuccessfulPackageInstallResult(package);
            },
            createInstalledDisplayPackage: null,
            cleanupPendingPackageSource: null);

        Assert.AreEqual(0, callbackCount);
        Assert.AreEqual(0, result.PendingPackagesToRemove.Count);
        Assert.AreEqual(0, result.FailedPackages.Count);
        Assert.AreEqual("C:\\Installed\\First", package.ChartEntries[0].Chart.InstallDestination);
        Assert.AreEqual("C:\\Installed\\Second", package.ChartEntries[1].Chart.InstallDestination);
        Assert.IsFalse(plan.MoveGuardLookup.ContainsPrimaryHash(firstFile.hash));
        Assert.IsFalse(plan.MoveGuardLookup.ContainsPrimaryHash(secondFile.hash));
    }

    [TestMethod]
    public void ExecuteEstimatedInstallBatchPlan_AllowsInstalledEmptyDestinationWithNewTarget()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryPackageInstallService();
        string destinationDirectory = "C:\\Installed\\Target";
        TestableBmsFile installedFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Lib\\installed.bms");
        TestableBmsFile pendingFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "C:\\Pending\\Pkg1\\new.bms");
        ChartPackage package = ChartPackageTestExtensions.CreatePackage(
            ChartPackageTestExtensions.CreateEntryWithInstallDestination(installedFile, string.Empty),
            ChartPackageTestExtensions.CreateEntryWithInstallDestination(pendingFile, destinationDirectory));
        package.path = "C:\\Pending\\Pkg1";

        PendingInstallBatchPlan plan = service.BuildEstimatedInstallBatchPlan(
            [package],
            [package],
            CreateInstalledChartLookup([installedFile]));
        PendingInstallBatchItem capturedItem = null;
        PendingInstallBatchResult result = service.ExecuteEstimatedInstallBatchPlan(
            plan,
            deletePendingPackageSourceAfterInstall: false,
            installPackage: item =>
            {
                capturedItem = item;
                return CreateSuccessfulPackageInstallResult(item.InstallWorkPackage);
            },
            createInstalledDisplayPackage: null,
            cleanupPendingPackageSource: null);

        Assert.IsNotNull(capturedItem);
        Assert.AreSame(pendingFile, capturedItem.InstallWorkPackage.GetBmsOwnersForTest().Single());
        Assert.IsTrue(capturedItem.ExcludedComponentPaths.Contains(installedFile.path));
        Assert.AreEqual(1, result.PendingPackagesToRemove.Count);
        Assert.IsTrue(capturedItem.DestinationDirectory.Equals(destinationDirectory, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void BuildEstimatedInstallBatchPlan_TreatsInstalledBmsonAsInstalledByPrimaryHash()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryPackageInstallService();
        string destinationDirectory = "C:\\Installed\\Target";
        var installedBmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Lib\\chart.bmson",
            folder = "C:\\Lib",
            title = "Installed Bmson",
            md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        };
        var pendingBmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Pending\\Pkg1\\chart.bmson",
            folder = "C:\\Pending\\Pkg1",
            title = "Pending Bmson",
            md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        };
        PackageChartEntry pendingEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(pendingBmson));
        pendingEntry.ApplyInstallDestination(destinationDirectory, "Pending Bmson", "Artist");
        ChartPackage pendingPackage = ChartPackage.FromChartEntries([pendingEntry]);
        pendingPackage.path = "C:\\Pending\\Pkg1";
        pendingPackage.delete_parent = false;

        PendingInstallBatchPlan plan = service.BuildEstimatedInstallBatchPlan(
            [pendingPackage],
            [pendingPackage],
            CreateInstalledChartLookup([], [installedBmson]));

        PendingInstallBatchResult result = service.ExecuteEstimatedInstallBatchPlan(
            plan,
            deletePendingPackageSourceAfterInstall: true,
            installPackage: _ => throw new AssertFailedException("resource-only package must not run a chart move when no component target exists."),
            createInstalledDisplayPackage: null,
            cleanupPendingPackageSource: _ => (true, CleanupSourceKind.MissingSource),
            countComponentMoveTargets: (_, _, _) => 0);

        Assert.AreEqual(1, plan.CleanupOnlyCandidates.Count);
        Assert.AreSame(pendingPackage, plan.CleanupOnlyCandidates[0]);
        Assert.AreEqual(1, result.CleanupOnlySucceeded);
        Assert.AreEqual(1, result.PendingPackagesToRemove.Count);
        Assert.IsTrue(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
        Assert.IsNull(pendingEntry.GetBmsOwnerForTest());
    }

    [TestMethod]
    public void BuildEstimatedInstallBatchPlan_CountsDeferredManualHoldPackagesSeparately()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryPackageInstallService();
        TestableBmsFile pendingFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\Pkg1\\a.bms");
        var deferredPackage = ChartPackageTestExtensions.CreatePackage([pendingFile]);
        deferredPackage.path = "C:\\Pending\\Pkg1";
        deferredPackage.delete_parent = false;
        deferredPackage.DeferredEstimateReason = PendingEstimateDeferredReason.HealthySourceBaseline;

        PendingInstallBatchPlan plan = service.BuildEstimatedInstallBatchPlan(
            [deferredPackage],
            [deferredPackage],
            CreateInstalledChartLookup([]));

        Assert.AreEqual(0, plan.SelectedPendingPackages.Count);
        Assert.AreEqual(1, plan.DeferredManualHoldCount);
        Assert.AreEqual(string.Empty, pendingFile.Warnings.BuildDigestText());
    }

    [TestMethod]
    public void ExecuteEstimatedInstallBatchPlan_ReturnsPendingMutationsAndCleanupSummary()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryPackageInstallService();
        string destinationDirectory = "C:\\Installed\\Target";
        TestableBmsFile installedFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Lib\\a.bms");
        TestableBmsFile cleanupInstalledFile = CreateFile("cccccccccccccccccccccccccccccccc", "C:\\Lib\\c.bms");
        TestableBmsFile alreadyInstalledInPackage = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\Pkg1\\a.bms");
        TestableBmsFile newFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "C:\\Pending\\Pkg1\\b.bms");
        TestableBmsFile cleanupOnlyFile = CreateFile("cccccccccccccccccccccccccccccccc", "C:\\Pending\\Pkg2\\c.bms");
        var mixedPackage = ChartPackageTestExtensions.CreatePackage(
            ChartPackageTestExtensions.CreateEntryWithInstallDestination(alreadyInstalledInPackage, destinationDirectory),
            ChartPackageTestExtensions.CreateEntryWithInstallDestination(newFile, destinationDirectory));
        mixedPackage.path = "C:\\Pending\\Pkg1";
        mixedPackage.delete_parent = false;
        var cleanupOnlyPackage = ChartPackageTestExtensions.CreatePackage(
            ChartPackageTestExtensions.CreateEntryWithInstallDestination(cleanupOnlyFile, destinationDirectory));
        cleanupOnlyPackage.path = "C:\\Pending\\Pkg2";
        cleanupOnlyPackage.delete_parent = false;
        PendingInstallBatchPlan plan = service.BuildEstimatedInstallBatchPlan(
            [mixedPackage, cleanupOnlyPackage],
            [mixedPackage, cleanupOnlyPackage],
            CreateInstalledChartLookup([installedFile, cleanupInstalledFile]));

        PendingInstallBatchResult result = service.ExecuteEstimatedInstallBatchPlan(
            plan,
            deletePendingPackageSourceAfterInstall: true,
            installPackage: item =>
            {
                Assert.AreSame(mixedPackage, item.OriginalPackage);
                Assert.AreEqual(1, item.InstallWorkPackage.ChartEntries.Count);
                Assert.AreSame(newFile, item.InstallWorkPackage.GetBmsOwnersForTest()[0]);
                CollectionAssert.Contains(item.ExcludedComponentPaths.ToList(), alreadyInstalledInPackage.path);
                return CreateSuccessfulPackageInstallResult(item.InstallWorkPackage);
            },
            createInstalledDisplayPackage: (originalPackage, destinationDirectoryArg) =>
            {
                ChartPackage package = ChartPackageTestExtensions.CreatePackage(originalPackage.GetBmsOwnersForTest());
                package.path = destinationDirectoryArg;
                package.delete_parent = false;
                return package;
            },
            cleanupPendingPackageSource: cleanupPackage => cleanupPackage == cleanupOnlyPackage
                ? (true, CleanupSourceKind.MissingSource)
                : (false, CleanupSourceKind.MissingSource),
            countComponentMoveTargets: (pkg, _, _) => pkg.path == cleanupOnlyPackage.path ? 0 : 2);

        Assert.AreEqual(2, result.PendingPackagesToRemove.Count);
        CollectionAssert.AreEquivalent(new[] { mixedPackage.path, cleanupOnlyPackage.path }, result.InstallRowsToDelete.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        Assert.AreEqual(0, result.DeferredInstalledPackages.Count);
        Assert.AreEqual(1, result.CleanupOnlySucceeded);
        Assert.AreEqual(0, result.CleanupOnlyFailed);
        Assert.AreEqual(1, result.CleanupOnlyMissingSource);
        Assert.AreEqual(0, result.DeferredMaintenanceCharts.Count);
        Assert.IsTrue(mixedPackage.ChartEntries.All(entry => string.IsNullOrWhiteSpace(entry.Chart.InstallDestination)));
        Assert.IsTrue(cleanupOnlyPackage.ChartEntries.All(entry => string.IsNullOrWhiteSpace(entry.Chart.InstallDestination)));
    }

    [TestMethod]
    public void ExecuteEstimatedInstallBatchPlan_ExecutesOneBmsonWorkItem()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryPackageInstallService();
        string destinationDirectory = "C:\\Installed\\Target";
        var bmsonSong = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Pending\\Pkg1\\chart.bmson",
            folder = "C:\\Pending\\Pkg1",
            title = "Bmson",
            md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            sha256 = new string('b', 64)
        };
        PackageChartEntry bmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmsonSong));
        bmsonEntry.ApplyInstallDestination(destinationDirectory, "Bmson", "Artist");
        ChartPackage bmsonPackage = ChartPackage.FromChartEntries([bmsonEntry]);
        bmsonPackage.path = "C:\\Pending\\Pkg1";

        PendingInstallBatchPlan plan = service.BuildEstimatedInstallBatchPlan(
            [bmsonPackage],
            [bmsonPackage],
            CreateInstalledChartLookup([]));

        PendingInstallBatchResult result = service.ExecuteEstimatedInstallBatchPlan(
            plan,
            deletePendingPackageSourceAfterInstall: false,
            installPackage: item =>
            {
                Assert.AreSame(bmsonPackage, item.OriginalPackage);
                Assert.AreSame(bmsonEntry, item.InstallWorkPackage.ChartEntries.Single());
                return CreateSuccessfulPackageInstallResult(item.InstallWorkPackage);
            },
            createInstalledDisplayPackage: null,
            cleanupPendingPackageSource: null);

        Assert.AreEqual(1, result.PendingPackagesToRemove.Count);
        Assert.IsNull(bmsonEntry.GetBmsOwnerForTest());
        Assert.AreEqual(string.Empty, bmsonEntry.Chart.InstallDestination);
    }

    [TestMethod]
    public void ForceInstallPackages_SkipsWhenConfirmationRejected()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryPackageInstallService();
        TestableBmsFile pendingFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\Pkg1\\a.bms");
        PackageChartEntry pendingEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(pendingFile));
        pendingEntry.SetInstallDestinationPathOnly("C:\\Installed\\Target");
        var pendingPackage = ChartPackage.FromChartEntries([pendingEntry]);
        pendingPackage.path = "C:\\Pending\\Pkg1";
        pendingPackage.delete_parent = false;

        ForceInstallBatchResult result = service.ForceInstallPackages(
            [pendingPackage],
            [pendingPackage],
            _ => false,
            (_, __) => []);

        Assert.AreEqual(1, result.Requested);
        Assert.AreEqual(1, result.Skipped);
        Assert.AreEqual(0, result.Processed);
        Assert.AreEqual(0, result.PendingPackagesToRemove.Count);
        Assert.AreEqual("C:\\Installed\\Target", pendingEntry.Chart.InstallDestination);
    }

    [TestMethod]
    public void ForceInstallPackages_ChecksInstallDestinationWithoutMaterializingAdapterlessBmsonEntries()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryPackageInstallService();
        TestableBmsFile pendingFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\Pkg1\\a.bms");
        PackageChartEntry pendingBmsEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(pendingFile));
        pendingBmsEntry.SetInstallDestinationPathOnly("C:\\Installed\\Target");
        var adapterlessBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Pending\\Pkg1\\chart.bmson",
            md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            sha256 = new string('b', 64),
            title = "Bmson"
        }));
        ChartPackage pendingPackage = ChartPackage.FromChartEntries([pendingBmsEntry, adapterlessBmsonEntry]);
        pendingPackage.path = "C:\\Pending\\Pkg1";
        Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());

        ForceInstallBatchResult result = service.ForceInstallPackages(
            [pendingPackage],
            [pendingPackage],
            _ => false,
            (_, __) => []);

        Assert.AreEqual(1, result.Skipped);
        Assert.AreEqual(0, result.Processed);
        Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());
    }

    /// <summary>
    /// force-install の package batch は ManualRecoveryRequired を受けた時点で
    /// 後続 package の mutation callback を開始しません。
    /// </summary>
    [TestMethod]
    public void ForceInstallPackagesWithFileMutationReceipts_StopsAfterManualRecoveryRequired()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryPackageInstallService();
        ChartPackage firstPackage = ChartPackage.FromChartEntries([]);
        firstPackage.path = "C:\\Pending\\Pkg1";
        ChartPackage secondPackage = ChartPackage.FromChartEntries([]);
        secondPackage.path = "C:\\Pending\\Pkg2";
        int callbackCount = 0;

        ForceInstallBatchResult result = service.ForceInstallPackagesWithFileMutationReceipts(
            [firstPackage, secondPackage],
            [firstPackage, secondPackage],
            _ => true,
            (packages, _) =>
            {
                callbackCount++;
                Assert.AreSame(firstPackage, packages.Single());
                return new ForceInstallPackageApplyResult([firstPackage], manualRecoveryRequired: true);
            });

        Assert.AreEqual(2, result.Requested);
        Assert.AreEqual(1, result.Processed);
        Assert.AreEqual(1, result.Failed);
        Assert.AreEqual(1, callbackCount);
        Assert.AreEqual(0, result.PendingPackagesToRemove.Count);
    }

    [TestMethod]
    public void ForceInstallPackagesWithFileMutationReceipts_PreservesInnerBatchFinalizationFailure()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryPackageInstallService();
        ChartPackage firstPackage = ChartPackage.FromChartEntries([]);
        firstPackage.path = "C:\\Pending\\Pkg1";
        ChartPackage secondPackage = ChartPackage.FromChartEntries([]);
        secondPackage.path = "C:\\Pending\\Pkg2";
        var finalizationFailure = new InvalidOperationException("force-inner-finalizer-failure");
        int callbackCount = 0;

        ForceInstallBatchResult result = service.ForceInstallPackagesWithFileMutationReceipts(
            [firstPackage, secondPackage],
            [firstPackage, secondPackage],
            _ => true,
            (packages, _) =>
            {
                callbackCount++;
                PackageInstallExecutionResult packageResult = CreateSuccessfulPackageInstallResult(packages.Single());
                return new ForceInstallPackageApplyResult(
                    [],
                    manualRecoveryRequired: false,
                    packageResult.MutationReceipt.WithFinalizationFailure(finalizationFailure));
            });

        Assert.AreEqual(1, callbackCount);
        Assert.AreEqual(1, result.Processed);
        Assert.AreEqual(1, result.Failed);
        Assert.AreSame(finalizationFailure, result.MutationReceipt.FinalizationFailure);
        Assert.IsTrue(result.HasDurableFinalizationFailure);
        Assert.AreEqual(1, result.MutationReceipt.Receipts.Count);
        Assert.AreEqual(FileDbMutationTerminalState.Completed, result.MutationReceipt.Receipts[0].TerminalState);
    }

    [TestMethod]
    public void ChartPackage_ClearEntryInstallDestinations_DoesNotMaterializeAdapterlessBmsonEntries()
    {
        TestableBmsFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\Pkg\\a.bms");
        PackageChartEntry adapterlessBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.WithPackageState(ChartFileProjection.FromBmsonSong(new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Pending\\Pkg\\adapterless.bmson",
            md5 = "cccccccccccccccccccccccccccccccc"
        }), "C:\\Installed\\Target", "Installed", "Artist", []));
        PackageChartEntry bmsEntry = ChartPackageTestExtensions.CreateEntryWithInstallDestination(bmsFile, "C:\\Installed\\Target");
        ChartPackage package = ChartPackage.FromChartEntries(
        [
            bmsEntry,
            adapterlessBmsonEntry
        ]);

        foreach (PackageChartEntry entry in package.ChartEntries)
        {
            entry.ClearInstallDestination();
        }

        Assert.AreEqual(string.Empty, bmsEntry.Chart.InstallDestination);
        Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());
        Assert.AreEqual(string.Empty, adapterlessBmsonEntry.Chart.InstallDestination);
    }

    [TestMethod]
    public void ApplyPendingResourceHealthProjection_BmsonUsesChartResourceSnapshot()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string chartPath = Path.Combine(tempDirectoryPath, "chart.bmson");
            var song = new LR2SongDBExtended.bmson_song
            {
                path = chartPath,
                folder = tempDirectoryPath,
                title = "BMSON",
                artist = "Artist",
                md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                wav_files = ["missing.wav"]
            };
            PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(song));

            IReadOnlyList<ChartWarning> warnings = BmsLibraryPackageInstallService.ApplyPendingResourceHealthProjection(entry);

            Assert.IsTrue(warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            Assert.IsNull(entry.GetBmsOwnerForTest());
        });
    }

    [TestMethod]
    public void ApplyPendingResourceHealthProjection_StoresWarningsAndHealthOnPackageEntry()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string chartPath = Path.Combine(tempDirectoryPath, "chart.bmson");
            var song = new LR2SongDBExtended.bmson_song
            {
                path = chartPath,
                folder = tempDirectoryPath,
                title = "BMSON",
                artist = "Artist",
                md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                wav_files = ["missing.wav"],
                bga_files = ["missing.png"]
            };
            PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(song));

            IReadOnlyList<ChartWarning> warnings = BmsLibraryPackageInstallService.ApplyPendingResourceHealthProjection(entry);

            Assert.IsTrue(warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            Assert.IsTrue(warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceBgaMissing));
            Assert.IsTrue(entry.Chart.WAVHealth.HasValue);
            Assert.IsTrue(entry.Chart.BGAHealth.HasValue);
            Assert.IsNull(entry.GetBmsOwnerForTest());
        });
    }

    [TestMethod]
    public void ApplyPendingResourceHealthProjection_BmsUsesChartProjectionWithoutMutatingMaintenance()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string chartPath = Path.Combine(tempDirectoryPath, "chart.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n"
                + "#TITLE BMS\r\n"
                + "#WAVAA missing.wav\r\n"
                + "#00111:AA\r\n");
            BMSFile file = BMSFile.CreateBMSFileFromFile(chartPath);
            PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(file));

            IReadOnlyList<ChartWarning> warnings = BmsLibraryPackageInstallService.ApplyPendingResourceHealthProjection(entry);

            Assert.IsTrue(warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            Assert.IsFalse(file.HasValidMaintenanceInfoSnapshot);
            Assert.IsFalse(file.Warnings.Contains(ChartWarningKind.ResourceWavMissing));
        });
    }

    /// <summary>
    /// Protected and ordinary sources may be dropped together. Normalized root
    /// aliases must not bypass protection or exclude a sibling with the same prefix.
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PrepareAutoInstallWorkflow_ExcludesRegisteredRootsButKeepsOutsideSibling(bool useRootAlias)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string registeredRoot = Path.Combine(tempDirectoryPath, "Library");
            string outsideDirectory = Path.Combine(tempDirectoryPath, "Library-pending");
            string protectedChart = CreateBmsFile(registeredRoot, "owned.bms", "#TITLE Owned");
            string outsideChart = CreateBmsFile(outsideDirectory, "copy.bms", "#TITLE Copy");
            File.WriteAllText(Path.Combine(tempDirectoryPath, "not-dropped.txt"), "Keep separate source directories.");
            string rootPath = useRootAlias
                ? Path.Combine(registeredRoot, ".").ToUpperInvariant() + Path.DirectorySeparatorChar
                : registeredRoot;
            var service = new BmsLibraryPackageInstallService();

            AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow(
                [registeredRoot, outsideDirectory],
                [],
                [rootPath],
                _ => true,
                0.6);

            Assert.AreEqual(1, result.DiscoveredPackages.Count);
            Assert.IsTrue(string.Equals(outsideDirectory, result.DiscoveredPackages[0].path,
                StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(1, result.PendingPackagesToAdd.Count);
            Assert.IsTrue(string.Equals(outsideDirectory, result.PendingPackagesToAdd[0].path,
                StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(0, result.AutoInstallCandidates.Count);
            Assert.AreEqual(0, result.PendingPackagesToRemove.Count);
            Assert.IsTrue(File.Exists(protectedChart));
            Assert.IsTrue(File.Exists(outsideChart));
        });
    }

    [TestMethod]
    public void PrepareAutoInstallWorkflow_ClassifiesBmsResourcesWithoutMutatingMaintenance()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "BmsMissingResource");
            Directory.CreateDirectory(packageDirectoryPath);
            string bmsFilePath = Path.Combine(packageDirectoryPath, "chart.bms");
            File.WriteAllText(
                bmsFilePath,
                "#PLAYER 1\r\n"
                + "#TITLE BMS\r\n"
                + "#WAVAA missing.wav\r\n"
                + "#00111:AA\r\n");
            var service = new BmsLibraryPackageInstallService();

            AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow(
                [packageDirectoryPath],
                [],
                [],
                _ => false,
                0.6);

            Assert.AreEqual(1, result.PendingPackagesToAdd.Count);
            PackageChartEntry chartEntry = result.PendingPackagesToAdd[0].ChartEntries.Single();
            BMSFile file = chartEntry.Chart.GetBmsStorageOwner();
            Assert.IsNotNull(file);
            Assert.IsFalse(file.HasValidMaintenanceInfoSnapshot);
            Assert.IsTrue(chartEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
        });
    }

    [TestMethod]
    public void PrepareAutoInstallWorkflow_ProjectsResourceHealthForAllPackageEntries()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "BmsMixedResource");
            Directory.CreateDirectory(packageDirectoryPath);
            File.WriteAllText(
                Path.Combine(packageDirectoryPath, "missing.bms"),
                "#PLAYER 1\r\n"
                + "#TITLE Missing\r\n"
                + "#WAVAA missing.wav\r\n"
                + "#00111:AA\r\n");
            File.WriteAllText(
                Path.Combine(packageDirectoryPath, "healthy.bms"),
                "#PLAYER 1\r\n"
                + "#TITLE Healthy\r\n"
                + "#WAVAA sound.wav\r\n"
                + "#00111:AA\r\n");
            File.WriteAllBytes(Path.Combine(packageDirectoryPath, "sound.wav"), new byte[] { 1 });
            var service = new BmsLibraryPackageInstallService();

            AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow(
                [packageDirectoryPath],
                [],
                [],
                _ => false,
                0.6);

            ChartPackage pendingPackage = result.PendingPackagesToAdd.Single();
            PackageChartEntry missingEntry = pendingPackage.ChartEntries.Single(entry => Path.GetFileName(entry.Chart.Path).Equals("missing.bms", StringComparison.OrdinalIgnoreCase));
            PackageChartEntry healthyEntry = pendingPackage.ChartEntries.Single(entry => Path.GetFileName(entry.Chart.Path).Equals("healthy.bms", StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(missingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            Assert.IsTrue(missingEntry.Chart.WAVHealth.HasValue);
            Assert.AreEqual(100, healthyEntry.Chart.WAVHealth);
            Assert.IsFalse(healthyEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
        });
    }

    [TestMethod]
    public void PrepareAutoInstallWorkflow_ProjectsResourceHealthForAlreadyInstalledEntries()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "BmsInstalledResource");
            Directory.CreateDirectory(packageDirectoryPath);
            string installedPath = Path.Combine(packageDirectoryPath, "installed.bms");
            File.WriteAllText(
                installedPath,
                "#PLAYER 1\r\n"
                + "#TITLE Installed\r\n"
                + "#WAVAA missing.wav\r\n"
                + "#00111:AA\r\n");
            var service = new BmsLibraryPackageInstallService();

            AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow(
                [packageDirectoryPath],
                [],
                [],
                chart => string.Equals(chart?.Path, installedPath, StringComparison.OrdinalIgnoreCase),
                0.6);

            PackageChartEntry installedEntry = result.PendingPackagesToAdd.Single().ChartEntries.Single();
            Assert.IsTrue(installedEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
            Assert.IsTrue(installedEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            Assert.IsTrue(installedEntry.Chart.WAVHealth.HasValue);
        });
    }

    [TestMethod]
    public void DeletePendingPackageSources_RemovesPackagesWhoseSourceWasDeleted()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryPackageInstallService();
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "Pkg1");
            Directory.CreateDirectory(packageDirectoryPath);
            File.WriteAllText(Path.Combine(packageDirectoryPath, "chart.bms"), "#PLAYER 1");
            var pendingPackage = ChartPackageTestExtensions.CreatePackage(Enumerable.Empty<BMSFile>());
            pendingPackage.path = packageDirectoryPath;
            pendingPackage.delete_parent = false;

            PendingPackageSourceDeletionResult result = service.DeletePendingPackageSources(
                [new PendingPackageSourceDeletionTarget(pendingPackage)],
                sendToRecycleBin: false,
                new TestFileMutationService(),
                null,
                null);

            Assert.AreEqual(1, result.Requested);
            Assert.AreEqual(1, result.Processed);
            Assert.AreEqual(1, result.Removed);
            Assert.AreEqual(0, result.Failed);
            CollectionAssert.AreEqual(new[] { pendingPackage }, result.PackagesToRemove);
            Assert.IsFalse(Directory.Exists(packageDirectoryPath));
        });
    }

    [TestMethod]
    public void SearchChartPackagesRecursivelyWithMetadata_PackagesSplitsIndependentChartsIntoSingleFilePackages()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "Pkg");
            Directory.CreateDirectory(packageDirectoryPath);
            File.WriteAllText(Path.Combine(packageDirectoryPath, "chart_a.bms"), "#PLAYER 1\r\n#TITLE A\r\n#WAVAA sound_a.wav\r\n#00111:AA\r\n");
            File.WriteAllText(Path.Combine(packageDirectoryPath, "chart_b.bms"), "#PLAYER 1\r\n#TITLE B\r\n#WAVAA sound_b.wav\r\n#00111:AA\r\n");

            var service = new BmsLibraryPackageInstallService();
            List<ChartPackage> result = service.SearchChartPackagesRecursivelyWithMetadata(packageDirectoryPath, 0.6).Packages;

            Assert.AreEqual(2, result.Count);
            Assert.IsTrue(result.All(package => File.Exists(package.path)));
        });
    }

    [TestMethod]
    public void SearchChartPackagesRecursivelyWithMetadata_MarksSplitDirectoryAsRegroupEligible()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "Pkg");
            Directory.CreateDirectory(packageDirectoryPath);
            File.WriteAllText(Path.Combine(packageDirectoryPath, "chart_a.bms"), "#PLAYER 1\r\n#TITLE A\r\n#WAVAA sound_a.wav\r\n#00111:AA\r\n");
            File.WriteAllText(Path.Combine(packageDirectoryPath, "chart_b.bms"), "#PLAYER 1\r\n#TITLE B\r\n#WAVAA sound_b.wav\r\n#00111:AA\r\n");

            var service = new BmsLibraryPackageInstallService();
            ChartPackageDiscoveryResult result = service.SearchChartPackagesRecursivelyWithMetadata(packageDirectoryPath, 0.6);

            Assert.AreEqual(2, result.Packages.Count);
            CollectionAssert.AreEqual(new[] { packageDirectoryPath }, result.RegroupEligibleSourceDirectories);
        });
    }

    [TestMethod]
    public void SearchChartPackagesRecursivelyWithMetadata_MarksNestedSplitDirectoryAsRegroupEligible()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string rootDirectoryPath = Path.Combine(tempDirectoryPath, "Root");
            string childDirectoryPath = Path.Combine(rootDirectoryPath, "Child");
            Directory.CreateDirectory(childDirectoryPath);
            File.WriteAllText(Path.Combine(childDirectoryPath, "chart_a.bms"), "#PLAYER 1\r\n#TITLE A\r\n#WAVAA sound_a.wav\r\n#00111:AA\r\n");
            File.WriteAllText(Path.Combine(childDirectoryPath, "chart_b.bms"), "#PLAYER 1\r\n#TITLE B\r\n#WAVAA sound_b.wav\r\n#00111:AA\r\n");

            var service = new BmsLibraryPackageInstallService();
            ChartPackageDiscoveryResult result = service.SearchChartPackagesRecursivelyWithMetadata(rootDirectoryPath, 0.6);

            Assert.AreEqual(2, result.Packages.Count);
            CollectionAssert.AreEqual(new[] { childDirectoryPath }, result.RegroupEligibleSourceDirectories);
        });
    }

    [TestMethod]
    public void SearchChartPackagesRecursivelyWithMetadata_DetectsPureBmsonDirectoryPackage()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "BmsonPkg");
            Directory.CreateDirectory(packageDirectoryPath);
            File.WriteAllText(Path.Combine(packageDirectoryPath, "chart.bmson"), "{\"version\":\"1.0.0\",\"info\":{\"title\":\"Title\",\"artist\":\"Artist\",\"mode_hint\":\"beat-7k\"},\"sound_channels\":[{\"name\":\"sound.wav\",\"notes\":[{\"x\":1,\"y\":0,\"l\":0}]}]}");
            File.WriteAllText(Path.Combine(packageDirectoryPath, "sound.wav"), "dummy");

            var service = new BmsLibraryPackageInstallService();
            ChartPackageDiscoveryResult result = service.SearchChartPackagesRecursivelyWithMetadata(packageDirectoryPath, 0.6);

            Assert.AreEqual(1, result.Packages.Count);
            Assert.AreEqual(packageDirectoryPath, result.Packages[0].path);
            Assert.AreEqual(1, result.Packages[0].ChartEntries.Count(entry => entry?.Chart?.Kind == ChartFileKind.Bmson));
            Assert.IsTrue(result.Packages[0].ChartEntries.All(entry => entry.GetBmsOwnerForTest() == null));
        });
    }

    [TestMethod]
    public void SearchChartPackagesRecursivelyWithMetadata_DetectsRootAndNestedChartsAsOneDirectoryPackage()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "Pkg");
            string nestedDirectoryPath = Path.Combine(packageDirectoryPath, "sub");
            Directory.CreateDirectory(nestedDirectoryPath);
            File.WriteAllText(Path.Combine(packageDirectoryPath, "root.bms"), "#PLAYER 1\r\n#TITLE Root\r\n");
            File.WriteAllText(Path.Combine(nestedDirectoryPath, "another.bms"), "#PLAYER 1\r\n#TITLE Nested\r\n");

            var service = new BmsLibraryPackageInstallService();
            ChartPackageDiscoveryResult result = service.SearchChartPackagesRecursivelyWithMetadata(packageDirectoryPath, 0.6);

            Assert.AreEqual(1, result.Packages.Count);
            Assert.AreEqual(packageDirectoryPath, result.Packages[0].path);
            CollectionAssert.AreEquivalent(
                new[] { "root.bms", "another.bms" },
                result.Packages[0].GetBmsOwnersForTest().Select(file => Path.GetFileName(file.path)).ToArray());
        });
    }

    [TestMethod]
    public void ApplyNestedChartFileWarnings_AddsNestedWarningWithoutMaterializingAdapterlessBmsonEntries()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "Pkg");
            string nestedDirectoryPath = Path.Combine(packageDirectoryPath, "sub");
            Directory.CreateDirectory(nestedDirectoryPath);
            string rootBmsonPath = Path.Combine(packageDirectoryPath, "root.bmson");
            string nestedBmsonPath = Path.Combine(nestedDirectoryPath, "nested.bmson");
            File.WriteAllText(rootBmsonPath, CreateBmsonJsonWithSound("root.wav"));
            File.WriteAllText(nestedBmsonPath, CreateBmsonJsonWithSound("nested.wav"));
            PackageChartEntry rootEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(BmsonSongParser.Parse(rootBmsonPath)));
            PackageChartEntry nestedEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(BmsonSongParser.Parse(nestedBmsonPath)));
            ChartPackage package = ChartPackage.FromChartEntries([rootEntry, nestedEntry]);
            package.path = packageDirectoryPath;

            bool applied = BmsLibraryPackageInstallService.ApplyNestedChartFileWarnings(package);

            Assert.IsTrue(applied);
            Assert.IsNull(rootEntry.GetBmsOwnerForTest());
            Assert.IsNull(nestedEntry.GetBmsOwnerForTest());
            Assert.IsTrue(nestedEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.NestedChartFileInPackage));
            StringAssert.Contains(ChartWarningCollection.BuildTooltipText(nestedEntry.Chart.Warnings), Resources.Warning_NestedChartFileInPackage);
            PackageChartEntry normalizedNestedEntry = PackageChartEntry.FromChart(nestedEntry.Chart);
            Assert.IsNull(normalizedNestedEntry.GetBmsOwnerForTest());
            Assert.IsTrue(normalizedNestedEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.NestedChartFileInPackage));
            PackageChartEntry clearedNormalizedNestedEntry = PackageChartEntry.FromChart(nestedEntry.Chart);
            clearedNormalizedNestedEntry.ClearStructuredWarnings();
            Assert.IsFalse(clearedNormalizedNestedEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.NestedChartFileInPackage));
        });
    }

    [TestMethod]
    public void PrepareAutoInstallWorkflow_DetectsSingleBmsonFileSelection()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourceDirectoryPath = Path.Combine(tempDirectoryPath, "BmsonSingle");
            Directory.CreateDirectory(sourceDirectoryPath);
            string bmsonFilePath = Path.Combine(sourceDirectoryPath, "chart.bmson");
            File.WriteAllText(bmsonFilePath, "{\"version\":\"1.0.0\",\"info\":{\"title\":\"Title\",\"artist\":\"Artist\",\"mode_hint\":\"beat-7k\"},\"lines\":[{\"y\":0}]}");
            File.WriteAllText(Path.Combine(sourceDirectoryPath, "other.txt"), "note");

            var service = new BmsLibraryPackageInstallService();
            AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow(
                [bmsonFilePath],
                [],
                [],
                _ => false,
                0.6);

            Assert.AreEqual(1, result.DiscoveredPackages.Count);
            Assert.AreEqual(bmsonFilePath, result.DiscoveredPackages[0].path);
            Assert.AreEqual(1, result.PendingPackagesToAdd.Count);
            Assert.AreEqual(bmsonFilePath, result.PendingPackagesToAdd[0].path);
            Assert.AreEqual(ChartFileKind.Bmson, result.PendingPackagesToAdd[0].ChartEntries.Single().Chart.Kind);
        });
    }

    [TestMethod]
    public void PrepareAutoInstallWorkflow_KeepsBmsonPackagePendingWhenResourcesAreMissing()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "BmsonMissingResource");
            Directory.CreateDirectory(packageDirectoryPath);
            string bmsonFilePath = Path.Combine(packageDirectoryPath, "chart.bmson");
            File.WriteAllText(bmsonFilePath, CreateBmsonJsonWithSound("missing.wav"));
            var service = new BmsLibraryPackageInstallService();

            AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow(
                [packageDirectoryPath],
                [],
                [],
                _ => false,
                0.6);

            Assert.AreEqual(1, result.DiscoveredPackages.Count);
            Assert.AreEqual(1, result.PendingPackagesToAdd.Count);
            Assert.AreEqual(0, result.AutoInstallCandidates.Count);
            PackageChartEntry chartEntry = result.PendingPackagesToAdd[0].ChartEntries.Single();
            Assert.IsNull(chartEntry.GetBmsOwnerForTest());
            Assert.AreEqual(ChartFileKind.Bmson, chartEntry.Chart.Kind);
            Assert.IsTrue(chartEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            StringAssert.Contains(chartEntry.Chart.Warnings.Single(warning => warning.Kind == ChartWarningKind.ResourceWavMissing).Message, "WAV");
        });
    }

    [TestMethod]
    public void PrepareAutoInstallWorkflow_ChecksInstalledChartsWithoutMaterializingUnmatchedBmsonEntries()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "BmsonInstalled");
            Directory.CreateDirectory(packageDirectoryPath);
            string installedBmsonPath = Path.Combine(packageDirectoryPath, "installed.bmson");
            string unmatchedBmsonPath = Path.Combine(packageDirectoryPath, "unmatched.bmson");
            File.WriteAllText(installedBmsonPath, CreateBmsonJsonWithSound("sound.wav"));
            File.WriteAllText(unmatchedBmsonPath, CreateBmsonJsonWithSound("sound.wav"));
            File.WriteAllText(Path.Combine(packageDirectoryPath, "sound.wav"), "audio");

            var service = new BmsLibraryPackageInstallService();

            AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow(
                [packageDirectoryPath],
                [],
                [],
                chart => string.Equals(chart?.Path, installedBmsonPath, StringComparison.OrdinalIgnoreCase),
                0.6);

            PackageChartEntry installedEntry = result.DiscoveredPackages
                .SelectMany(package => package.ChartEntries)
                .First(entry => string.Equals(entry.Chart.Path, installedBmsonPath, StringComparison.OrdinalIgnoreCase));
            PackageChartEntry unmatchedEntry = result.DiscoveredPackages
                .SelectMany(package => package.ChartEntries)
                .First(entry => string.Equals(entry.Chart.Path, unmatchedBmsonPath, StringComparison.OrdinalIgnoreCase));
            Assert.IsNull(installedEntry.GetBmsOwnerForTest());
            Assert.IsTrue(installedEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
            Assert.IsNull(unmatchedEntry.GetBmsOwnerForTest());
        });
    }

    [TestMethod]
    public void PackageChartEntry_ResourceSnapshotReadsBmsOwnerResourcesFromLightweightProjection()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string chartPath = Path.Combine(tempDirectoryPath, "chart.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n"
                + "#TITLE Resource Owner\r\n"
                + "#WAV01 audio.wav\r\n"
                + "#BMP01 movie.mpg\r\n"
                + "#STAGEFILE stage.png\r\n"
                + "#00111:01\r\n"
                + "#00104:01\r\n");
            BMSFile bmsFile = BMSFile.CreateBMSFileFromFile(chartPath);
            ChartFile lightweightChart = ChartFileProjection.FromBmsFile(
                bmsFile,
                includeWarningSnapshot: false,
                includeResourceReferences: false);
            PackageChartEntry entry = PackageChartEntry.FromChart(lightweightChart);

            Assert.AreEqual(1, entry.ResourceSnapshot.AudioReferenceCount);
            Assert.AreEqual(1, entry.ResourceSnapshot.MovieReferenceCount);
            Assert.AreEqual(1, entry.ResourceSnapshot.OptionalImageReferenceCount);
        });
    }

    [TestMethod]
    public void PackageChartEntry_ResourceSnapshotReadsBmsonOwnerResourcesFromLightweightProjection()
    {
        var song = new LR2SongDBExtended.bmson_song
        {
            path = @"D:\BMS\pkg\chart.bmson",
            md5 = "11111111111111111111111111111111",
            title = "Resource Owner",
            wav_files = ["audio.wav"],
            bga_files = ["movie.mpg"],
            stagefile = "stage.png"
        };
        ChartFile lightweightChart = ChartFileProjection.FromBmsonSong(
            song,
            includeWarningSnapshot: false,
            includeResourceReferences: false);
        PackageChartEntry entry = PackageChartEntry.FromChart(lightweightChart);

        Assert.AreEqual(1, entry.ResourceSnapshot.AudioReferenceCount);
        Assert.AreEqual(1, entry.ResourceSnapshot.MovieReferenceCount);
        Assert.AreEqual(1, entry.ResourceSnapshot.OptionalImageReferenceCount);
    }

    [TestMethod]
    public void PrepareAutoInstallWorkflow_KeepsChartPackagePendingWhenOnlySameStemChartFileExists()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "BmsMissingSameStem");
            Directory.CreateDirectory(packageDirectoryPath);
            string bmsFilePath = Path.Combine(packageDirectoryPath, "chart.bme");
            File.WriteAllText(
                bmsFilePath,
                "#PLAYER 1\r\n"
                + "#TITLE Same Stem Missing\r\n"
                + "#ARTIST Test\r\n"
                + "#WAVAA chart.wav\r\n"
                + "#BMP01 chart.mpg\r\n"
                + "#00111:AA\r\n"
                + "#00104:01\r\n");
            var service = new BmsLibraryPackageInstallService();

            AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow(
                [packageDirectoryPath],
                [],
                [],
                _ => false,
                0.6);

            Assert.AreEqual(1, result.DiscoveredPackages.Count);
            Assert.AreEqual(1, result.PendingPackagesToAdd.Count);
            Assert.AreEqual(0, result.AutoInstallCandidates.Count);
            PackageChartEntry chartEntry = result.PendingPackagesToAdd[0].ChartEntries
                .Single(entry => entry.Chart.Kind == ChartFileKind.Bms);
            BMSFile chart = chartEntry.Chart.GetBmsStorageOwner();
            Assert.IsNotNull(chart);
            Assert.AreEqual(1, chartEntry.ResourceSnapshot.AudioReferenceCount);
            Assert.AreEqual(1, chartEntry.ResourceSnapshot.MovieReferenceCount);
            Assert.IsTrue(chartEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            Assert.IsTrue(chartEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceMovieMissing));
        });
    }

    [TestMethod]
    public void PrepareAutoInstallWorkflow_DoesNotUseImageAsAudioOrMovieResource()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "BmsImageOnlySameStem");
            Directory.CreateDirectory(packageDirectoryPath);
            string bmsFilePath = Path.Combine(packageDirectoryPath, "chart.bme");
            File.WriteAllText(
                bmsFilePath,
                "#PLAYER 1\r\n"
                + "#TITLE Image Only Same Stem\r\n"
                + "#ARTIST Test\r\n"
                + "#WAVAA chart.wav\r\n"
                + "#BMP01 chart.mpg\r\n"
                + "#00111:AA\r\n"
                + "#00104:01\r\n");
            File.WriteAllText(Path.Combine(packageDirectoryPath, "chart.png"), "image");
            var service = new BmsLibraryPackageInstallService();

            AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow(
                [packageDirectoryPath],
                [],
                [],
                _ => false,
                0.6);

            Assert.AreEqual(1, result.PendingPackagesToAdd.Count);
            Assert.AreEqual(0, result.AutoInstallCandidates.Count);
            PackageChartEntry chartEntry = result.PendingPackagesToAdd[0].ChartEntries
                .Single(entry => entry.Chart.Kind == ChartFileKind.Bms);
            BMSFile chart = chartEntry.Chart.GetBmsStorageOwner();
            Assert.IsNotNull(chart);
            Assert.AreEqual(1, chartEntry.ResourceSnapshot.AudioReferenceCount);
            Assert.AreEqual(1, chartEntry.ResourceSnapshot.MovieReferenceCount);
            Assert.IsTrue(chartEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            Assert.IsTrue(chartEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceMovieMissing));
        });
    }

    [TestMethod]
    public void PrepareAutoInstallWorkflow_UsesSameStemAudioAndMovieResourcesByCategory()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "BmsCompleteSameStem");
            Directory.CreateDirectory(packageDirectoryPath);
            string bmsFilePath = Path.Combine(packageDirectoryPath, "chart.bme");
            File.WriteAllText(
                bmsFilePath,
                "#PLAYER 1\r\n"
                + "#TITLE Complete Same Stem\r\n"
                + "#ARTIST Test\r\n"
                + "#WAVAA chart.wav\r\n"
                + "#BMP01 chart.mpg\r\n"
                + "#00111:AA\r\n"
                + "#00104:01\r\n");
            File.WriteAllText(Path.Combine(packageDirectoryPath, "chart.wav"), "audio");
            File.WriteAllText(Path.Combine(packageDirectoryPath, "chart.mpg"), "movie");
            var service = new BmsLibraryPackageInstallService();

            AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow(
                [packageDirectoryPath],
                [],
                [],
                _ => false,
                0.6);

            Assert.AreEqual(1, result.AutoInstallCandidates.Count);
            Assert.AreEqual(0, result.PendingPackagesToAdd.Count);
            PackageChartEntry chartEntry = result.AutoInstallCandidates[0].ChartEntries
                .Single(entry => entry.Chart.Kind == ChartFileKind.Bms);
            BMSFile chart = chartEntry.Chart.GetBmsStorageOwner();
            Assert.IsNotNull(chart);
            Assert.AreEqual(1, chartEntry.ResourceSnapshot.AudioReferenceCount);
            Assert.AreEqual(1, chartEntry.ResourceSnapshot.MovieReferenceCount);
            Assert.IsFalse(chartEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            Assert.IsFalse(chartEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceMovieMissing));
        });
    }

    [TestMethod]
    public void PrepareAutoInstallWorkflow_PrioritizesNestedChartWarningBeforeResourceWarnings()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "Pkg");
            string nestedDirectoryPath = Path.Combine(packageDirectoryPath, "sub");
            Directory.CreateDirectory(nestedDirectoryPath);
            File.WriteAllText(Path.Combine(packageDirectoryPath, "root.bms"), "#PLAYER 1\r\n#TITLE Root\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            File.WriteAllText(Path.Combine(packageDirectoryPath, "sound.wav"), "dummy");
            File.WriteAllText(Path.Combine(nestedDirectoryPath, "another.bms"), "#PLAYER 1\r\n#TITLE Nested\r\n#WAVAA missing.wav\r\n#00111:AA\r\n");
            var service = new BmsLibraryPackageInstallService();

            AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow(
                [packageDirectoryPath],
                [],
                [],
                _ => false,
                0.6);

            Assert.AreEqual(1, result.PendingPackagesToAdd.Count);
            Assert.AreEqual(0, result.AutoInstallCandidates.Count);
            PackageChartEntry nestedEntry = result.PendingPackagesToAdd[0].ChartEntries.Single(entry => Path.GetFileName(entry.Chart.Path).Equals("another.bms", StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(nestedEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.NestedChartFileInPackage));
            Assert.IsTrue(nestedEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            Assert.AreEqual("[2] " + BeMusicSeeker.Properties.Resources.WarningDigest_NestedChart + ", " + BeMusicSeeker.Properties.Resources.WarningDigest_ResourceMissing, ChartWarningTestHelpers.BuildDigestText(nestedEntry));
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(nestedEntry), BeMusicSeeker.Properties.Resources.Warning_NestedChartFileInPackage);
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(nestedEntry), "WAV");
        });
    }

    [TestMethod]
    public void PrepareAutoInstallWorkflow_ExplicitBmsonFileWithAdjacentResourcesUsesDirectoryPackage()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "BmsonWithResource");
            Directory.CreateDirectory(packageDirectoryPath);
            string bmsonFilePath = Path.Combine(packageDirectoryPath, "chart.bmson");
            File.WriteAllText(bmsonFilePath, CreateBmsonJsonWithSound("sound.wav"));
            File.WriteAllText(Path.Combine(packageDirectoryPath, "sound.wav"), "dummy");
            var service = new BmsLibraryPackageInstallService();

            AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow(
                [bmsonFilePath],
                [],
                [],
                _ => false,
                0.6);

            Assert.AreEqual(1, result.DiscoveredPackages.Count);
            Assert.IsTrue(string.Equals(packageDirectoryPath, result.DiscoveredPackages[0].path, StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(1, result.AutoInstallCandidates.Count);
            Assert.IsTrue(string.Equals(packageDirectoryPath, result.AutoInstallCandidates[0].path, StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(0, result.PendingPackagesToAdd.Count);
            PackageChartEntry chartEntry = result.AutoInstallCandidates[0].ChartEntries.Single();
            Assert.IsNull(chartEntry.GetBmsOwnerForTest());
            Assert.AreEqual(ChartFileKind.Bmson, chartEntry.Chart.Kind);
            Assert.AreEqual(1, chartEntry.ResourceSnapshot.AudioReferenceCount);
            Assert.AreEqual(0, chartEntry.Chart.Warnings.Count);
        });
    }

    [TestMethod]
    public void PrepareAutoInstallWorkflow_ClassifiesDetectedDirectoriesAsInstallable()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string directoryPackagePath = Path.Combine(tempDirectoryPath, "DirPkg");
            Directory.CreateDirectory(directoryPackagePath);
            File.WriteAllText(Path.Combine(directoryPackagePath, "chart_dir.bms"), "#PLAYER 1\r\n#TITLE Dir\r\n#WAVAA sound_dir.wav\r\n#00111:AA\r\n");
            File.WriteAllText(Path.Combine(directoryPackagePath, "sound_dir.wav"), "audio");

            string filePackageDirectoryPath = Path.Combine(tempDirectoryPath, "SinglePkg");
            Directory.CreateDirectory(filePackageDirectoryPath);
            string singleFilePath = Path.Combine(filePackageDirectoryPath, "chart_single.bms");
            File.WriteAllText(singleFilePath, "#PLAYER 1\r\n#TITLE Single\r\n#WAVAA sound_single.wav\r\n#00111:AA\r\n");
            File.WriteAllText(Path.Combine(filePackageDirectoryPath, "sound_single.wav"), "audio");

            var service = new BmsLibraryPackageInstallService();
            AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow(
                [directoryPackagePath, singleFilePath],
                [],
                [],
                _ => false,
                0.6);

            Assert.AreEqual(2, result.DiscoveredPackages.Count);
            Assert.AreEqual(2, result.AutoInstallCandidates.Count);
            Assert.AreEqual(0, result.PendingPackagesToAdd.Count);
            Assert.AreEqual(0, result.RegroupEligibleSourceDirectories.Count);
            Assert.IsTrue(Directory.Exists(result.AutoInstallCandidates[0].path));
            Assert.IsTrue(result.AutoInstallCandidates.Any(package => package.path.Equals(filePackageDirectoryPath, StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(result.DiscoveredPackages.All(package => package.GetBmsOwnersForTest().Count > 0));
            Assert.IsTrue(result.DiscoveryMs >= 0);
            Assert.IsTrue(result.ClassificationMs >= 0);
            Assert.IsTrue(result.TotalMs >= 0);
        });
    }

    [TestMethod]
    public void ApplyAutoInstallWorkflow_KeepsLaterDuplicateInstallablePackagePendingAfterSuccess()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string firstPackageDirectoryPath = Path.Combine(tempDirectoryPath, "FirstPkg");
            string secondPackageDirectoryPath = Path.Combine(tempDirectoryPath, "SecondPkg");
            Directory.CreateDirectory(firstPackageDirectoryPath);
            Directory.CreateDirectory(secondPackageDirectoryPath);
            string chartContent = "#PLAYER 1\r\n#TITLE Duplicate\r\n#WAVAA sound.wav\r\n#00111:AA\r\n";
            File.WriteAllText(Path.Combine(firstPackageDirectoryPath, "chart.bms"), chartContent);
            File.WriteAllText(Path.Combine(firstPackageDirectoryPath, "sound.wav"), "audio");
            File.WriteAllText(Path.Combine(secondPackageDirectoryPath, "chart.bms"), chartContent);
            File.WriteAllText(Path.Combine(secondPackageDirectoryPath, "sound.wav"), "audio");

            var service = new BmsLibraryPackageInstallService();
            AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow(
                [firstPackageDirectoryPath, secondPackageDirectoryPath],
                [],
                [],
                _ => false,
                0.6);

            Assert.AreEqual(2, result.DiscoveredPackages.Count);
            Assert.AreEqual(2, result.AutoInstallCandidates.Count);
            Assert.AreEqual(0, result.PendingPackagesToAdd.Count);

            AutoInstallApplyResult applyResult = service.ApplyAutoInstallWorkflow(
                result,
                keepInstallablePackagesPending: false,
                canAutoInstallImmediately: true,
                packages => []);

            Assert.AreEqual(1, applyResult.AutoInstalledPackages.Count);
            Assert.AreEqual(1, applyResult.PendingPackagesToAdd.Count);
            PackageChartEntry pendingEntry = applyResult.PendingPackagesToAdd[0].ChartEntries.Single();
            Assert.IsTrue(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
            Assert.AreEqual("[1] " + BeMusicSeeker.Properties.Resources.WarningDigest_AlreadyInstalled, ChartWarningTestHelpers.BuildDigestText(pendingEntry));
        });
    }

    [TestMethod]
    public void ApplyAutoInstallWorkflowWithFileMutationReceipts_PreservesDurablePrefixAndDuplicateWarningsAfterManualRecovery()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        TestableBmsFile firstFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\First\\chart.bms");
        TestableBmsFile manualFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "C:\\Pending\\Manual\\chart.bms");
        TestableBmsFile unattemptedFile = CreateFile("cccccccccccccccccccccccccccccccc", "C:\\Pending\\Unattempted\\chart.bms");
        TestableBmsFile duplicateMatchingFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\Duplicate\\matching.bms");
        TestableBmsFile duplicateUnmatchedFile = CreateFile("dddddddddddddddddddddddddddddddd", "C:\\Pending\\Duplicate\\unmatched.bms");
        ChartPackage firstPackage = ChartPackageTestExtensions.CreatePackage([firstFile]);
        ChartPackage manualPackage = ChartPackageTestExtensions.CreatePackage([manualFile]);
        ChartPackage unattemptedPackage = ChartPackageTestExtensions.CreatePackage([unattemptedFile]);
        ChartPackage duplicatePackage = ChartPackageTestExtensions.CreatePackage([duplicateMatchingFile, duplicateUnmatchedFile]);
        var workflow = new AutoInstallWorkflowResult();
        workflow.AutoInstallCandidates.AddRange([
            firstPackage,
            manualPackage,
            unattemptedPackage,
            duplicatePackage]);

        var manualReceipt = new FileDbMutationBatchReceipt([
            new FileDbMutationReceipt(
                Guid.NewGuid(),
                FileDbMutationTerminalState.Completed,
                durableCommit: true,
                compensationAttemptCount: 0,
                cleanupAttemptCount: 0,
                sourcePaths: [],
                destinationPaths: [],
                stagingPaths: [],
                backupPaths: [],
                recoveryPaths: []),
            new FileDbMutationReceipt(
                Guid.NewGuid(),
                FileDbMutationTerminalState.ManualRecoveryRequired,
                durableCommit: false,
                compensationAttemptCount: 1,
                cleanupAttemptCount: 0,
                sourcePaths: [],
                destinationPaths: [],
                stagingPaths: [],
                backupPaths: [],
                recoveryPaths: ["C:\\Recovery\\Manual"],
                failure: new InvalidOperationException("manual recovery required"))]);
        var service = new BmsLibraryPackageInstallService();

        AutoInstallApplyResult result = service.ApplyAutoInstallWorkflowWithFileMutationReceipts(
            workflow,
            keepInstallablePackagesPending: false,
            canAutoInstallImmediately: true,
            installPackages: _ => new AutoInstallCandidateApplyResult(
                [manualPackage, unattemptedPackage],
                manualReceipt));

        Assert.IsTrue(result.ManualRecoveryRequired);
        Assert.AreEqual(1, result.AutoInstalledPackages.Count);
        Assert.AreSame(firstPackage, result.AutoInstalledPackages.Single());
        CollectionAssert.AreEquivalent(
            new[] { manualPackage, unattemptedPackage },
            result.AutoInstallFailures);
        CollectionAssert.AreEquivalent(
            new[] { manualPackage, unattemptedPackage, duplicatePackage },
            result.PendingPackagesToAdd);
        Assert.IsFalse(result.PendingPackagesToAdd.Contains(firstPackage));

        PackageChartEntry matchingEntry = duplicatePackage.ChartEntries.Single(entry => ReferenceEquals(
            entry.GetBmsOwnerForTest(),
            duplicateMatchingFile));
        PackageChartEntry unmatchedEntry = duplicatePackage.ChartEntries.Single(entry => ReferenceEquals(
            entry.GetBmsOwnerForTest(),
            duplicateUnmatchedFile));
        Assert.IsTrue(matchingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
        Assert.IsFalse(unmatchedEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
    }

    [TestMethod]
    public void ApplyAutoInstallWorkflow_DoesNotMarkDuplicatePendingWhenPredecessorFails()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BMSFile firstFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\First\\chart.bms");
        BMSFile secondFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\Second\\chart.bms");
        ChartPackage firstPackage = ChartPackageTestExtensions.CreatePackage([firstFile]);
        ChartPackage secondPackage = ChartPackageTestExtensions.CreatePackage([secondFile]);
        var workflow = new AutoInstallWorkflowResult();
        workflow.AutoInstallCandidates.Add(firstPackage);
        workflow.AutoInstallCandidates.Add(secondPackage);
        var service = new BmsLibraryPackageInstallService();

        AutoInstallApplyResult result = service.ApplyAutoInstallWorkflow(
            workflow,
            keepInstallablePackagesPending: false,
            canAutoInstallImmediately: true,
            packages => [firstPackage]);

        CollectionAssert.AreEqual(new[] { firstPackage, secondPackage }, result.PendingPackagesToAdd);
        Assert.AreEqual(0, result.AutoInstalledPackages.Count);
        Assert.IsFalse(secondPackage.ChartEntries.Single().Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
    }

    [TestMethod]
    public void ApplyAutoInstallWorkflow_DoesNotWarnPartialDuplicateWhenDuplicatePredecessorFails()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BMSFile firstAFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\First\\a.bms");
        BMSFile mixedAFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\Mixed\\a.bms");
        BMSFile mixedBFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "C:\\Pending\\Mixed\\b.bms");
        BMSFile thirdBFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "C:\\Pending\\Third\\b.bms");
        ChartPackage firstPackage = ChartPackageTestExtensions.CreatePackage([firstAFile]);
        ChartPackage mixedPackage = ChartPackageTestExtensions.CreatePackage([mixedAFile, mixedBFile]);
        ChartPackage thirdPackage = ChartPackageTestExtensions.CreatePackage([thirdBFile]);
        var workflow = new AutoInstallWorkflowResult();
        workflow.AutoInstallCandidates.Add(firstPackage);
        workflow.AutoInstallCandidates.Add(mixedPackage);
        workflow.AutoInstallCandidates.Add(thirdPackage);
        var service = new BmsLibraryPackageInstallService();

        AutoInstallApplyResult result = service.ApplyAutoInstallWorkflow(
            workflow,
            keepInstallablePackagesPending: false,
            canAutoInstallImmediately: true,
            packages => [firstPackage]);

        CollectionAssert.AreEqual(new[] { firstPackage, mixedPackage }, result.PendingPackagesToAdd);
        CollectionAssert.AreEqual(new[] { thirdPackage }, result.AutoInstalledPackages);
        Assert.IsFalse(mixedPackage.ChartEntries.Any(entry => entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled)));
    }

    [TestMethod]
    public void ApplyAutoInstallWorkflow_WarnsOnlyDuplicateReasonEntriesForPartialDuplicate()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BMSFile firstAFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\First\\a.bms");
        BMSFile mixedAFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\Mixed\\a.bms");
        BMSFile mixedBFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "C:\\Pending\\Mixed\\b.bms");
        BMSFile thirdBFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "C:\\Pending\\Third\\b.bms");
        ChartPackage firstPackage = ChartPackageTestExtensions.CreatePackage([firstAFile]);
        ChartPackage mixedPackage = ChartPackageTestExtensions.CreatePackage([mixedAFile, mixedBFile]);
        ChartPackage thirdPackage = ChartPackageTestExtensions.CreatePackage([thirdBFile]);
        var workflow = new AutoInstallWorkflowResult();
        workflow.AutoInstallCandidates.Add(firstPackage);
        workflow.AutoInstallCandidates.Add(mixedPackage);
        workflow.AutoInstallCandidates.Add(thirdPackage);
        var service = new BmsLibraryPackageInstallService();

        AutoInstallApplyResult result = service.ApplyAutoInstallWorkflow(
            workflow,
            keepInstallablePackagesPending: false,
            canAutoInstallImmediately: true,
            packages => []);

        CollectionAssert.AreEqual(new[] { mixedPackage }, result.PendingPackagesToAdd);
        CollectionAssert.AreEqual(new[] { firstPackage, thirdPackage }, result.AutoInstalledPackages);
        PackageChartEntry mixedAEntry = mixedPackage.ChartEntries.Single(entry => entry.Chart.Path.EndsWith("\\a.bms", StringComparison.OrdinalIgnoreCase));
        PackageChartEntry mixedBEntry = mixedPackage.ChartEntries.Single(entry => entry.Chart.Path.EndsWith("\\b.bms", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(mixedAEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
        Assert.IsFalse(mixedBEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
    }

    [TestMethod]
    public void PrepareAutoInstallWorkflow_DoesNotPrebuildSourceSurfaceForDiscoveredPackages()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
            {
                string directoryPackagePath = Path.Combine(tempDirectoryPath, "DirPkg");
                Directory.CreateDirectory(directoryPackagePath);
                File.WriteAllText(Path.Combine(directoryPackagePath, "chart_dir.bms"), "#PLAYER 1\r\n#TITLE Dir\r\n#WAVAA sound_dir.wav\r\n#00111:AA\r\n");
                File.WriteAllText(Path.Combine(directoryPackagePath, "sound_dir.wav"), "audio");

                string filePackageDirectoryPath = Path.Combine(tempDirectoryPath, "SinglePkg");
                Directory.CreateDirectory(filePackageDirectoryPath);
                string singleFilePath = Path.Combine(filePackageDirectoryPath, "chart_single.bms");
                File.WriteAllText(singleFilePath, "#PLAYER 1\r\n#TITLE Single\r\n#WAVAA sound_single.wav\r\n#00111:AA\r\n");
                File.WriteAllText(Path.Combine(filePackageDirectoryPath, "sound_single.wav"), "audio");

                var service = new BmsLibraryPackageInstallService();
                AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow(
                    [directoryPackagePath, singleFilePath],
                    [],
                    [],
                    _ => false,
                    0.6);

                Assert.AreEqual(2, result.DiscoveredPackages.Count);
                foreach (ChartPackage package in result.DiscoveredPackages)
                {
                    List<BMSFile> discoveredCharts = [.. package.GetBmsOwnersForTest()];
                    PackageInstallEstimationSnapshot firstSnapshot = BuildPackageSnapshot(package, discoveredCharts);
                    PackageInstallEstimationSnapshot secondSnapshot = BuildPackageSnapshot(package, discoveredCharts);

                    Assert.IsTrue(discoveredCharts.Count > 0);
                    Assert.IsFalse(firstSnapshot.SourceSurfaceCacheHit);
                    Assert.IsTrue(secondSnapshot.SourceSurfaceCacheHit);
                    if (Directory.Exists(package.path))
                    {
                        Assert.AreEqual("bounded_fast_source_surface", firstSnapshot.SourceSurfaceScanBackend);
                        Assert.IsTrue(firstSnapshot.SourceSurfaceTrackedFileCount > 0);
                        Assert.IsTrue(firstSnapshot.SourceSurfaceResourceFileCount > 0);
                        Assert.IsTrue(firstSnapshot.BundledAudioCount > 0);
                    }
                    else
                    {
                        Assert.AreEqual(filePackageDirectoryPath, firstSnapshot.SourceDirectory);
                        Assert.AreEqual(string.Empty, firstSnapshot.SourceSurfaceScanBackend);
                        Assert.AreEqual(0, firstSnapshot.SourceSurfaceResourceFileCount);
                        Assert.AreEqual(0, firstSnapshot.SourceSurfaceTrackedFileCount);
                        Assert.AreEqual(0, firstSnapshot.SourceSurfaceVisitedFileSystemEntryCount);
                        Assert.AreEqual(0, firstSnapshot.SourceSurfaceMaxVisitedFileSystemEntryCount);
                        Assert.AreEqual(0, firstSnapshot.BundledAudioCount);
                        Assert.AreEqual(0, firstSnapshot.SourceCandidateResources.AudioFileNameHashCount);
                    }
                }
            });
    }

    [TestMethod]
    public void PackageChartEntry_FromBmsChartProjection_ProjectsBmsMode()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        TestableBmsFile source = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\chart.bms");
        source.SetMode(7);

        PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(source));

        Assert.AreEqual(7, entry.Chart.Mode);
    }

    [TestMethod]
    public void PrepareAutoInstallWorkflow_DoesNotMarkRegroupEligibleSourceDirectoryForPartialFileSelection()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourceDirectoryPath = Path.Combine(tempDirectoryPath, "Partial");
            Directory.CreateDirectory(sourceDirectoryPath);
            string firstFilePath = Path.Combine(sourceDirectoryPath, "chart_a.bms");
            string secondFilePath = Path.Combine(sourceDirectoryPath, "chart_b.bms");
            File.WriteAllText(firstFilePath, "#PLAYER 1\r\n#TITLE A\r\n");
            File.WriteAllText(secondFilePath, "#PLAYER 1\r\n#TITLE B\r\n");
            File.WriteAllText(Path.Combine(sourceDirectoryPath, "chart_c.bms"), "#PLAYER 1\r\n#TITLE C\r\n");

            var service = new BmsLibraryPackageInstallService();
            AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow(
                [firstFilePath, secondFilePath],
                [],
                [],
                _ => false,
                0.6);

            Assert.AreEqual(2, result.DiscoveredPackages.Count);
            Assert.AreEqual(0, result.RegroupEligibleSourceDirectories.Count);
            Assert.IsTrue(result.DiscoveredPackages.All(package => File.Exists(package.path)));
        });
    }

    [TestMethod]
    public void ApplyAutoInstallWorkflow_ReturnsPendingAddsRemovesAndEstimateTargets()
    {
        var service = new BmsLibraryPackageInstallService();
        var removePackage = new ChartPackage { path = "C:\\Pending\\Remove" };
        var pendingPackage = new ChartPackage { path = "C:\\Pending\\Keep" };
        var successAutoInstallPackage = new ChartPackage { path = "C:\\Pending\\AutoOk" };
        var failedAutoInstallPackage = new ChartPackage { path = "C:\\Pending\\AutoNg" };
        var workflow = new AutoInstallWorkflowResult();
        workflow.PendingPackagesToRemove.Add(removePackage);
        workflow.PendingPackagesToAdd.Add(pendingPackage);
        workflow.AutoInstallCandidates.Add(successAutoInstallPackage);
        workflow.AutoInstallCandidates.Add(failedAutoInstallPackage);

        AutoInstallApplyResult result = service.ApplyAutoInstallWorkflow(
            workflow,
            keepInstallablePackagesPending: false,
            canAutoInstallImmediately: true,
            packages => [failedAutoInstallPackage]);

        CollectionAssert.AreEqual(new[] { removePackage }, result.PendingPackagesToRemove);
        CollectionAssert.AreEqual(new[] { pendingPackage, failedAutoInstallPackage }, result.PendingPackagesToAdd);
        CollectionAssert.AreEqual(new[] { successAutoInstallPackage }, result.AutoInstalledPackages);
        CollectionAssert.AreEqual(new[] { failedAutoInstallPackage }, result.AutoInstallFailures);
        CollectionAssert.AreEqual(new[] { pendingPackage, failedAutoInstallPackage }, result.EstimateTargets);
        CollectionAssert.AreEquivalent(new[] { removePackage.path }, result.InstallRowsToDelete);
        CollectionAssert.AreEquivalent(new[] { pendingPackage.path, failedAutoInstallPackage.path }, result.InstallRowsToUpsert.Select(pkg => pkg.path).ToArray());
        Assert.IsTrue(result.InstallMs >= 0);
        Assert.IsTrue(result.ApplyMs >= 0);
        Assert.IsTrue(result.TotalMs >= 0);
    }

    [TestMethod]
    public void ExecuteInstalledOnlyResourceOverwrite_CategorizesCleanupInstallAndMissingCases()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryPackageInstallService();
        var cleanupPackage = ChartPackageTestExtensions.CreatePackage([CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\Cleanup\\a.bms")]);
        cleanupPackage.path = "C:\\Pending\\Cleanup";
        cleanupPackage.delete_parent = false;
        var installPackage = ChartPackageTestExtensions.CreatePackage([CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "C:\\Pending\\Install\\b.bms")]);
        installPackage.path = "C:\\Pending\\Install";
        installPackage.delete_parent = false;
        var missingDestinationPackage = ChartPackageTestExtensions.CreatePackage([CreateFile("cccccccccccccccccccccccccccccccc", "C:\\Pending\\Missing\\c.bms")]);
        missingDestinationPackage.path = "C:\\Pending\\Missing";
        missingDestinationPackage.delete_parent = false;

        PendingResourceOverwriteExecutionResult result = service.ExecuteInstalledOnlyResourceOverwrite(
            [cleanupPackage, installPackage, missingDestinationPackage, new ChartPackage { path = "C:\\Pending\\Unknown" }],
            [cleanupPackage, installPackage, missingDestinationPackage],
            true,
            (package) => package.path switch
            {
                "C:\\Pending\\Cleanup" => new InstalledOnlyPackageResolutionResult { DestinationDirectory = "C:\\Installed\\Cleanup", Reason = InstalledDirectoryResolveReason.None },
                "C:\\Pending\\Install" => new InstalledOnlyPackageResolutionResult { DestinationDirectory = "C:\\Installed\\Install", Reason = InstalledDirectoryResolveReason.None },
                _ => new InstalledOnlyPackageResolutionResult { Reason = InstalledDirectoryResolveReason.MissingInstallDestination }
            },
            (_, package) => "log:" + package.path,
            (package, _) => package.path == "C:\\Pending\\Install",
            (_, _) => true,
            (package) => package.path == "C:\\Pending\\Cleanup" ? (true, CleanupSourceKind.MissingSource) : (false, CleanupSourceKind.MissingSource),
            (package) => package == missingDestinationPackage,
            default,
            null,
            _ => { });

        Assert.AreEqual(4, result.Requested);
        Assert.AreEqual(4, result.Processed);
        Assert.AreEqual(1, result.SucceededCleanupOnly);
        Assert.AreEqual(1, result.SucceededInstall);
        Assert.AreEqual(1, result.SkippedMissingInstlDst);
        Assert.AreEqual(1, result.SkippedNotPending);
        Assert.AreEqual(0, result.Failed);
        CollectionAssert.AreEqual(new[] { cleanupPackage }, result.PendingPackagesToRemove);
        CollectionAssert.AreEqual(new[] { cleanupPackage.path }, result.InstallRowsToDelete);
    }

    [TestMethod]
    public void ExecuteInstalledOnlyResourceOverwrite_DoesNotMaterializeAdapterlessBmsonDestinationState()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryPackageInstallService();
        var bmsonSong = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Pending\\Install\\chart.bmson",
            folder = "C:\\Pending\\Install",
            title = "Adapterless",
            artist = "Artist",
            md5 = "dddddddddddddddddddddddddddddddd",
            sha256 = new string('d', 64)
        };
        PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmsonSong));
        entry.SetWarning(ChartWarningKind.InstallEstimationAmbiguous, "keep warning");
        ChartPackage installPackage = ChartPackage.FromChartEntries([entry]);
        installPackage.path = "C:\\Pending\\Install";

        bool checkedInstallDestination = false;
        PendingResourceOverwriteExecutionResult result = service.ExecuteInstalledOnlyResourceOverwrite(
            [installPackage],
            [installPackage],
            false,
            _ => new InstalledOnlyPackageResolutionResult { DestinationDirectory = "C:\\Installed\\Install", Reason = InstalledDirectoryResolveReason.None },
            (_, package) => "log:" + package.path,
            (_, _) => true,
            delegate
            {
                checkedInstallDestination = string.Equals(entry.Chart.InstallDestination, "C:\\Installed\\Install", StringComparison.OrdinalIgnoreCase);
                return true;
            },
            _ => (false, CleanupSourceKind.MissingSource),
            _ => false,
            default,
            null,
            _ => { });

        Assert.AreEqual(1, result.SucceededInstall);
        Assert.IsTrue(checkedInstallDestination);
        Assert.IsNull(entry.GetBmsOwnerForTest());
        Assert.AreEqual("C:\\Installed\\Install", entry.Chart.InstallDestination);
        Assert.IsTrue(entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
    }

    [TestMethod]
    public void ExecuteInstalledOnlyResourceOverwrite_RestoresAdapterlessBmsonDestinationStateWhenStillPending()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryPackageInstallService();
        var bmsonSong = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Pending\\Install\\chart.bmson",
            folder = "C:\\Pending\\Install",
            title = "Adapterless",
            artist = "Artist",
            md5 = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
            sha256 = new string('e', 64)
        };
        PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmsonSong));
        entry.ApplyInstallDestination("C:\\Original", "Original", "Artist");
        entry.SetWarning(ChartWarningKind.InstallEstimationAmbiguous, "keep warning");
        ChartPackage installPackage = ChartPackage.FromChartEntries([entry]);
        installPackage.path = "C:\\Pending\\Install";

        PendingResourceOverwriteExecutionResult result = service.ExecuteInstalledOnlyResourceOverwrite(
            [installPackage],
            [installPackage],
            false,
            _ => new InstalledOnlyPackageResolutionResult { DestinationDirectory = "C:\\Installed\\Install", Reason = InstalledDirectoryResolveReason.None },
            (_, package) => "log:" + package.path,
            (_, _) => true,
            (_, _) => false,
            _ => (false, CleanupSourceKind.MissingSource),
            _ => true,
            default,
            null,
            _ => { });

        Assert.AreEqual(1, result.Failed);
        Assert.IsNull(entry.GetBmsOwnerForTest());
        Assert.AreEqual("C:\\Original", entry.Chart.InstallDestination);
        Assert.AreEqual("Original", entry.Chart.InstallDestinationTitle);
        Assert.IsTrue(entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
    }

    /// <summary>
    /// durable DB receipt 前は source と package owner の path を保持し、失敗時に destination を compensation することを検証します。
    /// </summary>
    [TestMethod]
    public void MovePackageFilesWithReceipt_PrecommitFailureRetainsSourceAndRestoresPackageOwner()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourceDirectoryPath = Path.Combine(tempDirectoryPath, "PendingPkg");
            string destinationDirectoryPath = Path.Combine(tempDirectoryPath, "Installed", "Pkg");
            Directory.CreateDirectory(sourceDirectoryPath);
            string chartPath = Path.Combine(sourceDirectoryPath, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE Receipt\r\n");
            TestableBmsFile chart = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath);
            ChartPackage package = ChartPackageTestExtensions.CreatePackage([chart]);
            package.path = sourceDirectoryPath;
            package.delete_parent = false;
            var service = new BmsLibraryPackageInstallService();
            bool callbackSawSource = false;
            bool callbackSawDestination = false;

            FileDbMutationReceipt receipt = service.MovePackageFilesWithReceipt(
                package,
                destinationDirectoryPath,
                new BmsLibraryOptionsSnapshot
                {
                    EnableSmartComponentOverwrite = false,
                    KeepSmartOverwriteProtectedFilesByRenaming = false
                },
                (_, _) => throw new AssertFailedException("createFolderPath should not be called for an explicit destination."),
                exception => exception.Message,
                new RealFileMutationService(),
                null,
                new FileMutationOptions(ReadOnlyNormalizationScope.TargetOnly),
                new FileMutationOptions(ReadOnlyNormalizationScope.RecursiveDirectoryTree),
                _ => { },
                installResult =>
                {
                    callbackSawSource = Directory.Exists(sourceDirectoryPath) && File.Exists(chartPath);
                    callbackSawDestination = File.Exists(Path.Combine(destinationDirectoryPath, "chart.bms"));
                    Assert.AreEqual(Path.Combine(destinationDirectoryPath, "chart.bms"), installResult.AddedCharts.Single().Path);
                    return FileDbMutationCommitResult.Failed(new InvalidOperationException("db-before-receipt"));
                },
                sourceCleanupPolicy: PackageSourceCleanupPolicy.PreserveUnconsumedContents,
                showMessageBoxOnInstallFail: false);

            Assert.AreEqual(FileDbMutationTerminalState.Failed, receipt.TerminalState);
            Assert.IsFalse(receipt.DurableCommit);
            Assert.AreEqual(1, receipt.CompensationAttemptCount);
            Assert.IsTrue(callbackSawSource);
            Assert.IsTrue(callbackSawDestination);
            Assert.IsTrue(Directory.Exists(sourceDirectoryPath));
            Assert.IsTrue(File.Exists(chartPath));
            Assert.IsFalse(File.Exists(Path.Combine(destinationDirectoryPath, "chart.bms")));
            Assert.AreEqual(sourceDirectoryPath, package.path);
            Assert.AreEqual(chartPath, chart.path);
        });
    }

    [DataTestMethod]
    [DataRow("same")]
    [DataRow("inside")]
    [DataRow("ancestor")]
    public void MovePackageFilesWithReceipt_RejectsOverlappingDirectoryDestinationBeforeMutation(string destinationShape)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourceDirectoryPath = Path.Combine(tempDirectoryPath, "Pending", "Source");
            Directory.CreateDirectory(sourceDirectoryPath);
            string sourceChartPath = CreateBmsFile(sourceDirectoryPath, "chart.bms", "#TITLE Overlap");
            ChartPackage package = ChartPackageTestExtensions.CreatePackage([
                CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", sourceChartPath)]);
            package.path = sourceDirectoryPath;
            string destinationDirectoryPath = destinationShape switch
            {
                "same" => sourceDirectoryPath,
                "inside" => Path.Combine(sourceDirectoryPath, "Destination"),
                _ => Path.GetDirectoryName(sourceDirectoryPath)!
            };
            int durableCallbackCount = 0;

            FileDbMutationReceipt receipt = new BmsLibraryPackageInstallService().MovePackageFilesWithReceipt(
                package,
                destinationDirectoryPath,
                new BmsLibraryOptionsSnapshot
                {
                    EnableSmartComponentOverwrite = false,
                    KeepSmartOverwriteProtectedFilesByRenaming = false
                },
                (_, _) => throw new AssertFailedException("createFolderPath should not be called for an explicit destination."),
                exception => exception.Message,
                new RealFileMutationService(),
                null,
                new FileMutationOptions(ReadOnlyNormalizationScope.TargetOnly),
                new FileMutationOptions(ReadOnlyNormalizationScope.RecursiveDirectoryTree),
                _ => { },
                _ =>
                {
                    durableCallbackCount++;
                    return FileDbMutationCommitResult.Durable();
                },
                sourceCleanupPolicy: PackageSourceCleanupPolicy.PreserveUnconsumedContents,
                showMessageBoxOnInstallFail: false);

            Assert.AreEqual(FileDbMutationTerminalState.Failed, receipt.TerminalState);
            Assert.AreEqual(0, durableCallbackCount);
            Assert.IsTrue(Directory.Exists(sourceDirectoryPath));
            Assert.IsTrue(File.Exists(sourceChartPath));
        });
    }

    /// <summary>
    /// A collision-resolved destination is the single path shared by the
    /// filesystem receipt, the detached projection, and the live package.
    /// The destination is intentionally discovered from the completed receipt
    /// and filesystem rather than reimplementing collision resolution here.
    /// </summary>
    [TestMethod]
    public void MovePackageFilesWithReceipt_CollisionUsesReceiptDestinationForAllPackageState()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourcePath = Path.Combine(tempDirectoryPath, "PendingPkg", "chart.bms");
            string destinationDirectoryPath = Path.Combine(tempDirectoryPath, "Installed", "Pkg");
            string collisionPath = Path.Combine(destinationDirectoryPath, "chart.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            Directory.CreateDirectory(destinationDirectoryPath);
            File.WriteAllText(sourcePath, "#PLAYER 1\r\n#TITLE source\r\n");
            File.WriteAllText(collisionPath, "#PLAYER 1\r\n#TITLE existing\r\n");
            byte[] sourceBytes = File.ReadAllBytes(sourcePath);
            byte[] collisionBytes = File.ReadAllBytes(collisionPath);

            BMSFile chart = BMSFile.CreateBMSFileFromFile(sourcePath);
            ChartPackage package = ChartPackageTestExtensions.CreatePackage([chart]);
            package.path = sourcePath;
            package.delete_parent = false;
            string detachedProjectionPath = string.Empty;

            FileDbMutationReceipt receipt = new BmsLibraryPackageInstallService().MovePackageFilesWithReceipt(
                package,
                destinationDirectoryPath,
                new BmsLibraryOptionsSnapshot
                {
                    EnableSmartComponentOverwrite = false,
                    KeepSmartOverwriteProtectedFilesByRenaming = false
                },
                (_, _) => throw new AssertFailedException("createFolderPath should not be called for an explicit destination."),
                exception => exception.Message,
                new RealFileMutationService(),
                null,
                new FileMutationOptions(ReadOnlyNormalizationScope.TargetOnly),
                new FileMutationOptions(ReadOnlyNormalizationScope.RecursiveDirectoryTree),
                _ => { },
                installResult =>
                {
                    detachedProjectionPath = installResult.AddedCharts.Single().Path;
                    return FileDbMutationCommitResult.Durable();
                },
                sourceCleanupPolicy: PackageSourceCleanupPolicy.PreserveUnconsumedContents,
                showMessageBoxOnInstallFail: false);

            Assert.IsTrue(receipt.DurableCommit);
            string actualDestinationPath = receipt.DestinationPaths.Single(path =>
                File.Exists(path)
                && !string.Equals(path, collisionPath, StringComparison.Ordinal));

            Assert.AreEqual(actualDestinationPath, detachedProjectionPath);
            Assert.AreEqual(actualDestinationPath, package.ChartEntries.Single().Chart.Path);
            Assert.AreEqual(actualDestinationPath, package.path);
            CollectionAssert.AreEqual(collisionBytes, File.ReadAllBytes(collisionPath));
            Assert.IsFalse(File.Exists(sourcePath));
            CollectionAssert.AreEqual(sourceBytes, File.ReadAllBytes(actualDestinationPath));
        });
    }

    [DataTestMethod]
    [DataRow("single-bms")]
    [DataRow("single-bmson")]
    [DataRow("directory-mixed")]
    public void MovePackageFilesWithReceipt_FormatShapesShareExactDestinationMap(string shape)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            bool isDirectoryPackage = string.Equals(shape, "directory-mixed", StringComparison.Ordinal);
            string sourceRootPath = Path.Combine(tempDirectoryPath, "PendingPkg");
            string destinationDirectoryPath = Path.Combine(tempDirectoryPath, "Installed", "Pkg");
            Directory.CreateDirectory(sourceRootPath);
            Directory.CreateDirectory(destinationDirectoryPath);
            var sourcePaths = new List<string>();
            var collisionPaths = new List<string>();
            var sourceBytesByPath = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var collisionBytesByPath = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var entries = new List<PackageChartEntry>();

            void AddBmsChart(string relativePath, string sourceTitle, string existingTitle, string hash)
            {
                string sourcePath = Path.Combine(sourceRootPath, relativePath);
                string collisionPath = Path.Combine(destinationDirectoryPath, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
                Directory.CreateDirectory(Path.GetDirectoryName(collisionPath)!);
                File.WriteAllText(sourcePath, "#PLAYER 1\r\n#TITLE " + sourceTitle + "\r\n#00111:01\r\n");
                File.WriteAllText(collisionPath, "#PLAYER 1\r\n#TITLE " + existingTitle + "\r\n#00111:02\r\n");
                sourcePaths.Add(sourcePath);
                collisionPaths.Add(collisionPath);
                sourceBytesByPath[sourcePath] = File.ReadAllBytes(sourcePath);
                collisionBytesByPath[collisionPath] = File.ReadAllBytes(collisionPath);
                entries.Add(PackageChartEntry.FromChart(
                    ChartFileProjection.FromBmsFile(
                        BMSFile.CreateBMSFileFromFile(sourcePath))));
            }

            void AddBmsonChart(string relativePath, string soundName, string md5)
            {
                string sourcePath = Path.Combine(sourceRootPath, relativePath);
                string collisionPath = Path.Combine(destinationDirectoryPath, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
                Directory.CreateDirectory(Path.GetDirectoryName(collisionPath)!);
                File.WriteAllText(sourcePath, CreateBmsonJsonWithSound(soundName));
                File.WriteAllText(collisionPath, CreateBmsonJsonWithSound(soundName + "-existing"));
                sourcePaths.Add(sourcePath);
                collisionPaths.Add(collisionPath);
                sourceBytesByPath[sourcePath] = File.ReadAllBytes(sourcePath);
                collisionBytesByPath[collisionPath] = File.ReadAllBytes(collisionPath);
                entries.Add(PackageChartEntry.FromChart(
                    ChartFileProjection.FromBmsonSong(
                        new LR2SongDBExtended.bmson_song
                        {
                            path = sourcePath,
                            folder = Path.GetDirectoryName(sourcePath),
                            title = "Bmson " + soundName,
                            md5 = md5
                        },
                        includeWarningSnapshot: false,
                        includeResourceReferences: false)));
            }

            if (string.Equals(shape, "single-bms", StringComparison.Ordinal))
            {
                AddBmsChart("chart.bms", "single bms source", "single bms existing", "11111111111111111111111111111111");
            }
            else if (string.Equals(shape, "single-bmson", StringComparison.Ordinal))
            {
                AddBmsonChart("chart.bmson", "single-bmson", "22222222222222222222222222222222");
            }
            else
            {
                AddBmsChart("chart.bms", "mixed bms source", "mixed bms existing", "33333333333333333333333333333333");
                AddBmsonChart("nested\\chart.bmson", "mixed-bmson", "44444444444444444444444444444444");
            }

            ChartPackage package = ChartPackageTestExtensions.CreatePackage(entries);
            package.path = isDirectoryPackage
                ? sourceRootPath
                : sourcePaths.Single();
            package.delete_parent = false;
            List<string> detachedProjectionPaths = [];
            FileDbMutationReceipt receipt = new BmsLibraryPackageInstallService().MovePackageFilesWithReceipt(
                package,
                destinationDirectoryPath,
                new BmsLibraryOptionsSnapshot
                {
                    EnableSmartComponentOverwrite = false,
                    KeepSmartOverwriteProtectedFilesByRenaming = false
                },
                (_, _) => throw new AssertFailedException("createFolderPath should not be called for an explicit destination."),
                exception => exception.Message,
                new RealFileMutationService(),
                null,
                new FileMutationOptions(ReadOnlyNormalizationScope.TargetOnly),
                new FileMutationOptions(ReadOnlyNormalizationScope.RecursiveDirectoryTree),
                 _ => { },
                 installResult =>
                 {
                     detachedProjectionPaths.AddRange(installResult.AddedCharts.Select(chart => chart.Path));
                     return FileDbMutationCommitResult.Durable();
                 },
                 sourceCleanupPolicy: PackageSourceCleanupPolicy.PreserveUnconsumedContents,
                 showMessageBoxOnInstallFail: false);

            Assert.IsTrue(receipt.DurableCommit);
            List<string> actualDestinationPaths = [.. receipt.DestinationPaths
                .Where(path => File.Exists(path))
                .Where(path => !collisionPaths.Contains(path, StringComparer.Ordinal))];
            Assert.AreEqual(sourcePaths.Count, actualDestinationPaths.Count);
            List<string> livePaths = [.. package.ChartEntries.Select(entry => entry.Chart.Path)];
            foreach (string sourcePath in sourcePaths)
            {
                string actualDestinationPath = actualDestinationPaths.Single(path =>
                    sourceBytesByPath[sourcePath].SequenceEqual(File.ReadAllBytes(path)));
                Assert.IsTrue(detachedProjectionPaths.Contains(actualDestinationPath, StringComparer.Ordinal));
                Assert.IsTrue(livePaths.Contains(actualDestinationPath, StringComparer.Ordinal));
                string collisionPath = collisionPaths.Single(path =>
                    string.Equals(Path.GetRelativePath(destinationDirectoryPath, path),
                        Path.GetRelativePath(sourceRootPath, sourcePath),
                        StringComparison.Ordinal));
                CollectionAssert.AreEqual(collisionBytesByPath[collisionPath], File.ReadAllBytes(collisionPath));
            }
            Assert.AreEqual(
                isDirectoryPackage ? destinationDirectoryPath : actualDestinationPaths.Single(),
                package.path);
        });
    }

    /// <summary>
    /// durable DB receipt 後にだけ source を finalize cleanup し、canonical
    /// package state finalizer は cleanup 前に実行されることを検証します。
    /// </summary>
    [TestMethod]
    public void MovePackageFilesWithReceipt_DurableCommitFinalizesSourceAfterCanonicalFinalizer()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourceDirectoryPath = Path.Combine(tempDirectoryPath, "PendingPkg");
            string destinationDirectoryPath = Path.Combine(tempDirectoryPath, "Installed", "Pkg");
            Directory.CreateDirectory(sourceDirectoryPath);
            string chartPath = Path.Combine(sourceDirectoryPath, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE Receipt success\r\n");
            TestableBmsFile chart = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", chartPath);
            ChartPackage package = ChartPackageTestExtensions.CreatePackage([chart]);
            package.path = sourceDirectoryPath;
            package.delete_parent = false;
            var service = new BmsLibraryPackageInstallService();
            bool callbackSawSource = false;
            bool durableFinalizerSawSource = false;

            FileDbMutationReceipt receipt = service.MovePackageFilesWithReceipt(
                package,
                destinationDirectoryPath,
                new BmsLibraryOptionsSnapshot
                {
                    EnableSmartComponentOverwrite = false,
                    KeepSmartOverwriteProtectedFilesByRenaming = false
                },
                (_, _) => throw new AssertFailedException("createFolderPath should not be called for an explicit destination."),
                exception => exception.Message,
                new RealFileMutationService(),
                null,
                new FileMutationOptions(ReadOnlyNormalizationScope.TargetOnly),
                new FileMutationOptions(ReadOnlyNormalizationScope.RecursiveDirectoryTree),
                 _ => { },
                 _ =>
                 {
                     callbackSawSource = Directory.Exists(sourceDirectoryPath) && File.Exists(chartPath);
                     return FileDbMutationCommitResult.Durable(() => durableFinalizerSawSource = Directory.Exists(sourceDirectoryPath));
                 },
                 sourceCleanupPolicy: PackageSourceCleanupPolicy.PreserveUnconsumedContents,
                 showMessageBoxOnInstallFail: false);

            Assert.AreEqual(FileDbMutationTerminalState.Completed, receipt.TerminalState);
            Assert.IsTrue(receipt.DurableCommit);
            Assert.AreEqual(0, receipt.CompensationAttemptCount);
            Assert.IsTrue(callbackSawSource);
            Assert.IsTrue(durableFinalizerSawSource);
            Assert.IsFalse(Directory.Exists(sourceDirectoryPath));
            Assert.IsTrue(File.Exists(Path.Combine(destinationDirectoryPath, "chart.bms")));
            Assert.AreEqual(Path.Combine(destinationDirectoryPath, "chart.bms"), chart.path);
        });
    }

    /// <summary>
    /// Force-install publishes entry changes only after the command lease is
    /// released.  A failing subscriber is best-effort and cannot change the
    /// durable receipt or prevent later entry notifications.
    /// </summary>
    [TestMethod]
    public void ForceInstallPendingPackages_PublishesEntryNotificationsAfterLeaseAndBestEffort()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "PendingNotification");
            string installRootPath = Path.Combine(tempRootPath, "Installed");
            string firstChartPath = CreateBmsFile(
                sourceDirectoryPath,
                "first.bms",
                "#TITLE Notification Package");
            string secondChartPath = CreateBmsFile(
                sourceDirectoryPath,
                "second.bms",
                "#TITLE Notification Package");
            BMSFile firstChart = BMSFile.CreateBMSFileFromFile(firstChartPath);
            BMSFile secondChart = BMSFile.CreateBMSFileFromFile(secondChartPath);
            ChartPackage package = ChartPackageTestExtensions.CreatePackage([firstChart, secondChart]);
            package.path = sourceDirectoryPath;
            package.delete_parent = false;

            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                new RealFileMutationService(),
                new RecordingDialogService(),
                new TestUiScheduler(() => null),
                () => new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = false,
                    FolderNameFormat = "%TITLE%",
                    BMSInstallDir = installRootPath
                });
            library.BMSFiles = [firstChart, secondChart];
            library.ChartPackagesPending = CreatePackageCollection([package]);
            library.ChartPackagesInstalled = CreatePackageCollection([]);

            int firstNotificationCount = 0;
            int secondNotificationCount = 0;
            bool firstNotificationAcquiredLease = false;
            bool secondNotificationAcquiredLease = false;
            Exception callbackFailure = null;
            package.ChartEntries[0].PropertyChanged += (_, _) =>
            {
                firstNotificationCount++;
                try
                {
                    using LibraryFileMutationLease lease = library.TryBeginLibraryFileMutation(
                        "test_notification_after_force_install",
                        showMessage: false);
                    if (lease == null)
                    {
                        throw new InvalidOperationException("The notification still held the package mutation lease.");
                    }
                    firstNotificationAcquiredLease = true;
                }
                catch (Exception exception)
                {
                    callbackFailure ??= exception;
                }
                throw new InvalidOperationException("first subscriber failure");
            };
            package.ChartEntries[1].PropertyChanged += (_, _) =>
            {
                secondNotificationCount++;
                try
                {
                    using LibraryFileMutationLease lease = library.TryBeginLibraryFileMutation(
                        "test_notification_after_force_install_later_entry",
                        showMessage: false);
                    if (lease == null)
                    {
                        throw new InvalidOperationException("The later notification still held the package mutation lease.");
                    }
                    secondNotificationAcquiredLease = true;
                }
                catch (Exception exception)
                {
                    callbackFailure ??= exception;
                }
            };

            FileDbMutationBatchReceipt receipt = library.ForceInstallPendingPackagesWithReceipt(
                [package],
                approveNormalInstallOverride: true,
                approvedNormalInstallOverridePackages: null);

            Assert.IsNull(callbackFailure, callbackFailure?.ToString());
            Assert.IsTrue(firstNotificationCount > 0);
            Assert.IsTrue(secondNotificationCount > 0);
            Assert.IsTrue(firstNotificationAcquiredLease);
            Assert.IsTrue(secondNotificationAcquiredLease);
            Assert.IsTrue(receipt.HasDurableCommit);
            Assert.IsFalse(receipt.ManualRecoveryRequired);
            Assert.AreEqual(1, receipt.Receipts.Count);
            Assert.AreEqual(FileDbMutationTerminalState.Completed, receipt.Receipts[0].TerminalState);
            Assert.IsNull(receipt.Receipts[0].Failure);
            Assert.AreEqual(0, library.ChartPackagesPending.Count);
            Assert.AreEqual(1, library.ChartPackagesInstalled.Count);
            Assert.AreSame(package, library.ChartPackagesInstalled.Single());
            Assert.AreNotEqual(sourceDirectoryPath, package.path, ignoreCase: true);
            Assert.IsTrue(Directory.Exists(package.path));
            Assert.IsFalse(Directory.Exists(sourceDirectoryPath));
            foreach (PackageChartEntry entry in package.ChartEntries)
            {
                Assert.IsTrue(
                    entry.Chart.Path.StartsWith(
                        package.path + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase));
                Assert.IsTrue(File.Exists(entry.Chart.Path));
            }

            using var verifySongDb = new LR2SongDBExtended(songDbPath);
            List<LR2SongDB.song> installedRows = [.. verifySongDb.Table<LR2SongDB.song>()];
            Assert.IsTrue(package.ChartEntries.All(entry =>
                installedRows.Any(row => string.Equals(row.path, entry.Chart.Path, StringComparison.OrdinalIgnoreCase))));
        });
    }

    /// <summary>
    /// Auto-install keeps the durable success prefix registered when a later
    /// package reaches manual recovery, while unattempted packages remain
    /// pending and are not installed.
    /// </summary>
    [TestMethod]
    public void InstallChartPackagesAutoWithProgress_AppliesDurablePrefixBeforeManualRecoveryStopsBatch()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string installRootPath = Path.Combine(tempRootPath, "AutoInstalled");
            string firstSourceDirectoryPath = Path.Combine(tempRootPath, "AutoPendingFirst");
            string secondSourceDirectoryPath = Path.Combine(tempRootPath, "AutoPendingSecond");
            string thirdSourceDirectoryPath = Path.Combine(tempRootPath, "AutoPendingThird");
            string firstChartPath = CreateBmsFile(firstSourceDirectoryPath, "first.bms", "#TITLE Auto Prefix First");
            string secondChartPath = CreateBmsFile(secondSourceDirectoryPath, "second.bms", "#TITLE Auto Prefix Second");
            string thirdChartPath = CreateBmsFile(thirdSourceDirectoryPath, "third.bms", "#TITLE Auto Prefix Third");
            string firstDestinationChartPath = Path.Combine(installRootPath, "Auto Prefix First", "first.bms");
            string secondDestinationDirectoryPath = Path.Combine(installRootPath, "Auto Prefix Second");
            string secondDestinationChartPath = Path.Combine(secondDestinationDirectoryPath, "second.bms");
            string thirdDestinationDirectoryPath = Path.Combine(installRootPath, "Auto Prefix Third");

            using (var seedSongDb = new LR2SongDBExtended(songDbPath))
            {
                string escapedSecondDestinationChartPath = secondDestinationChartPath.Replace("'", "''");
                seedSongDb.Execute(
                    "CREATE TRIGGER fail_second_auto_install_target BEFORE INSERT ON song WHEN NEW.path = '"
                    + escapedSecondDestinationChartPath
                    + "' BEGIN SELECT RAISE(ABORT, 'forced second auto-install target failure'); END;");
            }

            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                new FailingDestinationDeleteFileMutationService(secondDestinationDirectoryPath),
                new RecordingDialogService(),
                new TestUiScheduler(() => null),
                () => new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = false,
                    FolderNameFormat = "%TITLE%",
                    BMSInstallDir = installRootPath,
                    KeepInstallablePackagesPending = false
                });
            library.BMSFiles = [];
            Directory.CreateDirectory(installRootPath);
            library.SearchTargets = [installRootPath];

            PackageInstallCommandResult commandResult = library.InstallChartPackagesAutoWithProgress(
                [firstSourceDirectoryPath, secondSourceDirectoryPath, thirdSourceDirectoryPath],
                CancellationToken.None,
                NullPackageInstallProgressWriter.Instance);

            Assert.IsTrue(commandResult.ManualRecoveryRequired);
            Assert.IsTrue(commandResult.HasDurableCommit);
            Assert.AreEqual(2, commandResult.MutationReceipt.Receipts.Count);
            Assert.AreEqual(
                FileDbMutationTerminalState.Completed,
                commandResult.MutationReceipt.Receipts[0].TerminalState);
            Assert.AreEqual(
                FileDbMutationTerminalState.ManualRecoveryRequired,
                commandResult.MutationReceipt.Receipts[1].TerminalState);
            Assert.IsTrue(commandResult.RegisteredPackages.Any(package =>
                string.Equals(package.path, Path.Combine(installRootPath, "Auto Prefix First"), StringComparison.OrdinalIgnoreCase)),
                "registered=" + string.Join("|", commandResult.RegisteredPackages.Select(package => package?.path ?? "<null>")));
            Assert.IsTrue(library.ChartPackagesInstalled.Any(package =>
                string.Equals(package.path, Path.Combine(installRootPath, "Auto Prefix First"), StringComparison.OrdinalIgnoreCase)));
            Assert.IsFalse(library.ChartPackagesPending.Any(package =>
                string.Equals(package.path, Path.Combine(installRootPath, "Auto Prefix First"), StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(library.ChartPackagesPending.Any(package =>
                string.Equals(package.path, secondSourceDirectoryPath, StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(library.ChartPackagesPending.Any(package =>
                string.Equals(package.path, thirdSourceDirectoryPath, StringComparison.OrdinalIgnoreCase)));
            Assert.IsFalse(library.ChartPackagesInstalled.Any(package =>
                string.Equals(package.path, secondSourceDirectoryPath, StringComparison.OrdinalIgnoreCase)));
            Assert.IsFalse(library.ChartPackagesInstalled.Any(package =>
                string.Equals(package.path, thirdSourceDirectoryPath, StringComparison.OrdinalIgnoreCase)));

            Assert.IsFalse(Directory.Exists(firstSourceDirectoryPath));
            Assert.IsTrue(File.Exists(firstDestinationChartPath));
            Assert.IsTrue(Directory.Exists(secondSourceDirectoryPath));
            Assert.IsTrue(Directory.Exists(secondDestinationDirectoryPath));
            Assert.IsTrue(Directory.Exists(thirdSourceDirectoryPath));
            Assert.IsFalse(Directory.Exists(thirdDestinationDirectoryPath));

            using var verifySongDb = new LR2SongDBExtended(songDbPath);
            List<LR2SongDB.song> installedRows = [.. verifySongDb.Table<LR2SongDB.song>()];
            Assert.IsTrue(installedRows.Any(row => string.Equals(
                row.path,
                firstDestinationChartPath,
                StringComparison.OrdinalIgnoreCase)));
            Assert.IsFalse(installedRows.Any(row => string.Equals(
                row.path,
                secondDestinationChartPath,
                StringComparison.OrdinalIgnoreCase)));
            List<LR2SongDBExtended.install> installRows = [.. verifySongDb.Table<LR2SongDBExtended.install>()];
            Assert.IsFalse(installRows.Any(row => string.Equals(
                row.path,
                firstSourceDirectoryPath,
                StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(installRows.Any(row => string.Equals(
                row.path,
                secondSourceDirectoryPath,
                StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(installRows.Any(row => string.Equals(
                row.path,
                thirdSourceDirectoryPath,
                StringComparison.OrdinalIgnoreCase)));
        });
    }

    /// <summary>
    /// A manual-recovery receipt stops the batch, while the successful prefix
    /// is still applied to the pending and installed collections.
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ForceInstallPendingPackages_AppliesDurablePrefixBeforeManualRecoveryStopsBatch(bool reportAtTerminal)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string installRootPath = Path.Combine(tempRootPath, "Installed");
            string firstSourceDirectoryPath = Path.Combine(tempRootPath, "PendingFirst");
            string secondSourceDirectoryPath = Path.Combine(tempRootPath, "PendingSecond");
            string thirdSourceDirectoryPath = Path.Combine(tempRootPath, "PendingThird");
            string firstChartPath = CreateBmsFile(firstSourceDirectoryPath, "first.bms", "#TITLE Prefix First");
            string secondChartPath = CreateBmsFile(secondSourceDirectoryPath, "second.bms", "#TITLE Prefix Second");
            string thirdChartPath = CreateBmsFile(thirdSourceDirectoryPath, "third.bms", "#TITLE Prefix Third");
            BMSFile firstChart = BMSFile.CreateBMSFileFromFile(firstChartPath);
            BMSFile secondChart = BMSFile.CreateBMSFileFromFile(secondChartPath);
            BMSFile thirdChart = BMSFile.CreateBMSFileFromFile(thirdChartPath);
            ChartPackage firstPackage = ChartPackageTestExtensions.CreatePackage([firstChart]);
            ChartPackage secondPackage = ChartPackageTestExtensions.CreatePackage([secondChart]);
            ChartPackage thirdPackage = ChartPackageTestExtensions.CreatePackage([thirdChart]);
            firstPackage.path = firstSourceDirectoryPath;
            secondPackage.path = secondSourceDirectoryPath;
            thirdPackage.path = thirdSourceDirectoryPath;
            firstPackage.delete_parent = false;
            secondPackage.delete_parent = false;
            thirdPackage.delete_parent = false;

            string secondDestinationDirectoryPath = Path.Combine(installRootPath, "Prefix Second");
            string secondDestinationChartPath = Path.Combine(secondDestinationDirectoryPath, "second.bms");
            using (var seedSongDb = new LR2SongDBExtended(songDbPath))
            {
                string escapedSecondDestinationChartPath = secondDestinationChartPath.Replace("'", "''");
                seedSongDb.Execute(
                    "CREATE TRIGGER fail_second_force_install_target BEFORE INSERT ON song WHEN NEW.path = '"
                    + escapedSecondDestinationChartPath
                    + "' BEGIN SELECT RAISE(ABORT, 'forced second force-install target failure'); END;");
            }

            var dialogs = new FileDbReportRecordingDialogs();
            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                new FailingDestinationDeleteFileMutationService(secondDestinationDirectoryPath),
                dialogs,
                new TestUiScheduler(() => null),
                () => new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = false,
                    FolderNameFormat = "%TITLE%",
                    BMSInstallDir = installRootPath
                });
            library.BMSFiles = [firstChart, secondChart, thirdChart];
            library.ChartPackagesPending = CreatePackageCollection([firstPackage, secondPackage, thirdPackage]);
            library.ChartPackagesInstalled = CreatePackageCollection([]);

            FileDbMutationBatchReceipt receipt = library.ForceInstallPendingPackagesWithReceipt(
                [firstPackage, secondPackage, thirdPackage],
                approveNormalInstallOverride: true,
                approvedNormalInstallOverridePackages: null, reportAtTerminal: reportAtTerminal);
            Assert.AreEqual(reportAtTerminal ? 0 : 1, dialogs.ModelMessages);

            Assert.IsTrue(receipt.HasDurableCommit);
            Assert.IsTrue(receipt.ManualRecoveryRequired);
            Assert.AreEqual(2, receipt.Receipts.Count);
            Assert.AreEqual(FileDbMutationTerminalState.Completed, receipt.Receipts[0].TerminalState);
            Assert.IsTrue(receipt.Receipts[0].DurableCommit);
            Assert.AreEqual(FileDbMutationTerminalState.ManualRecoveryRequired, receipt.Receipts[1].TerminalState);
            Assert.IsFalse(receipt.Receipts[1].DurableCommit);

            Assert.AreEqual(1, library.ChartPackagesInstalled.Count);
            Assert.AreSame(firstPackage, library.ChartPackagesInstalled.Single());
            Assert.AreEqual(2, library.ChartPackagesPending.Count);
            CollectionAssert.Contains(library.ChartPackagesPending.ToList(), secondPackage);
            CollectionAssert.Contains(library.ChartPackagesPending.ToList(), thirdPackage);
            CollectionAssert.DoesNotContain(library.ChartPackagesPending.ToList(), firstPackage);

            Assert.IsFalse(Directory.Exists(firstSourceDirectoryPath));
            Assert.IsTrue(File.Exists(Path.Combine(installRootPath, "Prefix First", "first.bms")));
            Assert.AreEqual(
                Path.Combine(installRootPath, "Prefix First", "first.bms"),
                firstPackage.ChartEntries.Single().Chart.Path);
            Assert.AreEqual(secondSourceDirectoryPath, secondPackage.path);
            Assert.IsTrue(Directory.Exists(secondSourceDirectoryPath));
            Assert.IsTrue(Directory.Exists(secondDestinationDirectoryPath));
            Assert.AreEqual(thirdSourceDirectoryPath, thirdPackage.path);
            Assert.IsTrue(Directory.Exists(thirdSourceDirectoryPath));
            Assert.IsFalse(Directory.Exists(Path.Combine(installRootPath, "Prefix Third")));

            using var verifySongDb = new LR2SongDBExtended(songDbPath);
            List<LR2SongDB.song> installedRows = [.. verifySongDb.Table<LR2SongDB.song>()];
            Assert.IsTrue(installedRows.Any(row => string.Equals(
                row.path,
                Path.Combine(installRootPath, "Prefix First", "first.bms"),
                StringComparison.OrdinalIgnoreCase)));
            Assert.IsFalse(installedRows.Any(row => string.Equals(
                row.path,
                secondDestinationChartPath,
                StringComparison.OrdinalIgnoreCase)));
        });
    }

    /// <summary>
    /// 異なる path の同一 component を smart skip した場合も、source delete は durable receipt 後の finalize で行うことを検証します。
    /// </summary>
    [TestMethod]
    public void MovePackageFilesWithReceipt_SmartSameComponentDeletesSourceDuringFinalize()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourceDirectoryPath = Path.Combine(tempDirectoryPath, "PendingSame");
            string destinationDirectoryPath = Path.Combine(tempDirectoryPath, "InstalledSame");
            Directory.CreateDirectory(sourceDirectoryPath);
            Directory.CreateDirectory(destinationDirectoryPath);
            string sourcePath = Path.Combine(sourceDirectoryPath, "notes.txt");
            string destinationPath = Path.Combine(destinationDirectoryPath, "notes.txt");
            File.WriteAllText(sourcePath, "same-content");
            File.WriteAllText(destinationPath, "same-content");
            File.SetLastWriteTimeUtc(destinationPath, File.GetLastWriteTimeUtc(sourcePath));
            ChartPackage package = ChartPackageTestExtensions.CreatePackage(Enumerable.Empty<BMSFile>());
            package.path = sourceDirectoryPath;
            package.delete_parent = false;
            var service = new BmsLibraryPackageInstallService();
            bool callbackSawSource = false;

            FileDbMutationReceipt receipt = service.MovePackageFilesWithReceipt(
                package,
                destinationDirectoryPath,
                new BmsLibraryOptionsSnapshot
                {
                    EnableSmartComponentOverwrite = true,
                    KeepSmartOverwriteProtectedFilesByRenaming = false
                },
                (_, _) => throw new AssertFailedException("createFolderPath should not be called for an explicit destination."),
                exception => exception.Message,
                new RealFileMutationService(),
                null,
                new FileMutationOptions(ReadOnlyNormalizationScope.TargetOnly),
                new FileMutationOptions(ReadOnlyNormalizationScope.RecursiveDirectoryTree),
                _ => { },
                 _ =>
                 {
                     callbackSawSource = File.Exists(sourcePath);
                     return FileDbMutationCommitResult.Durable();
                 },
                 sourceCleanupPolicy: PackageSourceCleanupPolicy.PreserveUnconsumedContents,
                 showMessageBoxOnInstallFail: false);

            Assert.AreEqual(FileDbMutationTerminalState.Completed, receipt.TerminalState);
            Assert.IsTrue(receipt.DurableCommit);
            Assert.IsTrue(callbackSawSource);
            Assert.IsFalse(File.Exists(sourcePath));
            Assert.IsFalse(Directory.Exists(sourceDirectoryPath));
            Assert.IsTrue(File.Exists(destinationPath));
        });
    }

    /// <summary>
    /// single-file package は source を directory として列挙せず、file cleanup だけを finalize することを検証します。
    /// </summary>
    [TestMethod]
    public void MovePackageFilesWithReceipt_SingleFilePackageFinalizesFileSource()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourcePath = Path.Combine(tempDirectoryPath, "single.bms");
            string destinationDirectoryPath = Path.Combine(tempDirectoryPath, "InstalledSingle");
            File.WriteAllText(sourcePath, "#PLAYER 1\r\n#TITLE Single");
            TestableBmsFile chart = CreateFile("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", sourcePath);
            ChartPackage package = ChartPackageTestExtensions.CreatePackage([chart]);
            package.path = sourcePath;
            package.delete_parent = false;
            var service = new BmsLibraryPackageInstallService();

            FileDbMutationReceipt receipt = service.MovePackageFilesWithReceipt(
                package,
                destinationDirectoryPath,
                new BmsLibraryOptionsSnapshot
                {
                    EnableSmartComponentOverwrite = false,
                    KeepSmartOverwriteProtectedFilesByRenaming = false
                },
                (_, _) => throw new AssertFailedException("createFolderPath should not be called for an explicit destination."),
                exception => exception.Message,
                new RealFileMutationService(),
                null,
                new FileMutationOptions(ReadOnlyNormalizationScope.TargetOnly),
                new FileMutationOptions(ReadOnlyNormalizationScope.RecursiveDirectoryTree),
                _ => { },
                 _ => FileDbMutationCommitResult.Durable(),
                 sourceCleanupPolicy: PackageSourceCleanupPolicy.PreserveUnconsumedContents,
                 showMessageBoxOnInstallFail: false);

            Assert.AreEqual(FileDbMutationTerminalState.Completed, receipt.TerminalState);
            Assert.IsTrue(receipt.DurableCommit);
            Assert.IsFalse(File.Exists(sourcePath));
            Assert.IsTrue(File.Exists(Path.Combine(destinationDirectoryPath, "single.bms")));
            Assert.AreEqual(Path.Combine(destinationDirectoryPath, "single.bms"), chart.path);
        });
    }

    /// <summary>
    /// compensation failure では package batch の後続 mutation を開始しないことを検証します。
    /// </summary>
    [TestMethod]
    public void InstallPackagesWithFileMutationReceipts_StopsBatchAfterManualRecoveryRequired()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string firstSourceDirectoryPath = Path.Combine(tempDirectoryPath, "PendingFirst");
            string secondSourceDirectoryPath = Path.Combine(tempDirectoryPath, "PendingSecond");
            string thirdSourceDirectoryPath = Path.Combine(tempDirectoryPath, "PendingThird");
            Directory.CreateDirectory(firstSourceDirectoryPath);
            Directory.CreateDirectory(secondSourceDirectoryPath);
            Directory.CreateDirectory(thirdSourceDirectoryPath);
            string firstChartPath = Path.Combine(firstSourceDirectoryPath, "first.bms");
            string secondChartPath = Path.Combine(secondSourceDirectoryPath, "second.bms");
            string thirdChartPath = Path.Combine(thirdSourceDirectoryPath, "third.bms");
            File.WriteAllText(firstChartPath, "first");
            File.WriteAllText(secondChartPath, "second");
            File.WriteAllText(thirdChartPath, "third");
            ChartPackage firstPackage = ChartPackageTestExtensions.CreatePackage([
                CreateFile("cccccccccccccccccccccccccccccccc", firstChartPath)]);
            firstPackage.path = firstSourceDirectoryPath;
            ChartPackage secondPackage = ChartPackageTestExtensions.CreatePackage([
                CreateFile("dddddddddddddddddddddddddddddddd", secondChartPath)]);
            secondPackage.path = secondSourceDirectoryPath;
            ChartPackage thirdPackage = ChartPackageTestExtensions.CreatePackage([
                CreateFile("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", thirdChartPath)]);
            thirdPackage.path = thirdSourceDirectoryPath;
            string firstDestinationDirectoryPath = Path.Combine(tempDirectoryPath, "InstalledFirst");
            string secondDestinationDirectoryPath = Path.Combine(tempDirectoryPath, "InstalledSecond");
            string thirdDestinationDirectoryPath = Path.Combine(tempDirectoryPath, "InstalledThird");
            int moveInvocationCount = 0;
            var service = new BmsLibraryPackageInstallService();

            PackageInstallExecutionResult result = service.InstallPackagesWithFileMutationReceipts(
                [firstPackage, secondPackage, thirdPackage],
                string.Empty,
                (package, _, _, _, _, _, applyDurableCommit) =>
                {
                    moveInvocationCount++;
                    string destinationDirectoryPath = ReferenceEquals(package, firstPackage)
                        ? firstDestinationDirectoryPath
                        : ReferenceEquals(package, secondPackage)
                            ? secondDestinationDirectoryPath
                            : thirdDestinationDirectoryPath;
                    IFileMutationService fileMutationService = ReferenceEquals(package, secondPackage)
                        ? new FailingDestinationDeleteFileMutationService(Path.Combine(secondDestinationDirectoryPath, "second.bms"))
                        : new RealFileMutationService();
                    return service.MovePackageFilesWithReceipt(
                        package,
                        destinationDirectoryPath,
                        new BmsLibraryOptionsSnapshot
                        {
                            EnableSmartComponentOverwrite = false,
                            KeepSmartOverwriteProtectedFilesByRenaming = false
                        },
                        (_, _) => destinationDirectoryPath,
                        exception => exception.Message,
                        fileMutationService,
                        null,
                        new FileMutationOptions(ReadOnlyNormalizationScope.TargetOnly),
                        new FileMutationOptions(ReadOnlyNormalizationScope.RecursiveDirectoryTree),
                        _ => { },
                         _ => ReferenceEquals(package, secondPackage)
                             ? FileDbMutationCommitResult.Failed(new InvalidOperationException("db-before-receipt"))
                             : FileDbMutationCommitResult.Durable(),
                         sourceCleanupPolicy: PackageSourceCleanupPolicy.PreserveUnconsumedContents,
                         showMessageBoxOnInstallFail: false);
                },
                _ => FileDbMutationCommitResult.Durable(),
                _ => { },
                _ => { },
                _ => { },
                sourceCleanupPolicy: PackageSourceCleanupPolicy.PreserveUnconsumedContents);

            Assert.AreEqual(2, moveInvocationCount);
            Assert.AreEqual(1, result.FailedPackages.Count);
            Assert.AreSame(secondPackage, result.FailedPackages[0]);
            Assert.AreEqual(1, result.AddedEntries.Count);
            Assert.AreEqual(
                Path.Combine(firstDestinationDirectoryPath, "first.bms"),
                result.AddedEntries.Single().Chart.Path);
            Assert.IsTrue(Directory.Exists(firstDestinationDirectoryPath));
            Assert.IsFalse(Directory.Exists(firstSourceDirectoryPath));
            Assert.IsTrue(Directory.Exists(secondSourceDirectoryPath));
            Assert.IsTrue(Directory.Exists(secondDestinationDirectoryPath));
            Assert.IsTrue(Directory.Exists(thirdSourceDirectoryPath));
            Assert.IsFalse(Directory.Exists(thirdDestinationDirectoryPath));
            Assert.IsTrue(result.MutationReceipt.HasDurableCommit);
            Assert.IsTrue(result.MutationReceipt.ManualRecoveryRequired);
            Assert.AreEqual(2, result.MutationReceipt.Receipts.Count);
            Assert.AreEqual(FileDbMutationTerminalState.Completed, result.MutationReceipt.Receipts[0].TerminalState);
            Assert.AreEqual(FileDbMutationTerminalState.ManualRecoveryRequired, result.MutationReceipt.Receipts[1].TerminalState);
        });
    }

    [TestMethod]
    public void InstallPackagesWithFileMutationReceipts_StopsBatchAfterDurableFinalizationFailure()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string firstSourceDirectoryPath = Path.Combine(tempDirectoryPath, "PendingFirst");
            string secondSourceDirectoryPath = Path.Combine(tempDirectoryPath, "PendingSecond");
            string thirdSourceDirectoryPath = Path.Combine(tempDirectoryPath, "PendingThird");
            Directory.CreateDirectory(firstSourceDirectoryPath);
            Directory.CreateDirectory(secondSourceDirectoryPath);
            Directory.CreateDirectory(thirdSourceDirectoryPath);
            string firstChartPath = Path.Combine(firstSourceDirectoryPath, "first.bms");
            string secondChartPath = Path.Combine(secondSourceDirectoryPath, "second.bms");
            string thirdChartPath = Path.Combine(thirdSourceDirectoryPath, "third.bms");
            File.WriteAllText(firstChartPath, "first");
            File.WriteAllText(secondChartPath, "second");
            File.WriteAllText(thirdChartPath, "third");
            ChartPackage firstPackage = ChartPackageTestExtensions.CreatePackage([
                CreateFile("cccccccccccccccccccccccccccccccc", firstChartPath)]);
            firstPackage.path = firstSourceDirectoryPath;
            ChartPackage secondPackage = ChartPackageTestExtensions.CreatePackage([
                CreateFile("dddddddddddddddddddddddddddddddd", secondChartPath)]);
            secondPackage.path = secondSourceDirectoryPath;
            ChartPackage thirdPackage = ChartPackageTestExtensions.CreatePackage([
                CreateFile("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", thirdChartPath)]);
            thirdPackage.path = thirdSourceDirectoryPath;
            string firstDestinationDirectoryPath = Path.Combine(tempDirectoryPath, "InstalledFirst");
            string secondDestinationDirectoryPath = Path.Combine(tempDirectoryPath, "InstalledSecond");
            string thirdDestinationDirectoryPath = Path.Combine(tempDirectoryPath, "InstalledThird");
            int moveInvocationCount = 0;
            int maintenanceInvocationCount = 0;
            int scoreInvocationCount = 0;
            int stateInvocationCount = 0;
            var finalizationFailure = new InvalidOperationException("deferred-effect");
            var service = new BmsLibraryPackageInstallService();

            PackageInstallExecutionResult result = service.InstallPackagesWithFileMutationReceipts(
                [firstPackage, secondPackage, thirdPackage],
                string.Empty,
                (package, _, _, _, _, _, applyDurableCommit) =>
                {
                    moveInvocationCount++;
                    string destinationDirectoryPath = ReferenceEquals(package, firstPackage)
                        ? firstDestinationDirectoryPath
                        : ReferenceEquals(package, secondPackage)
                            ? secondDestinationDirectoryPath
                            : thirdDestinationDirectoryPath;
                    return service.MovePackageFilesWithReceipt(
                        package,
                        destinationDirectoryPath,
                        new BmsLibraryOptionsSnapshot
                        {
                            EnableSmartComponentOverwrite = false,
                            KeepSmartOverwriteProtectedFilesByRenaming = false
                        },
                        (_, _) => destinationDirectoryPath,
                        exception => exception.Message,
                        new RealFileMutationService(),
                        null,
                        new FileMutationOptions(ReadOnlyNormalizationScope.TargetOnly),
                        new FileMutationOptions(ReadOnlyNormalizationScope.RecursiveDirectoryTree),
                        _ => { },
                         _ => ReferenceEquals(package, secondPackage)
                             ? FileDbMutationCommitResult.Durable(
                                 () => throw finalizationFailure)
                             : FileDbMutationCommitResult.Durable(),
                         sourceCleanupPolicy: PackageSourceCleanupPolicy.PreserveUnconsumedContents,
                         showMessageBoxOnInstallFail: false);
                },
                _ => FileDbMutationCommitResult.Durable(),
                _ => maintenanceInvocationCount++,
                _ => scoreInvocationCount++,
                _ => stateInvocationCount++,
                sourceCleanupPolicy: PackageSourceCleanupPolicy.PreserveUnconsumedContents);

            Assert.AreEqual(2, moveInvocationCount);
            Assert.AreEqual(1, result.AddedEntries.Count);
            Assert.AreEqual(1, result.InstalledPackagesToRegister.Count);
            Assert.AreSame(firstPackage, result.InstalledPackagesToRegister[0]);
            Assert.AreEqual(1, result.FailedPackages.Count);
            Assert.AreSame(secondPackage, result.FailedPackages[0]);
            Assert.AreEqual(1, maintenanceInvocationCount);
            Assert.AreEqual(1, scoreInvocationCount);
            Assert.AreEqual(1, stateInvocationCount);
            Assert.IsTrue(result.MutationReceipt.HasDurableFinalizationFailure);
            Assert.AreEqual(FileDbMutationTerminalState.DurableFinalizationFailed, result.MutationReceipt.Receipts[1].TerminalState);
            Assert.AreSame(finalizationFailure, result.MutationReceipt.Receipts[1].FinalizationFailure);
            Assert.IsTrue(Directory.Exists(firstDestinationDirectoryPath));
            Assert.IsFalse(Directory.Exists(firstSourceDirectoryPath));
            Assert.IsTrue(Directory.Exists(secondDestinationDirectoryPath));
            Assert.IsFalse(Directory.Exists(secondSourceDirectoryPath));
            Assert.IsTrue(Directory.Exists(thirdSourceDirectoryPath));
            Assert.IsFalse(Directory.Exists(thirdDestinationDirectoryPath));
        });
    }

    /// <summary>
    /// durable receipt を取得した後の maintenance failure は、先行した
    /// filesystem/DB mutation を失わず batch finalization failure として保持します。
    /// </summary>
    [TestMethod]
    public void InstallPackagesWithFileMutationReceipts_RetainsReceiptWhenMaintenanceFailsAfterDurableCommit()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath, string tempDirectoryPath)
        {
            string sourceDirectoryPath = Path.Combine(tempDirectoryPath, "PendingMaintenanceFailure");
            string sourceChartPath = CreateBmsFile(sourceDirectoryPath, "chart.bms", "#TITLE Maintenance Failure");
            string destinationDirectoryPath = Path.Combine(tempDirectoryPath, "InstalledMaintenanceFailure");
            BMSFile sourceChart = BMSFile.CreateBMSFileFromFile(sourceChartPath);
            ChartPackage package = ChartPackageTestExtensions.CreatePackage([sourceChart]);
            package.path = sourceDirectoryPath;
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.install>();
            }

            var service = new BmsLibraryPackageInstallService();
            var songDbGateway = new BmsLibraryDbGateway(songDbPath);
            var maintenanceFailure = new InvalidOperationException("maintenance-finalization-failure");
            int maintenanceInvocationCount = 0;
            int scoreInvocationCount = 0;
            int stateInvocationCount = 0;
            string persistedInstallPath = null;

            PackageInstallExecutionResult result = service.InstallPackagesWithFileMutationReceipts(
                [package],
                destinationDirectoryPath,
                (movePackage, destination, sourceCleanupPolicy, existingHashes, _, excludedComponentPaths, applyDurableCommit) =>
                    service.MovePackageFilesWithReceipt(
                        movePackage,
                        destination,
                        new BmsLibraryOptionsSnapshot
                        {
                            EnableSmartComponentOverwrite = false,
                            KeepSmartOverwriteProtectedFilesByRenaming = false
                        },
                        (_, _) => throw new AssertFailedException("createFolderPath should not be called when destination is specified."),
                        exception => exception.Message,
                        new RealFileMutationService(),
                        null,
                        new FileMutationOptions(ReadOnlyNormalizationScope.TargetOnly),
                        new FileMutationOptions(ReadOnlyNormalizationScope.RecursiveDirectoryTree),
                        _ => { },
                        applyDurableCommit,
                        showMessageBoxOnInstallFail: false,
                        sourceCleanupPolicy: sourceCleanupPolicy,
                        existingHashes: existingHashes,
                        excludedComponentPaths: excludedComponentPaths),
                packageResult =>
                {
                    persistedInstallPath = package.path;
                    songDbGateway.ApplyInstallTableMutation([], [package]);
                    return FileDbMutationCommitResult.Durable();
                },
                _ =>
                {
                    maintenanceInvocationCount++;
                    throw maintenanceFailure;
                },
                _ => scoreInvocationCount++,
                _ => stateInvocationCount++,
                sourceCleanupPolicy: PackageSourceCleanupPolicy.PreserveUnconsumedContents);

            Assert.IsNotNull(result.MutationReceipt);
            Assert.IsTrue(result.MutationReceipt.HasDurableCommit);
            Assert.IsTrue(result.MutationReceipt.HasDurableFinalizationFailure);
            Assert.AreSame(maintenanceFailure, result.MutationReceipt.FinalizationFailure);
            Assert.AreEqual(1, result.MutationReceipt.Receipts.Count);
            Assert.AreEqual(FileDbMutationTerminalState.Completed, result.MutationReceipt.Receipts[0].TerminalState);
            Assert.AreEqual(1, result.FailedPackages.Count);
            Assert.AreSame(package, result.FailedPackages[0]);
            Assert.AreEqual(1, maintenanceInvocationCount);
            Assert.AreEqual(0, scoreInvocationCount);
            Assert.AreEqual(0, stateInvocationCount);
            Assert.AreEqual(0, result.InstalledPackagesToRegister.Count);
            Assert.IsTrue(File.Exists(Path.Combine(destinationDirectoryPath, "chart.bms")));
            Assert.IsFalse(Directory.Exists(sourceDirectoryPath));
            using var verifySongDb = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1, verifySongDb.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM install WHERE path = ?;",
                persistedInstallPath));
        });
    }

    [TestMethod]
    public void InstallPackagesWithFileMutationReceipts_ReportsAdapterlessBmsonRowsWithoutMaterializingCompatibilityAdapter()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourceDirectoryPath = Path.Combine(tempDirectoryPath, "PendingPkg");
            string destinationDirectoryPath = Path.Combine(tempDirectoryPath, "InstalledPkg");
            Directory.CreateDirectory(sourceDirectoryPath);
            string sourceBmsonPath = Path.Combine(sourceDirectoryPath, "chart.bmson");
            string destinationBmsonPath = Path.Combine(destinationDirectoryPath, "chart.bmson");
            File.WriteAllText(sourceBmsonPath, CreateBmsonJsonWithSound("new.wav"));

            var bmsonSong = new LR2SongDBExtended.bmson_song
            {
                path = sourceBmsonPath,
                folder = sourceDirectoryPath,
                title = "Adapterless",
                md5 = "dddddddddddddddddddddddddddddddd",
                sha256 = new string('d', 64)
            };
            PackageChartEntry bmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmsonSong));
            bmsonEntry.ApplyInstallDestination(destinationDirectoryPath, "Pending Bmson", "Pending Artist");
            ChartPackage package = ChartPackage.FromChartEntries([bmsonEntry]);
            package.path = sourceDirectoryPath;
            List<LR2SongDBExtended.bmson_song> storageRows = [];
            List<LR2SongDBExtended.bmson_song> maintenanceTargets = [];
            List<LR2SongDBExtended.bmson_song> applyTargets = [];

            var service = new BmsLibraryPackageInstallService();
            PackageInstallExecutionResult result = ExecuteReceiptInstall(
                service,
                package,
                destinationDirectoryPath,
                result => storageRows.AddRange(GetAddedBmsonSongs(result)),
                result => maintenanceTargets.AddRange(GetAddedBmsonSongs(result)),
                _ => { },
                result => applyTargets.AddRange(GetAddedBmsonSongs(result)));

            Assert.AreEqual(1, result.AddedEntries.Count);
            Assert.AreEqual(1, result.AddedCharts.Count);
            Assert.AreEqual(0, GetAddedBmsFiles(result).Count);
            Assert.AreEqual(1, GetAddedBmsonSongs(result).Count);
            Assert.AreSame(bmsonSong, GetAddedBmsonSongs(result)[0]);
            CollectionAssert.AreEqual(new[] { bmsonSong }, storageRows);
            CollectionAssert.AreEqual(new[] { bmsonSong }, maintenanceTargets);
            CollectionAssert.AreEqual(new[] { bmsonSong }, applyTargets);
            Assert.AreEqual(destinationBmsonPath, bmsonSong.path);
            Assert.AreEqual(string.Empty, result.AddedEntries[0].Chart.InstallDestination);
            Assert.AreEqual(string.Empty, result.AddedEntries[0].Chart.InstallDestinationTitle);
            Assert.AreEqual(string.Empty, result.AddedEntries[0].Chart.InstallDestinationArtist);
            Assert.AreEqual(string.Empty, result.AddedCharts[0].InstallDestination);
            Assert.AreEqual(string.Empty, result.AddedCharts[0].InstallDestinationTitle);
            Assert.AreEqual(string.Empty, result.AddedCharts[0].InstallDestinationArtist);
            Assert.IsNull(bmsonEntry.GetBmsOwnerForTest());
            Assert.IsNull(result.AddedEntries[0].GetBmsOwnerForTest());
        });
    }

    [TestMethod]
    public void InstallPackagesWithFileMutationReceipts_DropsAdapterBackedBmsonAdapterAfterInstalledPath()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourceDirectoryPath = Path.Combine(tempDirectoryPath, "PendingPkg");
            string destinationDirectoryPath = Path.Combine(tempDirectoryPath, "InstalledPkg");
            Directory.CreateDirectory(sourceDirectoryPath);
            string sourceBmsonPath = Path.Combine(sourceDirectoryPath, "chart.bmson");
            string destinationBmsonPath = Path.Combine(destinationDirectoryPath, "chart.bmson");
            File.WriteAllText(sourceBmsonPath, CreateBmsonJsonWithSound("new.wav"));

            LR2SongDBExtended.bmson_song bmsonSong = BmsonSongParser.Parse(sourceBmsonPath);
            ChartPackage package = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmsonSong))]);
            package.path = sourceDirectoryPath;
            List<BMSFile> songUpserts = [];
            List<BMSFile> maintenanceTargets = [];
            List<BMSFile> scoreTargets = [];
            List<BMSFile> applyTargets = [];

            var service = new BmsLibraryPackageInstallService();
            PackageInstallExecutionResult result = ExecuteReceiptInstall(
                service,
                package,
                destinationDirectoryPath,
                result => songUpserts.AddRange(GetAddedBmsFiles(result)),
                result => maintenanceTargets.AddRange(GetAddedBmsFiles(result)),
                result => scoreTargets.AddRange(GetAddedBmsFiles(result)),
                result => applyTargets.AddRange(GetAddedBmsFiles(result)));

            Assert.AreEqual(1, result.AddedEntries.Count);
            Assert.AreEqual(1, result.AddedCharts.Count);
            Assert.AreEqual(0, GetAddedBmsFiles(result).Count);
            Assert.AreEqual(1, GetAddedBmsonSongs(result).Count);
            Assert.AreSame(bmsonSong, GetAddedBmsonSongs(result)[0]);
            Assert.AreEqual(0, songUpserts.Count);
            Assert.AreEqual(0, maintenanceTargets.Count);
            Assert.AreEqual(0, scoreTargets.Count);
            Assert.AreEqual(0, applyTargets.Count);
            Assert.AreEqual(destinationBmsonPath, bmsonSong.path);
            Assert.AreEqual(destinationDirectoryPath, bmsonSong.folder);
            Assert.AreEqual(destinationBmsonPath, result.AddedEntries[0].Chart.Path);
            Assert.IsNull(result.AddedEntries[0].GetBmsOwnerForTest());
        });
    }

    [TestMethod]
    public void IsSmartOverwriteProtectedExtension_DoesNotTreatBmsonAsProtectedResource()
    {
        var service = new BmsLibraryPackageInstallService();

        Assert.IsFalse(service.IsSmartOverwriteProtectedExtension("chart.bmson"));
        Assert.IsTrue(service.IsSmartOverwriteProtectedExtension("notes.txt"));
    }

    [TestMethod]
    public void DeletePendingCharts_DeletesWholePackageDirectoryWhenSelectionCoversPackage()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryPackageInstallService();
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "PendingPkg");
            Directory.CreateDirectory(packageDirectoryPath);
            string chartPath = Path.Combine(packageDirectoryPath, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n");
            File.WriteAllText(Path.Combine(packageDirectoryPath, "sound.wav"), "resource");
            string resourceDirectoryPath = Path.Combine(packageDirectoryPath, "images");
            Directory.CreateDirectory(resourceDirectoryPath);
            File.WriteAllText(Path.Combine(resourceDirectoryPath, "stage.png"), "image");
            TestableBmsFile chart = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath);
            var package = ChartPackageTestExtensions.CreatePackage([chart]);
            package.path = packageDirectoryPath;
            package.delete_parent = false;

            PendingFileDeletionResult result = service.DeletePendingCharts(
                [ChartFileProjection.FromBmsFile(chart)],
                [package],
                sendToRecycleBin: false,
                deleteContainingPackageFoldersWhenNoBms: true,
                new RealFileMutationService(),
                null,
                null);

            Assert.AreEqual(1, result.Requested);
            Assert.AreEqual(1, result.Processed);
            Assert.AreEqual(1, result.Removed);
            Assert.AreEqual(0, result.Failed);
            Assert.AreEqual(0, result.Skipped);
            CollectionAssert.AreEqual(new[] { chartPath }, result.ChartPathsToRemove);
            Assert.IsFalse(Directory.Exists(packageDirectoryPath));
        });
    }

    [TestMethod]
    public void DeletePendingCharts_DeletesWholeAdapterlessBmsonPackageDirectoryFromPackageSelectionWithoutMaterializing()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryPackageInstallService();
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "PendingBmsonPkg");
            Directory.CreateDirectory(packageDirectoryPath);
            string bmsonPath = Path.Combine(packageDirectoryPath, "chart.bmson");
            File.WriteAllText(bmsonPath, CreateBmsonJsonWithSound("sound.wav"));
            File.WriteAllText(Path.Combine(packageDirectoryPath, "sound.wav"), "resource");
            LR2SongDBExtended.bmson_song bmsonSong = BmsonSongParser.Parse(bmsonPath);
            PackageChartEntry adapterlessBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmsonSong));
            ChartPackage package = ChartPackage.FromChartEntries([adapterlessBmsonEntry]);
            package.path = packageDirectoryPath;

            PendingFileDeletionResult result = service.DeletePendingCharts(
                [adapterlessBmsonEntry.Chart],
                [package],
                sendToRecycleBin: false,
                deleteContainingPackageFoldersWhenNoBms: true,
                new RealFileMutationService(),
                null,
                null);

            Assert.AreEqual(1, result.Requested);
            Assert.AreEqual(1, result.Processed);
            Assert.AreEqual(1, result.Removed);
            Assert.AreEqual(0, result.Failed);
            Assert.AreEqual(0, result.Skipped);
            CollectionAssert.AreEqual(new[] { bmsonPath }, result.ChartPathsToRemove);
            Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());
            Assert.IsFalse(Directory.Exists(packageDirectoryPath));
        });
    }

    [TestMethod]
    public void DeletePendingCharts_DeletesAdapterlessBmsonChartByPathWithoutMaterializing()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryPackageInstallService();
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "PendingBmsonPkg");
            Directory.CreateDirectory(packageDirectoryPath);
            string bmsonPath = Path.Combine(packageDirectoryPath, "chart.bmson");
            File.WriteAllText(bmsonPath, CreateBmsonJsonWithSound("sound.wav"));
            File.WriteAllText(Path.Combine(packageDirectoryPath, "sound.wav"), "resource");
            LR2SongDBExtended.bmson_song bmsonSong = BmsonSongParser.Parse(bmsonPath);
            PackageChartEntry adapterlessBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmsonSong));
            ChartPackage package = ChartPackage.FromChartEntries([adapterlessBmsonEntry]);
            package.path = packageDirectoryPath;

            PendingFileDeletionResult result = service.DeletePendingCharts(
                [adapterlessBmsonEntry.Chart],
                [package],
                sendToRecycleBin: false,
                deleteContainingPackageFoldersWhenNoBms: false,
                new RealFileMutationService(),
                null,
                null);

            Assert.AreEqual(1, result.Requested);
            Assert.AreEqual(1, result.Processed);
            Assert.AreEqual(1, result.Removed);
            Assert.AreEqual(0, result.Failed);
            Assert.AreEqual(0, result.Skipped);
            CollectionAssert.AreEqual(new[] { bmsonPath }, result.ChartPathsToRemove);
            Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());
            Assert.IsFalse(File.Exists(bmsonPath));
        });
    }

    [TestMethod]
    public void DeletePendingCharts_DeletesWholeAdapterlessBmsonPackageDirectoryByPathWithoutMaterializing()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryPackageInstallService();
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "PendingBmsonPkg");
            Directory.CreateDirectory(packageDirectoryPath);
            string bmsonPath = Path.Combine(packageDirectoryPath, "chart.bmson");
            File.WriteAllText(bmsonPath, CreateBmsonJsonWithSound("sound.wav"));
            File.WriteAllText(Path.Combine(packageDirectoryPath, "sound.wav"), "resource");
            LR2SongDBExtended.bmson_song bmsonSong = BmsonSongParser.Parse(bmsonPath);
            PackageChartEntry adapterlessBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmsonSong));
            ChartPackage package = ChartPackage.FromChartEntries([adapterlessBmsonEntry]);
            package.path = packageDirectoryPath;

            PendingFileDeletionResult result = service.DeletePendingCharts(
                [adapterlessBmsonEntry.Chart],
                [package],
                sendToRecycleBin: false,
                deleteContainingPackageFoldersWhenNoBms: true,
                new RealFileMutationService(),
                null,
                null);

            Assert.AreEqual(1, result.Requested);
            Assert.AreEqual(1, result.Processed);
            Assert.AreEqual(1, result.Removed);
            Assert.AreEqual(0, result.Failed);
            Assert.AreEqual(0, result.Skipped);
            CollectionAssert.AreEqual(new[] { bmsonPath }, result.ChartPathsToRemove);
            Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());
            Assert.IsFalse(Directory.Exists(packageDirectoryPath));
        });
    }

    [TestMethod]
    public void DeletePendingCharts_FailedPackageFolderDeleteDoesNotMaterializeAdapterlessBmsonEntries()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryPackageInstallService();
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "PendingBmsonPkg");
            Directory.CreateDirectory(packageDirectoryPath);
            string bmsonPath = Path.Combine(packageDirectoryPath, "chart.bmson");
            File.WriteAllText(bmsonPath, CreateBmsonJsonWithSound("sound.wav"));
            File.WriteAllText(Path.Combine(packageDirectoryPath, "sound.wav"), "resource");
            LR2SongDBExtended.bmson_song bmsonSong = BmsonSongParser.Parse(bmsonPath);
            PackageChartEntry adapterlessBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmsonSong));
            ChartPackage package = ChartPackage.FromChartEntries([adapterlessBmsonEntry]);
            package.path = packageDirectoryPath;

            PendingFileDeletionResult result = service.DeletePendingCharts(
                [adapterlessBmsonEntry.Chart],
                [package],
                sendToRecycleBin: false,
                deleteContainingPackageFoldersWhenNoBms: true,
                new FailingDeleteDirectoryFileMutationService(),
                null,
                null);

            Assert.AreEqual(1, result.Requested);
            Assert.AreEqual(1, result.Processed);
            Assert.AreEqual(0, result.Removed);
            Assert.AreEqual(1, result.Failed);
            Assert.AreEqual(1, result.Failures.Count);
            Assert.IsTrue(result.Failures[0].IsDirectory);
            Assert.AreEqual(packageDirectoryPath, result.Failures[0].Path);
            Assert.AreEqual(0, result.Skipped);
            Assert.AreEqual(0, result.ChartPathsToRemove.Count);
            Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());
            Assert.IsTrue(File.Exists(bmsonPath));
            Assert.IsTrue(File.Exists(Path.Combine(packageDirectoryPath, "sound.wav")));
            Assert.IsTrue(Directory.Exists(packageDirectoryPath));
        });
    }

    [TestMethod]
    public void GetPendingBmsFormatChartFilesSnapshot_ExcludesBmsonCharts()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryPackageInstallService();
            string bmsonPath = Path.Combine(tempDirectoryPath, "chart.bmson");
            File.WriteAllText(bmsonPath, CreateBmsonJsonWithSound("sound.wav"));
            TestableBmsFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(tempDirectoryPath, "chart.bms"));
            PackageChartEntry adapterlessBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(BmsonSongParser.Parse(bmsonPath)));
            PackageChartEntry plainBmsonPathEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(new LR2SongDBExtended.bmson_song
            {
                path = Path.Combine(tempDirectoryPath, "plain.bmson"),
                md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
            }));
            var package = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(bmsFile)), adapterlessBmsonEntry, plainBmsonPathEntry]);
            ChartPackage adapterlessBmsonPackage = ChartPackage.FromChartEntries([adapterlessBmsonEntry]);

            List<ChartFile> result = service.GetPendingBmsFormatChartFilesSnapshot([package, adapterlessBmsonPackage]);

            CollectionAssert.AreEqual(new[] { bmsFile }, result.Select(chart => chart.GetBmsStorageOwner()).ToArray());
            Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());
        });
    }

    [TestMethod]
    public void GetPendingBmsFormatChartFilesSnapshot_UsesBmsStorageOwnerFromChartFile()
    {
        var service = new BmsLibraryPackageInstallService();
        TestableBmsFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\chart.bms");
        PackageChartEntry bmsEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(bmsFile));
        ChartPackage package = ChartPackage.FromChartEntries([bmsEntry]);

        List<ChartFile> result = service.GetPendingBmsFormatChartFilesSnapshot([package]);

        CollectionAssert.AreEqual(new[] { bmsFile }, result.Select(chart => chart.GetBmsStorageOwner()).ToArray());
        Assert.AreSame(bmsFile, bmsEntry.GetBmsOwnerForTest());
    }

    [TestMethod]
    public void RenamePendingZeroNoteBmsFormatChartsToInvalidExtensions_ExcludesBmsonCharts()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryPackageInstallService();
            string bmsonPath = Path.Combine(tempDirectoryPath, "chart.bmson");
            File.WriteAllText(bmsonPath, "{}");
            int renameCallCount = 0;

            PendingZeroNoteRenameResult result = service.RenamePendingZeroNoteBmsFormatChartsToInvalidExtensions(
                [],
                delegate
                {
                    renameCallCount++;
                    return new RenameInvalidExtensionOutcome
                    {
                        Action = RenameInvalidExtensionAction.Renamed
                    };
                });

            Assert.AreEqual(0, result.Total);
            Assert.AreEqual(0, result.Processed);
            Assert.AreEqual(0, renameCallCount);
            Assert.IsTrue(File.Exists(bmsonPath));
        });
    }

    [TestMethod]
    public void RenamePendingBmsFormatChartFileExtensions_ReturnsRenamedDuplicateDeletedAndFailedFiles()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryPackageInstallService();
            var fileOperationService = new BmsLibraryLibraryFileOperationsService();
            var fileMutationService = new RealFileMutationService();
            string renameSourcePath = Path.Combine(tempDirectoryPath, "rename_me.bms");
            string duplicateSourcePath = Path.Combine(tempDirectoryPath, "duplicate.bms");
            string duplicateDestinationPath = Path.Combine(tempDirectoryPath, "duplicate.bme");
            string failureSourcePath = Path.Combine(tempDirectoryPath, "failure.bms");
            string bmsonSourcePath = Path.Combine(tempDirectoryPath, "skip.bmson");
            File.WriteAllText(renameSourcePath, "rename");
            File.WriteAllText(duplicateSourcePath, "same");
            File.WriteAllText(duplicateDestinationPath, "same");
            File.WriteAllText(failureSourcePath, "failure");
            File.WriteAllText(bmsonSourcePath, "{}");
            TestableBmsFile renameFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", renameSourcePath);
            TestableBmsFile duplicateFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", duplicateSourcePath);
            TestableBmsFile failureFile = CreateFile("cccccccccccccccccccccccccccccccc", failureSourcePath);
            duplicateFile.SetHash(fileOperationService.TryComputeFileMd5ForPath(duplicateSourcePath));

            PendingExtensionRenameResult result = service.RenamePendingBmsFormatChartFileExtensions(
                [
                    ChartFileProjection.FromBmsFile(renameFile),
                    ChartFileProjection.FromBmsFile(duplicateFile),
                    ChartFileProjection.FromBmsFile(failureFile)
                ],
                ".bme",
                delegate (BMSFile file, string requestedPath)
                {
                    if (ReferenceEquals(file, failureFile))
                    {
                        return new RenameInvalidExtensionOutcome
                        {
                            Action = RenameInvalidExtensionAction.Skipped,
                            FinalPath = requestedPath,
                            FailureException = new IOException("failure")
                        };
                    }
                    return fileOperationService.ProcessInvalidExtensionRename(file, requestedPath, fileMutationService, null);
                });

            Assert.AreEqual(3, result.Total);
            Assert.AreEqual(1, result.Renamed);
            Assert.AreEqual(1, result.DuplicateDeleted);
            Assert.AreEqual(1, result.Skipped);
            Assert.AreEqual(1, result.Failed);
            CollectionAssert.AreEquivalent(new[] { renameFile.path, duplicateFile.path }, result.ChartPathsToRemove);
            Assert.AreEqual(1, result.Failures.Count);
            Assert.AreSame(failureFile, result.Failures[0].File);
            Assert.IsTrue(File.Exists(bmsonSourcePath));
        });
    }

    [TestMethod]
    public void MovePackageFilesWithReceipt_DeletesParentDirectory_WhenRemainingFilesAreEmpty()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            SafeDeleteMoveSetup setup = CreateSingleChartParentDeleteSetup(tempDirectoryPath, "install-target", "#TITLE Installed");

            bool moved = ExecuteSingleChartParentDeleteMoveWithReceipt(setup, independentlyOwnedLookup: null);

            Assert.IsTrue(moved);
            Assert.IsFalse(Directory.Exists(setup.ParentDirectoryPath));
            Assert.IsTrue(File.Exists(Path.Combine(setup.DestinationDirectoryPath, "install-target.bms")));
        });
    }

    [TestMethod]
    public void MovePackageFilesWithReceipt_KeepsParentDirectory_WhenUninstalledChartRemains()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            SafeDeleteMoveSetup setup = CreateSingleChartParentDeleteSetup(tempDirectoryPath, "install-target", "#TITLE Installed");
            string remainingChartPath = CreateBmsFile(setup.ParentDirectoryPath, "remain-uninstalled.bms", "#TITLE Remain Uninstalled");

            bool moved = ExecuteSingleChartParentDeleteMoveWithReceipt(setup, independentlyOwnedLookup: null);

            Assert.IsTrue(moved);
            Assert.IsTrue(Directory.Exists(setup.ParentDirectoryPath));
            Assert.IsTrue(File.Exists(remainingChartPath));
            Assert.IsTrue(File.Exists(Path.Combine(setup.DestinationDirectoryPath, "install-target.bms")));
        });
    }

    [TestMethod]
    public void MovePackageFilesWithReceipt_DeletesParentDirectory_WhenOnlyInstalledChartsRemain()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            SafeDeleteMoveSetup setup = CreateSingleChartParentDeleteSetup(tempDirectoryPath, "install-target", "#TITLE Installed");
            string remainingChartPath = CreateBmsFile(setup.ParentDirectoryPath, "remain-installed.bms", "#TITLE Remain Installed");

            string independentlyOwnedPath = Path.Combine(tempDirectoryPath, "Installed", "Owned", "remain-installed.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(independentlyOwnedPath)!);
            File.Copy(remainingChartPath, independentlyOwnedPath);
            bool moved = ExecuteSingleChartParentDeleteMoveWithReceipt(
                setup,
                CreateInstalledChartLookup([BMSFile.CreateBMSFileFromFile(independentlyOwnedPath)]));

            Assert.IsTrue(moved);
            Assert.IsFalse(Directory.Exists(setup.ParentDirectoryPath));
            Assert.IsTrue(File.Exists(Path.Combine(setup.DestinationDirectoryPath, "install-target.bms")));
        });
    }

    [TestMethod]
    public void MovePackageFilesWithReceipt_KeepsParentDirectory_WhenOwnedChartRemainsInsideCleanupBoundary()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            SafeDeleteMoveSetup setup = CreateSingleChartParentDeleteSetup(tempDirectoryPath, "install-target", "#TITLE Installed");
            string insideOwnedPath = Path.Combine(setup.ParentDirectoryPath, "Registered", "owned-inside.bms");
            string outsideOwnedPath = Path.Combine(tempDirectoryPath, "Installed", "Owned", "owned-outside.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(insideOwnedPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(outsideOwnedPath)!);
            File.Copy(setup.SourceChartPath, insideOwnedPath);
            File.Copy(setup.SourceChartPath, outsideOwnedPath);

            bool moved = ExecuteSingleChartParentDeleteMoveWithReceipt(
                setup,
                CreateInstalledChartLookup([
                    BMSFile.CreateBMSFileFromFile(insideOwnedPath),
                    BMSFile.CreateBMSFileFromFile(outsideOwnedPath)]));

            Assert.IsTrue(moved);
            Assert.IsTrue(Directory.Exists(setup.ParentDirectoryPath));
            Assert.IsTrue(File.Exists(insideOwnedPath));
            Assert.IsTrue(File.Exists(outsideOwnedPath));
            Assert.IsTrue(File.Exists(Path.Combine(setup.DestinationDirectoryPath, "install-target.bms")));
        });
    }

    [TestMethod]
    public void MovePackageFilesWithReceipt_KeepsParentDirectory_WhenNonChartFileRemains()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            SafeDeleteMoveSetup setup = CreateSingleChartParentDeleteSetup(tempDirectoryPath, "install-target", "#TITLE Installed");
            string readmePath = Path.Combine(setup.ParentDirectoryPath, "readme.txt");
            string safeResidualPath = CreateBmsFile(
                setup.ParentDirectoryPath,
                "safe-residual.bms",
                "#TITLE Safe Residual");
            string safeResidualOwnedPath = Path.Combine(
                tempDirectoryPath,
                "Installed",
                "Owned",
                "safe-residual.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(safeResidualOwnedPath)!);
            File.Copy(safeResidualPath, safeResidualOwnedPath);
            File.WriteAllText(readmePath, "remaining text");

            bool moved = ExecuteSingleChartParentDeleteMoveWithReceipt(
                setup,
                CreateInstalledChartLookup([BMSFile.CreateBMSFileFromFile(safeResidualOwnedPath)]));

            Assert.IsTrue(moved);
            Assert.IsTrue(Directory.Exists(setup.ParentDirectoryPath));
            Assert.IsTrue(File.Exists(safeResidualPath));
            Assert.IsTrue(File.Exists(readmePath));
            Assert.IsTrue(File.Exists(safeResidualOwnedPath));
            Assert.IsTrue(File.Exists(Path.Combine(setup.DestinationDirectoryPath, "install-target.bms")));
        });
    }

    [TestMethod]
    public void MovePackageFilesWithReceipt_DeletesParentDirectory_WhenResidualMatchesPlanDestination()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            SafeDeleteMoveSetup setup = CreateSingleChartParentDeleteSetup(tempDirectoryPath, "install-target", "#TITLE Installed");
            string residualChartPath = Path.Combine(setup.ParentDirectoryPath, "same-as-install-target.bms");
            File.Copy(setup.SourceChartPath, residualChartPath);

            bool moved = ExecuteSingleChartParentDeleteMoveWithReceipt(setup, independentlyOwnedLookup: null);

            Assert.IsTrue(moved);
            Assert.IsFalse(Directory.Exists(setup.ParentDirectoryPath));
            Assert.IsTrue(File.Exists(Path.Combine(setup.DestinationDirectoryPath, "install-target.bms")));
        });
    }

    [TestMethod]
    public void MovePackageFilesWithReceipt_KeepsParentDirectory_WhenRemainingChartHashCannotBeMatched()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            SafeDeleteMoveSetup setup = CreateSingleChartParentDeleteSetup(tempDirectoryPath, "install-target", "#TITLE Installed");
            string nestedDirectoryPath = Path.Combine(setup.ParentDirectoryPath, "Nested");
            Directory.CreateDirectory(nestedDirectoryPath);
            string nestedChartPath = CreateBmsFile(nestedDirectoryPath, "remain-nested.bms", "#TITLE Nested Remain");
            string safeResidualPath = CreateBmsFile(
                setup.ParentDirectoryPath,
                "safe-residual.bms",
                "#TITLE Safe Residual");
            string safeResidualOwnedPath = Path.Combine(
                tempDirectoryPath,
                "Installed",
                "Owned",
                "safe-residual.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(safeResidualOwnedPath)!);
            File.Copy(safeResidualPath, safeResidualOwnedPath);

            bool moved = ExecuteSingleChartParentDeleteMoveWithReceipt(
                setup,
                CreateInstalledChartLookup([BMSFile.CreateBMSFileFromFile(safeResidualOwnedPath)]));

            Assert.IsTrue(moved);
            Assert.IsTrue(Directory.Exists(setup.ParentDirectoryPath));
            Assert.IsTrue(File.Exists(safeResidualPath));
            Assert.IsTrue(File.Exists(nestedChartPath));
            Assert.IsTrue(File.Exists(safeResidualOwnedPath));
            Assert.IsTrue(File.Exists(Path.Combine(setup.DestinationDirectoryPath, "install-target.bms")));
        });
    }

    [TestMethod]
    public void MovePackageFilesWithReceipt_DeletesParentDirectory_WhenNestedRemainingChartsAreAllInstalled()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            SafeDeleteMoveSetup setup = CreateSingleChartParentDeleteSetup(tempDirectoryPath, "install-target", "#TITLE Installed");
            string nestedDirectoryPath = Path.Combine(setup.ParentDirectoryPath, "Nested");
            Directory.CreateDirectory(nestedDirectoryPath);
            string nestedChartPath = CreateBmsFile(nestedDirectoryPath, "remain-nested-installed.bms", "#TITLE Nested Installed");
            string independentlyOwnedPath = Path.Combine(tempDirectoryPath, "Installed", "Owned", "remain-nested-installed.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(independentlyOwnedPath)!);
            File.Copy(nestedChartPath, independentlyOwnedPath);

            bool moved = ExecuteSingleChartParentDeleteMoveWithReceipt(
                setup,
                CreateInstalledChartLookup([BMSFile.CreateBMSFileFromFile(independentlyOwnedPath)]));

            Assert.IsTrue(moved);
            Assert.IsFalse(Directory.Exists(setup.ParentDirectoryPath));
            Assert.IsTrue(File.Exists(Path.Combine(setup.DestinationDirectoryPath, "install-target.bms")));
        });
    }

    [TestMethod]
    public void MovePackageFilesWithReceipt_DeletesParentDirectory_WhenRemainingBmsonChartIsInstalled()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            SafeDeleteMoveSetup setup = CreateSingleChartParentDeleteSetup(tempDirectoryPath, "install-target", "#TITLE Installed");
            string remainingBmsonPath = Path.Combine(setup.ParentDirectoryPath, "remain-installed.bmson");
            File.WriteAllText(remainingBmsonPath, CreateBmsonJsonWithSound("sound.wav"));
            string independentlyOwnedPath = Path.Combine(tempDirectoryPath, "Installed", "Owned", "remain-installed.bmson");
            Directory.CreateDirectory(Path.GetDirectoryName(independentlyOwnedPath)!);
            File.Copy(remainingBmsonPath, independentlyOwnedPath);

            bool moved = ExecuteSingleChartParentDeleteMoveWithReceipt(
                setup,
                CreateInstalledChartLookup(
                    [],
                    [BmsonSongParser.Parse(independentlyOwnedPath)]));

            Assert.IsTrue(moved);
            Assert.IsFalse(Directory.Exists(setup.ParentDirectoryPath));
            Assert.IsTrue(File.Exists(Path.Combine(setup.DestinationDirectoryPath, "install-target.bms")));
        });
    }

    [DataTestMethod]
    [DataRow("fixture.zip")]
    [DataRow("fixture.7z")]
    [DataRow("fixture.rar")]
    [DataRow("fixture.lzh")]
    [DoNotParallelize]
    public void ExpandInstallSources_ExtractsSupportedArchiveAndRestoresLastWriteTime(string archiveFileName)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string archivePath = Path.Combine(tempDirectoryPath, archiveFileName);
            File.Copy(GetArchiveFixturePath(archiveFileName), archivePath);
            var service = new BmsLibraryPackageInstallService();
            List<string> logs = [];
            var progressWriter = new RecordingPackageInstallProgressWriter();

            try
            {
                List<string> expandedPaths = service.ExpandInstallSourcesWithProgress(
                    [archivePath],
                    new RealFileMutationService(),
                    new FileMutationOptions(ReadOnlyNormalizationScope.TargetOnly),
                    logs.Add,
                    null,
                    progressWriter,
                    CancellationToken.None,
                    action => action());

                Assert.AreEqual(1, expandedPaths.Count);
                string extractedDirectoryPath = expandedPaths[0];
                string extractedChartPath = Path.Combine(extractedDirectoryPath, "maybe_H.bms");
                Assert.IsTrue(File.Exists(extractedChartPath));
                Assert.AreEqual(GetExpectedArchiveLastWriteTime(), File.GetLastWriteTime(extractedChartPath));
                Assert.AreEqual(1, progressWriter.SourceProcessedCount);
                CollectionAssert.AreEqual(
                    new[] { "1/1:" + archiveFileName },
                    progressWriter.ArchiveStarts.ToArray());
                Assert.IsFalse(logs.Any(message => message.IndexOf("extract_failed", StringComparison.OrdinalIgnoreCase) >= 0));
                Assert.IsFalse(logs.Any(message => message.IndexOf("metadata_restore_required_failed", StringComparison.OrdinalIgnoreCase) >= 0));
            }
            finally
            {
                global::BeMusicSeeker.TempDirectoryPublisher.RemoveAll();
            }
        });
    }

    [DataTestMethod]
    [DataRow("fixture.zip")]
    [DataRow("fixture.7z")]
    [DataRow("fixture.rar")]
    [DataRow("fixture.lzh")]
    [DoNotParallelize]
    public void ExpandInstallSources_AbortsArchiveWhenRequiredLastWriteRestoreFails(string archiveFileName)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string archivePath = Path.Combine(tempDirectoryPath, archiveFileName);
            File.Copy(GetArchiveFixturePath(archiveFileName), archivePath);
            var service = new BmsLibraryPackageInstallService();
            List<string> logs = [];
            var dialogService = new RecordingDialogService();
            var progressWriter = new RecordingPackageInstallProgressWriter();

            try
            {
                List<string> expandedPaths = service.ExpandInstallSourcesWithProgress(
                    [archivePath],
                    new FailingLastWriteFileMutationService(),
                    new FileMutationOptions(ReadOnlyNormalizationScope.TargetOnly),
                    logs.Add,
                    dialogService,
                    progressWriter,
                    CancellationToken.None,
                    action => action());

                Assert.AreEqual(0, expandedPaths.Count);
                Assert.AreEqual(1, progressWriter.SourceProcessedCount);
                CollectionAssert.AreEqual(
                    new[] { "1/1:" + archiveFileName },
                    progressWriter.ArchiveStarts.ToArray());
                Assert.IsTrue(logs.Any(message => message.IndexOf("metadata_restore_required_failed", StringComparison.OrdinalIgnoreCase) >= 0));
                Assert.IsTrue(logs.Any(message => message.IndexOf(".bms", StringComparison.OrdinalIgnoreCase) >= 0));
                Assert.AreEqual(1, dialogService.Messages.Count);
                Assert.IsTrue(dialogService.Messages[0].IndexOf("更新日時", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            finally
            {
                global::BeMusicSeeker.TempDirectoryPublisher.RemoveAll();
            }
        });
    }

    [TestMethod]
    [DoNotParallelize]
    public void ExpandInstallSources_ReportsArchiveOrdinalAmongArchivesOnly()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string directoryPath = Path.Combine(tempDirectoryPath, "source-folder");
            Directory.CreateDirectory(directoryPath);
            string firstArchivePath = Path.Combine(tempDirectoryPath, "first.zip");
            string secondArchivePath = Path.Combine(tempDirectoryPath, "second.7z");
            File.Copy(GetArchiveFixturePath("fixture.zip"), firstArchivePath);
            File.Copy(GetArchiveFixturePath("fixture.7z"), secondArchivePath);
            var service = new BmsLibraryPackageInstallService();
            var progressWriter = new RecordingPackageInstallProgressWriter();

            try
            {
                List<string> expandedPaths = service.ExpandInstallSourcesWithProgress(
                    [directoryPath, firstArchivePath, secondArchivePath],
                    new RealFileMutationService(),
                    new FileMutationOptions(ReadOnlyNormalizationScope.TargetOnly),
                    null,
                    null,
                    progressWriter,
                    CancellationToken.None,
                    action => action());

                Assert.AreEqual(3, expandedPaths.Count);
                Assert.AreEqual(3, progressWriter.SourceProcessedCount);
                CollectionAssert.AreEqual(
                    new[] { "1/2:first.zip", "2/2:second.7z" },
                    progressWriter.ArchiveStarts.ToArray());
            }
            finally
            {
                global::BeMusicSeeker.TempDirectoryPublisher.RemoveAll();
            }
        });
    }

    [DataTestMethod]
    [DataRow("encrypted.7z")]
    [DataRow("corrupt.7z")]
    [DoNotParallelize]
    public void ExpandInstallSources_RejectsArchiveFailureWithoutConsumingSource(string archiveFileName)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string archivePath = Path.Combine(tempDirectoryPath, archiveFileName);
            if (string.Equals(archiveFileName, "corrupt.7z", StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllText(archivePath, "not a valid archive");
            }
            else
            {
                File.Copy(GetArchiveFixturePath(archiveFileName), archivePath);
            }

            var service = new BmsLibraryPackageInstallService();
            List<string> logs = [];
            var dialogService = new RecordingDialogService();
            var progressWriter = new RecordingPackageInstallProgressWriter();

            try
            {
                List<string> expandedPaths = service.ExpandInstallSourcesWithProgress(
                    [archivePath],
                    new RealFileMutationService(),
                    new FileMutationOptions(ReadOnlyNormalizationScope.TargetOnly),
                    logs.Add,
                    dialogService,
                    progressWriter,
                    CancellationToken.None,
                    action => action());

                Assert.AreEqual(0, expandedPaths.Count);
                Assert.AreEqual(1, progressWriter.SourceProcessedCount);
                Assert.IsTrue(File.Exists(archivePath));
                Assert.IsTrue(logs.Any(message => message.IndexOf("extract_failed", StringComparison.OrdinalIgnoreCase) >= 0));
                Assert.AreEqual(1, dialogService.Messages.Count);

                string extractStart = logs.Single(message => message.IndexOf("extract_start", StringComparison.OrdinalIgnoreCase) >= 0);
                int destinationMarker = extractStart.IndexOf(" destination=", StringComparison.Ordinal);
                Assert.IsTrue(destinationMarker >= 0);
                string extractionDirectoryPath = extractStart[(destinationMarker + " destination=".Length)..];
                Assert.IsFalse(Directory.Exists(extractionDirectoryPath));
            }
            finally
            {
                global::BeMusicSeeker.TempDirectoryPublisher.RemoveAll();
            }
        });
    }

    [DataTestMethod]
    [DataRow("../outside.txt")]
    [DataRow(".. /outside.txt")]
    [DataRow(" ../outside.txt")]
    public void SevenZipArchiveExtractor_RejectsEntryOutsideExtractionDirectory(string entryName)
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string archivePath = Path.Combine(tempDirectoryPath, "unsafe.zip");
            using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                archive.CreateEntry(entryName);
            }

            string extractionDirectoryPath = Path.Combine(tempDirectoryPath, "extracted");
            Assert.ThrowsException<InvalidDataException>(() => SevenZipArchiveExtractor.ExtractArchiveEntries(
                archivePath,
                SevenZipArchiveExtractor.ResolveBundledSevenZipLibraryPath(),
                extractionDirectoryPath));
            Assert.IsFalse(File.Exists(Path.Combine(tempDirectoryPath, "outside.txt")));
            Assert.IsFalse(Directory.Exists(extractionDirectoryPath));
        });
    }

    private static TestableBmsFile CreateFile(string hash, string path)
    {
        var file = new TestableBmsFile
        {
            path = path
        };
        file.SetHash(hash);
        return file;
    }

    private static PackageInstallExecutionResult CreateSuccessfulPackageInstallResult(ChartPackage package)
    {
        var result = new PackageInstallExecutionResult();
        result.AddedEntries.AddRange(package?.ChartEntries ?? []);
        result.AddedCharts.AddRange(result.AddedEntries
            .Select(entry => entry?.Chart)
            .Where(chart => chart != null));
        result.MutationReceipt = new FileDbMutationBatchReceipt([
            new FileDbMutationReceipt(
                Guid.NewGuid(),
                FileDbMutationTerminalState.Completed,
                durableCommit: true,
                compensationAttemptCount: 0,
                cleanupAttemptCount: 0,
                sourcePaths: [],
                destinationPaths: [],
                stagingPaths: [],
                backupPaths: [],
                recoveryPaths: [])]);
        return result;
    }

    private static InstalledChartLookupIndexSnapshot CreateInstalledChartLookup(IEnumerable<BMSFile> installedFiles)
    {
        return CreateInstalledChartLookup(installedFiles, []);
    }

    private static InstalledChartLookupIndexSnapshot CreateInstalledChartLookup(
        IEnumerable<BMSFile> installedFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> installedBmsonSongs)
    {
        var state = new InstalledChartLookupIndexState();
        foreach (BMSFile file in installedFiles ?? [])
        {
            if (file != null)
            {
                state.AddChart(file.path, file.hash, file.sha256);
            }
        }
        foreach (LR2SongDBExtended.bmson_song song in installedBmsonSongs ?? [])
        {
            if (song != null)
            {
                state.AddChart(song.path, song.md5, song.sha256);
            }
        }
        return state.CreateSnapshot();
    }

    private static List<BMSFile> GetAddedBmsFiles(PackageInstallExecutionResult result)
    {
        return [.. (result?.AddedCharts ?? [])
            .Select(chart => chart?.GetBmsStorageOwner())
            .OfType<BMSFile>()
            .Where(ChartFileKindResolver.IsBmsChartFile)];
    }

    private static List<LR2SongDBExtended.bmson_song> GetAddedBmsonSongs(PackageInstallExecutionResult result)
    {
        return [.. (result?.AddedCharts ?? [])
            .Select(chart => chart?.GetBmsonStorageOwner())
            .OfType<LR2SongDBExtended.bmson_song>()
            .Where(song => !string.IsNullOrWhiteSpace(song.path))];
    }

    private static PackageInstallEstimationSnapshot BuildPackageSnapshot(ChartPackage package, IEnumerable<BMSFile> targetFiles)
    {
        List<BMSFile> targetFileList = [.. (targetFiles ?? []).Where(file => file != null)];
        List<PackageChartEntry> targetEntries = targetFileList.Count == 0
            ? package.ChartEntries
            : ResolvePackageEntries(package, targetFileList);
        return package.GetOrBuildInstallEstimationSnapshotFromEntries(targetEntries);
    }

    private static List<PackageChartEntry> ResolvePackageEntries(ChartPackage package, IEnumerable<BMSFile> targetFiles)
    {
        List<PackageChartEntry> packageEntries = package.ChartEntries;
        var result = new List<PackageChartEntry>();
        foreach (BMSFile targetFile in (targetFiles ?? []).Where(file => file != null))
        {
            PackageChartEntry packageEntry = packageEntries.FirstOrDefault(entry => IsSamePackageChartTarget(entry, targetFile));
            result.Add(packageEntry ?? PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(targetFile)));
        }
        return [.. result.Where(entry => entry?.Chart != null)];
    }

    private static bool IsSamePackageChartTarget(PackageChartEntry entry, BMSFile targetFile)
    {
        if (entry?.Chart == null || targetFile == null)
        {
            return false;
        }
        if (ReferenceEquals(entry.GetBmsOwnerForTest(), targetFile))
        {
            return true;
        }
        return !string.IsNullOrWhiteSpace(entry.Chart.Path)
            && !string.IsNullOrWhiteSpace(targetFile.path)
            && entry.Chart.Path.Equals(targetFile.path, StringComparison.OrdinalIgnoreCase);
    }

    private static SafeDeleteMoveSetup CreateSingleChartParentDeleteSetup(string tempDirectoryPath, string chartBaseName, string chartBody)
    {
        string parentDirectoryPath = Path.Combine(tempDirectoryPath, "Pending", "Parent");
        string destinationDirectoryPath = Path.Combine(tempDirectoryPath, "Installed", "Package");
        Directory.CreateDirectory(parentDirectoryPath);
        string sourceChartPath = CreateBmsFile(parentDirectoryPath, chartBaseName + ".bms", chartBody);
        var sourceChart = BMSFile.CreateBMSFileFromFile(sourceChartPath);
        var package = ChartPackageTestExtensions.CreatePackage([sourceChart]);
        package.path = sourceChartPath;
        package.delete_parent = true;
        return new SafeDeleteMoveSetup
        {
            ParentDirectoryPath = parentDirectoryPath,
            DestinationDirectoryPath = destinationDirectoryPath,
            SourceChartPath = sourceChartPath,
            Package = package
        };
    }

    private static PackageInstallExecutionResult ExecuteReceiptInstall(
        BmsLibraryPackageInstallService service,
        ChartPackage package,
        string destinationDirectoryPath,
        Action<PackageInstallExecutionResult> upsertStorageRows,
        Action<PackageInstallExecutionResult> updateMaintenance,
        Action<PackageInstallExecutionResult> applyScores,
        Action<PackageInstallExecutionResult> applyState)
    {
        return service.InstallPackagesWithFileMutationReceipts(
            [package],
            destinationDirectoryPath,
            (movePackage, destination, sourceCleanupPolicy, existingHashes, _, excludedComponentPaths, applyDurableCommit) =>
                service.MovePackageFilesWithReceipt(
                    movePackage,
                    destination,
                    new BmsLibraryOptionsSnapshot
                    {
                        EnableSmartComponentOverwrite = false,
                        KeepSmartOverwriteProtectedFilesByRenaming = false
                    },
                    (_, _) => throw new AssertFailedException("createFolderPath should not be called when destination is specified."),
                    ex => ex.Message,
                    new RealFileMutationService(),
                    null,
                    new FileMutationOptions(ReadOnlyNormalizationScope.TargetOnly),
                    new FileMutationOptions(ReadOnlyNormalizationScope.RecursiveDirectoryTree),
                    _ => { },
                    applyDurableCommit,
                    showMessageBoxOnInstallFail: false,
                    sourceCleanupPolicy: sourceCleanupPolicy,
                    existingHashes: existingHashes,
                    excludedComponentPaths: excludedComponentPaths),
            result =>
            {
                upsertStorageRows?.Invoke(result);
                return FileDbMutationCommitResult.Durable();
            },
            updateMaintenance,
            applyScores,
            applyState,
            sourceCleanupPolicy: PackageSourceCleanupPolicy.PreserveUnconsumedContents);
    }

    private static bool ExecuteSingleChartParentDeleteMoveWithReceipt(
        SafeDeleteMoveSetup setup,
        IInstalledChartLookupIndex? independentlyOwnedLookup)
    {
        var service = new BmsLibraryPackageInstallService();
        var fileMutationService = new RealFileMutationService();
        FileDbMutationReceipt receipt = service.MovePackageFilesWithReceipt(
            setup.Package,
            setup.DestinationDirectoryPath,
            new BmsLibraryOptionsSnapshot
            {
                EnableSmartComponentOverwrite = false,
                KeepSmartOverwriteProtectedFilesByRenaming = false
            },
            (_, _) => throw new AssertFailedException("createFolderPath should not be called when destination is specified."),
            ex => ex.Message,
            fileMutationService,
            null,
            new FileMutationOptions(ReadOnlyNormalizationScope.TargetOnly),
            new FileMutationOptions(ReadOnlyNormalizationScope.RecursiveDirectoryTree),
            _ => { },
            _ => FileDbMutationCommitResult.Durable(),
            showMessageBoxOnInstallFail: false,
            sourceCleanupPolicy: PackageSourceCleanupPolicy.DeleteVerifiedResidualContents,
            independentOwnershipLookup: independentlyOwnedLookup);
        return receipt?.DurableCommit == true
            && receipt.TerminalState != FileDbMutationTerminalState.DurableFinalizationFailed;
    }

    private static string CreateBmsFile(string directoryPath, string fileName, string titleLine)
    {
        Directory.CreateDirectory(directoryPath);
        string filePath = Path.Combine(directoryPath, fileName);
        File.WriteAllText(filePath, "#PLAYER 1\r\n" + titleLine + "\r\n#ARTIST Test\r\n");
        return filePath;
    }

    private static string CreateBmsonJsonWithSound(string soundName)
    {
        return "{"
            + "\"version\":\"1.0.0\","
            + "\"info\":{\"title\":\"Bmson\",\"artist\":\"Artist\",\"mode_hint\":\"beat-7k\"},"
            + "\"sound_channels\":[{\"name\":\"" + soundName + "\",\"notes\":[{\"x\":1,\"y\":0,\"l\":0}]}]"
            + "}";
    }

    private static string GetArchiveFixturePath(string fileName)
    {
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "archives", fileName);
    }

    private static DateTime GetExpectedArchiveLastWriteTime()
    {
        return new DateTime(2002, 1, 11, 18, 0, 8);
    }

    private static void AssertLibraryWriterCanBeAcquired(
        BMSLibrary library,
        string fieldName)
    {
        FieldInfo field = typeof(BMSLibrary).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        PropertyInfo property = typeof(BMSLibrary).GetProperty(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        object gateValue = field != null
            ? field.GetValue(library)
            : property?.GetValue(library);
        Assert.IsNotNull(gateValue, fieldName + " was not found.");
        var gate = gateValue as ReaderWriterLockSlimWrapper;
        Assert.IsNotNull(gate, fieldName + " was not a reader/writer gate.");
        Assert.IsFalse(gate.IsReadLockHeld, fieldName + " reader was held by the subscriber thread.");
        using (gate.GetWriterGuard())
        {
            Assert.IsTrue(gate.IsWriteLockHeld, fieldName + " writer could not be reacquired.");
        }
    }

    private static void WithTemporaryDirectory(Action<string> testAction)
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_PackageInstallTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        try
        {
            testAction(tempDirectoryPath);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    private static ObservableCollection<ChartPackage> CreatePackageCollection(IEnumerable<ChartPackage> packages)
    {
        return new ObservableCollection<ChartPackage>([.. (packages ?? [])]);
    }

    private static void WithTemporarySongDb(Action<string, string> testAction)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_PackageInstallSongDbTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDBExtended.bmson_song>();
            }
            testAction(songDbPath, tempRootPath);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    private sealed class SafeDeleteMoveSetup
    {
        public string ParentDirectoryPath { get; set; } = string.Empty;

        public string DestinationDirectoryPath { get; set; } = string.Empty;

        public string SourceChartPath { get; set; } = string.Empty;

        public ChartPackage Package { get; set; } = null!;
    }

    private sealed class RecordingPackageInstallProgressWriter : IPackageInstallProgressWriter
    {
        public List<PackageInstallProgressUpdate> Updates { get; } = [];

        public int SourceProcessedCount => Updates.Count(update =>
            update.Kind == PackageInstallProgressKind.SourceProcessed);

        public IEnumerable<string> ArchiveStarts => Updates
            .Where(update => update.Kind == PackageInstallProgressKind.ArchiveExtractStarted)
            .Select(update => update.Index + "/" + update.Total + ":" + Path.GetFileName(update.Path));

        public void TryWrite(PackageInstallProgressUpdate update)
        {
            Updates.Add(update);
        }
    }

    private sealed class TestableBmsFile : BMSFile
    {
        public void SetHash(string value)
        {
            hash = value;
        }

        public void SetSha256(string value)
        {
            sha256 = value;
        }

        public void SetMode(int? value)
        {
            mode = value;
        }
    }

    private sealed class TestFileMutationService : IFileMutationService
    {
        public void EnsureDirectory(string directoryPath, FileMutationOptions options = null!)
        {
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }
        }

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
        }

        public void MoveDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
        }

        public void CopyFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
        }

        public void CopyDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
        }

        public void DeleteFileDirect(string filePath, FileMutationOptions options = null!)
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }

        public void DeleteFileShell(string filePath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            DeleteFileDirect(filePath, options);
        }

        public void DeleteDirectoryDirect(string directoryPath, bool recursive, FileMutationOptions options = null!)
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive);
            }
        }

        public void DeleteDirectoryShell(string directoryPath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            DeleteDirectoryDirect(directoryPath, recursive: true, options);
        }

        public void SetTimestamps(string path, bool isDirectory, DateTime? creationTime, DateTime? lastWriteTime, FileMutationOptions options = null!)
        {
        }
    }

    private sealed class ReentrantEstimateFileMutationService : IFileMutationService
    {
        private readonly RealFileMutationService inner = new();
        private BMSLibrary library;
        private ChartPackage reentryPackage;
        private Task reentryTask;
        private int executorEntryObserved;

        internal bool ReentryCompletedDuringFilesystem { get; private set; }

        internal Exception ReentryFailure { get; private set; }

        internal void Configure(BMSLibrary library, ChartPackage reentryPackage)
        {
            this.library = library ?? throw new ArgumentNullException(nameof(library));
            this.reentryPackage = reentryPackage ?? throw new ArgumentNullException(nameof(reentryPackage));
        }

        internal void WaitForReentryCompletion()
        {
            reentryTask?.GetAwaiter().GetResult();
        }

        public void EnsureDirectory(string directoryPath, FileMutationOptions options = null!)
        {
            ObserveExecutorEntry();
            inner.EnsureDirectory(directoryPath, options);
        }

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            ObserveExecutorEntry();
            inner.MoveFile(sourcePath, destinationPath, overwrite, options);
        }

        public void MoveDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            ObserveExecutorEntry();
            inner.MoveDirectory(sourcePath, destinationPath, overwrite, options);
        }

        public void CopyFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            ObserveExecutorEntry();
            inner.CopyFile(sourcePath, destinationPath, overwrite, options);
        }

        public void CopyDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            ObserveExecutorEntry();
            inner.CopyDirectory(sourcePath, destinationPath, overwrite, options);
        }

        public void DeleteFileDirect(string filePath, FileMutationOptions options = null!)
        {
            ObserveExecutorEntry();
            inner.DeleteFileDirect(filePath, options);
        }

        public void DeleteFileShell(string filePath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            ObserveExecutorEntry();
            inner.DeleteFileShell(filePath, uiOption, recycleOption, options);
        }

        public void DeleteDirectoryDirect(string directoryPath, bool recursive, FileMutationOptions options = null!)
        {
            ObserveExecutorEntry();
            inner.DeleteDirectoryDirect(directoryPath, recursive, options);
        }

        public void DeleteDirectoryShell(string directoryPath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            ObserveExecutorEntry();
            inner.DeleteDirectoryShell(directoryPath, uiOption, recycleOption, options);
        }

        public void SetTimestamps(string path, bool isDirectory, DateTime? creationTime, DateTime? lastWriteTime, FileMutationOptions options = null!)
        {
            ObserveExecutorEntry();
            inner.SetTimestamps(path, isDirectory, creationTime, lastWriteTime, options);
        }

        private void ObserveExecutorEntry()
        {
            if (Interlocked.Exchange(ref executorEntryObserved, 1) != 0)
            {
                return;
            }

            reentryTask = Task.Run(() =>
            {
                try
                {
                    library.SearchEstimatedInstallationDirectory(reentryPackage);
                }
                catch (Exception exception)
                {
                    ReentryFailure = exception;
                }
            });
            // This is a deadlock watchdog only. The normal completion signal is
            // the reentry task itself, which is awaited after the command.
            ReentryCompletedDuringFilesystem = reentryTask.Wait(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class ReentrantCleanupFileMutationService : IFileMutationService
    {
        private readonly RealFileMutationService inner = new();
        private BMSLibrary library;
        private ChartPackage reentryPackage;
        private Task reentryTask;
        private int cleanupEntryObserved;

        internal bool ReentryCompletedDuringCleanup { get; private set; }

        internal Exception ReentryFailure { get; private set; }

        internal PendingInstalledOnlyResourceOverwriteResult ReentryResult { get; private set; }

        internal void Configure(BMSLibrary library, ChartPackage reentryPackage)
        {
            this.library = library ?? throw new ArgumentNullException(nameof(library));
            this.reentryPackage = reentryPackage ?? throw new ArgumentNullException(nameof(reentryPackage));
        }

        internal void WaitForReentryCompletion()
        {
            reentryTask?.GetAwaiter().GetResult();
        }

        public void EnsureDirectory(string directoryPath, FileMutationOptions options = null!)
            => inner.EnsureDirectory(directoryPath, options);

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
            => inner.MoveFile(sourcePath, destinationPath, overwrite, options);

        public void MoveDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
            => inner.MoveDirectory(sourcePath, destinationPath, overwrite, options);

        public void CopyFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
            => inner.CopyFile(sourcePath, destinationPath, overwrite, options);

        public void CopyDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
            => inner.CopyDirectory(sourcePath, destinationPath, overwrite, options);

        public void DeleteFileDirect(string filePath, FileMutationOptions options = null!)
        {
            ObserveCleanupEntry();
            inner.DeleteFileDirect(filePath, options);
        }

        public void DeleteFileShell(string filePath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            ObserveCleanupEntry();
            inner.DeleteFileShell(filePath, uiOption, recycleOption, options);
        }

        public void DeleteDirectoryDirect(string directoryPath, bool recursive, FileMutationOptions options = null!)
        {
            ObserveCleanupEntry();
            inner.DeleteDirectoryDirect(directoryPath, recursive, options);
        }

        public void DeleteDirectoryShell(string directoryPath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            ObserveCleanupEntry();
            inner.DeleteDirectoryShell(directoryPath, uiOption, recycleOption, options);
        }

        public void SetTimestamps(string path, bool isDirectory, DateTime? creationTime, DateTime? lastWriteTime, FileMutationOptions options = null!)
            => inner.SetTimestamps(path, isDirectory, creationTime, lastWriteTime, options);

        private void ObserveCleanupEntry()
        {
            if (Interlocked.Exchange(ref cleanupEntryObserved, 1) != 0)
            {
                return;
            }

            reentryTask = Task.Run(() =>
            {
                try
                {
                    ReentryResult = library.OverwritePendingInstalledOnlyPackagesResources([reentryPackage]);
                }
                catch (Exception exception)
                {
                    ReentryFailure = exception;
                }
            });
            // The task itself is the completion signal.  This bounded wait is
            // only a deadlock watchdog while the outer cleanup call is active.
            ReentryCompletedDuringCleanup = reentryTask.Wait(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class FailingLastWriteFileMutationService : IFileMutationService
    {
        public void EnsureDirectory(string directoryPath, FileMutationOptions options = null!)
        {
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }
        }

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
        }

        public void MoveDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
        }

        public void CopyFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
        }

        public void CopyDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
        }

        public void DeleteFileDirect(string filePath, FileMutationOptions options = null!)
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }

        public void DeleteFileShell(string filePath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            DeleteFileDirect(filePath, options);
        }

        public void DeleteDirectoryDirect(string directoryPath, bool recursive, FileMutationOptions options = null!)
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive);
            }
        }

        public void DeleteDirectoryShell(string directoryPath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            DeleteDirectoryDirect(directoryPath, recursive: true, options);
        }

        public void SetTimestamps(string path, bool isDirectory, DateTime? creationTime, DateTime? lastWriteTime, FileMutationOptions options = null!)
        {
            if (lastWriteTime.HasValue && !isDirectory)
            {
                throw new IOException("required_last_write_restore_failure");
            }

            if (!lastWriteTime.HasValue)
            {
                return;
            }

            if (isDirectory)
            {
                Directory.SetLastWriteTime(path, lastWriteTime.Value);
            }
            else
            {
                File.SetLastWriteTime(path, lastWriteTime.Value);
            }
        }
    }

    private sealed class RecordingDialogService : IBmsLibraryDialogService
    {
        public List<string> Messages { get; } = [];

        public MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.None)
        {
            Messages.Add(messageBoxText);
            return defaultResult;
        }
    }

    private sealed class ThrowOnFirstPendingRenameDialogService : IBmsLibraryDialogService
    {
        public int CallCount { get; private set; }

        public bool LaterFailureWasObserved { get; private set; }

        public MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.None)
        {
            CallCount++;
            if (CallCount == 1)
            {
                throw new InvalidOperationException("first pending rename notification failed");
            }
            LaterFailureWasObserved = true;
            return defaultResult;
        }
    }

    private sealed class FailFirstMoveFileMutationService(int failureCount) : IFileMutationService
    {
        private readonly RealFileMutationService inner = new();

        private int moveCount;

        public void EnsureDirectory(string directoryPath, FileMutationOptions options = null!)
            => inner.EnsureDirectory(directoryPath, options);

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            if (Interlocked.Increment(ref moveCount) <= failureCount)
            {
                throw new IOException("pending invalid-extension move failed");
            }
            inner.MoveFile(sourcePath, destinationPath, overwrite, options);
        }

        public void MoveDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
            => inner.MoveDirectory(sourcePath, destinationPath, overwrite, options);

        public void CopyFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
            => inner.CopyFile(sourcePath, destinationPath, overwrite, options);

        public void CopyDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
            => inner.CopyDirectory(sourcePath, destinationPath, overwrite, options);

        public void DeleteFileDirect(string filePath, FileMutationOptions options = null!)
            => inner.DeleteFileDirect(filePath, options);

        public void DeleteFileShell(string filePath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null!)
            => inner.DeleteFileShell(filePath, uiOption, recycleOption, options);

        public void DeleteDirectoryDirect(string directoryPath, bool recursive, FileMutationOptions options = null!)
            => inner.DeleteDirectoryDirect(directoryPath, recursive, options);

        public void DeleteDirectoryShell(string directoryPath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null!)
            => inner.DeleteDirectoryShell(directoryPath, uiOption, recycleOption, options);

        public void SetTimestamps(string path, bool isDirectory, DateTime? creationTime, DateTime? lastWriteTime, FileMutationOptions options = null!)
            => inner.SetTimestamps(path, isDirectory, creationTime, lastWriteTime, options);
    }

    private sealed class TrackingDisposable(bool throwOnDispose = false) : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
            if (throwOnDispose)
            {
                throw new InvalidOperationException("dispose-failed");
            }
        }
    }

    private sealed class CallbackDisposable(Action dispose) : IDisposable
    {
        private Action dispose = dispose;

        public void Dispose()
        {
            Interlocked.Exchange(ref dispose, null)?.Invoke();
        }
    }

    private sealed class FailingDeleteDirectoryFileMutationService : IFileMutationService
    {
        public void EnsureDirectory(string directoryPath, FileMutationOptions options = null!)
        {
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }
        }

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
        }

        public void MoveDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
        }

        public void CopyFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
        }

        public void CopyDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
        }

        public void DeleteFileDirect(string filePath, FileMutationOptions options = null!)
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }

        public void DeleteFileShell(string filePath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            DeleteFileDirect(filePath, options);
        }

        public void DeleteDirectoryDirect(string directoryPath, bool recursive, FileMutationOptions options = null!)
        {
            throw new IOException("required_directory_delete_failure");
        }

        public void DeleteDirectoryShell(string directoryPath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            DeleteDirectoryDirect(directoryPath, recursive: true, options);
        }

        public void SetTimestamps(string path, bool isDirectory, DateTime? creationTime, DateTime? lastWriteTime, FileMutationOptions options = null!)
        {
        }
    }

    /// <summary>Injects one path-specific deletion failure while retaining real mutation behavior.</summary>
    internal sealed class FailingDestinationDeleteFileMutationService(string failurePath) : IFileMutationService
    {
        private readonly RealFileMutationService inner = new();

        public void EnsureDirectory(string directoryPath, FileMutationOptions options = null!)
            => inner.EnsureDirectory(directoryPath, options);

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
            => inner.MoveFile(sourcePath, destinationPath, overwrite, options);

        public void MoveDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
            => inner.MoveDirectory(sourcePath, destinationPath, overwrite, options);

        public void CopyFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
            => inner.CopyFile(sourcePath, destinationPath, overwrite, options);

        public void CopyDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
            => inner.CopyDirectory(sourcePath, destinationPath, overwrite, options);

        public void DeleteFileDirect(string filePath, FileMutationOptions options = null!)
        {
            if (string.Equals(filePath, failurePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("injected-destination-delete-failure");
            }
            inner.DeleteFileDirect(filePath, options);
        }

        public void DeleteFileShell(string filePath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null!)
            => inner.DeleteFileShell(filePath, uiOption, recycleOption, options);

        public void DeleteDirectoryDirect(string directoryPath, bool recursive, FileMutationOptions options = null!)
        {
            if (string.Equals(directoryPath, failurePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("injected-destination-delete-failure");
            }
            inner.DeleteDirectoryDirect(directoryPath, recursive, options);
        }

        public void DeleteDirectoryShell(string directoryPath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null!)
            => inner.DeleteDirectoryShell(directoryPath, uiOption, recycleOption, options);

        public void SetTimestamps(string path, bool isDirectory, DateTime? creationTime, DateTime? lastWriteTime, FileMutationOptions options = null!)
            => inner.SetTimestamps(path, isDirectory, creationTime, lastWriteTime, options);
    }

    private sealed class RealFileMutationService : IFileMutationService
    {
        public void EnsureDirectory(string directoryPath, FileMutationOptions options = null!)
        {
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }
        }

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            string destinationDirectoryPath = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectoryPath))
            {
                Directory.CreateDirectory(destinationDirectoryPath);
            }
            if (overwrite && File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }
            File.Move(sourcePath, destinationPath);
        }

        public void MoveDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            if (overwrite && Directory.Exists(destinationPath))
            {
                Directory.Delete(destinationPath, recursive: true);
            }
            string destinationParentDirectoryPath = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationParentDirectoryPath))
            {
                Directory.CreateDirectory(destinationParentDirectoryPath);
            }
            Directory.Move(sourcePath, destinationPath);
        }

        public void CopyFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            string destinationParent = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationParent))
            {
                Directory.CreateDirectory(destinationParent);
            }
            File.Copy(sourcePath, destinationPath, overwrite);
        }

        public void CopyDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            Directory.CreateDirectory(destinationPath);
            foreach (string directoryPath in Directory.GetDirectories(sourcePath, "*", System.IO.SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(directoryPath.Replace(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase));
            }
            foreach (string filePath in Directory.GetFiles(sourcePath, "*", System.IO.SearchOption.AllDirectories))
            {
                string destinationFilePath = filePath.Replace(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationFilePath)!);
                File.Copy(filePath, destinationFilePath, overwrite);
            }
        }

        public void DeleteFileDirect(string filePath, FileMutationOptions options = null!)
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }

        public void DeleteFileShell(string filePath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            DeleteFileDirect(filePath, options);
        }

        public void DeleteDirectoryDirect(string directoryPath, bool recursive, FileMutationOptions options = null!)
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive);
            }
        }

        public void DeleteDirectoryShell(string directoryPath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            DeleteDirectoryDirect(directoryPath, recursive: true, options);
        }

        public void SetTimestamps(string path, bool isDirectory, DateTime? creationTime, DateTime? lastWriteTime, FileMutationOptions options = null!)
        {
            if (lastWriteTime.HasValue)
            {
                if (isDirectory)
                {
                    Directory.SetLastWriteTime(path, lastWriteTime.Value);
                }
                else
                {
                    File.SetLastWriteTime(path, lastWriteTime.Value);
                }
            }
        }
    }

}
