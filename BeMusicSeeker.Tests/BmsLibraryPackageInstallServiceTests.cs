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

    /// <summary>
    /// Observes the resource index before the second package is staged and verifies that
    /// successful package directories are published only once at the operation terminal.
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PendingResourcePackages_InstallThroughLibraryAndPreserveEarlierResourceSnapshot(bool force)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb((songDbPath, root) =>
        {
            string existingDirectory = Path.Combine(root, "Existing");
            string installationRoot = Path.Combine(root, "Installed");
            string[] sources = [Path.Combine(root, "Pending1"), Path.Combine(root, "Pending2")];
            string[] names = ["first.bms", "second.bms"];
            string body = "#BPM 120\r\n#WAV01 shared.wav\r\n#BMP01 picture.png\r\n#BMP02 clip.mp4\r\n#00111:01\r\n";
            var packages = new List<ChartPackage>();
            for (int i = 0; i < sources.Length; i++)
            {
                string path = CreateBmsFile(sources[i], names[i], "#TITLE P0Package" + i + "\r\n" + body);
                // These bytes are only enumerated, existence-checked and moved, never decoded.
                File.WriteAllBytes(Path.Combine(sources[i], "shared.wav"), [1, 2, 3]);
                File.WriteAllBytes(Path.Combine(sources[i], "picture.png"), [4, 5]);
                File.WriteAllBytes(Path.Combine(sources[i], "clip.mp4"), [6, 7]);
                Directory.CreateDirectory(Path.Combine(sources[i], "sub"));
                File.WriteAllBytes(Path.Combine(sources[i], "sub", "extra.ogg"), [8, 9]);
                string destination = Path.Combine(installationRoot, "Estimated" + i);
                var entries = new List<PackageChartEntry>
                {
                    ChartPackageTestExtensions.CreateEntryWithInstallDestination(
                        BMSFile.CreateBMSFileFromFile(path), destination)
                };
                if (i == 0)
                {
                    string alternate = CreateBmsFile(sources[i], "alternate.bms", "#TITLE P0Alternate\r\n" + body);
                    entries.Add(ChartPackageTestExtensions.CreateEntryWithInstallDestination(
                        BMSFile.CreateBMSFileFromFile(alternate), destination));
                }
                ChartPackage package = ChartPackageTestExtensions.CreatePackage(entries.ToArray());
                package.path = sources[i];
                package.delete_parent = false;
                packages.Add(package);
            }
            string existingPath = CreateBmsFile(existingDirectory, "existing.bms", "#TITLE Existing\r\n" + body);
            if (force)
            {
                // Force mode must still install real resources when one chart is already owned.
                File.Copy(Path.Combine(sources[0], names[0]), existingPath, overwrite: true);
            }
            File.WriteAllBytes(Path.Combine(existingDirectory, "shared.wav"), [1, 2, 3]);
            BMSFile existing = BMSFile.CreateBMSFileFromFile(existingPath);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(existing.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
            }
            var fileMutations = new RealFileMutationService();
            var library = new TestBmsLibrary(songDbPath, null, null, fileMutations,
                new RecordingDialogService(), new TestUiScheduler(() => null!),
                () => new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = false,
                    BMSInstallDir = installationRoot,
                    FolderNameFormat = "%TITLE%",
                    DeletePendingPackageSourceAfterInstall = false,
                    EnableSmartComponentOverwrite = false,
                    KeepSmartOverwriteProtectedFilesByRenaming = false
                })
            {
                BMSFiles = [existing],
                BmsonSongs = [],
                ChartPackagesPending = CreatePackageCollection(packages),
                ChartPackagesInstalled = CreatePackageCollection([])
            };
            uint shared = ChartResourceKeyHash.GetLookupHash("shared");
            uint image = ChartResourceKeyHash.GetLookupHash("picture");
            uint movie = ChartResourceKeyHash.GetLookupHash("clip");
            LibraryResourceIndexOwner owner = LibraryResourceIndexTestSupport.GetOwner(library);
            owner.Replace(LibraryResourceIndex.CreateFromNativeCanonicalArrays(
                [existingDirectory], [[shared]], [[]], [[]], [[shared]], [[]], [[]],
                new Dictionary<uint, string[]> { [shared] = [existingDirectory] }, new Dictionary<uint, string[]>(), new Dictionary<uint, string[]>()));
            LibraryResourceIndexSnapshot before = owner.CaptureSnapshot();
            LibraryResourceIndexSnapshot? beforeSecondPackage = null;
            string[] audioAtSecondPackage = [];
            string[] imageAtSecondPackage = [];
            string[] movieAtSecondPackage = [];
            fileMutations.BeforeCopy = sourcePath =>
            {
                if (beforeSecondPackage.HasValue
                    || !(string.Equals(sourcePath, sources[1], StringComparison.OrdinalIgnoreCase)
                        || sourcePath.StartsWith(sources[1] + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }
                // Observe actual lookup results now, not just a snapshot inspected after the batch.
                // Assertions stay outside the executor so they cannot be caught as mutation failures.
                LibraryResourceIndexSnapshot snapshot = owner.CaptureSnapshot();
                beforeSecondPackage = snapshot;
                audioAtSecondPackage = snapshot.DirectoryLookupCache.GetDirectoriesByAudioRelativeHash(shared).ToArray();
                imageAtSecondPackage = snapshot.DirectoryLookupCache.GetDirectoriesByImageRelativeHash(image).ToArray();
                movieAtSecondPackage = snapshot.DirectoryLookupCache.GetDirectoriesByMovieRelativeHash(movie).ToArray();
            };

            LibraryMutationSessionReceipt sessionReceipt;
            if (force)
            {
                sessionReceipt = library.ForceInstallPendingPackagesWithReceipt(
                    packages, approveNormalInstallOverride: true, approvedNormalInstallOverridePackages: null);
            }
            else
            {
                PendingInstallBatchResult result = library.InstallPendingPackagesToEstimatedDestinationsWithReceipt(packages);
                Assert.AreEqual(0, result.FailedPackages.Count);
                sessionReceipt = result.SessionReceipt;
            }
            Assert.IsTrue(sessionReceipt.DurableCommit);
            Assert.AreEqual(new LibraryMutationSessionApplyCounts
            {
                InstalledTargetApplyCount = 1,
                ReverseLookupApplyCount = 1,
                Lr2SyncCount = 1,
                RequiredPublicationCount = 1
            }, sessionReceipt.ApplyCounts);
            Assert.IsFalse(sessionReceipt.HasRequiredFailure);
            Assert.IsFalse(sessionReceipt.HasDurableFinalizationFailure);

            string[] installedPaths = names.Select(name => library.BMSFiles.Single(file =>
                Path.GetFileName(file.path) == name).path).ToArray();
            string[] destinations = installedPaths.Select(path => Path.GetDirectoryName(path)!).ToArray();
            Assert.AreEqual(2, destinations.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.IsTrue(library.BMSFiles.Any(file => Path.GetFileName(file.path) == "alternate.bms"));
            foreach (string destination in destinations)
            {
                CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(destination, "shared.wav")));
                CollectionAssert.AreEqual(new byte[] { 8, 9 }, File.ReadAllBytes(Path.Combine(destination, "sub", "extra.ogg")));
                Assert.IsTrue(File.Exists(Path.Combine(destination, "picture.png")));
                Assert.IsTrue(File.Exists(Path.Combine(destination, "clip.mp4")));
            }
            LibraryResourceIndexSnapshot intermediate = beforeSecondPackage
                ?? throw new AssertFailedException("The second package must reach real filesystem staging.");
            Assert.AreEqual(before.Generation, intermediate.Generation);
            CollectionAssert.AreEqual(new[] { existingDirectory }, audioAtSecondPackage);
            Assert.AreEqual(0, imageAtSecondPackage.Length);
            Assert.AreEqual(0, movieAtSecondPackage.Length);

            LibraryResourceIndexSnapshot after = owner.CaptureSnapshot();
            Assert.AreEqual(before.Generation + 1, after.Generation);
            Assert.AreSame(before.DirectoryLookupCache, intermediate.DirectoryLookupCache);
            Assert.AreNotSame(intermediate.DirectoryLookupCache, after.DirectoryLookupCache);
            CollectionAssert.AreEqual(new[] { existingDirectory },
                intermediate.DirectoryLookupCache.GetDirectoriesByAudioRelativeHash(shared).ToArray());
            Assert.AreEqual(0,
                intermediate.DirectoryLookupCache.GetDirectoriesByImageRelativeHash(image).Count);
            Assert.AreEqual(0,
                intermediate.DirectoryLookupCache.GetDirectoriesByMovieRelativeHash(movie).Count);
            CollectionAssert.AreEquivalent(new[] { existingDirectory }.Concat(destinations).ToArray(),
                after.DirectoryLookupCache.GetDirectoriesByAudioRelativeHash(shared).ToArray());
            CollectionAssert.AreEquivalent(destinations, after.DirectoryLookupCache.GetDirectoriesByImageRelativeHash(image).ToArray());
            CollectionAssert.AreEquivalent(destinations, after.DirectoryLookupCache.GetDirectoriesByMovieRelativeHash(movie).ToArray());
            CollectionAssert.AreEqual(new[] { existingDirectory }, before.DirectoryLookupCache.GetDirectoriesByAudioRelativeHash(shared).ToArray());
            Assert.AreEqual(0, before.DirectoryLookupCache.GetDirectoriesByImageRelativeHash(image).Count);
            Assert.AreEqual(0, before.DirectoryLookupCache.GetDirectoriesByMovieRelativeHash(movie).Count);
            using var readback = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(4, readback.Table<LR2SongDB.song>().Count());
            Assert.AreEqual(1, readback.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ?;", existingPath));
            foreach (string installedPath in installedPaths)
            {
                Assert.AreEqual(1, readback.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ?;", installedPath));
            }
            Assert.AreSame(existing, library.BMSFiles.Single(file =>
                string.Equals(file.path, existingPath, StringComparison.OrdinalIgnoreCase)));
        });
    }

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
            var dialogService = new RecordingDialogService();
            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                new RealFileMutationService(),
                dialogService)
            {
                ChartPackagesPending = CreatePackageCollection([pendingPackage])
            };
            library.ResetCatalogPathConvergence(CatalogPathConvergenceBlockReason.StartupFileScanDisabled);

            library.RemovePendingPackages([pendingPackage]);

            Assert.AreEqual(0, library.ChartPackagesPending.Count);
            Assert.IsFalse(Directory.Exists(packageDirectoryPath));
            Assert.AreEqual(0, dialogService.Messages.Count);
        });
    }

    [TestMethod]
    public void RemovePendingPackagesAll_UnconvergedCatalogStillClearsPendingWithoutWarning()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            var dialogService = new RecordingDialogService();
            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                new RealFileMutationService(),
                dialogService)
            {
                ChartPackagesPending = CreatePackageCollection([
                    new ChartPackage { path = Path.Combine(tempRootPath, "pending-clear-first") },
                    new ChartPackage { path = Path.Combine(tempRootPath, "pending-clear-second") }])
            };
            library.ResetCatalogPathConvergence(CatalogPathConvergenceBlockReason.StartupFileScanDisabled);

            library.RemovePendingPackagesAll();

            Assert.AreEqual(0, library.ChartPackagesPending.Count);
            Assert.AreEqual(0, dialogService.Messages.Count);
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
    public void RenameBMSFilesExtensions_UnregistersOnlySuccessfulChartsAndPreservesHashOwner()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string failedDirectoryPath = Path.Combine(tempRootPath, "normal-invalid-extension-failed");
            string selectedSharedDirectoryPath = Path.Combine(tempRootPath, "normal-invalid-extension-selected-shared");
            string survivingSharedDirectoryPath = Path.Combine(tempRootPath, "normal-invalid-extension-surviving-shared");
            string lastOwnerDirectoryPath = Path.Combine(tempRootPath, "normal-invalid-extension-last-owner");
            string failedSourcePath = CreateBmsFile(failedDirectoryPath, "failed.bms", "#TITLE Failed target");
            string selectedSharedSourcePath = CreateBmsFile(
                selectedSharedDirectoryPath,
                "selected-shared.bms",
                "#TITLE Shared owner");
            string survivingSharedSourcePath = Path.Combine(survivingSharedDirectoryPath, "surviving-shared.bms");
            Directory.CreateDirectory(survivingSharedDirectoryPath);
            File.Copy(selectedSharedSourcePath, survivingSharedSourcePath);
            string lastOwnerSourcePath = CreateBmsFile(
                lastOwnerDirectoryPath,
                "last-owner.bms",
                "#TITLE Last owner");
            string failedDestinationPath = Path.Combine(failedDirectoryPath, "failed.bme");
            string selectedSharedDestinationPath = Path.Combine(selectedSharedDirectoryPath, "selected-shared.bme");
            string lastOwnerDestinationPath = Path.Combine(lastOwnerDirectoryPath, "last-owner.bme");
            BMSFile failed = BMSFile.CreateBMSFileFromFile(failedSourcePath);
            BMSFile selectedShared = BMSFile.CreateBMSFileFromFile(selectedSharedSourcePath);
            BMSFile survivingShared = BMSFile.CreateBMSFileFromFile(survivingSharedSourcePath);
            BMSFile lastOwner = BMSFile.CreateBMSFileFromFile(lastOwnerSourcePath);
            Assert.AreEqual(selectedShared.hash, survivingShared.hash);

            using (var seedSongDb = new LR2SongDBExtended(songDbPath))
            {
                foreach (BMSFile file in new[] { failed, selectedShared, survivingShared, lastOwner })
                {
                    seedSongDb.InsertOrReplace(file.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                }
            }

            var dialogService = new BmsLibraryInitializationTestSupport.RecordingDialogService();
            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                new FailFirstMoveFileMutationService(1),
                dialogService);
            OwnedChartCollectionTestSupport.SetLibraryFilesWithoutNotification(
                library,
                [failed, selectedShared, survivingShared, lastOwner]);

            BMSLibrary.InstalledPrimaryHashWarmupResult initialPrimary =
                library.WarmInstalledPrimaryHashLookup("normal_invalid_extension_unregister_warmup");
            Assert.IsFalse(initialPrimary.FullDirectoryLookupInitialized);
            OwnedChartHashIndexVersionedSnapshot initialHash = library.GetOwnedChartHashIndexSnapshot();
            InstalledChartLookupIndexSnapshot initialInstalled =
                OwnedChartCollectionTestSupport.InvokeCreateInstalledChartLookupSnapshot(library);
            PlaylistLibraryResolveIndexSnapshot initialPlaylist = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool initialPlaylistCacheHit,
                out int initialPlaylistStaleRetries);
            Assert.IsFalse(initialPlaylistCacheHit);
            Assert.AreEqual(0, initialPlaylistStaleRetries);
            Assert.IsTrue(initialHash.ContainsMd5(failed.hash));
            Assert.IsTrue(initialHash.ContainsMd5(selectedShared.hash));
            Assert.IsTrue(initialHash.ContainsMd5(lastOwner.hash));
            Assert.IsTrue(initialInstalled.ContainsPrimaryHash(failed.hash));
            Assert.IsTrue(initialInstalled.ContainsPrimaryHash(selectedShared.hash));
            Assert.IsTrue(initialInstalled.ContainsPrimaryHash(lastOwner.hash));
            CollectionAssert.AreEquivalent(
                new[] { survivingSharedDirectoryPath, selectedSharedDirectoryPath },
                initialInstalled.GetDistinctDirectoriesByPrimaryHash(selectedShared.hash).ToArray());
            Assert.IsTrue(initialPlaylist.ContainsCandidate(LibraryChartKind.Bms, failedSourcePath));
            Assert.IsTrue(initialPlaylist.ContainsCandidate(LibraryChartKind.Bms, selectedSharedSourcePath));
            Assert.IsTrue(initialPlaylist.ContainsCandidate(LibraryChartKind.Bms, survivingSharedSourcePath));
            Assert.IsTrue(initialPlaylist.ContainsCandidate(LibraryChartKind.Bms, lastOwnerSourcePath));

            List<string> hashWork = [];
            List<string> playlistWork = [];
            List<string> installedWork = [];
            library.OwnedChartHashIndexStoreWorkObserver = hashWork.Add;
            library.PlaylistLibraryResolveIndexStoreWorkObserver = playlistWork.Add;
            library.InstalledChartLookupStoreWorkObserver = installedWork.Add;

            library.RenameBMSFilesExtensions(
                [
                    ChartFileProjection.FromBmsFile(failed),
                    ChartFileProjection.FromBmsFile(selectedShared),
                    ChartFileProjection.FromBmsFile(lastOwner)
                ],
                ".bme",
                unregister: true);

            Assert.AreEqual(1, dialogService.Calls.Count);
            Assert.IsTrue(File.Exists(failedSourcePath));
            Assert.IsFalse(File.Exists(failedDestinationPath));
            Assert.IsFalse(File.Exists(selectedSharedSourcePath));
            Assert.IsTrue(File.Exists(selectedSharedDestinationPath));
            Assert.IsFalse(File.Exists(lastOwnerSourcePath));
            Assert.IsTrue(File.Exists(lastOwnerDestinationPath));
            Assert.IsTrue(File.Exists(survivingSharedSourcePath));

            Assert.IsTrue(library.BMSFiles.Any(file =>
                string.Equals(file.path, failedSourcePath, StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(library.BMSFiles.Any(file =>
                string.Equals(file.path, survivingSharedSourcePath, StringComparison.OrdinalIgnoreCase)));
            Assert.IsFalse(library.BMSFiles.Any(file =>
                string.Equals(file.path, selectedSharedSourcePath, StringComparison.OrdinalIgnoreCase)));
            Assert.IsFalse(library.BMSFiles.Any(file =>
                string.Equals(file.path, lastOwnerSourcePath, StringComparison.OrdinalIgnoreCase)));

            using (var verifySongDb = new LR2SongDBExtended(songDbPath))
            {
                Assert.AreEqual(1, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ?;", failedSourcePath));
                Assert.AreEqual(1, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ?;", survivingSharedSourcePath));
                Assert.AreEqual(0, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ?;", selectedSharedSourcePath));
                Assert.AreEqual(0, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ?;", lastOwnerSourcePath));
            }

            OwnedChartHashIndexVersionedSnapshot updatedHash = library.GetOwnedChartHashIndexSnapshot();
            OwnedChartHashIndexVersionedSnapshot cachedHash = library.GetOwnedChartHashIndexSnapshot();
            InstalledChartLookupIndexSnapshot updatedInstalled =
                OwnedChartCollectionTestSupport.InvokeCreateInstalledChartLookupSnapshot(library);
            InstalledChartLookupIndexSnapshot cachedInstalled =
                OwnedChartCollectionTestSupport.InvokeCreateInstalledChartLookupSnapshot(library);
            BMSLibrary.InstalledPrimaryHashWarmupResult updatedPrimary =
                library.WarmInstalledPrimaryHashLookup("normal_invalid_extension_unregister_warmup");
            PlaylistLibraryResolveIndexSnapshot updatedPlaylist = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool updatedPlaylistCacheHit,
                out int updatedPlaylistStaleRetries);
            PlaylistLibraryResolveIndexSnapshot cachedPlaylist = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool cachedPlaylistCacheHit,
                out int cachedPlaylistStaleRetries);

            Assert.AreSame(updatedHash, cachedHash);
            Assert.AreSame(updatedInstalled, cachedInstalled);
            Assert.AreEqual("cached", updatedPrimary.Status);
            Assert.AreEqual(0L, updatedPrimary.BuildMs);
            Assert.IsTrue(updatedPlaylistCacheHit);
            Assert.AreEqual(0, updatedPlaylistStaleRetries);
            Assert.IsTrue(cachedPlaylistCacheHit);
            Assert.AreEqual(0, cachedPlaylistStaleRetries);
            Assert.AreSame(updatedPlaylist, cachedPlaylist);
            Assert.IsTrue(updatedHash.ContainsMd5(failed.hash));
            Assert.IsTrue(updatedHash.ContainsMd5(selectedShared.hash));
            Assert.IsFalse(updatedHash.ContainsMd5(lastOwner.hash));
            Assert.IsTrue(updatedInstalled.ContainsPrimaryHash(failed.hash));
            Assert.IsTrue(updatedInstalled.ContainsPrimaryHash(selectedShared.hash));
            Assert.IsFalse(updatedInstalled.ContainsPrimaryHash(lastOwner.hash));
            CollectionAssert.AreEquivalent(
                new[] { survivingSharedDirectoryPath },
                updatedInstalled.GetDistinctDirectoriesByPrimaryHash(selectedShared.hash).ToArray());
            Assert.AreEqual(0, updatedInstalled.GetDistinctDirectoriesByPrimaryHash(lastOwner.hash).Count);
            Assert.IsTrue(updatedPlaylist.ContainsCandidate(LibraryChartKind.Bms, failedSourcePath));
            Assert.IsFalse(updatedPlaylist.ContainsCandidate(LibraryChartKind.Bms, selectedSharedSourcePath));
            Assert.IsTrue(updatedPlaylist.ContainsCandidate(LibraryChartKind.Bms, survivingSharedSourcePath));
            Assert.IsFalse(updatedPlaylist.ContainsCandidate(LibraryChartKind.Bms, lastOwnerSourcePath));
            Assert.IsTrue(initialHash.ContainsMd5(lastOwner.hash));
            Assert.IsTrue(initialInstalled.ContainsPrimaryHash(lastOwner.hash));
            Assert.IsTrue(initialPlaylist.ContainsCandidate(LibraryChartKind.Bms, lastOwnerSourcePath));
            Assert.AreEqual(0, hashWork.Count(operation => operation == "owned_hash_source_enumeration"));
            Assert.AreEqual(
                0,
                playlistWork.Count(operation => operation == "playlist_resolve_source_enumeration"
                    || operation == "playlist_resolve_full_root_enumeration"));
            Assert.IsTrue(
                installedWork.Count(operation => operation == "installed_primary_hash_count_update") > 0,
                "通常拡張子修正の登録解除でinstalled lookup差分更新を観測できませんでした。");
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
                new TestUiScheduler(() => null!),
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
                new TestUiScheduler(() => null!),
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
            Assert.IsTrue(result.SessionReceipt.DurableCommit);
            Assert.IsFalse(result.SessionReceipt.HasRequiredFailure);
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
                new TestUiScheduler(() => null!),
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
            Assert.IsTrue(result.SessionReceipt.DurableCommit);
            Assert.IsFalse(result.FailedPackages.Contains(secondPackage));
        });
    }

    /// <summary>
    /// 複数 package の physical prepare 後に canonical transaction が失敗しても、
    /// package/catalog/resource publication を部分確定せず recovery candidate を一つの session terminal に保持します。
    /// </summary>
    [TestMethod]
    public void InstallPendingPackagesToEstimatedDestinations_CanonicalApplyFailureKeepsPreparedTargetsWithoutPartialPublication()
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
            string firstChartPath = CreateBmsFileWithResources(
                firstSourceDirectoryPath, "first.bms", "#TITLE Estimated Prefix First", "first-resource");
            string secondChartPath = CreateBmsFileWithResources(
                secondSourceDirectoryPath, "second.bms", "#TITLE Estimated Prefix Second", "second-resource");
            string thirdChartPath = CreateBmsFileWithResources(
                thirdSourceDirectoryPath, "third.bms", "#TITLE Estimated Prefix Third", "third-resource");
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
                new RealFileMutationService(),
                new RecordingDialogService(),
                new TestUiScheduler(() => null!),
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

            LibraryResourceIndexOwner resourceOwner = LibraryResourceIndexTestSupport.GetOwner(library);
            resourceOwner.Replace(LibraryResourceIndex.CreateFromNativeCanonicalArrays(
                [], [], [], [], [], [], [], new Dictionary<uint, string[]>(), new Dictionary<uint, string[]>(), new Dictionary<uint, string[]>()));
            LibraryResourceIndexSnapshot before = resourceOwner.CaptureSnapshot();

            PendingInstallBatchResult result = library.InstallPendingPackagesToEstimatedDestinationsWithReceipt(
                [firstPackage, secondPackage, thirdPackage]);

            Assert.IsFalse(result.SessionReceipt.DurableCommit);
            Assert.AreEqual(new LibraryMutationSessionApplyCounts
            {
                InstalledTargetApplyCount = 1
            }, result.SessionReceipt.ApplyCounts);
            Assert.IsTrue(result.SessionReceipt.HasRequiredFailure);
            Assert.IsNotNull(result.SessionReceipt.ApplyFailure);
            Assert.IsFalse(result.SessionReceipt.ManualRecoveryRequired);
            Assert.AreEqual(0, result.FailedPackages.Count);
            Assert.AreEqual(3, library.ChartPackagesPending.Count);
            Assert.AreEqual(0, library.ChartPackagesInstalled.Count);
            string firstDestinationChartPath = Path.Combine(firstDestinationDirectoryPath, "first.bms");
            string thirdDestinationChartPath = Path.Combine(thirdDestinationDirectoryPath, "third.bms");
            Assert.IsTrue(File.Exists(firstDestinationChartPath));
            Assert.IsTrue(File.Exists(secondDestinationChartPath));
            Assert.IsTrue(File.Exists(thirdDestinationChartPath));
            CollectionAssert.IsSubsetOf(
                new[] { firstDestinationChartPath, secondDestinationChartPath, thirdDestinationChartPath },
                result.SessionReceipt.CandidatePaths.ToArray());

            LibraryResourceIndexSnapshot after = resourceOwner.CaptureSnapshot();
            Assert.AreEqual(before.Generation, after.Generation);
            Assert.AreEqual(0, after.DirectoryLookupCache.Count);
            AssertResourceCandidates(after, "first-resource");
            AssertResourceCandidates(after, "second-resource");
            AssertResourceCandidates(after, "third-resource");
            foreach (string resourceKey in new[] { "first-resource", "second-resource", "third-resource" })
            {
                AssertResourceCandidates(before, resourceKey);
            }
            Assert.AreEqual(0, before.DirectoryLookupCache.Count);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 },
                File.ReadAllBytes(Path.Combine(firstDestinationDirectoryPath, "first-resource.wav")));
            Assert.IsTrue(File.Exists(Path.Combine(firstDestinationDirectoryPath, "first-resource.png")));
            Assert.IsTrue(File.Exists(Path.Combine(firstDestinationDirectoryPath, "first-resource.mp4")));
            Assert.IsTrue(File.Exists(thirdChartPath));

            using var verifySongDb = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(
                0,
                verifySongDb.ExecuteScalar<int>(
                    "SELECT COUNT(1) FROM song WHERE path = ?;",
                    Path.Combine(firstDestinationDirectoryPath, "first.bms")));
            Assert.AreEqual(
                0,
                verifySongDb.ExecuteScalar<int>(
                    "SELECT COUNT(1) FROM song WHERE path = ?;",
                    secondDestinationChartPath));
            Assert.AreEqual(
                0,
                verifySongDb.ExecuteScalar<int>(
                    "SELECT COUNT(1) FROM song WHERE path = ?;",
                    Path.Combine(thirdDestinationDirectoryPath, "third.bms")));
            Assert.AreEqual(firstSourceDirectoryPath, firstPackage.path, ignoreCase: true);
            Assert.AreEqual(secondSourceDirectoryPath, secondPackage.path, ignoreCase: true);
            Assert.AreEqual(thirdSourceDirectoryPath, thirdPackage.path, ignoreCase: true);
        });
    }

    /// <summary>
    /// 後続 duplicate 判定は DB commit ではなく先行 physical success overlay を使い、
    /// canonical apply が後で失敗しても未実行予約を成功扱いしません。
    /// </summary>
    [TestMethod]
    public void InstallPendingPackagesToEstimatedDestinations_UsesPhysicalSuccessOverlayBeforeCanonicalApplyFailure()
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
                new TestUiScheduler(() => null!),
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

            Assert.IsFalse(result.SessionReceipt.DurableCommit);
            Assert.IsTrue(result.SessionReceipt.HasRequiredFailure);
            Assert.IsNotNull(result.SessionReceipt.ApplyFailure);
            Assert.IsFalse(result.SessionReceipt.ManualRecoveryRequired);
            Assert.AreEqual(0, result.FailedPackages.Count);
            Assert.AreEqual(2, library.ChartPackagesPending.Count);
            Assert.AreEqual(0, library.ChartPackagesInstalled.Count);
            Assert.IsTrue(File.Exists(firstDestinationChartPath));
            Assert.IsFalse(File.Exists(secondDestinationChartPath));
            Assert.IsTrue(File.Exists(firstChartPath));
            Assert.IsTrue(File.Exists(secondChartPath));
            CollectionAssert.Contains(result.SessionReceipt.CandidatePaths.ToArray(), firstDestinationChartPath);

            using var verifySongDb = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ?;", firstDestinationChartPath));
            Assert.AreEqual(0, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ?;", secondDestinationChartPath));
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
            using (var seedSongDb = new LR2SongDBExtended(songDbPath))
            {
                seedSongDb.InsertOrReplace(
                    CatalogMaintenanceWriteRequest.CreateBmsonPersistenceCopy(installedBmson),
                    typeof(LR2SongDBExtended.bmson_song));
            }
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
            Exception? packageEntryInspectionFailure = null;
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
    /// 先行 package の physical success 後に source cleanup だけが失敗しても、
    /// 後続 package が同じ推定導入 session で固有譜面または resource-only として
    /// 再評価され、operation terminal に cleanup failure を保持することを検証します。
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void InstallPendingPackagesToEstimatedDestinations_ReevaluatesResourceOnlyAfterEarlierPhysicalSuccess(
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
            ChartFile? uniqueSourceChart = includeUniqueChart
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
                new TestUiScheduler(() => null!),
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
            Assert.IsTrue(result.SessionReceipt.DurableCommit);
            Assert.IsTrue(result.SessionReceipt.CompletedWithCleanupFailure);
            Assert.IsFalse(result.SessionReceipt.HasRequiredFailure);
            Assert.IsFalse(result.SessionReceipt.HasDurableFinalizationFailure);
            Assert.IsNotNull(result.SessionReceipt.CleanupFailure);
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

    /// <summary>
    /// resource-only / cleanup-only の
    /// canonical install-row apply が失敗した場合、prepare 件数を成功として公開しません。
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void OverwritePendingInstalledOnlyPackagesResources_CanonicalFailureDoesNotPublishSuccessCounts(
        bool cleanupOnly)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb((songDbPath, root) =>
        {
            string installedDirectory = Path.Combine(root, "installed");
            string pendingDirectory = Path.Combine(root, "pending");
            const string chartBody = "#TITLE Resource Overwrite Failure";
            string installedChartPath = CreateBmsFile(installedDirectory, "chart.bms", chartBody);
            string pendingChartPath = CreateBmsFile(pendingDirectory, "chart.bms", chartBody);
            BMSFile installedChart = BMSFile.CreateBMSFileFromFile(installedChartPath);
            BMSFile pendingChart = BMSFile.CreateBMSFileFromFile(pendingChartPath);
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingChart]);
            pendingPackage.path = pendingDirectory;
            pendingPackage.delete_parent = false;
            string pendingResourcePath = Path.Combine(pendingDirectory, "sound.wav");
            string installedResourcePath = Path.Combine(installedDirectory, "sound.wav");
            if (!cleanupOnly)
            {
                File.WriteAllBytes(pendingResourcePath, [1, 2, 3]);
            }
            using (var seedSongDb = new LR2SongDBExtended(songDbPath))
            {
                seedSongDb.CreateTable<LR2SongDBExtended.install>();
                seedSongDb.InsertOrReplace(installedChart, typeof(LR2SongDB.song));
                seedSongDb.InsertOrReplace(pendingPackage, typeof(LR2SongDBExtended.install));
                seedSongDb.Execute(
                    "CREATE TRIGGER fail_resource_overwrite_install_delete BEFORE DELETE ON install "
                    + "BEGIN SELECT RAISE(ABORT, 'resource-overwrite-canonical-marker'); END;");
            }
            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                new RealFileMutationService(),
                new RecordingDialogService(),
                new TestUiScheduler(() => null!),
                () => new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = false,
                    DeletePendingPackageSourceAfterInstall = true
                })
            {
                BMSFiles = [installedChart],
                ChartPackagesPending = CreatePackageCollection([pendingPackage]),
                ChartPackagesInstalled = CreatePackageCollection([])
            };

            PendingInstalledOnlyResourceOverwriteResult result =
                library.OverwritePendingInstalledOnlyPackagesResources([pendingPackage]);

            Assert.AreEqual(1, result.Processed);
            Assert.AreEqual(0, result.SucceededInstall);
            Assert.AreEqual(0, result.SucceededCleanupOnly);
            Assert.AreEqual(0, result.Failed, "canonical failure を架空の item failure に置き換えません。");
            Assert.IsFalse(result.HasDurableCommit);
            Assert.IsNotNull(result.SessionReceipt);
            Assert.IsTrue(result.SessionReceipt.HasRequiredFailure);
            Assert.IsNotNull(result.SessionReceipt.ApplyFailure);
            StringAssert.Contains(result.SessionReceipt.ApplyFailure.Message, "resource-overwrite-canonical-marker");
            Assert.AreSame(pendingPackage, library.ChartPackagesPending.Single());
            Assert.AreEqual(0, library.ChartPackagesInstalled.Count);
            Assert.IsTrue(File.Exists(pendingChartPath));
            if (!cleanupOnly)
            {
                Assert.IsTrue(File.Exists(pendingResourcePath));
                Assert.IsTrue(File.Exists(installedResourcePath));
                CollectionAssert.Contains(result.RecoveryPaths.ToArray(), installedResourcePath);
            }
            using var verifySongDb = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1, verifySongDb.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM install WHERE path = ?;", pendingDirectory));
            Assert.AreEqual(1, verifySongDb.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM song WHERE path = ?;", installedChartPath));
        });
    }

    /// <summary>
    /// DST 通知は outer file mutation lease 解放後に行い、
    /// 購読者の例外を required finalization failure に変換しません。
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void OverwritePendingInstalledOnlyPackagesResources_ReleasesOuterWritersBeforeEstimatedInstallPublication(
        bool subscriberThrows)
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
            using (var seedSongDb = new LR2SongDBExtended(songDbPath))
            {
                seedSongDb.InsertOrReplace(
                    CatalogMaintenanceWriteRequest.CreateBmsonPersistenceCopy(installedBmson),
                    typeof(LR2SongDBExtended.bmson_song));
            }
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
            Exception? packageEntryInspectionFailure = null;
            pendingEntry.PropertyChanged += (_, _) =>
            {
                try
                {
                    Assert.IsFalse(library.IsWriteLockHeldPendingInstallCharts);
                    Assert.IsFalse(library.IsWriteLockHeldInitializeBMSFiles);
                    using LibraryFileMutationLease lease = library.TryBeginLibraryFileMutation(
                        "test_notification_after_resource_overwrite",
                        showMessage: false);
                    Assert.IsNotNull(lease, "DST 通知中に outer file mutation lease が保持されています。");
                    Assert.AreEqual(0, library.ChartPackagesPending.Count);
                    Assert.AreEqual(1, library.ChartPackagesInstalled.Count);
                    Assert.AreSame(installedBmson, library.BmsonSongs.Single());
                    using var notifiedSongDb = new LR2SongDBExtended(songDbPath);
                    Assert.AreEqual(
                        1,
                        notifiedSongDb.ExecuteScalar<int>(
                            "SELECT COUNT(1) FROM bmson_song WHERE path = ?;",
                            installedBmsonPath));
                }
                catch (Exception exception)
                {
                    packageEntryInspectionFailure = exception;
                }
                packageEntryNotificationObserved = true;
                if (subscriberThrows)
                {
                    throw new InvalidOperationException("resource-overwrite-subscriber-marker");
                }
            };

            PendingInstalledOnlyResourceOverwriteResult result =
                library.OverwritePendingInstalledOnlyPackagesResources([pendingPackage]);

            Assert.AreEqual(1, result.SucceededInstall);
            Assert.AreEqual(0, result.Failed);
            Assert.IsTrue(result.HasDurableCommit);
            Assert.IsFalse(result.HasDurableFinalizationFailure);
            Assert.IsNotNull(result.SessionReceipt);
            Assert.IsFalse(result.SessionReceipt.HasRequiredFailure);
            Assert.AreSame(pendingEntry, pendingPackage.ChartEntries.Single());
            Assert.IsTrue(packageEntryNotificationObserved);
            Assert.IsNull(
                packageEntryInspectionFailure,
                packageEntryInspectionFailure?.ToString());
            Assert.AreEqual(0, library.ChartPackagesPending.Count);
            Assert.AreEqual(1, library.ChartPackagesInstalled.Count);
            Assert.IsTrue(File.Exists(Path.Combine(destinationDirectoryPath, "sound.wav")));
            Assert.AreSame(installedBmson, library.BmsonSongs.Single());
            Assert.AreEqual(installedBmsonPath, library.BmsonSongs.Single().path);
            using var verifySongDb = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(
                1,
                verifySongDb.ExecuteScalar<int>(
                    "SELECT COUNT(1) FROM bmson_song WHERE path = ?;",
                    installedBmsonPath));
            Assert.AreEqual(
                0,
                verifySongDb.ExecuteScalar<int>(
                    "SELECT COUNT(1) FROM bmson_song WHERE path = ?;",
                    pendingBmsonPath));
        });
    }

    /// <summary>
    /// resource-only 導入はcatalogの譜面件数を増やさず、既存のBMSON owner・snapshotを
    /// 保持したまま、実resource移動とpending install rowのdurable receiptを完了します。
    /// </summary>
    [DataTestMethod]
    [DataRow(16)]
    [DataRow(128)]
    public void OverwritePendingInstalledOnlyPackagesResources_UsesWarmCatalogWithoutChartDelta(int backgroundCount)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string backgroundDirectoryPath = Path.Combine(tempRootPath, "resource-only-warm-background");
            Directory.CreateDirectory(backgroundDirectoryPath);

            (string DestinationDirectoryPath, string PendingDirectoryPath,
                string FirstInstalledPath, string SecondInstalledPath,
                string FirstPendingPath, string SecondPendingPath,
                string FirstResourceName, string SecondResourceName,
                LR2SongDBExtended.bmson_song FirstInstalled,
                LR2SongDBExtended.bmson_song SecondInstalled,
                ChartPackage Package) CreateResourceStep(int step)
            {
                string destinationDirectoryPath = Path.Combine(
                    tempRootPath,
                    "resource-only-warm-installed-" + step);
                string pendingDirectoryPath = Path.Combine(
                    tempRootPath,
                    "resource-only-warm-pending-" + step);
                Directory.CreateDirectory(destinationDirectoryPath);
                Directory.CreateDirectory(pendingDirectoryPath);
                string firstResourceName = "sound-" + step + "-first.wav";
                string secondResourceName = "sound-" + step + "-second.wav";
                string firstMissingName = "missing-" + step + "-first.wav";
                string secondMissingName = "missing-" + step + "-second.wav";
                string firstInstalledPath = Path.Combine(destinationDirectoryPath, "first.bmson");
                string secondInstalledPath = Path.Combine(destinationDirectoryPath, "second.bmson");
                string firstPendingPath = Path.Combine(pendingDirectoryPath, "first.bmson");
                string secondPendingPath = Path.Combine(pendingDirectoryPath, "second.bmson");
                string firstJson = CreateBmsonJsonWithSounds(firstResourceName, firstMissingName);
                string secondJson = CreateBmsonJsonWithSounds(secondResourceName, secondMissingName);
                File.WriteAllText(firstInstalledPath, firstJson);
                File.WriteAllText(secondInstalledPath, secondJson);
                File.WriteAllText(firstPendingPath, firstJson);
                File.WriteAllText(secondPendingPath, secondJson);
                File.WriteAllBytes(Path.Combine(pendingDirectoryPath, firstResourceName), [1, 2, 3]);
                File.WriteAllBytes(Path.Combine(pendingDirectoryPath, secondResourceName), [4, 5, 6]);
                File.WriteAllBytes(Path.Combine(destinationDirectoryPath, firstMissingName), [7, 8, 9]);
                Assert.IsTrue(File.Exists(Path.Combine(destinationDirectoryPath, firstMissingName)));
                Assert.IsFalse(File.Exists(Path.Combine(pendingDirectoryPath, firstMissingName)));

                LR2SongDBExtended.bmson_song installedFirst = BmsonSongParser.Parse(firstInstalledPath);
                LR2SongDBExtended.bmson_song installedSecond = BmsonSongParser.Parse(secondInstalledPath);
                LR2SongDBExtended.bmson_song pendingFirst = BmsonSongParser.Parse(firstPendingPath);
                LR2SongDBExtended.bmson_song pendingSecond = BmsonSongParser.Parse(secondPendingPath);
                Assert.AreEqual(installedFirst.md5, pendingFirst.md5);
                Assert.AreEqual(installedFirst.sha256, pendingFirst.sha256);
                Assert.AreEqual(installedSecond.md5, pendingSecond.md5);
                Assert.AreEqual(installedSecond.sha256, pendingSecond.sha256);
                PackageChartEntry firstEntry = PackageChartEntry.FromChart(
                    ChartFileProjection.FromBmsonSong(
                        pendingFirst,
                        includeWarningSnapshot: false,
                        includeResourceReferences: false));
                PackageChartEntry secondEntry = PackageChartEntry.FromChart(
                    ChartFileProjection.FromBmsonSong(
                        pendingSecond,
                        includeWarningSnapshot: false,
                        includeResourceReferences: false));
                BmsLibraryPackageInstallService.ApplyPendingResourceHealthProjectionToEntries([firstEntry, secondEntry]);
                Assert.AreEqual(0, firstEntry.Chart.WAVHealth);
                Assert.AreEqual(0, secondEntry.Chart.WAVHealth);
                Assert.IsTrue(firstEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
                Assert.IsTrue(secondEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
                ChartPackage package = ChartPackage.FromChartEntries([firstEntry, secondEntry]);
                package.path = pendingDirectoryPath;
                package.delete_parent = false;
                return (
                    destinationDirectoryPath,
                    pendingDirectoryPath,
                    firstInstalledPath,
                    secondInstalledPath,
                    firstPendingPath,
                    secondPendingPath,
                    firstResourceName,
                    secondResourceName,
                    installedFirst,
                    installedSecond,
                    package);
            }

            var firstStep = CreateResourceStep(1);
            var secondStep = CreateResourceStep(2);
            var backgroundSongs = new List<LR2SongDBExtended.bmson_song>(backgroundCount);
            for (int index = 0; index < backgroundCount; index++)
            {
                string path = Path.Combine(backgroundDirectoryPath, "background-" + index + ".bmson");
                File.WriteAllText(path, "{}");
                string hash = (index + 1).ToString("x8") + new string('e', 24);
                backgroundSongs.Add(new LR2SongDBExtended.bmson_song
                {
                    path = path,
                    folder = backgroundDirectoryPath,
                    title = "Background " + index,
                    md5 = hash,
                    sha256 = (index + 1).ToString("x8") + new string('f', 56)
                });
            }
            using (var seedSongDb = new LR2SongDBExtended(songDbPath))
            {
                seedSongDb.InsertOrReplace(
                    CatalogMaintenanceWriteRequest.CreateBmsonPersistenceCopy(firstStep.FirstInstalled),
                    typeof(LR2SongDBExtended.bmson_song));
                seedSongDb.InsertOrReplace(
                    CatalogMaintenanceWriteRequest.CreateBmsonPersistenceCopy(firstStep.SecondInstalled),
                    typeof(LR2SongDBExtended.bmson_song));
                seedSongDb.InsertOrReplace(
                    CatalogMaintenanceWriteRequest.CreateBmsonPersistenceCopy(secondStep.FirstInstalled),
                    typeof(LR2SongDBExtended.bmson_song));
                seedSongDb.InsertOrReplace(
                    CatalogMaintenanceWriteRequest.CreateBmsonPersistenceCopy(secondStep.SecondInstalled),
                    typeof(LR2SongDBExtended.bmson_song));
                foreach (LR2SongDBExtended.bmson_song backgroundSong in backgroundSongs)
                {
                    seedSongDb.InsertOrReplace(
                        CatalogMaintenanceWriteRequest.CreateBmsonPersistenceCopy(backgroundSong),
                    typeof(LR2SongDBExtended.bmson_song));
                }
            }

            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = null,
                BmsonSongs = [
                    firstStep.FirstInstalled,
                    firstStep.SecondInstalled,
                    secondStep.FirstInstalled,
                    secondStep.SecondInstalled,
                    .. backgroundSongs
                ],
                ChartPackagesPending = CreatePackageCollection([firstStep.Package]),
                ChartPackagesInstalled = CreatePackageCollection([])
            };
            library.SearchTargets = [firstStep.DestinationDirectoryPath, secondStep.DestinationDirectoryPath];

            // cold構築では実root列挙を一度だけ観測し、warm区間のsource workと
            // 混同しないよう観測口を切り替えます。
            List<string> coldHashWork = [];
            List<string> coldInstalledWork = [];
            library.OwnedChartHashIndexStoreWorkObserver = coldHashWork.Add;
            library.InstalledChartLookupStoreWorkObserver = coldInstalledWork.Add;
            OwnedChartCollectionTestSupport.SetLibraryBmsonSongsWithoutNotification(library, []);
            OwnedChartCollectionTestSupport.SetLibraryBmsonSongsWithoutNotification(
                library,
                [
                    firstStep.FirstInstalled,
                    firstStep.SecondInstalled,
                    secondStep.FirstInstalled,
                    secondStep.SecondInstalled,
                    .. backgroundSongs
                ]);
            _ = library.GetOwnedChartHashIndexSnapshot();
            InstalledChartLookupIndexSnapshot coldInstalled =
                OwnedChartCollectionTestSupport.InvokeCreateInstalledChartLookupSnapshot(library);
            _ = coldInstalled.Md5Directories.ToArray();
            _ = coldInstalled.Sha256Directories.ToArray();
            _ = coldInstalled.PrimaryHashCounts.ToArray();
            Assert.IsTrue(coldHashWork.Contains("owned_hash_source_enumeration"));
            Assert.IsTrue(coldHashWork.Contains("owned_hash_source_entry_visited"));
            Assert.IsTrue(coldInstalledWork.Contains("installed_root_map_enumeration"));
            Assert.IsTrue(coldInstalledWork.Contains("installed_root_map_key_visited"));
            Assert.IsTrue(coldInstalledWork.Contains("installed_primary_hash_count_update"));
            Assert.IsTrue(coldInstalledWork.Contains("installed_directory_bucket_update"));
            library.OwnedChartHashIndexStoreWorkObserver = null;
            library.InstalledChartLookupStoreWorkObserver = null;

            OwnedChartHashIndexVersionedSnapshot oldHash = library.GetOwnedChartHashIndexSnapshot();
            InstalledChartLookupIndexSnapshot oldInstalled =
                OwnedChartCollectionTestSupport.InvokeCreateInstalledChartLookupSnapshot(library);
            PlaylistLibraryResolveIndexSnapshot oldPlaylist = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool oldPlaylistCacheHit,
                out int oldPlaylistStaleRetries);
            Assert.IsFalse(oldPlaylistCacheHit);
            Assert.AreEqual(0, oldPlaylistStaleRetries);
            Assert.AreEqual(backgroundCount + 4, library.BmsonSongs.Count);

            List<string> hashWork = [];
            List<string> playlistWork = [];
            List<string> installedWork = [];
            library.OwnedChartHashIndexStoreWorkObserver = hashWork.Add;
            library.PlaylistLibraryResolveIndexStoreWorkObserver = playlistWork.Add;
            library.InstalledChartLookupStoreWorkObserver = installedWork.Add;
            OwnedChartHashIndexVersionedSnapshot? snapshotAfterFirstHash = null;
            InstalledChartLookupIndexSnapshot? snapshotAfterFirstInstalled = null;
            PlaylistLibraryResolveIndexSnapshot? snapshotAfterFirstPlaylist = null;
            for (int stepIndex = 0; stepIndex < 2; stepIndex++)
            {
                var step = stepIndex == 0 ? firstStep : secondStep;
                if (stepIndex > 0)
                {
                    library.ChartPackagesPending = CreatePackageCollection([step.Package]);
                }

                bool packageEntryNotificationObserved = false;
                Exception? packageEntryInspectionFailure = null;
                void InspectPackageEntryNotification(object? _, System.ComponentModel.PropertyChangedEventArgs? __)
                {
                    try
                    {
                        Assert.IsFalse(library.IsWriteLockHeldPendingInstallCharts);
                        using var notifiedSongDb = new LR2SongDBExtended(songDbPath);
                        Assert.AreEqual(
                            1,
                            notifiedSongDb.ExecuteScalar<int>(
                                "SELECT COUNT(1) FROM bmson_song WHERE path = ?;",
                                step.FirstInstalledPath));
                    }
                    catch (Exception exception)
                    {
                        packageEntryInspectionFailure = exception;
                    }
                    packageEntryNotificationObserved = true;
                }
                PackageChartEntry observedPendingEntry = step.Package.ChartEntries[0];
                observedPendingEntry.PropertyChanged += InspectPackageEntryNotification;
                int previousHashWorkCount = hashWork.Count;
                int previousInstalledWorkCount = installedWork.Count;
                int previousPlaylistWorkCount = playlistWork.Count;
                try
                {
                    PendingInstalledOnlyResourceOverwriteResult result =
                        library.OverwritePendingInstalledOnlyPackagesResources([step.Package]);

                    Assert.AreEqual(1, result.SucceededInstall);
                    Assert.AreEqual(0, result.Failed);
                }
                finally
                {
                    observedPendingEntry.PropertyChanged -= InspectPackageEntryNotification;
                }

                Assert.AreEqual(2, step.Package.ChartEntries.Count);
                Assert.AreSame(observedPendingEntry, step.Package.ChartEntries[0]);
                Assert.IsTrue(packageEntryNotificationObserved);
                Assert.IsNull(packageEntryInspectionFailure, packageEntryInspectionFailure?.ToString());
                Assert.AreEqual(0, library.ChartPackagesPending.Count);
                Assert.AreEqual(stepIndex + 1, library.ChartPackagesInstalled.Count);
                CollectionAssert.AreEqual(
                    new byte[] { 1, 2, 3 },
                    File.ReadAllBytes(Path.Combine(step.DestinationDirectoryPath, step.FirstResourceName)));
                CollectionAssert.AreEqual(
                    new byte[] { 4, 5, 6 },
                    File.ReadAllBytes(Path.Combine(step.DestinationDirectoryPath, step.SecondResourceName)));
                Assert.AreSame(
                    step.FirstInstalled,
                    library.BmsonSongs.Single(song => song.path == step.FirstInstalledPath));
                Assert.AreSame(
                    step.SecondInstalled,
                    library.BmsonSongs.Single(song => song.path == step.SecondInstalledPath));

                PackageChartEntry installedFirstEntry = library.ChartPackagesInstalled
                    .SelectMany(package => package.ChartEntries)
                    .Single(entry => entry.Chart.Md5 == step.FirstInstalled.md5);
                PackageChartEntry installedSecondEntry = library.ChartPackagesInstalled
                    .SelectMany(package => package.ChartEntries)
                    .Single(entry => entry.Chart.Md5 == step.SecondInstalled.md5);
                Assert.AreEqual(step.FirstInstalledPath, installedFirstEntry.Chart.Path);
                Assert.AreEqual(step.SecondInstalledPath, installedSecondEntry.Chart.Path);
                Assert.AreSame(step.FirstInstalled, installedFirstEntry.Chart.GetBmsonStorageOwner());
                Assert.AreSame(step.SecondInstalled, installedSecondEntry.Chart.GetBmsonStorageOwner());
                Assert.AreEqual(100, installedFirstEntry.Chart.WAVHealth);
                Assert.AreEqual(0, installedSecondEntry.Chart.WAVHealth);
                Assert.IsFalse(installedFirstEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
                Assert.IsTrue(installedSecondEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));

                using (var verifySongDb = new LR2SongDBExtended(songDbPath))
                {
                    Assert.AreEqual(backgroundCount + 4, verifySongDb.Table<LR2SongDBExtended.bmson_song>().Count());
                    Assert.AreEqual(1, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM bmson_song WHERE path = ?;", step.FirstInstalledPath));
                    Assert.AreEqual(1, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM bmson_song WHERE path = ?;", step.SecondInstalledPath));
                    Assert.AreEqual(0, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM bmson_song WHERE path = ?;", step.FirstPendingPath));
                    Assert.AreEqual(0, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM bmson_song WHERE path = ?;", step.SecondPendingPath));
                    LR2SongDBExtended.bmson_song persisted = verifySongDb.Table<LR2SongDBExtended.bmson_song>()
                        .Single(song => song.path == step.FirstInstalledPath);
                    Assert.AreEqual(step.FirstInstalled.md5, persisted.md5);
                    Assert.AreEqual(step.FirstInstalled.sha256, persisted.sha256);
                }

                OwnedChartHashIndexVersionedSnapshot updatedHash = library.GetOwnedChartHashIndexSnapshot();
                OwnedChartHashIndexVersionedSnapshot cachedHash = library.GetOwnedChartHashIndexSnapshot();
                InstalledChartLookupIndexSnapshot updatedInstalled =
                    OwnedChartCollectionTestSupport.InvokeCreateInstalledChartLookupSnapshot(library);
                InstalledChartLookupIndexSnapshot cachedInstalled =
                    OwnedChartCollectionTestSupport.InvokeCreateInstalledChartLookupSnapshot(library);
                PlaylistLibraryResolveIndexSnapshot updatedPlaylist = library.GetPlaylistLibraryResolveIndexSnapshot(
                    CancellationToken.None,
                    out bool updatedPlaylistCacheHit,
                    out int updatedPlaylistStaleRetries);
                PlaylistLibraryResolveIndexSnapshot cachedPlaylist = library.GetPlaylistLibraryResolveIndexSnapshot(
                    CancellationToken.None,
                    out bool cachedPlaylistCacheHit,
                    out int cachedPlaylistStaleRetries);
                Assert.AreSame(oldHash, updatedHash);
                Assert.AreSame(updatedHash, cachedHash);
                Assert.AreSame(oldInstalled, updatedInstalled);
                Assert.AreSame(updatedInstalled, cachedInstalled);
                Assert.AreSame(oldPlaylist, updatedPlaylist);
                Assert.AreSame(updatedPlaylist, cachedPlaylist);
                Assert.IsTrue(updatedPlaylistCacheHit);
                Assert.AreEqual(0, updatedPlaylistStaleRetries);
                Assert.IsTrue(cachedPlaylistCacheHit);
                Assert.AreEqual(0, cachedPlaylistStaleRetries);
                Assert.IsTrue(oldHash.ContainsMd5(backgroundSongs[0].md5));
                Assert.IsTrue(oldInstalled.ContainsPrimaryHash(backgroundSongs[0].md5));
                Assert.IsTrue(oldPlaylist.ContainsCandidate(LibraryChartKind.Bmson, backgroundSongs[0].path));
                if (stepIndex == 0)
                {
                    snapshotAfterFirstHash = updatedHash;
                    snapshotAfterFirstInstalled = updatedInstalled;
                    snapshotAfterFirstPlaylist = updatedPlaylist;
                }
                else
                {
                    Assert.AreSame(snapshotAfterFirstHash, updatedHash);
                    Assert.AreSame(snapshotAfterFirstInstalled, updatedInstalled);
                    Assert.AreSame(snapshotAfterFirstPlaylist, updatedPlaylist);
                }
                string[] operationHashWork = [.. hashWork.Skip(previousHashWorkCount)];
                string[] operationInstalledWork = [.. installedWork.Skip(previousInstalledWorkCount)];
                string[] operationPlaylistWork = [.. playlistWork.Skip(previousPlaylistWorkCount)];
                Assert.IsFalse(operationHashWork.Contains("owned_hash_source_enumeration"));
                Assert.IsFalse(operationHashWork.Contains("owned_hash_source_entry_visited"));
                Assert.IsFalse(operationInstalledWork.Contains("installed_root_map_enumeration"));
                Assert.IsFalse(operationInstalledWork.Contains("installed_root_map_key_visited"));
                Assert.IsTrue(
                    operationInstalledWork.Count(operation => operation == "installed_directory_bucket_entry_copied") < backgroundCount,
                    "resource-only operation copied an entire directory bucket.");
                Assert.IsFalse(operationPlaylistWork.Contains("playlist_resolve_source_enumeration"));
                Assert.IsFalse(operationPlaylistWork.Contains("playlist_resolve_full_root_enumeration"));
                Assert.IsFalse(operationPlaylistWork.Contains("playlist_resolve_full_root_key_visited"));
            }
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
            Exception? packageEntryInspectionFailure = null;
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

    /// <summary>
    /// 強制導入の本番入口を、primary hash lookupだけを先に温めた状態で実行し、
    /// 確定した譜面がFS・SQLite・installed lookupへ反映されることを確認します。
    /// </summary>
    [TestMethod]
    public void ForceInstallPendingPackages_UpdatesPrimaryLookupThroughLibraryInstall()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb((songDbPath, tempRootPath) =>
        {
            string installRootPath = Path.Combine(tempRootPath, "PrimaryOnlyInstalled");
            string existingDirectoryPath = Path.Combine(installRootPath, "Existing");
            string sourceDirectoryPath = Path.Combine(tempRootPath, "PrimaryOnlySource");
            string existingPath = CreateBmsFile(existingDirectoryPath, "existing.bms", "#TITLE Existing");
            string sourcePath = CreateBmsFile(sourceDirectoryPath, "added.bms", "#TITLE Primary Only Added");
            BMSFile existing = BMSFile.CreateBMSFileFromFile(existingPath);
            BMSFile source = BMSFile.CreateBMSFileFromFile(sourcePath);
            using (var seedSongDb = new LR2SongDBExtended(songDbPath))
            {
                seedSongDb.InsertOrReplace(
                    existing.CreateSongRowPersistenceCopy(),
                    typeof(LR2SongDB.song));
            }

            ChartPackage package = ChartPackageTestExtensions.CreatePackage([source]);
            package.path = sourceDirectoryPath;
            package.delete_parent = false;
            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                new RealFileMutationService(),
                new RecordingDialogService(),
                new TestUiScheduler(() => null!),
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
                BMSFiles = [existing],
                BmsonSongs = [],
                ChartPackagesPending = CreatePackageCollection([package]),
                ChartPackagesInstalled = CreatePackageCollection([])
            };

            BMSLibrary.InstalledPrimaryHashWarmupResult primaryWarmup =
                library.WarmInstalledPrimaryHashLookup("u5c1_primary_only_before_install");
            Assert.AreEqual("installed_primary_hash", primaryWarmup.IndexName);
            Assert.IsFalse(primaryWarmup.FullDirectoryLookupInitialized);
            Assert.IsTrue(OwnedChartCollectionTestSupport.IsInstalledPrimaryHashLookupInitialized(library));
            Assert.IsFalse(OwnedChartCollectionTestSupport.IsInstalledChartLookupIndexInitialized(library));
            IPrimaryHashLookup oldPrimary =
                OwnedChartCollectionTestSupport.InvokeCreateInstalledChartKeySnapshotExcludingCharts(library, []);
            Assert.IsTrue(oldPrimary.ContainsPrimaryHash(existing.hash));
            Assert.IsFalse(oldPrimary.ContainsPrimaryHash(source.hash));

            LibraryMutationSessionReceipt receipt = library.ForceInstallPendingPackagesWithReceipt(
                [package],
                approveNormalInstallOverride: true,
                approvedNormalInstallOverridePackages: null);

            Assert.IsTrue(receipt.DurableCommit);
            Assert.IsFalse(receipt.HasRequiredFailure);
            Assert.IsFalse(receipt.HasDurableFinalizationFailure);
            Assert.AreEqual(0, library.ChartPackagesPending.Count);
            Assert.AreEqual(1, library.ChartPackagesInstalled.Count);
            BMSFile installed = library.BMSFiles.Single(file => file.hash == source.hash);
            Assert.AreNotEqual(sourcePath, installed.path);
            StringAssert.StartsWith(installed.path, installRootPath);
            Assert.IsTrue(File.Exists(installed.path));
            Assert.IsTrue(OwnedChartCollectionTestSupport.IsInstalledPrimaryHashLookupInitialized(library));

            // 実導入後のprimary所有と、導入前に捕捉したsnapshotの不変性を確認します。
            BMSLibrary.InstalledPrimaryHashWarmupResult updatedPrimary =
                library.WarmInstalledPrimaryHashLookup("u5c2_primary_only_after_install");
            IPrimaryHashLookup currentPrimary =
                OwnedChartCollectionTestSupport.InvokeCreateInstalledChartKeySnapshotExcludingCharts(library, []);
            Assert.AreEqual(2, updatedPrimary.PrimaryHashCount);
            Assert.IsTrue(currentPrimary.ContainsPrimaryHash(existing.hash));
            Assert.IsTrue(currentPrimary.ContainsPrimaryHash(source.hash));
            Assert.IsTrue(oldPrimary.ContainsPrimaryHash(existing.hash));
            Assert.IsFalse(oldPrimary.ContainsPrimaryHash(source.hash));
            using (var verifySongDbBeforeFull = new LR2SongDBExtended(songDbPath))
            {
                Assert.AreEqual(1, verifySongDbBeforeFull.ExecuteScalar<int>(
                    "SELECT COUNT(1) FROM song WHERE path = ?;",
                    installed.path));
                Assert.AreEqual(0, verifySongDbBeforeFull.ExecuteScalar<int>(
                    "SELECT COUNT(1) FROM song WHERE path = ?;",
                    sourcePath));
            }

            InstalledChartLookupIndexSnapshot installedLookup =
                OwnedChartCollectionTestSupport.InvokeCreateInstalledChartLookupSnapshot(library);
            Assert.IsTrue(installedLookup.ContainsPrimaryHash(existing.hash));
            Assert.IsTrue(installedLookup.ContainsPrimaryHash(source.hash));
            using var verifySongDb = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1, verifySongDb.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM song WHERE path = ?;",
                installed.path));
            Assert.AreEqual(0, verifySongDb.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM song WHERE path = ?;",
                sourcePath));
        });
    }

    /// <summary>
    /// 推定導入の本番入口で、実際に確定したexact pathへ譜面を追加し、
    /// 既存行と新行のhashがinstalled lookupへ反映されることを確認します。
    /// </summary>
    [TestMethod]
    public void InstallPendingPackagesToEstimatedDestinations_AddsExactTargetThroughLibraryInstall()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb((songDbPath, tempRootPath) =>
        {
            string installRootPath = Path.Combine(tempRootPath, "ExactInstalled");
            string destinationDirectoryPath = Path.Combine(installRootPath, "Target");
            string sourceDirectoryPath = Path.Combine(tempRootPath, "ExactSource");
            string oldDirectoryPath = Path.Combine(installRootPath, "Existing");
            string oldPath = CreateBmsFile(oldDirectoryPath, "chart.bms", "#TITLE Original");
            string addedPath = CreateBmsFile(sourceDirectoryPath, "added.bms", "#TITLE Replacement");
            BMSFile oldChart = BMSFile.CreateBMSFileFromFile(oldPath);
            BMSFile addedChart = BMSFile.CreateBMSFileFromFile(addedPath);
            Assert.AreNotEqual(oldChart.hash, addedChart.hash);
            using (var seedSongDb = new LR2SongDBExtended(songDbPath))
            {
                seedSongDb.InsertOrReplace(
                    oldChart.CreateSongRowPersistenceCopy(),
                    typeof(LR2SongDB.song));
            }

            ChartPackage package = ChartPackageTestExtensions.CreatePackage(
                ChartPackageTestExtensions.CreateEntryWithInstallDestination(
                    addedChart,
                    destinationDirectoryPath));
            package.path = sourceDirectoryPath;
            package.delete_parent = false;
            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                new RealFileMutationService(),
                new RecordingDialogService(),
                new TestUiScheduler(() => null!),
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
                BMSFiles = [oldChart],
                BmsonSongs = [],
                ChartPackagesPending = CreatePackageCollection([package]),
                ChartPackagesInstalled = CreatePackageCollection([])
            };

            BMSLibrary.InstalledPrimaryHashWarmupResult primaryWarmup =
                library.WarmInstalledPrimaryHashLookup("u5c1_exact_target_before_install");
            Assert.IsFalse(primaryWarmup.FullDirectoryLookupInitialized);
            Assert.IsFalse(OwnedChartCollectionTestSupport.IsInstalledChartLookupIndexInitialized(library));
            IPrimaryHashLookup oldPrimary =
                OwnedChartCollectionTestSupport.InvokeCreateInstalledChartKeySnapshotExcludingCharts(library, []);
            Assert.IsTrue(oldPrimary.ContainsPrimaryHash(oldChart.hash));
            Assert.IsFalse(oldPrimary.ContainsPrimaryHash(addedChart.hash));

            PendingInstallBatchResult result =
                library.InstallPendingPackagesToEstimatedDestinationsWithReceipt([package]);

            Assert.IsTrue(result.SessionReceipt.DurableCommit);
            Assert.IsFalse(result.SessionReceipt.HasRequiredFailure);
            Assert.IsFalse(result.SessionReceipt.HasDurableFinalizationFailure);
            Assert.AreEqual(0, result.FailedPackages.Count);
            Assert.AreEqual(
                2,
                library.BMSFiles.Count,
                string.Join("|", library.BMSFiles.Select(file => file.path + ":" + file.hash)));
            BMSFile installed = library.BMSFiles.Single(file => file.hash == addedChart.hash);
            string expectedInstalledPath = Path.Combine(destinationDirectoryPath, "added.bms");
            Assert.AreEqual(expectedInstalledPath, installed.path);
            Assert.AreEqual(addedChart.hash, installed.hash);
            Assert.IsTrue(File.Exists(expectedInstalledPath));
            Assert.IsTrue(library.BMSFiles.Any(file => file.path == oldPath && file.hash == oldChart.hash));
            // 推定先導入の確定結果をprimary lookupとDBから確認します。
            BMSLibrary.InstalledPrimaryHashWarmupResult updatedPrimary =
                library.WarmInstalledPrimaryHashLookup("u5c2_exact_target_after_install");
            IPrimaryHashLookup currentPrimary =
                OwnedChartCollectionTestSupport.InvokeCreateInstalledChartKeySnapshotExcludingCharts(library, []);
            Assert.AreEqual(2, updatedPrimary.PrimaryHashCount);
            Assert.IsTrue(currentPrimary.ContainsPrimaryHash(oldChart.hash));
            Assert.IsTrue(currentPrimary.ContainsPrimaryHash(addedChart.hash));
            Assert.IsTrue(oldPrimary.ContainsPrimaryHash(oldChart.hash));
            Assert.IsFalse(oldPrimary.ContainsPrimaryHash(addedChart.hash));
            using (var verifySongDbBeforeFull = new LR2SongDBExtended(songDbPath))
            {
                Assert.AreEqual(1, verifySongDbBeforeFull.ExecuteScalar<int>(
                    "SELECT COUNT(1) FROM song WHERE path = ?;",
                    expectedInstalledPath));
                Assert.AreEqual(1, verifySongDbBeforeFull.ExecuteScalar<int>(
                    "SELECT COUNT(1) FROM song WHERE path = ?;",
                    oldPath));
            }
            InstalledChartLookupIndexSnapshot installedLookup =
                OwnedChartCollectionTestSupport.InvokeCreateInstalledChartLookupSnapshot(library);
            Assert.IsTrue(installedLookup.ContainsPrimaryHash(oldChart.hash));
            Assert.IsTrue(installedLookup.ContainsPrimaryHash(addedChart.hash));
            using var verifySongDb = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1, verifySongDb.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM song WHERE path = ?;",
                expectedInstalledPath));
            Assert.AreEqual(1, verifySongDb.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM song WHERE path = ?;",
                oldPath));
            Assert.AreEqual(2, verifySongDb.Table<LR2SongDB.song>().Count());
            Assert.AreEqual(expectedInstalledPath, package.ChartEntries.Single().Chart.Path);
        });
    }

    /// <summary>
    /// 強制導入の本番入口で、別sourceから同一bytesを別destinationへ導入します。
    /// distinct hash集合が変わらない場合のcontent versionとplaylist summary cacheを確認します。
    /// </summary>
    [TestMethod]
    public void ForceInstallPendingPackages_SameDigestAdditionKeepsHashVersionAndPlaylistSummaryCache()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb((songDbPath, tempRootPath) =>
        {
            string installRootPath = Path.Combine(tempRootPath, "SameDigestInstalled");
            string initialDirectoryPath = Path.Combine(installRootPath, "Initial");
            string sourceDirectoryPath = Path.Combine(tempRootPath, "SameDigestSource");
            string initialPath = CreateBmsFile(initialDirectoryPath, "chart.bms", "#TITLE Same Digest");
            string sourcePath = Path.Combine(sourceDirectoryPath, "chart.bms");
            Directory.CreateDirectory(sourceDirectoryPath);
            File.Copy(initialPath, sourcePath, overwrite: true);
            BMSFile initialChart = BMSFile.CreateBMSFileFromFile(initialPath);
            BMSFile sourceChart = BMSFile.CreateBMSFileFromFile(sourcePath);
            Assert.AreEqual(initialChart.hash, sourceChart.hash);
            Assert.AreEqual(initialChart.sha256, sourceChart.sha256);
            using (var seedSongDb = new LR2SongDBExtended(songDbPath))
            {
                seedSongDb.InsertOrReplace(
                    initialChart.CreateSongRowPersistenceCopy(),
                    typeof(LR2SongDB.song));
            }

            ChartPackage package = ChartPackageTestExtensions.CreatePackage([sourceChart]);
            package.path = sourceDirectoryPath;
            package.delete_parent = false;
            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                new RealFileMutationService(),
                new RecordingDialogService(),
                new TestUiScheduler(() => null!),
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
                BMSFiles = [initialChart],
                BmsonSongs = [],
                ChartPackagesPending = CreatePackageCollection([package]),
                ChartPackagesInstalled = CreatePackageCollection([])
            };

            OwnedChartHashIndexVersionedSnapshot initialHashes = library.GetOwnedChartHashIndexSnapshot();
            Assert.AreEqual(1, initialHashes.GetMd5OwnerCount(initialChart.hash));
            Assert.AreEqual(1, initialHashes.GetSha256OwnerCount(initialChart.sha256));
            Assert.IsFalse(OwnedChartCollectionTestSupport.IsInstalledPrimaryHashLookupInitialized(library));
            Assert.IsFalse(OwnedChartCollectionTestSupport.IsInstalledChartLookupIndexInitialized(library));

            var table = new BMSTable { playlist_id = 42 };
            table.entries = [PlaylistSummaryAggregationTestSupport.CreateEntry(initialChart.hash, initialChart.sha256)];
            var summaryOwner = new PlaylistCatalogSummaryOwner();
            PlaylistSummaryCountResult initialSummary = summaryOwner.GetOrBuildTableCount(
                table,
                initialHashes,
                CancellationToken.None,
                out bool initialCacheHit);
            PlaylistSummaryCountResult cachedSummary = summaryOwner.GetOrBuildTableCount(
                table,
                initialHashes,
                CancellationToken.None,
                out bool cachedCacheHit);
            Assert.IsFalse(initialCacheHit);
            Assert.IsTrue(cachedCacheHit);
            Assert.AreEqual(1, initialSummary.OwnedCharts);
            Assert.AreEqual(1, cachedSummary.OwnedCharts);

            OwnedChartHashIndexVersionedSnapshot warmedHashes = library.GetOwnedChartHashIndexSnapshot();
            Assert.AreEqual(initialHashes.Version, warmedHashes.Version);
            Assert.IsFalse(OwnedChartCollectionTestSupport.IsInstalledPrimaryHashLookupInitialized(library));
            Assert.IsFalse(OwnedChartCollectionTestSupport.IsInstalledChartLookupIndexInitialized(library));

            LibraryMutationSessionReceipt receipt = library.ForceInstallPendingPackagesWithReceipt(
                [package],
                approveNormalInstallOverride: true,
                approvedNormalInstallOverridePackages: null);

            Assert.IsTrue(receipt.DurableCommit);
            Assert.IsFalse(receipt.HasRequiredFailure);
            Assert.IsFalse(receipt.HasDurableFinalizationFailure);
            Assert.AreEqual(
                2,
                library.BMSFiles.Count,
                string.Join("|", library.BMSFiles.Select(file => file.path + ":" + file.hash)));
            BMSFile installed = library.BMSFiles.Single(file =>
                !string.Equals(initialPath, file.path, StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(initialChart.hash, installed.hash);
            Assert.AreNotEqual(initialPath, installed.path);
            StringAssert.StartsWith(installed.path, installRootPath);
            Assert.IsTrue(File.Exists(installed.path));
            Assert.AreEqual(1, library.ChartPackagesInstalled.Count);

            // 同digestを別配置へ導入した後もprimary所有が維持されることを確認します。
            BMSLibrary.InstalledPrimaryHashWarmupResult updatedPrimary =
                library.WarmInstalledPrimaryHashLookup("u5c2_hash_only_after_install");
            Assert.AreEqual(1, updatedPrimary.PrimaryHashCount);
            IPrimaryHashLookup currentPrimary =
                OwnedChartCollectionTestSupport.InvokeCreateInstalledChartKeySnapshotExcludingCharts(library, []);
            Assert.IsTrue(currentPrimary.ContainsPrimaryHash(initialChart.hash));
            using (var verifySongDbBeforeFull = new LR2SongDBExtended(songDbPath))
            {
                Assert.AreEqual(2, verifySongDbBeforeFull.Table<LR2SongDB.song>().Count());
                Assert.AreEqual(1, verifySongDbBeforeFull.ExecuteScalar<int>(
                    "SELECT COUNT(1) FROM song WHERE path = ?;",
                    initialPath));
                Assert.AreEqual(1, verifySongDbBeforeFull.ExecuteScalar<int>(
                    "SELECT COUNT(1) FROM song WHERE path = ?;",
                    installed.path));
            }

            OwnedChartHashIndexVersionedSnapshot updatedHashes = library.GetOwnedChartHashIndexSnapshot();
            PlaylistSummaryCountResult updatedSummary = summaryOwner.GetOrBuildTableCount(
                table,
                updatedHashes,
                CancellationToken.None,
                out bool updatedCacheHit);
            Assert.AreEqual(initialHashes.Version, updatedHashes.Version);
            Assert.AreEqual(2, updatedHashes.GetMd5OwnerCount(initialChart.hash));
            Assert.AreEqual(2, updatedHashes.GetSha256OwnerCount(initialChart.sha256));
            Assert.IsTrue(updatedCacheHit);
            Assert.AreEqual(1, updatedSummary.TotalCharts);
            Assert.AreEqual(1, updatedSummary.OwnedCharts);
            Assert.AreEqual(installed.path, package.ChartEntries.Single().Chart.Path);
        });
    }

    /// <summary>
    /// 自動導入も推定／強制導入と同じ本番catalog入口へ到達し、
    /// preflightで確定したdestinationをFS・SQLite・lookup・通知へ渡します。
    /// warmな背景件数を変えても、新規2譜面分の局所差分だけで反映します。
    /// </summary>
    [DataTestMethod]
    [DataRow(16)]
    [DataRow(128)]
    public void InstallChartPackagesAuto_UsesPreflightDestinationAndWarmDelta(int backgroundCount)
    {
        AssertNormalInstallRouteUsesPreflightDestinationAndWarmDelta("auto", backgroundCount);
    }

    [DataTestMethod]
    [DataRow(16)]
    [DataRow(128)]
    public void InstallPendingPackagesToEstimatedDestinations_UsesPreflightDestinationAndWarmDelta(int backgroundCount)
    {
        AssertNormalInstallRouteUsesPreflightDestinationAndWarmDelta("estimated", backgroundCount);
    }

    [DataTestMethod]
    [DataRow(16)]
    [DataRow(128)]
    public void ForceInstallPendingPackages_UsesPreflightDestinationAndWarmDelta(int backgroundCount)
    {
        AssertNormalInstallRouteUsesPreflightDestinationAndWarmDelta("force", backgroundCount);
    }

    private static void AssertNormalInstallRouteUsesPreflightDestinationAndWarmDelta(
        string route,
        int backgroundCount)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            bool isAutoRoute = string.Equals(route, "auto", StringComparison.Ordinal);
            bool isEstimatedRoute = string.Equals(route, "estimated", StringComparison.Ordinal);
            bool isForceRoute = string.Equals(route, "force", StringComparison.Ordinal);
            Assert.IsTrue(isAutoRoute || isEstimatedRoute || isForceRoute);
            string installRootPath = Path.Combine(tempRootPath, "AutoInstalled");
            Directory.CreateDirectory(installRootPath);

            (string SourceDirectoryPath, string FirstSourcePath, string SecondSourcePath,
                string FirstResourceName, string SecondResourceName, BMSFile FirstSource,
                BMSFile SecondSource, ChartPackage Package) CreateInstallStep(int step)
            {
                string sourceDirectoryPath = Path.Combine(tempRootPath, "AutoSource" + step);
                string explicitDestinationDirectoryPath = Path.Combine(installRootPath, "Explicit" + step);
                string firstResourceName = "auto-" + step + "-first";
                string secondResourceName = "auto-" + step + "-second";
                string firstSourcePath = CreateBmsFileWithResources(
                    sourceDirectoryPath,
                    "first.bms",
                    "#TITLE Auto " + step + " First",
                    firstResourceName);
                string secondSourcePath = CreateBmsFileWithResources(
                    sourceDirectoryPath,
                    "second.bms",
                    "#TITLE Auto " + step + " Second",
                    secondResourceName);
                BMSFile firstSource = BMSFile.CreateBMSFileFromFile(firstSourcePath);
                BMSFile secondSource = BMSFile.CreateBMSFileFromFile(secondSourcePath);
                ChartPackage package = ChartPackage.FromChartEntries([
                    ChartPackageTestExtensions.CreateEntryWithInstallDestination(
                        firstSource,
                        explicitDestinationDirectoryPath),
                    ChartPackageTestExtensions.CreateEntryWithInstallDestination(
                        secondSource,
                        explicitDestinationDirectoryPath)
                ]);
                package.path = sourceDirectoryPath;
                package.delete_parent = false;
                return (
                    sourceDirectoryPath,
                    firstSourcePath,
                    secondSourcePath,
                    firstResourceName,
                    secondResourceName,
                    firstSource,
                    secondSource,
                    package);
            }

            var firstStep = CreateInstallStep(1);
            var secondStep = CreateInstallStep(2);
            string backgroundDirectoryPath = Path.Combine(tempRootPath, "Background");
            var backgroundFiles = new List<BMSFile>(backgroundCount);
            for (int index = 0; index < backgroundCount; index++)
            {
                string backgroundPath = CreateBmsFile(
                    backgroundDirectoryPath,
                    "background-" + index + ".bms",
                    "#TITLE Background " + index);
                backgroundFiles.Add(BMSFile.CreateBMSFileFromFile(backgroundPath));
            }
            using (var seedSongDb = new LR2SongDBExtended(songDbPath))
            {
                foreach (BMSFile backgroundFile in backgroundFiles)
                {
                    seedSongDb.InsertOrReplace(
                        backgroundFile.CreateSongRowPersistenceCopy(),
                        typeof(LR2SongDB.song));
                }
            }

            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                new RealFileMutationService(),
                new RecordingDialogService(),
                new TestUiScheduler(() => null!),
                () => new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = false,
                    BMSInstallDir = installRootPath,
                    FolderNameFormat = "%TITLE%",
                    DeletePendingPackageSourceAfterInstall = false,
                    KeepInstallablePackagesPending = false,
                    EnableSmartComponentOverwrite = false,
                    KeepSmartOverwriteProtectedFilesByRenaming = false
                })
            {
                ChartPackagesPending = CreatePackageCollection(isAutoRoute ? [] : [firstStep.Package]),
                ChartPackagesInstalled = CreatePackageCollection([])
            };
            OwnedChartCollectionTestSupport.SetLibraryFilesWithoutNotification(library, backgroundFiles);
            OwnedChartCollectionTestSupport.SetLibraryBmsonSongsWithoutNotification(library, []);
            library.SearchTargets = [installRootPath];

            // 正対照として一度だけcold rootを構築し、実際の列挙markerが観測できる
            // ことを確認してから、同じlibraryをwarm状態へ戻して本番操作を測定します。
            List<string> coldHashWork = [];
            List<string> coldInstalledWork = [];
            library.OwnedChartHashIndexStoreWorkObserver = coldHashWork.Add;
            library.InstalledChartLookupStoreWorkObserver = coldInstalledWork.Add;
            OwnedChartCollectionTestSupport.SetLibraryFilesWithoutNotification(library, []);
            OwnedChartCollectionTestSupport.SetLibraryFilesWithoutNotification(library, backgroundFiles);
            _ = library.GetOwnedChartHashIndexSnapshot();
            InstalledChartLookupIndexSnapshot coldInstalled =
                OwnedChartCollectionTestSupport.InvokeCreateInstalledChartLookupSnapshot(library);
            _ = coldInstalled.Md5Directories.ToArray();
            _ = coldInstalled.Sha256Directories.ToArray();
            _ = coldInstalled.PrimaryHashCounts.ToArray();
            Assert.IsTrue(coldHashWork.Contains("owned_hash_source_enumeration"));
            Assert.IsTrue(coldHashWork.Contains("owned_hash_source_entry_visited"));
            Assert.IsTrue(coldInstalledWork.Contains("installed_root_map_enumeration"));
            Assert.IsTrue(coldInstalledWork.Contains("installed_root_map_key_visited"));
            Assert.IsTrue(coldInstalledWork.Contains("installed_primary_hash_count_update"));
            Assert.IsTrue(coldInstalledWork.Contains("installed_directory_bucket_update"));
            library.OwnedChartHashIndexStoreWorkObserver = null;
            library.InstalledChartLookupStoreWorkObserver = null;

            OwnedChartHashIndexVersionedSnapshot oldHash = library.GetOwnedChartHashIndexSnapshot();
            InstalledChartLookupIndexSnapshot oldInstalled =
                OwnedChartCollectionTestSupport.InvokeCreateInstalledChartLookupSnapshot(library);
            PlaylistLibraryResolveIndexSnapshot oldPlaylist = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool oldPlaylistCacheHit,
                out int oldPlaylistStaleRetries);
            Assert.IsFalse(oldPlaylistCacheHit);
            Assert.AreEqual(0, oldPlaylistStaleRetries);
            Assert.AreEqual(backgroundCount, library.BMSFiles.Count);
            IPrimaryHashLookup oldPrimary =
                OwnedChartCollectionTestSupport.InvokeCreateInstalledChartKeySnapshotExcludingCharts(library, []);

            List<string> hashWork = [];
            List<string> playlistWork = [];
            List<string> installedWork = [];
            library.OwnedChartHashIndexStoreWorkObserver = hashWork.Add;
            library.PlaylistLibraryResolveIndexStoreWorkObserver = playlistWork.Add;
            library.InstalledChartLookupStoreWorkObserver = installedWork.Add;
            string? expectedSourcePath = null;
            string? expectedSourceHash = null;
            int notificationCount = 0;
            Exception? notificationInspectionFailure = null;
            library.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                {
                    return;
                }
                notificationCount++;
                try
                {
                    BMSFile notifiedFirst = library.BMSFiles.Single(file => file.hash == expectedSourceHash);
                    Assert.IsFalse(string.Equals(notifiedFirst.path, expectedSourcePath, StringComparison.OrdinalIgnoreCase));
                    using var notifiedSongDb = new LR2SongDBExtended(songDbPath);
                    Assert.AreEqual(
                        1,
                        notifiedSongDb.ExecuteScalar<int>(
                            "SELECT COUNT(1) FROM song WHERE path = ?;",
                            notifiedFirst.path));
                }
                catch (Exception exception)
                {
                    notificationInspectionFailure = exception;
                }
            };

            OwnedChartHashIndexVersionedSnapshot? snapshotAfterFirstHash = null;
            InstalledChartLookupIndexSnapshot? snapshotAfterFirstInstalled = null;
            PlaylistLibraryResolveIndexSnapshot? snapshotAfterFirstPlaylist = null;
            for (int stepIndex = 0; stepIndex < 2; stepIndex++)
            {
                var step = stepIndex == 0 ? firstStep : secondStep;
                if (stepIndex > 0 && !isAutoRoute)
                {
                    library.ChartPackagesPending = CreatePackageCollection([step.Package]);
                }

                expectedSourcePath = step.FirstSourcePath;
                expectedSourceHash = step.FirstSource.hash;
                int previousNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
                int previousHashWorkCount = hashWork.Count;
                int previousInstalledWorkCount = installedWork.Count;
                int previousPlaylistWorkCount = playlistWork.Count;
                int previousNotificationCount = notificationCount;
                notificationInspectionFailure = null;

                LibraryMutationSessionReceipt sessionReceipt;
                if (isAutoRoute)
                {
                    PackageInstallCommandResult command = library.InstallChartPackagesAutoWithProgress(
                        [step.SourceDirectoryPath],
                        CancellationToken.None,
                        NullPackageInstallProgressWriter.Instance);
                    Assert.AreEqual(1, command.RegisteredPackages.Count);
                    sessionReceipt = command.SessionReceipt;
                }
                else if (isEstimatedRoute)
                {
                    PendingInstallBatchResult result = library.InstallPendingPackagesToEstimatedDestinationsWithReceipt(
                        [step.Package]);
                    Assert.AreEqual(0, result.FailedPackages.Count);
                    sessionReceipt = result.SessionReceipt;
                }
                else
                {
                    sessionReceipt = library.ForceInstallPendingPackagesWithReceipt(
                        [step.Package],
                        approveNormalInstallOverride: true,
                        approvedNormalInstallOverridePackages: null);
                }

                Assert.IsTrue(sessionReceipt.DurableCommit);
                Assert.IsFalse(sessionReceipt.HasRequiredFailure);
                Assert.IsFalse(sessionReceipt.HasDurableFinalizationFailure);
                Assert.AreEqual(stepIndex + 1, library.ChartPackagesInstalled.Count);
                Assert.AreEqual(0, library.ChartPackagesPending.Count);
                BMSFile firstInstalled = library.BMSFiles.Single(file => file.hash == step.FirstSource.hash);
                BMSFile secondInstalled = library.BMSFiles.Single(file => file.hash == step.SecondSource.hash);
                string firstDestinationPath = firstInstalled.path;
                string secondDestinationPath = secondInstalled.path;
                Assert.IsFalse(string.Equals(firstDestinationPath, step.FirstSourcePath, StringComparison.OrdinalIgnoreCase));
                Assert.IsFalse(string.Equals(secondDestinationPath, step.SecondSourcePath, StringComparison.OrdinalIgnoreCase));
                StringAssert.StartsWith(firstDestinationPath, installRootPath);
                StringAssert.StartsWith(secondDestinationPath, installRootPath);
                Assert.IsTrue(File.Exists(firstDestinationPath));
                Assert.IsTrue(File.Exists(secondDestinationPath));
                CollectionAssert.AreEqual(
                    new byte[] { 1, 2, 3 },
                    File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(firstDestinationPath)!, step.FirstResourceName + ".wav")));
                CollectionAssert.AreEqual(
                    new byte[] { 1, 2, 3 },
                    File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(secondDestinationPath)!, step.SecondResourceName + ".wav")));
                PackageChartEntry installedEntry = library.ChartPackagesInstalled
                    .SelectMany(package => package.ChartEntries)
                    .Single(entry => entry.Chart.Md5 == step.FirstSource.hash);
                Assert.AreEqual(firstDestinationPath, installedEntry.Chart.Path);

                using (var verifySongDb = new LR2SongDBExtended(songDbPath))
                {
                    Assert.AreEqual(1, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ?;", firstDestinationPath));
                    Assert.AreEqual(1, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ?;", secondDestinationPath));
                    Assert.AreEqual(0, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ?;", step.FirstSourcePath));
                    Assert.AreEqual(0, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ?;", step.SecondSourcePath));
                }

                OwnedChartHashIndexVersionedSnapshot updatedHash = library.GetOwnedChartHashIndexSnapshot();
                OwnedChartHashIndexVersionedSnapshot cachedHash = library.GetOwnedChartHashIndexSnapshot();
                BMSLibrary.InstalledPrimaryHashWarmupResult updatedPrimary =
                    library.WarmInstalledPrimaryHashLookup("u5c2_preflight_primary_after_install_" + stepIndex);
                IPrimaryHashLookup currentPrimary =
                    OwnedChartCollectionTestSupport.InvokeCreateInstalledChartKeySnapshotExcludingCharts(library, []);
                Assert.AreEqual(backgroundCount + ((stepIndex + 1) * 2), updatedPrimary.PrimaryHashCount);
                Assert.IsTrue(currentPrimary.ContainsPrimaryHash(backgroundFiles[0].hash));
                Assert.IsTrue(currentPrimary.ContainsPrimaryHash(firstInstalled.hash));
                Assert.IsTrue(currentPrimary.ContainsPrimaryHash(secondInstalled.hash));
                InstalledChartLookupIndexSnapshot updatedInstalled =
                    OwnedChartCollectionTestSupport.InvokeCreateInstalledChartLookupSnapshot(library);
                InstalledChartLookupIndexSnapshot cachedInstalled =
                    OwnedChartCollectionTestSupport.InvokeCreateInstalledChartLookupSnapshot(library);
                PlaylistLibraryResolveIndexSnapshot updatedPlaylist = library.GetPlaylistLibraryResolveIndexSnapshot(
                    CancellationToken.None,
                    out bool updatedPlaylistCacheHit,
                    out int updatedPlaylistStaleRetries);
                PlaylistLibraryResolveIndexSnapshot cachedPlaylist = library.GetPlaylistLibraryResolveIndexSnapshot(
                    CancellationToken.None,
                    out bool cachedPlaylistCacheHit,
                    out int cachedPlaylistStaleRetries);
                Assert.AreSame(updatedHash, cachedHash);
                Assert.AreSame(updatedInstalled, cachedInstalled);
                Assert.AreSame(updatedPlaylist, cachedPlaylist);
                Assert.IsTrue(updatedPlaylistCacheHit);
                Assert.AreEqual(0, updatedPlaylistStaleRetries);
                Assert.IsTrue(cachedPlaylistCacheHit);
                Assert.AreEqual(0, cachedPlaylistStaleRetries);
                Assert.IsTrue(oldHash.ContainsMd5(backgroundFiles[0].hash));
                Assert.IsTrue(oldInstalled.ContainsPrimaryHash(backgroundFiles[0].hash));
                Assert.IsTrue(oldPrimary.ContainsPrimaryHash(backgroundFiles[0].hash));
                Assert.IsFalse(oldPrimary.ContainsPrimaryHash(firstStep.FirstSource.hash));
                Assert.IsTrue(oldPlaylist.ContainsCandidate(LibraryChartKind.Bms, backgroundFiles[0].path));
                Assert.IsTrue(updatedHash.ContainsMd5(backgroundFiles[0].hash));
                Assert.IsTrue(updatedHash.ContainsMd5(firstInstalled.hash));
                Assert.IsTrue(updatedHash.ContainsMd5(secondInstalled.hash));
                Assert.IsTrue(updatedInstalled.ContainsPrimaryHash(firstInstalled.hash));
                Assert.IsTrue(updatedInstalled.ContainsPrimaryHash(secondInstalled.hash));
                Assert.IsTrue(updatedPlaylist.ContainsCandidate(LibraryChartKind.Bms, firstDestinationPath));
                Assert.IsTrue(updatedPlaylist.ContainsCandidate(LibraryChartKind.Bms, secondDestinationPath));

                if (stepIndex == 0)
                {
                    snapshotAfterFirstHash = updatedHash;
                    snapshotAfterFirstInstalled = updatedInstalled;
                    snapshotAfterFirstPlaylist = updatedPlaylist;
                }
                else
                {
                    // 2回目のroot更新後も、1回目のimmutable snapshotはその内容を保持します。
                    Assert.IsTrue(snapshotAfterFirstHash!.ContainsMd5(firstStep.FirstSource.hash));
                    Assert.IsTrue(snapshotAfterFirstInstalled!.ContainsPrimaryHash(firstStep.FirstSource.hash));
                    Assert.IsTrue(snapshotAfterFirstPlaylist!.ContainsCandidate(
                        LibraryChartKind.Bms,
                        library.BMSFiles.Single(file => file.hash == firstStep.FirstSource.hash).path));
                }

                NormalLibraryRefreshNotificationBatch notificationBatch =
                    library.GetNormalLibraryRefreshNotificationsAfter(previousNotificationVersion);
                Assert.IsTrue(notificationCount > previousNotificationCount);
                Assert.IsNull(notificationInspectionFailure, notificationInspectionFailure?.ToString());
                Assert.IsTrue(notificationBatch.HasRefreshNotification);
                Assert.IsTrue(notificationBatch.NotifiesStorageRows);
                Assert.IsTrue(notificationBatch.NotifiesBmsFiles);
                Assert.IsTrue(notificationBatch.HasEffect(LibraryChartRefreshEffects.SourceChanged));

                string[] operationHashWork = [.. hashWork.Skip(previousHashWorkCount)];
                string[] operationInstalledWork = [.. installedWork.Skip(previousInstalledWorkCount)];
                string[] operationPlaylistWork = [.. playlistWork.Skip(previousPlaylistWorkCount)];
                Assert.IsFalse(operationHashWork.Contains("owned_hash_source_enumeration"));
                Assert.IsFalse(operationHashWork.Contains("owned_hash_source_entry_visited"));
                Assert.IsTrue(operationHashWork.Contains("owned_hash_delta_apply"));
                Assert.IsFalse(operationInstalledWork.Contains("installed_root_map_enumeration"));
                Assert.IsFalse(operationInstalledWork.Contains("installed_root_map_key_visited"));
                Assert.IsTrue(operationInstalledWork.Contains("installed_primary_hash_count_update"));
                Assert.IsTrue(
                    operationInstalledWork.Count(operation => operation == "installed_primary_hash_count_update") <= 8,
                    "warm installed lookup updated more primary hash entries than the fixed two-chart delta.");
                Assert.IsTrue(
                    operationInstalledWork.Count(operation => operation == "installed_directory_bucket_entry_copied") < backgroundCount,
                    "warm installed lookup copied an entire directory bucket.");
                Assert.IsFalse(operationPlaylistWork.Contains("playlist_resolve_source_enumeration"));
                Assert.IsFalse(operationPlaylistWork.Contains("playlist_resolve_full_root_enumeration"));
                Assert.IsFalse(operationPlaylistWork.Contains("playlist_resolve_full_root_key_visited"));
            }
        });
    }

    [TestMethod]
    public void InstallChartPackagesAuto_UnconvergedCatalogKeepsDiscoveredPackagePendingInsteadOfInstalling()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string installRoot = Path.Combine(tempRootPath, "Library");
            string sourceDirectory = Path.Combine(tempRootPath, "Incoming");
            Directory.CreateDirectory(installRoot);
            string sourceChartPath = CreateBmsFile(sourceDirectory, "chart.bms", "#TITLE Pending until file diff");
            var dialogService = new RecordingDialogService();
            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                new RealFileMutationService(),
                dialogService,
                new TestUiScheduler(() => null!),
                () => new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = false,
                    BMSInstallDir = installRoot,
                    FolderNameFormat = "%TITLE%",
                    KeepInstallablePackagesPending = false,
                    PendingInstallEstimateMaxParallelPackages = 1
                })
            {
                BMSFiles = [],
                BmsonSongs = [],
                SearchTargets = [installRoot],
                ChartPackagesPending = CreatePackageCollection([]),
                ChartPackagesInstalled = CreatePackageCollection([])
            };
            library.ResetCatalogPathConvergence(CatalogPathConvergenceBlockReason.StartupFileScanDisabled);

            List<ChartPackage> installed = library.InstallChartPackagesAuto([sourceDirectory]);

            Assert.AreEqual(0, installed.Count);
            Assert.AreEqual(1, library.ChartPackagesPending.Count);
            Assert.AreEqual(0, library.ChartPackagesInstalled.Count);
            Assert.IsTrue(File.Exists(sourceChartPath));
            Assert.AreEqual(1, dialogService.Messages.Count);
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

    /// <summary>
    /// 推定導入の分類を実入口から確認します。重複の除外、異なる DST の拒否、導入済み entry の空 DST、
    /// MD5 が異なる場合の独立性を、物理出力と canonical DB の結果で区別します。
    /// </summary>
    [TestMethod]
    [DataRow("duplicate")]
    [DataRow("different_destinations")]
    [DataRow("installed_without_destination")]
    [DataRow("different_md5_same_sha256")]
    public void InstallPendingPackagesToEstimatedDestinations_ClassifiesEntriesBeforeOneCanonicalApply(string scenario)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb((songDbPath, root) =>
        {
            bool hasBaseline = scenario is "installed_without_destination" or "different_md5_same_sha256";
            bool rejected = scenario == "different_destinations";
            bool duplicate = scenario == "duplicate";
            string source = Path.Combine(root, "Pending");
            string target = Path.Combine(root, "Installed", "Target");
            string firstPath = CreateBmsFile(hasBaseline ? Path.Combine(root, "Owned") : source,
                "first.bms", "#TITLE First");
            string secondPath = CreateBmsFile(source, "second.bms", duplicate ? "#TITLE First" : "#TITLE Second");
            BMSFile first = BMSFile.CreateBMSFileFromFile(firstPath);
            BMSFile second = BMSFile.CreateBMSFileFromFile(secondPath);
            if (scenario == "different_md5_same_sha256")
            {
                // 同一 SHA256 を明示し、BMS の primary identity が MD5 である契約を分離します。
                TestableBmsFile firstWithSharedSha = CreateFile(first.hash, firstPath);
                TestableBmsFile secondWithSharedSha = CreateFile(second.hash, secondPath);
                firstWithSharedSha.SetSha256(new string('a', 64));
                secondWithSharedSha.SetSha256(new string('a', 64));
                first = firstWithSharedSha;
                second = secondWithSharedSha;
            }
            PackageChartEntry firstEntry = ChartPackageTestExtensions.CreateEntryWithInstallDestination(
                first, hasBaseline ? string.Empty : target);
            PackageChartEntry secondEntry = ChartPackageTestExtensions.CreateEntryWithInstallDestination(
                second, rejected ? Path.Combine(root, "Installed", "Other") : target);
            ChartPackage package = ChartPackage.FromChartEntries(scenario == "different_md5_same_sha256"
                ? [secondEntry] : [firstEntry, secondEntry]);
            package.path = source;
            package.delete_parent = false;
            if (hasBaseline)
            {
                using var seed = new LR2SongDBExtended(songDbPath);
                seed.InsertOrReplace(first.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
            }
            var library = new TestBmsLibrary(songDbPath, null, null,
                new RealFileMutationService(), new RecordingDialogService(), new TestUiScheduler(() => null!),
                () => new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = false,
                    BMSInstallDir = Path.Combine(root, "Installed"),
                    DeletePendingPackageSourceAfterInstall = false,
                    EnableSmartComponentOverwrite = false
                })
            {
                BMSFiles = hasBaseline ? [first] : [],
                BmsonSongs = [],
                ChartPackagesPending = CreatePackageCollection([package]),
                ChartPackagesInstalled = CreatePackageCollection([])
            };

            PendingInstallBatchResult result = library.InstallPendingPackagesToEstimatedDestinationsWithReceipt([package]);

            Assert.AreEqual(!rejected, result.HasDurableCommit);
            Assert.AreEqual(rejected ? 0 : 1, result.SessionReceipt.ApplyCounts.InstalledTargetApplyCount);
            Assert.AreEqual(0, result.SessionReceipt.ApplyCounts.CatalogApplyCount);
            Assert.AreEqual(rejected ? 0 : 1, result.SessionReceipt.ApplyCounts.RequiredPublicationCount);
            Assert.AreEqual(rejected ? 1 : 0, library.ChartPackagesPending.Count);
            using var readback = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual((hasBaseline ? 1 : 0) + (rejected ? 0 : 1), readback.Table<LR2SongDB.song>().Count());
            if (rejected)
            {
                Assert.AreEqual(default(LibraryMutationSessionApplyCounts), result.SessionReceipt.ApplyCounts);
                Assert.IsTrue(File.Exists(firstPath));
                Assert.IsTrue(File.Exists(secondPath));
                Assert.AreNotEqual(firstEntry.Chart.InstallDestination, secondEntry.Chart.InstallDestination);
                return;
            }
            string installedPath = Path.Combine(target, duplicate ? "first.bms" : "second.bms");
            Assert.IsTrue(File.Exists(installedPath));
            Assert.AreEqual(1, readback.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ?;", installedPath));
            Assert.IsTrue(package.ChartEntries.All(entry => string.IsNullOrEmpty(entry.Chart.InstallDestination)));
            if (hasBaseline)
            {
                Assert.IsTrue(File.Exists(firstPath));
                Assert.AreEqual(1, readback.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ?;", firstPath));
            }
            if (duplicate)
            {
                Assert.IsTrue(File.Exists(secondPath), "未消費の重複譜面は source cleanup 設定に従って保持する。");
                Assert.IsFalse(File.Exists(Path.Combine(target, "second.bms")));
                Assert.IsFalse(secondEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
            }
        });
    }

    /// <summary>BMSON を実際に導入し、canonical row と package entry の更新に BMS adapter を要求しません。</summary>
    [TestMethod]
    public void InstallPendingPackagesToEstimatedDestinations_InstallsBmsonWithoutCompatibilityAdapter()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb((songDbPath, root) =>
        {
            string source = Path.Combine(root, "Pending");
            string target = Path.Combine(root, "Installed");
            Directory.CreateDirectory(source);
            string sourcePath = Path.Combine(source, "chart.bmson");
            File.WriteAllText(sourcePath, CreateBmsonJsonWithSound("sound.wav"));
            File.WriteAllBytes(Path.Combine(source, "sound.wav"), [1, 2, 3]);
            var entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(BmsonSongParser.Parse(sourcePath)));
            entry.ApplyInstallDestination(target, "Target", "Artist");
            ChartPackage package = ChartPackage.FromChartEntries([entry]);
            package.path = source;
            package.delete_parent = false;
            var library = new TestBmsLibrary(songDbPath, null, null,
                new RealFileMutationService(), new RecordingDialogService(), new TestUiScheduler(() => null!),
                () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false, BMSInstallDir = target })
            {
                BMSFiles = [], BmsonSongs = [],
                ChartPackagesPending = CreatePackageCollection([package]),
                ChartPackagesInstalled = CreatePackageCollection([])
            };

            PendingInstallBatchResult result = library.InstallPendingPackagesToEstimatedDestinationsWithReceipt([package]);

            Assert.IsTrue(result.HasDurableCommit);
            Assert.IsFalse(result.SessionReceipt.HasRequiredFailure);
            Assert.AreEqual(1, result.SessionReceipt.ApplyCounts.InstalledTargetApplyCount);
            Assert.AreEqual(0, library.BMSFiles.Count);
            Assert.AreEqual(1, library.BmsonSongs.Count);
            Assert.AreEqual(0, library.ChartPackagesPending.Count);
            Assert.IsNull(entry.GetBmsOwnerForTest());
            Assert.IsTrue(string.IsNullOrEmpty(entry.Chart.InstallDestination));
            string installedPath = Path.Combine(target, "chart.bmson");
            Assert.AreEqual(installedPath, library.BmsonSongs.Single().path);
            Assert.IsTrue(File.Exists(installedPath));
            using var readback = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0, readback.Table<LR2SongDB.song>().Count());
            Assert.AreEqual(installedPath, readback.Table<LR2SongDBExtended.bmson_song>().Single().path);
        });
    }

    /// <summary>先行成功との部分重複で保留した package の未導入 hash を、後続の所有として予約しません。</summary>
    [TestMethod]
    public void InstallChartPackagesAuto_OnlyPhysicalSuccessReservesHashesForLaterCandidates()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb((songDbPath, root) =>
        {
            string installed = Path.Combine(root, "Installed");
            string first = Path.Combine(root, "First");
            string mixed = Path.Combine(root, "Mixed");
            string third = Path.Combine(root, "Third");
            const string resources = "\r\n#BPM 120\r\n#WAV01 sound.wav\r\n#00111:01";
            CreateBmsFile(first, "first.bms", "#TITLE First" + resources);
            CreateBmsFile(mixed, "matching.bms", "#TITLE First" + resources);
            CreateBmsFile(mixed, "unmatched.bms", "#TITLE Third" + resources);
            CreateBmsFile(third, "third.bms", "#TITLE Third" + resources);
            foreach (string directory in new[] { first, mixed, third })
            {
                File.WriteAllBytes(Path.Combine(directory, "sound.wav"), [1, 2, 3]);
            }
            Directory.CreateDirectory(installed);
            var library = new TestBmsLibrary(songDbPath, null, null,
                new RealFileMutationService(), new RecordingDialogService(), new TestUiScheduler(() => null!),
                () => new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = false, BMSInstallDir = installed,
                    FolderNameFormat = "%TITLE%", KeepInstallablePackagesPending = false
                })
            {
                BMSFiles = [], BmsonSongs = [], SearchTargets = [installed],
                ChartPackagesPending = CreatePackageCollection([]),
                ChartPackagesInstalled = CreatePackageCollection([])
            };

            PackageInstallCommandResult result = library.InstallChartPackagesAutoWithProgress(
                [first, mixed, third], CancellationToken.None, NullPackageInstallProgressWriter.Instance);

            Assert.IsTrue(result.HasDurableCommit);
            Assert.IsFalse(result.HasRequiredFailure);
            Assert.AreEqual(1, result.SessionReceipt.ApplyCounts.InstalledTargetApplyCount);
            Assert.AreEqual(2, library.ChartPackagesInstalled.Count);
            ChartPackage pending = library.ChartPackagesPending.Single();
            Assert.AreEqual(mixed, pending.path);
            PackageChartEntry matching = pending.ChartEntries.Single(entry => Path.GetFileName(entry.Chart.Path) == "matching.bms");
            PackageChartEntry unmatched = pending.ChartEntries.Single(entry => Path.GetFileName(entry.Chart.Path) == "unmatched.bms");
            Assert.IsTrue(matching.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
            Assert.IsFalse(unmatched.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
            Assert.IsTrue(File.Exists(Path.Combine(installed, "First", "first.bms")));
            Assert.IsTrue(File.Exists(Path.Combine(installed, "Third", "third.bms")));
            using var readback = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(2, readback.Table<LR2SongDB.song>().Count());
        });
    }

    /// <summary>通常導入先が設定された package の未承認 force は、BMSON adapter を作らず no-op にします。</summary>
    [TestMethod]
    public void ForceInstallPendingPackages_RejectsUnapprovedDestinationWithoutMaterializingBmson()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb((songDbPath, root) =>
        {
            string source = Path.Combine(root, "Pending");
            string target = Path.Combine(root, "Installed");
            Directory.CreateDirectory(source);
            string sourcePath = Path.Combine(source, "chart.bmson");
            File.WriteAllText(sourcePath, CreateBmsonJsonWithSound("sound.wav"));
            var entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(BmsonSongParser.Parse(sourcePath)));
            entry.ApplyInstallDestination(target, "Target", "Artist");
            ChartPackage package = ChartPackage.FromChartEntries([entry]);
            package.path = source;
            var library = new TestBmsLibrary(songDbPath, null, null,
                new RealFileMutationService(), new RecordingDialogService(), new TestUiScheduler(() => null!),
                () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false, BMSInstallDir = target })
            {
                BMSFiles = [], BmsonSongs = [],
                ChartPackagesPending = CreatePackageCollection([package]),
                ChartPackagesInstalled = CreatePackageCollection([])
            };

            LibraryMutationSessionReceipt receipt = library.ForceInstallPendingPackagesWithReceipt(
                [package], approveNormalInstallOverride: false, approvedNormalInstallOverridePackages: null);

            Assert.IsFalse(receipt.DurableCommit);
            Assert.AreEqual(default(LibraryMutationSessionApplyCounts), receipt.ApplyCounts);
            Assert.AreSame(package, library.ChartPackagesPending.Single());
            Assert.AreEqual(target, entry.Chart.InstallDestination);
            Assert.IsNull(entry.GetBmsOwnerForTest());
            Assert.IsTrue(File.Exists(sourcePath));
            Assert.IsFalse(Directory.Exists(target));
            using var readback = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0, readback.Table<LR2SongDBExtended.bmson_song>().Count());
        });
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
    /// 同梱通常ファイルと既存ディレクトリの型衝突は、smart overwrite の設定にかかわらず
    /// パッケージ全体を変更前に拒否し、source と宛先を保持することを検証します。
    /// </summary>
    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void MovePackageFilesWithReceipt_RejectsBundledFileWhenDestinationIsDirectory(bool enableSmartOverwrite)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourceDirectoryPath = Path.Combine(tempDirectoryPath, "PendingPkg");
            string destinationDirectoryPath = Path.Combine(tempDirectoryPath, "Installed", "Pkg");
            string sourceChartPath = CreateBmsFile(sourceDirectoryPath, "chart.bms", "#TITLE Type conflict");
            string sourceBundledFilePath = Path.Combine(sourceDirectoryPath, "BGA");
            File.WriteAllText(sourceBundledFilePath, "bundled-file");
            Directory.CreateDirectory(destinationDirectoryPath);
            string destinationBundledDirectoryPath = Path.Combine(destinationDirectoryPath, "BGA");
            Directory.CreateDirectory(destinationBundledDirectoryPath);
            string firstSentinelPath = Path.Combine(destinationBundledDirectoryPath, "first.sentinel");
            string secondSentinelPath = Path.Combine(destinationBundledDirectoryPath, "second.sentinel");
            File.WriteAllText(firstSentinelPath, "first-sentinel");
            File.WriteAllText(secondSentinelPath, "second-sentinel");
            byte[] sourceChartBytes = File.ReadAllBytes(sourceChartPath);
            byte[] sourceBundledFileBytes = File.ReadAllBytes(sourceBundledFilePath);
            byte[] firstSentinelBytes = File.ReadAllBytes(firstSentinelPath);
            byte[] secondSentinelBytes = File.ReadAllBytes(secondSentinelPath);
            ChartPackage package = ChartPackageTestExtensions.CreatePackage([
                BMSFile.CreateBMSFileFromFile(sourceChartPath)]);
            package.path = sourceDirectoryPath;
            package.delete_parent = false;
            int durableCallbackCount = 0;

            FileDbMutationReceipt receipt = new BmsLibraryPackageInstallService().MovePackageFilesWithReceipt(
                package,
                destinationDirectoryPath,
                new BmsLibraryOptionsSnapshot
                {
                    EnableSmartComponentOverwrite = enableSmartOverwrite,
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
            Assert.IsFalse(receipt.DurableCommit);
            Assert.AreEqual(0, durableCallbackCount);
            Assert.IsNotNull(receipt.Failure);
            Assert.IsTrue(Directory.Exists(sourceDirectoryPath));
            CollectionAssert.AreEqual(sourceChartBytes, File.ReadAllBytes(sourceChartPath));
            CollectionAssert.AreEqual(sourceBundledFileBytes, File.ReadAllBytes(sourceBundledFilePath));
            Assert.IsTrue(Directory.Exists(destinationDirectoryPath));
            Assert.IsTrue(Directory.Exists(destinationBundledDirectoryPath));
            CollectionAssert.AreEqual(firstSentinelBytes, File.ReadAllBytes(firstSentinelPath));
            CollectionAssert.AreEqual(secondSentinelBytes, File.ReadAllBytes(secondSentinelPath));
            Assert.IsFalse(File.Exists(Path.Combine(destinationDirectoryPath, "chart.bms")));
        });
    }

    /// <summary>
    /// 保留画面の実入口では、型衝突 package だけを拒否し、前後の独立 package
    /// は durable な DB 登録と一覧更新まで継続することを検証します。
    /// </summary>
    [TestMethod]
    public void InstallPendingPackagesToEstimatedDestinations_RejectsConflictingPackageAndContinuesIndependentPackages()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string installRootPath = Path.Combine(tempRootPath, "pending-route-installed");
            string firstSourceDirectoryPath = Path.Combine(tempRootPath, "pending-route-first");
            string conflictSourceDirectoryPath = Path.Combine(tempRootPath, "pending-route-conflict");
            string thirdSourceDirectoryPath = Path.Combine(tempRootPath, "pending-route-third");
            string firstDestinationDirectoryPath = Path.Combine(installRootPath, "first");
            string conflictDestinationDirectoryPath = Path.Combine(installRootPath, "conflict");
            string thirdDestinationDirectoryPath = Path.Combine(installRootPath, "third");
            string firstChartPath = CreateBmsFile(firstSourceDirectoryPath, "first.bms", "#TITLE Pending first");
            string conflictChartPath = CreateBmsFile(conflictSourceDirectoryPath, "conflict.bms", "#TITLE Pending conflict");
            string thirdChartPath = CreateBmsFile(thirdSourceDirectoryPath, "third.bms", "#TITLE Pending third");
            string conflictBundledFilePath = Path.Combine(conflictSourceDirectoryPath, "BGA");
            File.WriteAllText(conflictBundledFilePath, "conflicting bundled file");
            Directory.CreateDirectory(firstDestinationDirectoryPath);
            Directory.CreateDirectory(conflictDestinationDirectoryPath);
            Directory.CreateDirectory(thirdDestinationDirectoryPath);
            string conflictDestinationDirectoryPathForBundledFile = Path.Combine(conflictDestinationDirectoryPath, "BGA");
            Directory.CreateDirectory(conflictDestinationDirectoryPathForBundledFile);
            string conflictSentinelPath = Path.Combine(conflictDestinationDirectoryPathForBundledFile, "sentinel.txt");
            File.WriteAllText(conflictSentinelPath, "destination sentinel");

            ChartPackage firstPackage = ChartPackageTestExtensions.CreatePackage(
                ChartPackageTestExtensions.CreateEntryWithInstallDestination(
                    BMSFile.CreateBMSFileFromFile(firstChartPath),
                    firstDestinationDirectoryPath));
            ChartPackage conflictPackage = ChartPackageTestExtensions.CreatePackage(
                ChartPackageTestExtensions.CreateEntryWithInstallDestination(
                    BMSFile.CreateBMSFileFromFile(conflictChartPath),
                    conflictDestinationDirectoryPath));
            ChartPackage thirdPackage = ChartPackageTestExtensions.CreatePackage(
                ChartPackageTestExtensions.CreateEntryWithInstallDestination(
                    BMSFile.CreateBMSFileFromFile(thirdChartPath),
                    thirdDestinationDirectoryPath));
            firstPackage.path = firstSourceDirectoryPath;
            conflictPackage.path = conflictSourceDirectoryPath;
            thirdPackage.path = thirdSourceDirectoryPath;
            firstPackage.delete_parent = conflictPackage.delete_parent = thirdPackage.delete_parent = false;

            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                new RealFileMutationService(),
                new RecordingDialogService(),
                new TestUiScheduler(() => null!),
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
                ChartPackagesPending = CreatePackageCollection([firstPackage, conflictPackage, thirdPackage]),
                ChartPackagesInstalled = CreatePackageCollection([])
            };

            PendingInstallBatchResult result = library.InstallPendingPackagesToEstimatedDestinationsWithReceipt(
                [firstPackage, conflictPackage, thirdPackage]);

            Assert.IsTrue(result.SessionReceipt.DurableCommit);
            Assert.IsFalse(result.SessionReceipt.HasRequiredFailure);
            Assert.AreEqual(2, result.PendingPackagesToRemove.Count);
            Assert.AreEqual(2, result.DeferredInstalledPackages.Count);
            Assert.AreEqual(1, result.FailedPackages.Count);
            Assert.AreSame(conflictPackage, result.FailedPackages.Single());
            Assert.AreEqual(1, result.SessionReceipt.ItemFailures.Count);
            Assert.IsTrue(result.SessionReceipt.ItemFailures[0].IsDestinationTypeConflictRefusal);
            Assert.AreEqual(1, result.DestinationTypeConflicts.Count);
            Assert.AreEqual(conflictBundledFilePath, result.DestinationTypeConflicts[0].SourcePath);
            Assert.AreEqual(
                conflictDestinationDirectoryPathForBundledFile,
                result.DestinationTypeConflicts[0].DestinationPath);
            Assert.IsFalse(result.DestinationTypeConflicts[0].ExpectedIsDirectory);
            Assert.IsTrue(result.DestinationTypeConflicts[0].ExistingIsDirectory);

            Assert.IsTrue(File.Exists(Path.Combine(firstDestinationDirectoryPath, "first.bms")));
            Assert.IsTrue(File.Exists(Path.Combine(thirdDestinationDirectoryPath, "third.bms")));
            Assert.IsTrue(File.Exists(conflictChartPath));
            Assert.IsTrue(File.Exists(conflictBundledFilePath));
            Assert.IsTrue(Directory.Exists(conflictDestinationDirectoryPathForBundledFile));
            Assert.AreEqual("destination sentinel", File.ReadAllText(conflictSentinelPath));
            Assert.AreEqual(1, library.ChartPackagesPending.Count);
            Assert.AreSame(conflictPackage, library.ChartPackagesPending.Single());
            Assert.AreEqual(2, library.ChartPackagesInstalled.Count);
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
                    conflictChartPath));
            Assert.AreEqual(
                1,
                verifySongDb.ExecuteScalar<int>(
                    "SELECT COUNT(1) FROM song WHERE path = ?;",
                    Path.Combine(thirdDestinationDirectoryPath, "third.bms")));
        });
    }

    /// <summary>
    /// 操作開始前に消えた source は item failure として保持し、正常な後続を継続します。
    /// 欠落 package と同じ hash の後続も、予約を成功扱いせず導入できることを検証します。
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void InstallPendingPackages_RejectsMissingSourceAndContinuesIndependentPackages(bool force)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb((songDbPath, root) =>
        {
            string installationRoot = Path.Combine(root, "Installed");
            string[] sources = [Path.Combine(root, "First"), Path.Combine(root, "Missing"), Path.Combine(root, "Last")];
            string[] titles = ["First", "Shared", "Shared"];
            var packages = new List<ChartPackage>();
            for (int i = 0; i < sources.Length; i++)
            {
                string chartPath = CreateBmsFile(sources[i], "chart.bms", "#TITLE " + titles[i]);
                ChartPackage package = ChartPackageTestExtensions.CreatePackage(
                    ChartPackageTestExtensions.CreateEntryWithInstallDestination(
                        BMSFile.CreateBMSFileFromFile(chartPath),
                        Path.Combine(installationRoot, titles[i])));
                package.path = sources[i];
                package.delete_parent = false;
                packages.Add(package);
            }
            using (var seedSongDb = new LR2SongDBExtended(songDbPath))
            {
                seedSongDb.CreateTable<LR2SongDBExtended.install>();
                foreach (ChartPackage package in packages)
                {
                    seedSongDb.InsertOrReplace(package, typeof(LR2SongDBExtended.install));
                }
            }
            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                new RealFileMutationService(),
                new RecordingDialogService(),
                new TestUiScheduler(() => null!),
                () => new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = false,
                    BMSInstallDir = installationRoot,
                    FolderNameFormat = "%TITLE%",
                    DeletePendingPackageSourceAfterInstall = false,
                    EnableSmartComponentOverwrite = false,
                    KeepSmartOverwriteProtectedFilesByRenaming = false
                })
            {
                BMSFiles = [],
                BmsonSongs = [],
                ChartPackagesPending = CreatePackageCollection(packages),
                ChartPackagesInstalled = CreatePackageCollection([])
            };
            // 保留一覧への読み込み後、導入操作を開始する前に source が削除された状態です。
            Directory.Delete(sources[1], recursive: true);

            LibraryMutationSessionReceipt receipt = force
                ? library.ForceInstallPendingPackagesWithReceipt(
                    packages, approveNormalInstallOverride: true,
                    approvedNormalInstallOverridePackages: null, reportAtTerminal: true)
                : library.InstallPendingPackagesToEstimatedDestinationsWithReceipt(
                    packages, reportAtTerminal: true).SessionReceipt;

            Assert.IsTrue(receipt.DurableCommit);
            Assert.IsTrue(receipt.HasRequiredFailure);
            Assert.IsFalse(receipt.HasDurableFinalizationFailure);
            Assert.IsNull(receipt.PhysicalFailure);
            Assert.IsNull(receipt.FailedTarget);
            Assert.AreEqual(0, receipt.UnprocessedTargets.Count);
            Assert.AreEqual(0, receipt.DestinationTypeConflicts.Count);
            Assert.AreEqual(1, receipt.ItemFailures.Count);
            Assert.AreEqual(sources[1], receipt.ItemFailures[0].Target.SourcePath);
            Assert.IsInstanceOfType(receipt.ItemFailures[0].Failure, typeof(FileNotFoundException));
            Assert.AreEqual(1, library.ChartPackagesPending.Count);
            Assert.AreSame(packages[1], library.ChartPackagesPending.Single());
            Assert.AreEqual(sources[1], packages[1].path);
            Assert.AreEqual(
                Path.Combine(installationRoot, "Shared"),
                packages[1].ChartEntries.Single().Chart.InstallDestination);
            Assert.AreEqual(2, library.ChartPackagesInstalled.Count);
            Assert.IsTrue(File.Exists(Path.Combine(installationRoot, "First", "chart.bms")));
            Assert.IsTrue(File.Exists(Path.Combine(installationRoot, "Shared", "chart.bms")));
            Assert.IsFalse(Directory.Exists(sources[1]));

            using var verifySongDb = new LR2SongDBExtended(songDbPath);
            CollectionAssert.AreEquivalent(
                new[]
                {
                    Path.Combine(installationRoot, "First", "chart.bms"),
                    Path.Combine(installationRoot, "Shared", "chart.bms")
                },
                verifySongDb.Table<LR2SongDB.song>().Select(row => row.path).ToArray());
            Assert.AreEqual(sources[1], verifySongDb.Table<LR2SongDBExtended.install>().Single().path);
        });
    }

    /// <summary>
    /// 宛先 root 自体が通常ファイルの場合も、root の作成前に型衝突として拒否し、
    /// source と既存ファイルを変更しないことを検証します。
    /// </summary>
    [TestMethod]
    public void MovePackageFilesWithReceipt_RejectsFileDestinationRootBeforeMutation()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourceDirectoryPath = Path.Combine(tempDirectoryPath, "PendingPkg");
            string destinationPath = Path.Combine(tempDirectoryPath, "Installed");
            string sourceChartPath = CreateBmsFile(sourceDirectoryPath, "chart.bms", "#TITLE Root conflict");
            File.WriteAllText(destinationPath, "existing destination file");
            byte[] sourceBytes = File.ReadAllBytes(sourceChartPath);
            int durableCallbackCount = 0;
            ChartPackage package = ChartPackageTestExtensions.CreatePackage(
                BMSFile.CreateBMSFileFromFile(sourceChartPath));
            package.path = sourceDirectoryPath;
            package.delete_parent = false;

            FileDbMutationReceipt receipt = new BmsLibraryPackageInstallService().MovePackageFilesWithReceipt(
                package,
                destinationPath,
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
            Assert.IsFalse(receipt.DurableCommit);
            Assert.AreEqual(0, durableCallbackCount);
            FileDbMutationDestinationTypeConflict conflict = receipt.DestinationTypeConflicts.Single();
            Assert.AreEqual(sourceDirectoryPath, conflict.SourcePath);
            Assert.AreEqual(destinationPath, conflict.DestinationPath);
            Assert.IsTrue(conflict.ExpectedIsDirectory);
            Assert.IsFalse(conflict.ExistingIsDirectory);
            Assert.IsTrue(Directory.Exists(sourceDirectoryPath));
            CollectionAssert.AreEqual(sourceBytes, File.ReadAllBytes(sourceChartPath));
            Assert.AreEqual("existing destination file", File.ReadAllText(destinationPath));
        });
    }

    /// <summary>
    /// 明示した候補順の後半に型衝突がある場合、前半の候補も公開せずに
    /// パッケージの変更を全体として拒否することを検証します。
    /// </summary>
    [TestMethod]
    public void BuildComponentMovePlan_ExplicitOrderedCandidatesRejectWholePackageBeforePromotion()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourceDirectoryPath = Path.Combine(tempDirectoryPath, "PendingPkg");
            string destinationDirectoryPath = Path.Combine(tempDirectoryPath, "Installed", "Pkg");
            Directory.CreateDirectory(sourceDirectoryPath);
            Directory.CreateDirectory(destinationDirectoryPath);
            string firstSourcePath = Path.Combine(sourceDirectoryPath, "normal.bin");
            string secondSourcePath = Path.Combine(sourceDirectoryPath, "BGA");
            File.WriteAllText(firstSourcePath, "normal");
            File.WriteAllText(secondSourcePath, "bundled-file");
            string secondDestinationPath = Path.Combine(destinationDirectoryPath, "BGA");
            Directory.CreateDirectory(secondDestinationPath);
            string sentinelPath = Path.Combine(secondDestinationPath, "sentinel.txt");
            File.WriteAllText(sentinelPath, "sentinel");
            ComponentMovePlanBuildResult componentPlan = new BmsLibraryPackageInstallService().BuildComponentMovePlan(
                [firstSourcePath, secondSourcePath],
                destinationDirectoryPath,
                excludedComponentPaths: null);
            Assert.AreEqual(2, componentPlan.PlanItems.Count);
            Assert.AreEqual(firstSourcePath, componentPlan.PlanItems[0].SourcePath);
            Assert.AreEqual(secondSourcePath, componentPlan.PlanItems[1].SourcePath);
            var mutationPaths = componentPlan.PlanItems.Select(item =>
            {
                string stagingPath = LongPathFileSystem.CreateMutationSiblingPath(item.DestinationPath, "stage");
                string backupPath = LongPathFileSystem.EntryExists(item.DestinationPath)
                    ? LongPathFileSystem.CreateMutationSiblingPath(item.DestinationPath, "backup")
                    : string.Empty;
                return new FileDbMutationPathPlan(
                    item.SourcePath,
                    item.DestinationPath,
                    stagingPath,
                    backupPath,
                    isDirectory: false);
            }).ToList();
            FileDbMutationPlan plan = new(
                Guid.NewGuid(),
                mutationPaths,
                [firstSourcePath, secondSourcePath],
                [],
                recursiveSourceCleanup: false);
            bool durableCallbackCalled = false;
            FileDbMutationReceipt receipt = new FileDbMutationExecutor(
                plan,
                new RealFileMutationService(),
                new FileMutationOptions(ReadOnlyNormalizationScope.TargetOnly),
                new FileMutationOptions(ReadOnlyNormalizationScope.RecursiveDirectoryTree))
                .Execute(() =>
                {
                    durableCallbackCalled = true;
                    return FileDbMutationCommitResult.Durable();
                });

            Assert.AreEqual(FileDbMutationTerminalState.Failed, receipt.TerminalState);
            Assert.IsFalse(receipt.DurableCommit);
            Assert.IsFalse(durableCallbackCalled);
            Assert.IsInstanceOfType(receipt.Failure, typeof(FileDbMutationDestinationTypeConflictException));
            Assert.IsTrue(File.Exists(firstSourcePath));
            Assert.IsFalse(File.Exists(Path.Combine(destinationDirectoryPath, "normal.bin")));
            Assert.IsTrue(File.Exists(secondSourcePath));
            Assert.IsTrue(Directory.Exists(secondDestinationPath));
            Assert.AreEqual("sentinel", File.ReadAllText(sentinelPath));
            foreach (FileDbMutationPathPlan path in mutationPaths)
            {
                Assert.IsFalse(LongPathFileSystem.FileExists(path.StagingPath));
                Assert.IsFalse(LongPathFileSystem.EntryExists(path.BackupPath));
            }
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
                new TestUiScheduler(() => null!),
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
            Exception? callbackFailure = null;
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

            LibraryMutationSessionReceipt receipt = library.ForceInstallPendingPackagesWithReceipt(
                [package],
                approveNormalInstallOverride: true,
                approvedNormalInstallOverridePackages: null);

            Assert.IsNull(callbackFailure, callbackFailure?.ToString());
            Assert.IsTrue(firstNotificationCount > 0);
            Assert.IsTrue(secondNotificationCount > 0);
            Assert.IsTrue(firstNotificationAcquiredLease);
            Assert.IsTrue(secondNotificationAcquiredLease);
            Assert.IsTrue(receipt.DurableCommit);
            Assert.IsFalse(receipt.HasRequiredFailure);
            Assert.IsFalse(receipt.ManualRecoveryRequired);
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
    /// auto-install の package physical prepare がすべて成功しても canonical transaction が失敗した場合、
    /// DB/package/resource publication を部分適用せず、prepared destination を session recovery facts に保持します。
    /// </summary>
    [TestMethod]
    public void InstallChartPackagesAutoWithProgress_CanonicalApplyFailurePublishesNoPartialPackageState()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string installRootPath = Path.Combine(tempRootPath, "AutoInstalled");
            string firstSourceDirectoryPath = Path.Combine(tempRootPath, "AutoPendingFirst");
            string secondSourceDirectoryPath = Path.Combine(tempRootPath, "AutoPendingSecond");
            string thirdSourceDirectoryPath = Path.Combine(tempRootPath, "AutoPendingThird");
            CreateBmsFile(firstSourceDirectoryPath, "first.bms", "#TITLE Auto Prefix First");
            CreateBmsFile(secondSourceDirectoryPath, "second.bms", "#TITLE Auto Prefix Second");
            CreateBmsFile(thirdSourceDirectoryPath, "third.bms", "#TITLE Auto Prefix Third");
            string firstDestinationChartPath = Path.Combine(installRootPath, "Auto Prefix First", "first.bms");
            string secondDestinationChartPath = Path.Combine(installRootPath, "Auto Prefix Second", "second.bms");
            string thirdDestinationChartPath = Path.Combine(installRootPath, "Auto Prefix Third", "third.bms");
            string firstDestinationDirectoryPath = Path.GetDirectoryName(firstDestinationChartPath)!;
            string secondDestinationDirectoryPath = Path.GetDirectoryName(secondDestinationChartPath)!;
            string thirdDestinationDirectoryPath = Path.GetDirectoryName(thirdDestinationChartPath)!;

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
                new RealFileMutationService(),
                new RecordingDialogService(),
                new TestUiScheduler(() => null!),
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

            Assert.IsFalse(commandResult.HasDurableCommit);
            Assert.IsTrue(commandResult.HasRequiredFailure);
            Assert.IsNotNull(commandResult.SessionReceipt.ApplyFailure);
            Assert.IsFalse(commandResult.ManualRecoveryRequired);
            Assert.AreEqual(0, commandResult.RegisteredPackages.Count);
            Assert.AreEqual(0, library.ChartPackagesInstalled.Count);
            Assert.AreEqual(0, library.ChartPackagesPending.Count);

            // session-wide DB failure does not invent per-item compensation. All physical prepares remain
            // observable for recovery and source cleanup has not run because the durable point was not reached.
            Assert.IsTrue(File.Exists(firstDestinationChartPath));
            Assert.IsTrue(File.Exists(secondDestinationChartPath));
            Assert.IsTrue(File.Exists(thirdDestinationChartPath));
            Assert.IsTrue(Directory.Exists(firstSourceDirectoryPath));
            Assert.IsTrue(Directory.Exists(secondSourceDirectoryPath));
            Assert.IsTrue(Directory.Exists(thirdSourceDirectoryPath));
            // Auto-named directory packages are prepared as one directory mutation per package,
            // so session recovery facts retain the promoted directory boundary rather than
            // synthesizing per-chart mutation paths that were never executor targets.
            Assert.IsTrue(commandResult.RecoveryPaths.Contains(firstDestinationDirectoryPath, StringComparer.OrdinalIgnoreCase));
            Assert.IsTrue(commandResult.RecoveryPaths.Contains(secondDestinationDirectoryPath, StringComparer.OrdinalIgnoreCase));
            Assert.IsTrue(commandResult.RecoveryPaths.Contains(thirdDestinationDirectoryPath, StringComparer.OrdinalIgnoreCase));

            using var verifySongDb = new LR2SongDBExtended(songDbPath);
            List<LR2SongDB.song> installedRows = [.. verifySongDb.Table<LR2SongDB.song>()];
            Assert.IsFalse(installedRows.Any(row => string.Equals(
                row.path,
                firstDestinationChartPath,
                StringComparison.OrdinalIgnoreCase)));
            Assert.IsFalse(installedRows.Any(row => string.Equals(
                row.path,
                secondDestinationChartPath,
                StringComparison.OrdinalIgnoreCase)));
            Assert.IsFalse(installedRows.Any(row => string.Equals(
                row.path,
                thirdDestinationChartPath,
                StringComparison.OrdinalIgnoreCase)));
            Assert.AreEqual(0, verifySongDb.Table<LR2SongDBExtended.install>().Count());
        });
    }

    /// <summary>
    /// 先行 package の physical success を durable prefix として一回 commit し、
    /// current physical failure と未処理 suffix を同じ session terminal に保持します。
    /// </summary>
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public void ForceInstallPendingPackages_PreservesPhysicalSuccessPrefixAndUnprocessedSuffix(
        bool reportAtTerminal,
        bool sourceFileMissingDuringCopy)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string installRootPath = Path.Combine(tempRootPath, "Installed");
            string firstSourceDirectoryPath = Path.Combine(tempRootPath, "PendingFirst");
            string secondSourceDirectoryPath = Path.Combine(tempRootPath, "PendingSecond");
            string thirdSourceDirectoryPath = Path.Combine(tempRootPath, "PendingThird");
            string firstChartPath = CreateBmsFileWithResources(
                firstSourceDirectoryPath, "first.bms", "#TITLE Prefix First", "first-resource");
            string secondChartPath = CreateBmsFileWithResources(
                secondSourceDirectoryPath, "second.bms", "#TITLE Prefix Second", "second-resource");
            string thirdChartPath = CreateBmsFileWithResources(
                thirdSourceDirectoryPath, "third.bms", "#TITLE Prefix Third", "third-resource");
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

            string firstDestinationDirectoryPath = Path.Combine(installRootPath, "Prefix First");
            string secondDestinationDirectoryPath = Path.Combine(installRootPath, "Prefix Second");
            string secondDestinationChartPath = Path.Combine(secondDestinationDirectoryPath, "second.bms");
            var fileMutations = new RealFileMutationService();
            fileMutations.BeforeCopy = sourcePath =>
            {
                if (string.Equals(sourcePath, secondSourceDirectoryPath, StringComparison.OrdinalIgnoreCase)
                    || sourcePath.StartsWith(secondSourceDirectoryPath + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (sourceFileMissingDuringCopy)
                    {
                        // 開始前の存在確認を通過後、コピー中に一ファイルが失われるケースです。
                        // 同じ FileNotFoundException でも事前拒否へ格下げしてはいけません。
                        File.Delete(secondChartPath);
                        throw new FileNotFoundException("source disappeared during copy", secondChartPath);
                    }
                    throw new IOException("forced second force-install physical failure");
                }
            };

            var dialogs = new FileDbReportRecordingDialogs();
            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                fileMutations,
                dialogs,
                new TestUiScheduler(() => null!),
                () => new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = false,
                    FolderNameFormat = "%TITLE%",
                    BMSInstallDir = installRootPath
                });
            library.BMSFiles = [firstChart, secondChart, thirdChart];
            library.ChartPackagesPending = CreatePackageCollection([firstPackage, secondPackage, thirdPackage]);
            library.ChartPackagesInstalled = CreatePackageCollection([]);

            LibraryResourceIndexOwner resourceOwner = LibraryResourceIndexTestSupport.GetOwner(library);
            resourceOwner.Replace(LibraryResourceIndex.CreateFromNativeCanonicalArrays(
                [], [], [], [], [], [], [], new Dictionary<uint, string[]>(), new Dictionary<uint, string[]>(), new Dictionary<uint, string[]>()));
            LibraryResourceIndexSnapshot before = resourceOwner.CaptureSnapshot();

            LibraryMutationSessionReceipt receipt = library.ForceInstallPendingPackagesWithReceipt(
                [firstPackage, secondPackage, thirdPackage],
                approveNormalInstallOverride: true,
                approvedNormalInstallOverridePackages: null, reportAtTerminal: reportAtTerminal);
            Assert.AreEqual(reportAtTerminal ? 0 : 1, dialogs.ModelMessages);

            Assert.IsTrue(receipt.DurableCommit);
            Assert.IsTrue(receipt.HasRequiredFailure);
            Assert.IsNotNull(receipt.PhysicalFailure);
            Assert.AreEqual(0, receipt.ItemFailures.Count);
            Assert.IsFalse(receipt.ManualRecoveryRequired);
            Assert.IsNotNull(receipt.FailedTarget);
            StringAssert.StartsWith(receipt.FailedTarget.SourcePath, secondSourceDirectoryPath);
            Assert.AreEqual(1, receipt.UnprocessedTargets.Count);
            Assert.AreEqual(thirdSourceDirectoryPath, receipt.UnprocessedTargets[0].SourcePath, ignoreCase: true);

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
            Assert.IsFalse(Directory.Exists(secondDestinationDirectoryPath));
            Assert.AreEqual(thirdSourceDirectoryPath, thirdPackage.path);
            Assert.IsTrue(Directory.Exists(thirdSourceDirectoryPath));
            Assert.IsFalse(Directory.Exists(Path.Combine(installRootPath, "Prefix Third")));

            LibraryResourceIndexSnapshot after = resourceOwner.CaptureSnapshot();
            Assert.AreEqual(before.Generation + 1, after.Generation);
            CollectionAssert.AreEquivalent(new[] { firstDestinationDirectoryPath },
                after.DirectoryLookupCache.Keys.ToArray());
            AssertResourceCandidates(after, "first-resource", firstDestinationDirectoryPath);
            AssertResourceCandidates(after, "second-resource");
            // Each package has a distinct key so failed and unexecuted resources cannot hide
            // behind a candidate already published for the successful prefix.
            AssertResourceCandidates(after, "third-resource");
            foreach (string resourceKey in new[] { "first-resource", "second-resource", "third-resource" })
            {
                AssertResourceCandidates(before, resourceKey);
            }
            Assert.AreEqual(0, before.DirectoryLookupCache.Count);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 },
                File.ReadAllBytes(Path.Combine(firstDestinationDirectoryPath, "first-resource.wav")));
            Assert.IsTrue(File.Exists(Path.Combine(firstDestinationDirectoryPath, "first-resource.png")));
            Assert.IsTrue(File.Exists(Path.Combine(firstDestinationDirectoryPath, "first-resource.mp4")));
            Assert.IsTrue(File.Exists(thirdChartPath));

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
            PackageChartEntry? packageEntry = packageEntries.FirstOrDefault(entry => IsSamePackageChartTarget(entry, targetFile));
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

    private static string CreateBmsFileWithResources(
        string directoryPath,
        string fileName,
        string titleLine,
        string resourceKey)
    {
        string chartPath = CreateBmsFile(directoryPath, fileName, titleLine
            + "\r\n#BPM 120\r\n#WAV01 " + resourceKey + ".wav"
            + "\r\n#BMP01 " + resourceKey + ".png"
            + "\r\n#BMP02 " + resourceKey + ".mp4\r\n#00111:01\r\n");
        // The fixture only scans, checks and transfers these bytes; it never decodes media.
        File.WriteAllBytes(Path.Combine(directoryPath, resourceKey + ".wav"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(directoryPath, resourceKey + ".png"), [4, 5]);
        File.WriteAllBytes(Path.Combine(directoryPath, resourceKey + ".mp4"), [6, 7]);
        return chartPath;
    }

    private static void AssertResourceCandidates(
        LibraryResourceIndexSnapshot snapshot,
        string resourceKey,
        params string[] expectedDirectories)
    {
        uint hash = ChartResourceKeyHash.GetLookupHash(resourceKey);
        CollectionAssert.AreEquivalent(expectedDirectories,
            snapshot.DirectoryLookupCache.GetDirectoriesByAudioRelativeHash(hash).ToArray(), "Audio: " + resourceKey);
        CollectionAssert.AreEquivalent(expectedDirectories,
            snapshot.DirectoryLookupCache.GetDirectoriesByImageRelativeHash(hash).ToArray(), "Image: " + resourceKey);
        CollectionAssert.AreEquivalent(expectedDirectories,
            snapshot.DirectoryLookupCache.GetDirectoriesByMovieRelativeHash(hash).ToArray(), "Movie: " + resourceKey);
    }

    private static string CreateBmsonJsonWithSound(string soundName)
    {
        return CreateBmsonJsonWithSounds(soundName);
    }

    private static string CreateBmsonJsonWithSounds(params string[] soundNames)
    {
        string channels = string.Join(
            ",",
            (soundNames ?? [])
                .Select(soundName => "{\"name\":\"" + soundName + "\",\"notes\":[{\"x\":1,\"y\":0,\"l\":0}]}")
                .ToArray());
        return "{"
            + "\"version\":\"1.0.0\","
            + "\"info\":{\"title\":\"Bmson\",\"artist\":\"Artist\",\"mode_hint\":\"beat-7k\"},"
            + "\"sound_channels\":[" + channels + "]"
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
        FieldInfo? field = typeof(BMSLibrary).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        PropertyInfo? property = typeof(BMSLibrary).GetProperty(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        object? gateValue = field != null
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
        private BMSLibrary library = null!;
        private ChartPackage reentryPackage = null!;
        private Task reentryTask = null!;
        private int executorEntryObserved;

        internal bool ReentryCompletedDuringFilesystem { get; private set; }

        internal Exception? ReentryFailure { get; private set; }

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
        private BMSLibrary library = null!;
        private ChartPackage reentryPackage = null!;
        private Task reentryTask = null!;
        private int cleanupEntryObserved;

        internal bool ReentryCompletedDuringCleanup { get; private set; }

        internal Exception? ReentryFailure { get; private set; }

        internal PendingInstalledOnlyResourceOverwriteResult ReentryResult { get; private set; } = null!;

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
        private Action? dispose = dispose;

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
        /// <summary>Observes a source immediately before a real file or directory copy; unset by default.</summary>
        public Action<string>? BeforeCopy { get; set; }

        public void EnsureDirectory(string directoryPath, FileMutationOptions options = null!)
        {
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }
        }

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            string? destinationDirectoryPath = Path.GetDirectoryName(destinationPath);
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
            string? destinationParentDirectoryPath = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationParentDirectoryPath))
            {
                Directory.CreateDirectory(destinationParentDirectoryPath);
            }
            Directory.Move(sourcePath, destinationPath);
        }

        public void CopyFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            BeforeCopy?.Invoke(sourcePath);
            string? destinationParent = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationParent))
            {
                Directory.CreateDirectory(destinationParent);
            }
            File.Copy(sourcePath, destinationPath, overwrite);
        }

        public void CopyDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            BeforeCopy?.Invoke(sourcePath);
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
