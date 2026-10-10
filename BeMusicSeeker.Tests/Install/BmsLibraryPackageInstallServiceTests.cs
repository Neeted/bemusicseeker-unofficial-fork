using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualBasic.FileIO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed partial class BmsLibraryPackageInstallServiceTests
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
                        BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(path)), destination)
                };
                if (i == 0)
                {
                    string alternate = CreateBmsFile(sources[i], "alternate.bms", "#TITLE P0Alternate\r\n" + body);
                    entries.Add(ChartPackageTestExtensions.CreateEntryWithInstallDestination(
                        BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(alternate)), destination));
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
            ChartFile existing = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(existingPath));
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(existing), typeof(LR2SongDB.song));
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
                BmsCharts = [existing],
                BmsonCharts = [],
                ChartPackagesPending = CreatePackageCollection(packages),
                ChartPackagesInstalled = CreatePackageCollection([])
            };
            OwnedChartToken existingToken = library.BmsCharts.Single().Token;
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
            foreach (PackageChartEntry entry in packages.SelectMany(package => package.ChartEntries))
            {
                ChartFile ownedChart = library.BmsCharts.Single(chart => chart.Path == entry.Chart.Path);
                Assert.IsNotNull(entry.Chart.Token);
                Assert.AreSame(ownedChart.Token, entry.Chart.Token);
            }


            string[] installedPaths = names.Select(name => library.BmsCharts.Single(file =>
                Path.GetFileName(file.Path) == name).Path).ToArray();
            string[] destinations = installedPaths.Select(path => Path.GetDirectoryName(path)!).ToArray();
            Assert.AreEqual(2, destinations.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.IsTrue(library.BmsCharts.Any(file => Path.GetFileName(file.Path) == "alternate.bms"));
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
            using LR2SongDBExtended readback = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(4, readback.Table<LR2SongDB.song>().Count());
            Assert.AreEqual(1, readback.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ?;", existingPath));
            foreach (string installedPath in installedPaths)
            {
                Assert.AreEqual(1, readback.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ?;", installedPath));
            }
            Assert.AreSame(existingToken, library.BmsCharts.Single(file =>
                string.Equals(file.Path, existingPath, StringComparison.OrdinalIgnoreCase)).Token);
        });
    }

    /// <summary>一要求内の非交差導入を確定し、後続の実交差だけBusy未処理に残します。交差先へFS/DB作用を開始しません。</summary>
    [TestMethod]
    public void AutoInstall_ManagedIntersectionBusyPreservesNonIntersectingCommittedPackage()
    {

        WithTemporarySongDb((songDbPath, root) =>
        {
            string installRoot = Path.Combine(root, "Library");
            string managed = Path.Combine(root, "Managed");
            string firstSource = Path.Combine(root, "Incoming");
            string crossingSource = Path.Combine(managed, "Crossing");
            Directory.CreateDirectory(installRoot);
            string firstPath = CreateBmsFile(firstSource, "first.bms", "#PLAYER 1\n#TITLE First");
            string crossingPath = CreateBmsFile(crossingSource, "cross.bms", "#PLAYER 1\n#TITLE Crossing");
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
                BMSInstallDir = installRoot,
                LR2CustomFolderOutputBaseDir = managed,
                FolderNameFormat = "%TITLE%",
                KeepInstallablePackagesPending = false
            };
            var library = new TestBmsLibrary(songDbPath, null, null, new RealFileMutationService(), new RecordingDialogService(),
                new TestUiScheduler(() => null!), () => options)
            {
                BmsCharts = [],
                BmsonCharts = [],
                SearchTargets = [installRoot],
                ChartPackagesPending = CreatePackageCollection([]),
                ChartPackagesInstalled = CreatePackageCollection([])
            };
            Assert.IsTrue(library.Lr2Synchronization.PlaylistOperationAdmission.TryEnter(out IDisposable held));
            try
            {
                PackageInstallCommandResult result = library.InstallChartPackagesAutoWithProgressAsync([firstSource, crossingSource],
                    CancellationToken.None, new RecordingPackageInstallProgressWriter()).GetAwaiter().GetResult();
                Assert.IsTrue(result.HasDurableCommit);
                Assert.IsTrue(result.HasRequiredFailure);
                Assert.AreEqual(1, result.RegisteredPackages.Count);
                Assert.AreEqual(1, result.SessionReceipt.ItemFailures.Count);
                StringAssert.Contains(result.SessionReceipt.ItemFailures.Single().Failure.Message, BeMusicSeeker.Properties.Resources.Warn_LibraryOperationBusy);
                Assert.IsFalse(File.Exists(firstPath));
                Assert.IsTrue(File.Exists(Path.Combine(installRoot, "First", "first.bms")));
                Assert.IsTrue(File.Exists(crossingPath));
                Assert.IsFalse(Directory.Exists(Path.Combine(installRoot, "Crossing")));
                Assert.IsTrue(string.Equals(crossingSource, library.ChartPackagesPending.Single().path, StringComparison.OrdinalIgnoreCase));
                using (LR2SongDBExtended db = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
                {
                    Assert.IsNotNull(db.Find<LR2SongDB.song>(Path.Combine(installRoot, "First", "first.bms")));
                    Assert.IsNull(db.Find<LR2SongDB.song>(Path.Combine(installRoot, "Crossing", "cross.bms")));
                    Assert.AreEqual(1, db.Table<LR2SongDBExtended.install>().Count());
                }
                Assert.IsFalse(library.OperationAdmission.IsActive);
            }
            finally { held.Dispose(); }
            Assert.IsTrue(File.Exists(crossingPath), "Busy対象は自動再実行せず次の明示要求へ残します。");
            Assert.AreEqual(1, library.ChartPackagesPending.Count);
        });
    }

    [TestMethod]
    public void RemovePendingPackages_DeletesManagedTemporaryPackageSource()
    {

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

        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "pending-invalid-extension");
            string sourceChartPath = CreateBmsFile(sourceDirectoryPath, "chart.bms", "#TITLE Pending rename");
            string destinationChartPath = Path.Combine(sourceDirectoryPath, "chart.bme");
            ChartFile sourceChart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(sourceChartPath));
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
                [(sourceChart)],
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

        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string firstDirectoryPath = Path.Combine(tempRootPath, "pending-invalid-extension-first");
            string secondDirectoryPath = Path.Combine(tempRootPath, "pending-invalid-extension-second");
            string thirdDirectoryPath = Path.Combine(tempRootPath, "pending-invalid-extension-third");
            string firstChartPath = CreateBmsFile(firstDirectoryPath, "first.bms", "#TITLE First failure");
            string secondChartPath = CreateBmsFile(secondDirectoryPath, "second.bms", "#TITLE Second failure");
            string thirdChartPath = CreateBmsFile(thirdDirectoryPath, "third.bms", "#TITLE Durable rename");
            string thirdDestinationChartPath = Path.Combine(thirdDirectoryPath, "third.bme");
            ChartFile firstChart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(firstChartPath));
            ChartFile secondChart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(secondChartPath));
            ChartFile thirdChart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(thirdChartPath));
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
                        (firstChart),
                        (secondChart),
                        (thirdChart)
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

        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "normal-invalid-extension");
            string firstChartPath = CreateBmsFile(sourceDirectoryPath, "first.bms", "#TITLE First failure");
            string secondChartPath = CreateBmsFile(sourceDirectoryPath, "second.bms", "#TITLE Durable rename");
            string secondDestinationChartPath = Path.Combine(sourceDirectoryPath, "second.bme");
            ChartFile firstChart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(firstChartPath));
            ChartFile secondChart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(secondChartPath));
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
                        (firstChart),
                        (secondChart)
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

        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "normal-invalid-extension-durable-failure");
            string lr2RootPath = Path.Combine(tempRootPath, "LR2beta3");
            string sourceChartPath = CreateBmsFile(sourceDirectoryPath, "chart.bms", "#TITLE Durable failure");
            string destinationChartPath = Path.Combine(sourceDirectoryPath, "chart.bme");
            ChartFile chart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(sourceChartPath));
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
                    [(chart)],
                    ".bme",
                    unregister: false));

            Assert.IsNotNull(durableFailure);
            Assert.IsFalse(File.Exists(sourceChartPath));
            Assert.IsTrue(File.Exists(destinationChartPath));
            Assert.AreEqual(0, library.BmsCharts.Count(file => string.Equals(file.Path, sourceChartPath, StringComparison.OrdinalIgnoreCase)));
            Assert.AreEqual(1, library.BmsCharts.Count(file => string.Equals(file.Path, destinationChartPath, StringComparison.OrdinalIgnoreCase)));
            using LR2SongDBExtended verifySongDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(0, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ?;", sourceChartPath));
            Assert.AreEqual(1, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ?;", destinationChartPath));
        });
    }

    [TestMethod]
    public void RenameBMSFilesExtensions_UnregistersOnlySuccessfulChartsAndPreservesHashOwner()
    {

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
            ChartFile failed = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(failedSourcePath));
            ChartFile selectedShared = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(selectedSharedSourcePath));
            ChartFile survivingShared = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(survivingSharedSourcePath));
            ChartFile lastOwner = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(lastOwnerSourcePath));
            Assert.AreEqual(selectedShared.Md5, survivingShared.Md5);

            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, seedSongDb =>
            {
                foreach (ChartFile file in new[] { failed, selectedShared, survivingShared, lastOwner })
                {
                    seedSongDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
                }
            });

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
            Assert.IsTrue(initialHash.ContainsMd5(failed.Md5));
            Assert.IsTrue(initialHash.ContainsMd5(selectedShared.Md5));
            Assert.IsTrue(initialHash.ContainsMd5(lastOwner.Md5));
            Assert.IsTrue(initialInstalled.ContainsPrimaryHash(failed.Md5));
            Assert.IsTrue(initialInstalled.ContainsPrimaryHash(selectedShared.Md5));
            Assert.IsTrue(initialInstalled.ContainsPrimaryHash(lastOwner.Md5));
            CollectionAssert.AreEquivalent(
                new[] { survivingSharedDirectoryPath, selectedSharedDirectoryPath },
                initialInstalled.GetDistinctDirectoriesByPrimaryHash(selectedShared.Md5).ToArray());
            Assert.IsTrue(initialPlaylist.ContainsCandidate(ChartFileKind.Bms, failedSourcePath));
            Assert.IsTrue(initialPlaylist.ContainsCandidate(ChartFileKind.Bms, selectedSharedSourcePath));
            Assert.IsTrue(initialPlaylist.ContainsCandidate(ChartFileKind.Bms, survivingSharedSourcePath));
            Assert.IsTrue(initialPlaylist.ContainsCandidate(ChartFileKind.Bms, lastOwnerSourcePath));

            List<string> hashWork = [];
            List<string> playlistWork = [];
            List<string> installedWork = [];
            library.OwnedChartHashIndexStoreWorkObserver = hashWork.Add;
            library.PlaylistLibraryResolveIndexStoreWorkObserver = playlistWork.Add;
            library.InstalledChartLookupStoreWorkObserver = installedWork.Add;

            library.RenameBMSFilesExtensions(
                [
                    (failed),
                    (selectedShared),
                    (lastOwner)
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

            Assert.IsTrue(library.BmsCharts.Any(file =>
                string.Equals(file.Path, failedSourcePath, StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(library.BmsCharts.Any(file =>
                string.Equals(file.Path, survivingSharedSourcePath, StringComparison.OrdinalIgnoreCase)));
            Assert.IsFalse(library.BmsCharts.Any(file =>
                string.Equals(file.Path, selectedSharedSourcePath, StringComparison.OrdinalIgnoreCase)));
            Assert.IsFalse(library.BmsCharts.Any(file =>
                string.Equals(file.Path, lastOwnerSourcePath, StringComparison.OrdinalIgnoreCase)));

            using (LR2SongDBExtended verifySongDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
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
            Assert.IsTrue(updatedPlaylistCacheHit, string.Join(";", playlistWork));
            Assert.AreEqual(0, updatedPlaylistStaleRetries);
            Assert.IsTrue(cachedPlaylistCacheHit);
            Assert.AreEqual(0, cachedPlaylistStaleRetries);
            Assert.AreSame(updatedPlaylist, cachedPlaylist);
            Assert.IsTrue(updatedHash.ContainsMd5(failed.Md5));
            Assert.IsTrue(updatedHash.ContainsMd5(selectedShared.Md5));
            Assert.IsFalse(updatedHash.ContainsMd5(lastOwner.Md5));
            Assert.IsTrue(updatedInstalled.ContainsPrimaryHash(failed.Md5));
            Assert.IsTrue(updatedInstalled.ContainsPrimaryHash(selectedShared.Md5));
            Assert.IsFalse(updatedInstalled.ContainsPrimaryHash(lastOwner.Md5));
            CollectionAssert.AreEquivalent(
                new[] { survivingSharedDirectoryPath },
                updatedInstalled.GetDistinctDirectoriesByPrimaryHash(selectedShared.Md5).ToArray());
            Assert.AreEqual(0, updatedInstalled.GetDistinctDirectoriesByPrimaryHash(lastOwner.Md5).Count);
            Assert.IsTrue(updatedPlaylist.ContainsCandidate(ChartFileKind.Bms, failedSourcePath));
            Assert.IsFalse(updatedPlaylist.ContainsCandidate(ChartFileKind.Bms, selectedSharedSourcePath));
            Assert.IsTrue(updatedPlaylist.ContainsCandidate(ChartFileKind.Bms, survivingSharedSourcePath));
            Assert.IsFalse(updatedPlaylist.ContainsCandidate(ChartFileKind.Bms, lastOwnerSourcePath));
            Assert.IsTrue(initialHash.ContainsMd5(lastOwner.Md5));
            Assert.IsTrue(initialInstalled.ContainsPrimaryHash(lastOwner.Md5));
            Assert.IsTrue(initialPlaylist.ContainsCandidate(ChartFileKind.Bms, lastOwnerSourcePath));
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

        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string packageDirectoryPath = Path.Combine(tempRootPath, "package");
            Directory.CreateDirectory(packageDirectoryPath);
            string bmsonPath = Path.Combine(packageDirectoryPath, "chart.bmson");
            File.WriteAllText(bmsonPath, "{\"info\":{\"title\":\"Song\",\"mode_hint\":\"beat-7k\"},\"sound_channels\":[]}");
            ChartFile installedBmson = ChartTestValues.ReadBmson(bmsonPath);
            var pendingPackage = new ChartPackage
            {
                path = packageDirectoryPath
            };
            var library = new TestBmsLibrary(songDbPath)
            {
                BmsonCharts = [installedBmson],
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

        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string destinationDirectoryPath = Path.Combine(tempRootPath, "r5-01-installed");
            string sourceDirectoryPath = Path.Combine(tempRootPath, "r5-01-pending");
            string installedChartPath = CreateBmsFile(destinationDirectoryPath, "owned.bms", "#TITLE R5-01 Owned");
            string sourceOwnedChartPath = CreateBmsFile(sourceDirectoryPath, "owned.bms", "#TITLE R5-01 Owned");
            string sourceNewChartPath = CreateBmsFile(sourceDirectoryPath, "new.bms", "#TITLE R5-01 New");
            ChartFile installedChart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedChartPath));
            ChartFile sourceOwnedChart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(sourceOwnedChartPath));
            ChartFile sourceNewChart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(sourceNewChartPath));
            Assert.AreEqual(installedChart.Md5, sourceOwnedChart.Md5);
            using (var seedSongDb = new LR2SongDBExtended(songDbPath))
            {
                seedSongDb.InsertOrReplace(
                    ChartSongStorageMapping.ToBmsRow(installedChart),
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
                BmsCharts = [installedChart],
                BmsonCharts = [],
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
            using LR2SongDBExtended verifySongDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
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

        WithTemporarySongDb((dbPath, root) =>
        {
            string installedDirectory = Path.Combine(root, "installed");
            string firstSource = Path.Combine(root, "pending-first");
            string secondSource = Path.Combine(root, "pending-second");
            ChartFile installed = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(CreateBmsFile(installedDirectory, "chart.bms", "#TITLE AlreadyInstalled")));
            ChartFile firstChart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(CreateBmsFile(firstSource, "chart.bms", "#TITLE AlreadyInstalled")));
            ChartFile secondChart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(CreateBmsFile(secondSource, "chart.bms", "#TITLE AlreadyInstalled")));
            ChartPackage first = ChartPackageTestExtensions.CreatePackage(
                ChartPackageTestExtensions.CreateEntryWithInstallDestination(firstChart, installedDirectory));
            ChartPackage second = ChartPackageTestExtensions.CreatePackage(
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
                BmsCharts = [installed],
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

        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string destinationDirectoryPath = Path.Combine(tempRootPath, "installed-r2-02");
            string firstSourceDirectoryPath = Path.Combine(tempRootPath, "pending-empty-destination");
            string secondSourceDirectoryPath = Path.Combine(tempRootPath, "pending-valid-destination");
            string firstChartPath = CreateBmsFile(firstSourceDirectoryPath, "shared.bms", "#TITLE Shared");
            string secondSharedChartPath = CreateBmsFile(secondSourceDirectoryPath, "shared.bms", "#TITLE Shared");
            string secondUniqueChartPath = CreateBmsFile(secondSourceDirectoryPath, "unique.bms", "#TITLE Unique");
            ChartFile firstChart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(firstChartPath));
            ChartFile secondSharedChart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(secondSharedChartPath));
            ChartFile secondUniqueChart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(secondUniqueChartPath));
            Assert.AreEqual(firstChart.Md5, secondSharedChart.Md5);

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
                BmsCharts = [],
                BmsonCharts = [],
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
            Assert.IsTrue(library.BmsCharts.Any(file => string.Equals(file.Path, installedSharedPath, StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(library.BmsCharts.Any(file => string.Equals(file.Path, installedUniquePath, StringComparison.OrdinalIgnoreCase)));
            using LR2SongDBExtended verifySongDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
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
                    BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(firstChartPath)),
                    firstDestinationDirectoryPath));
            ChartPackage secondPackage = ChartPackageTestExtensions.CreatePackage(
                ChartPackageTestExtensions.CreateEntryWithInstallDestination(
                    BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(secondChartPath)),
                    secondDestinationDirectoryPath));
            ChartPackage thirdPackage = ChartPackageTestExtensions.CreatePackage(
                ChartPackageTestExtensions.CreateEntryWithInstallDestination(
                    BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(thirdChartPath)),
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
                BmsCharts = [],
                BmsonCharts = [],
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

            using LR2SongDBExtended verifySongDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
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

        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string destinationDirectoryPath = Path.Combine(tempRootPath, "estimated-retry-installed");
            string firstSourceDirectoryPath = Path.Combine(tempRootPath, "estimated-retry-first");
            string secondSourceDirectoryPath = Path.Combine(tempRootPath, "estimated-retry-second");
            string firstChartPath = CreateBmsFile(firstSourceDirectoryPath, "first.bms", "#TITLE Estimated Retry");
            string secondChartPath = CreateBmsFile(secondSourceDirectoryPath, "second.bms", "#TITLE Estimated Retry");
            ChartFile firstChart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(firstChartPath));
            ChartFile secondChart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(secondChartPath));
            Assert.AreEqual(firstChart.Md5, secondChart.Md5);
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
                BmsCharts = [],
                BmsonCharts = [],
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

            using LR2SongDBExtended verifySongDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(0, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ?;", firstDestinationChartPath));
            Assert.AreEqual(0, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ?;", secondDestinationChartPath));
        });
    }

    [TestMethod]
    public void InstallPendingPackagesToEstimatedDestinations_ResourceOnlyBmsonWorksWithoutBmsFiles()
    {

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
            ChartFile installedBmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = installedBmsonPath,
                Folder = destinationDirectoryPath,
                RawTitle = "Installed",
                Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                Sha256 = new string('b', 64)
            };
            ChartFile pendingBmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = pendingBmsonPath,
                Folder = pendingDirectoryPath,
                RawTitle = "Pending",
                Md5 = installedBmson.Md5,
                Sha256 = installedBmson.Sha256
            };
            using (var seedSongDb = new LR2SongDBExtended(songDbPath))
            {
                seedSongDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(installedBmson), typeof(LR2SongDBExtended.bmson_song));
            }
            var pendingEntry = PackageChartEntry.FromChart((pendingBmson));
            pendingEntry.ApplyInstallDestination(destinationDirectoryPath, "Installed", "Artist");
            var pendingPackage = ChartPackage.FromChartEntries([pendingEntry]);
            pendingPackage.path = pendingDirectoryPath;
            var library = new TestBmsLibrary(songDbPath)
            {
                BmsCharts = null,
                BmsonCharts = [installedBmson],
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
            Assert.AreSame(library.BmsonCharts.Single().Token, displayChart.Token);
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

            ChartFile firstSourceChart = (ChartTestValues.ReadBmson(firstBmsonPath));
            ChartFile? uniqueSourceChart = includeUniqueChart
                ? (ChartTestValues.ReadBmson(secondUniqueBmsonPath))
                : null;
            Assert.IsFalse(string.IsNullOrWhiteSpace(firstSourceChart?.Md5));
            if (includeUniqueChart)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(uniqueSourceChart?.Md5));
            }
            string sharedMd5 = firstSourceChart.Md5;
            string sharedSha256 = firstSourceChart.Sha256;
            ChartFile firstBmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = firstBmsonPath,
                Folder = firstPendingDirectoryPath,
                RawTitle = "Shared",
                RawArtist = "Artist",
                Md5 = sharedMd5,
                Sha256 = sharedSha256
            };
            ChartFile secondBmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = secondBmsonPath,
                Folder = secondPendingDirectoryPath,
                RawTitle = "Shared",
                RawArtist = "Artist",
                Md5 = sharedMd5,
                Sha256 = sharedSha256
            };
            ChartFile secondUniqueBmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = secondUniqueBmsonPath,
                Folder = secondPendingDirectoryPath,
                RawTitle = "Unique",
                RawArtist = "Artist",
                Md5 = uniqueSourceChart?.Md5 ?? "abcdefabcdefabcdefabcdefabcdefab",
                Sha256 = uniqueSourceChart?.Sha256 ?? "abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd"
            };
            var firstEntry = PackageChartEntry.FromChart(
                (firstBmson));
            firstEntry.ApplyInstallDestination(destinationDirectoryPath, "Shared", "Artist");
            var secondEntry = PackageChartEntry.FromChart(
                (secondBmson));
            secondEntry.ApplyInstallDestination(destinationDirectoryPath, "Shared", "Artist");
            var secondUniqueEntry = PackageChartEntry.FromChart(
                (secondUniqueBmson));
            secondUniqueEntry.ApplyInstallDestination(destinationDirectoryPath, "Unique", "Artist");
            var firstPackage = ChartPackage.FromChartEntries([firstEntry]);
            firstPackage.path = firstPendingDirectoryPath;
            firstPackage.delete_parent = false;
            var secondPackage = ChartPackage.FromChartEntries(
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
                BmsCharts = [],
                BmsonCharts = [],
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
            using LR2SongDBExtended verifySongDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
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

        WithTemporarySongDb((songDbPath, root) =>
        {
            string installedDirectory = Path.Combine(root, "installed");
            string pendingDirectory = Path.Combine(root, "pending");
            const string chartBody = "#TITLE Resource Overwrite Failure";
            string installedChartPath = CreateBmsFile(installedDirectory, "chart.bms", chartBody);
            string pendingChartPath = CreateBmsFile(pendingDirectory, "chart.bms", chartBody);
            ChartFile installedChart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedChartPath));
            ChartFile pendingChart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingChartPath));
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingChart]);
            pendingPackage.path = pendingDirectory;
            pendingPackage.delete_parent = false;
            string pendingResourcePath = Path.Combine(pendingDirectory, "sound.wav");
            string installedResourcePath = Path.Combine(installedDirectory, "sound.wav");
            if (!cleanupOnly)
            {
                File.WriteAllBytes(pendingResourcePath, [1, 2, 3]);
            }
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, seedSongDb =>
            {
                seedSongDb.CreateTable<LR2SongDBExtended.install>();
                seedSongDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(installedChart), typeof(LR2SongDB.song));
                seedSongDb.InsertOrReplace(pendingPackage, typeof(LR2SongDBExtended.install));
                seedSongDb.Execute(
                    "CREATE TRIGGER fail_resource_overwrite_install_delete BEFORE DELETE ON install "
                    + "BEGIN SELECT RAISE(ABORT, 'resource-overwrite-canonical-marker'); END;");
            });
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
                BmsCharts = [installedChart],
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
            using LR2SongDBExtended verifySongDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
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
            ChartFile installedBmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = installedBmsonPath,
                Folder = destinationDirectoryPath,
                RawTitle = "Installed",
                Md5 = "cccccccccccccccccccccccccccccccc",
                Sha256 = new string('d', 64)
            };
            ChartFile pendingBmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = pendingBmsonPath,
                Folder = pendingDirectoryPath,
                RawTitle = "Pending",
                Md5 = installedBmson.Md5,
                Sha256 = installedBmson.Sha256
            };
            using (var seedSongDb = new LR2SongDBExtended(songDbPath))
            {
                seedSongDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(installedBmson), typeof(LR2SongDBExtended.bmson_song));
            }
            var pendingEntry = PackageChartEntry.FromChart(
                (pendingBmson));
            var pendingPackage = ChartPackage.FromChartEntries([pendingEntry]);
            pendingPackage.path = pendingDirectoryPath;
            var library = new TestBmsLibrary(songDbPath)
            {
                BmsCharts = null,
                BmsonCharts = [installedBmson],
                ChartPackagesPending = CreatePackageCollection([pendingPackage]),
                ChartPackagesInstalled = CreatePackageCollection([])
            };
            installedBmson = library.BmsonCharts.Single();
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
                    Assert.AreSame(installedBmson.Token, library.BmsonCharts.Single().Token);
                    using LR2SongDBExtended notifiedSongDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
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
            Assert.AreSame(installedBmson.Token, library.BmsonCharts.Single().Token);
            Assert.AreEqual(installedBmsonPath, library.BmsonCharts.Single().Path);
            using LR2SongDBExtended verifySongDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
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
    /// 背景16件は全件列挙を検出する閾値を上回り、resource-only操作の局所更新を確認できます。
    /// 背景件数による時間・仕事量比較はこの契約に含めません。
    /// </summary>
    [DataTestMethod]
    [DataRow(16)]
    public void OverwritePendingInstalledOnlyPackagesResources_UsesWarmCatalogWithoutChartDelta(int backgroundCount)
    {

        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string backgroundDirectoryPath = Path.Combine(tempRootPath, "resource-only-warm-background");
            Directory.CreateDirectory(backgroundDirectoryPath);

            (string DestinationDirectoryPath, string PendingDirectoryPath,
                string FirstInstalledPath, string SecondInstalledPath,
                string FirstPendingPath, string SecondPendingPath,
                string FirstResourceName, string SecondResourceName,
                ChartFile FirstInstalled,
                ChartFile SecondInstalled,
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

                ChartFile installedFirst = ChartTestValues.ReadBmson(firstInstalledPath);
                ChartFile installedSecond = ChartTestValues.ReadBmson(secondInstalledPath);
                ChartFile pendingFirst = ChartTestValues.ReadBmson(firstPendingPath);
                ChartFile pendingSecond = ChartTestValues.ReadBmson(secondPendingPath);
                Assert.AreEqual(installedFirst.Md5, pendingFirst.Md5);
                Assert.AreEqual(installedFirst.Sha256, pendingFirst.Sha256);
                Assert.AreEqual(installedSecond.Md5, pendingSecond.Md5);
                Assert.AreEqual(installedSecond.Sha256, pendingSecond.Sha256);
                var firstEntry = PackageChartEntry.FromChart(
                    (pendingFirst));
                var secondEntry = PackageChartEntry.FromChart(
                    (pendingSecond));
                BmsLibraryPackageInstallService.ApplyPendingResourceHealthProjectionToEntries([firstEntry, secondEntry]);
                Assert.AreEqual(0, firstEntry.Chart.WAVHealth);
                Assert.AreEqual(0, secondEntry.Chart.WAVHealth);
                Assert.IsTrue(firstEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
                Assert.IsTrue(secondEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
                var package = ChartPackage.FromChartEntries([firstEntry, secondEntry]);
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

            (string DestinationDirectoryPath, string PendingDirectoryPath, string FirstInstalledPath, string SecondInstalledPath, string FirstPendingPath, string SecondPendingPath, string FirstResourceName, string SecondResourceName, ChartFile FirstInstalled, ChartFile SecondInstalled, ChartPackage Package) firstStep = CreateResourceStep(1);
            (string DestinationDirectoryPath, string PendingDirectoryPath, string FirstInstalledPath, string SecondInstalledPath, string FirstPendingPath, string SecondPendingPath, string FirstResourceName, string SecondResourceName, ChartFile FirstInstalled, ChartFile SecondInstalled, ChartPackage Package) secondStep = CreateResourceStep(2);
            var backgroundSongs = new List<ChartFile>(backgroundCount);
            for (int index = 0; index < backgroundCount; index++)
            {
                string path = Path.Combine(backgroundDirectoryPath, "background-" + index + ".bmson");
                File.WriteAllText(path, "{}");
                string hash = (index + 1).ToString("x8") + new string('e', 24);
                backgroundSongs.Add(ChartTestValues.Empty(ChartFileKind.Bmson) with
                {
                    Path = path,
                    Folder = backgroundDirectoryPath,
                    RawTitle = "Background " + index,
                    Md5 = hash,
                    Sha256 = (index + 1).ToString("x8") + new string('f', 56)
                });
            }
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, seedSongDb =>
            {
                seedSongDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(firstStep.FirstInstalled), typeof(LR2SongDBExtended.bmson_song));
                seedSongDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(firstStep.SecondInstalled), typeof(LR2SongDBExtended.bmson_song));
                seedSongDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(secondStep.FirstInstalled), typeof(LR2SongDBExtended.bmson_song));
                seedSongDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(secondStep.SecondInstalled), typeof(LR2SongDBExtended.bmson_song));
                foreach (ChartFile backgroundSong in backgroundSongs)
                {
                    seedSongDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(backgroundSong), typeof(LR2SongDBExtended.bmson_song));
                }
            });

            var library = new TestBmsLibrary(songDbPath)
            {
                BmsCharts = null,
                BmsonCharts = [
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
            Assert.AreEqual(backgroundCount + 4, library.BmsonCharts.Count);

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
                (string DestinationDirectoryPath, string PendingDirectoryPath, string FirstInstalledPath, string SecondInstalledPath, string FirstPendingPath, string SecondPendingPath, string FirstResourceName, string SecondResourceName, ChartFile FirstInstalled, ChartFile SecondInstalled, ChartPackage Package) step = stepIndex == 0 ? firstStep : secondStep;
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
                        using LR2SongDBExtended notifiedSongDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
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
                Assert.AreEqual(
                    step.FirstInstalled.Md5,
                    library.BmsonCharts.Single(song => song.Path == step.FirstInstalledPath).Md5);
                Assert.AreEqual(
                    step.SecondInstalled.Md5,
                    library.BmsonCharts.Single(song => song.Path == step.SecondInstalledPath).Md5);

                PackageChartEntry installedFirstEntry = library.ChartPackagesInstalled
                    .SelectMany(package => package.ChartEntries)
                    .Single(entry => entry.Chart.Md5 == step.FirstInstalled.Md5);
                PackageChartEntry installedSecondEntry = library.ChartPackagesInstalled
                    .SelectMany(package => package.ChartEntries)
                    .Single(entry => entry.Chart.Md5 == step.SecondInstalled.Md5);
                Assert.AreEqual(step.FirstInstalledPath, installedFirstEntry.Chart.Path);
                Assert.AreEqual(step.SecondInstalledPath, installedSecondEntry.Chart.Path);
                Assert.AreSame(library.BmsonCharts.Single(chart => chart.Path == step.FirstInstalledPath).Token, installedFirstEntry.Chart.Token);
                Assert.AreSame(library.BmsonCharts.Single(chart => chart.Path == step.SecondInstalledPath).Token, installedSecondEntry.Chart.Token);
                Assert.AreEqual(100, installedFirstEntry.Chart.WAVHealth);
                Assert.AreEqual(0, installedSecondEntry.Chart.WAVHealth);
                Assert.IsFalse(installedFirstEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
                Assert.IsTrue(installedSecondEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));

                using (LR2SongDBExtended verifySongDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
                {
                    Assert.AreEqual(backgroundCount + 4, verifySongDb.Table<LR2SongDBExtended.bmson_song>().Count());
                    Assert.AreEqual(1, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM bmson_song WHERE path = ?;", step.FirstInstalledPath));
                    Assert.AreEqual(1, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM bmson_song WHERE path = ?;", step.SecondInstalledPath));
                    Assert.AreEqual(0, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM bmson_song WHERE path = ?;", step.FirstPendingPath));
                    Assert.AreEqual(0, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM bmson_song WHERE path = ?;", step.SecondPendingPath));
                    LR2SongDBExtended.bmson_song persisted = verifySongDb.Table<LR2SongDBExtended.bmson_song>()
                        .Single(song => song.path == step.FirstInstalledPath);
                    Assert.AreEqual(step.FirstInstalled.Md5, persisted.md5);
                    Assert.AreEqual(step.FirstInstalled.Sha256, persisted.sha256);
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
                Assert.IsTrue(updatedPlaylistCacheHit, string.Join(";", playlistWork));
                Assert.AreEqual(0, updatedPlaylistStaleRetries);
                Assert.IsTrue(cachedPlaylistCacheHit);
                Assert.AreEqual(0, cachedPlaylistStaleRetries);
                Assert.IsTrue(oldHash.ContainsMd5(backgroundSongs[0].Md5));
                Assert.IsTrue(oldInstalled.ContainsPrimaryHash(backgroundSongs[0].Md5));
                Assert.IsTrue(oldPlaylist.ContainsCandidate(ChartFileKind.Bmson, backgroundSongs[0].Path));
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
            ChartFile installedBmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = installedBmsonPath,
                Folder = destinationDirectoryPath,
                RawTitle = "Installed cleanup-only",
                Md5 = "11111111111111111111111111111111",
                Sha256 = new string('2', 64)
            };
            ChartFile pendingBmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = pendingBmsonPath,
                Folder = pendingDirectoryPath,
                RawTitle = "Pending cleanup-only",
                Md5 = installedBmson.Md5,
                Sha256 = installedBmson.Sha256
            };
            var pendingEntry = PackageChartEntry.FromChart(
                (pendingBmson));
            var pendingPackage = ChartPackage.FromChartEntries([pendingEntry]);
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
            library.BmsCharts = null;
            library.BmsonCharts = [installedBmson];
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
            ChartFile installedBmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = installedBmsonPath,
                Folder = destinationDirectoryPath,
                RawTitle = "Installed",
                Md5 = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
                Sha256 = new string('f', 64)
            };
            ChartFile pendingBmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = pendingBmsonPath,
                Folder = pendingDirectoryPath,
                RawTitle = "Pending",
                Md5 = installedBmson.Md5,
                Sha256 = installedBmson.Sha256
            };
            var pendingEntry = PackageChartEntry.FromChart(
                (pendingBmson));
            pendingEntry.ApplyInstallDestination(destinationDirectoryPath, "Installed", "Artist");
            var pendingPackage = ChartPackage.FromChartEntries([pendingEntry]);
            pendingPackage.path = pendingDirectoryPath;
            var library = new TestBmsLibrary(songDbPath)
            {
                BmsCharts = null,
                BmsonCharts = [installedBmson],
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
    public void InstallPendingPackagesToEstimatedDestinations_LeavesPackageUnchangedWhenItIsNotPending()
    {

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
    /// 自動導入の非同期入口が、譜面索引なしでも単独動作・LR2設定の登録ルート自身・子孫を除外し、FSとDBを保持します。
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
            library.BmsCharts = [];
            library.SearchTargets = useLr2 ? [] : [registeredRoot];
            string sourcePath = sourceKind switch
            {
                "root" => registeredRoot,
                "directory" => nestedDirectory,
                _ => nestedChartPath
            };

            PackageInstallCommandResult installed = library.InstallChartPackagesAutoWithProgressAsync([sourcePath], CancellationToken.None, new RecordingPackageInstallProgressWriter()).GetAwaiter().GetResult();

            Assert.AreEqual(0, installed.RegisteredPackages.Count);
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

        WithTemporarySongDb((songDbPath, tempRootPath) =>
        {
            string installRootPath = Path.Combine(tempRootPath, "PrimaryOnlyInstalled");
            string existingDirectoryPath = Path.Combine(installRootPath, "Existing");
            string sourceDirectoryPath = Path.Combine(tempRootPath, "PrimaryOnlySource");
            string existingPath = CreateBmsFile(existingDirectoryPath, "existing.bms", "#TITLE Existing");
            string sourcePath = CreateBmsFile(sourceDirectoryPath, "added.bms", "#TITLE Primary Only Added");
            ChartFile existing = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(existingPath));
            ChartFile source = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(sourcePath));
            using (var seedSongDb = new LR2SongDBExtended(songDbPath))
            {
                seedSongDb.InsertOrReplace(
                    ChartSongStorageMapping.ToBmsRow(existing),
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
                BmsCharts = [existing],
                BmsonCharts = [],
                ChartPackagesPending = CreatePackageCollection([package]),
                ChartPackagesInstalled = CreatePackageCollection([])
            };

            BMSLibrary.InstalledPrimaryHashWarmupResult primaryWarmup =
                library.WarmInstalledPrimaryHashLookup("primary_only_before_install");
            Assert.AreEqual("installed_primary_hash", primaryWarmup.IndexName);
            Assert.IsFalse(primaryWarmup.FullDirectoryLookupInitialized);
            Assert.IsTrue(OwnedChartCollectionTestSupport.IsInstalledPrimaryHashLookupInitialized(library));
            Assert.IsFalse(OwnedChartCollectionTestSupport.IsInstalledChartLookupIndexInitialized(library));
            IPrimaryHashLookup oldPrimary =
                OwnedChartCollectionTestSupport.InvokeCreateInstalledChartKeySnapshotExcludingCharts(library, []);
            Assert.IsTrue(oldPrimary.ContainsPrimaryHash(existing.Md5));
            Assert.IsFalse(oldPrimary.ContainsPrimaryHash(source.Md5));

            LibraryMutationSessionReceipt receipt = library.ForceInstallPendingPackagesWithReceipt(
                [package],
                approveNormalInstallOverride: true,
                approvedNormalInstallOverridePackages: null);

            Assert.IsTrue(receipt.DurableCommit);
            Assert.IsFalse(receipt.HasRequiredFailure);
            Assert.IsFalse(receipt.HasDurableFinalizationFailure);
            Assert.AreEqual(0, library.ChartPackagesPending.Count);
            Assert.AreEqual(1, library.ChartPackagesInstalled.Count);
            ChartFile installed = library.BmsCharts.Single(file => file.Md5 == source.Md5);
            Assert.AreNotEqual(sourcePath, installed.Path);
            StringAssert.StartsWith(installed.Path, installRootPath);
            Assert.IsTrue(File.Exists(installed.Path));
            Assert.IsTrue(OwnedChartCollectionTestSupport.IsInstalledPrimaryHashLookupInitialized(library));

            // 実導入後のprimary所有と、導入前に捕捉したsnapshotの不変性を確認します。
            BMSLibrary.InstalledPrimaryHashWarmupResult updatedPrimary =
                library.WarmInstalledPrimaryHashLookup("primary_only_after_install");
            IPrimaryHashLookup currentPrimary =
                OwnedChartCollectionTestSupport.InvokeCreateInstalledChartKeySnapshotExcludingCharts(library, []);
            Assert.AreEqual(2, updatedPrimary.PrimaryHashCount);
            Assert.IsTrue(currentPrimary.ContainsPrimaryHash(existing.Md5));
            Assert.IsTrue(currentPrimary.ContainsPrimaryHash(source.Md5));
            Assert.IsTrue(oldPrimary.ContainsPrimaryHash(existing.Md5));
            Assert.IsFalse(oldPrimary.ContainsPrimaryHash(source.Md5));
            using (LR2SongDBExtended verifySongDbBeforeFull = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(1, verifySongDbBeforeFull.ExecuteScalar<int>(
                    "SELECT COUNT(1) FROM song WHERE path = ?;",
                    installed.Path));
                Assert.AreEqual(0, verifySongDbBeforeFull.ExecuteScalar<int>(
                    "SELECT COUNT(1) FROM song WHERE path = ?;",
                    sourcePath));
            }

            InstalledChartLookupIndexSnapshot installedLookup =
                OwnedChartCollectionTestSupport.InvokeCreateInstalledChartLookupSnapshot(library);
            Assert.IsTrue(installedLookup.ContainsPrimaryHash(existing.Md5));
            Assert.IsTrue(installedLookup.ContainsPrimaryHash(source.Md5));
            using LR2SongDBExtended verifySongDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(1, verifySongDb.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM song WHERE path = ?;",
                installed.Path));
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

        WithTemporarySongDb((songDbPath, tempRootPath) =>
        {
            string installRootPath = Path.Combine(tempRootPath, "ExactInstalled");
            string destinationDirectoryPath = Path.Combine(installRootPath, "Target");
            string sourceDirectoryPath = Path.Combine(tempRootPath, "ExactSource");
            string oldDirectoryPath = Path.Combine(installRootPath, "Existing");
            string oldPath = CreateBmsFile(oldDirectoryPath, "chart.bms", "#TITLE Original");
            string addedPath = CreateBmsFile(sourceDirectoryPath, "added.bms", "#TITLE Replacement");
            ChartFile oldChart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(oldPath));
            ChartFile addedChart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(addedPath));
            Assert.AreNotEqual(oldChart.Md5, addedChart.Md5);
            using (var seedSongDb = new LR2SongDBExtended(songDbPath))
            {
                seedSongDb.InsertOrReplace(
                    ChartSongStorageMapping.ToBmsRow(oldChart),
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
                BmsCharts = [oldChart],
                BmsonCharts = [],
                ChartPackagesPending = CreatePackageCollection([package]),
                ChartPackagesInstalled = CreatePackageCollection([])
            };

            BMSLibrary.InstalledPrimaryHashWarmupResult primaryWarmup =
                library.WarmInstalledPrimaryHashLookup("exact_target_before_install");
            Assert.IsFalse(primaryWarmup.FullDirectoryLookupInitialized);
            Assert.IsFalse(OwnedChartCollectionTestSupport.IsInstalledChartLookupIndexInitialized(library));
            IPrimaryHashLookup oldPrimary =
                OwnedChartCollectionTestSupport.InvokeCreateInstalledChartKeySnapshotExcludingCharts(library, []);
            Assert.IsTrue(oldPrimary.ContainsPrimaryHash(oldChart.Md5));
            Assert.IsFalse(oldPrimary.ContainsPrimaryHash(addedChart.Md5));

            PendingInstallBatchResult result =
                library.InstallPendingPackagesToEstimatedDestinationsWithReceipt([package]);

            Assert.IsTrue(result.SessionReceipt.DurableCommit);
            Assert.IsFalse(result.SessionReceipt.HasRequiredFailure);
            Assert.IsFalse(result.SessionReceipt.HasDurableFinalizationFailure);
            Assert.AreEqual(0, result.FailedPackages.Count);
            Assert.AreEqual(
                2,
                library.BmsCharts.Count,
                string.Join("|", library.BmsCharts.Select(file => file.Path + ":" + file.Md5)));
            ChartFile installed = library.BmsCharts.Single(file => file.Md5 == addedChart.Md5);
            string expectedInstalledPath = Path.Combine(destinationDirectoryPath, "added.bms");
            Assert.AreEqual(expectedInstalledPath, installed.Path);
            Assert.AreEqual(addedChart.Md5, installed.Md5);
            Assert.IsTrue(File.Exists(expectedInstalledPath));
            Assert.IsTrue(library.BmsCharts.Any(file => file.Path == oldPath && file.Md5 == oldChart.Md5));
            // 推定先導入の確定結果をprimary lookupとDBから確認します。
            BMSLibrary.InstalledPrimaryHashWarmupResult updatedPrimary =
                library.WarmInstalledPrimaryHashLookup("exact_target_after_install");
            IPrimaryHashLookup currentPrimary =
                OwnedChartCollectionTestSupport.InvokeCreateInstalledChartKeySnapshotExcludingCharts(library, []);
            Assert.AreEqual(2, updatedPrimary.PrimaryHashCount);
            Assert.IsTrue(currentPrimary.ContainsPrimaryHash(oldChart.Md5));
            Assert.IsTrue(currentPrimary.ContainsPrimaryHash(addedChart.Md5));
            Assert.IsTrue(oldPrimary.ContainsPrimaryHash(oldChart.Md5));
            Assert.IsFalse(oldPrimary.ContainsPrimaryHash(addedChart.Md5));
            using (LR2SongDBExtended verifySongDbBeforeFull = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
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
            Assert.IsTrue(installedLookup.ContainsPrimaryHash(oldChart.Md5));
            Assert.IsTrue(installedLookup.ContainsPrimaryHash(addedChart.Md5));
            using LR2SongDBExtended verifySongDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
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

        WithTemporarySongDb((songDbPath, tempRootPath) =>
        {
            string installRootPath = Path.Combine(tempRootPath, "SameDigestInstalled");
            string initialDirectoryPath = Path.Combine(installRootPath, "Initial");
            string sourceDirectoryPath = Path.Combine(tempRootPath, "SameDigestSource");
            string initialPath = CreateBmsFile(initialDirectoryPath, "chart.bms", "#TITLE Same Digest");
            string sourcePath = Path.Combine(sourceDirectoryPath, "chart.bms");
            Directory.CreateDirectory(sourceDirectoryPath);
            File.Copy(initialPath, sourcePath, overwrite: true);
            ChartFile initialChart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(initialPath));
            ChartFile sourceChart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(sourcePath));
            Assert.AreEqual(initialChart.Md5, sourceChart.Md5);
            Assert.AreEqual(initialChart.Sha256, sourceChart.Sha256);
            using (var seedSongDb = new LR2SongDBExtended(songDbPath))
            {
                seedSongDb.InsertOrReplace(
                    ChartSongStorageMapping.ToBmsRow(initialChart),
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
                BmsCharts = [initialChart],
                BmsonCharts = [],
                ChartPackagesPending = CreatePackageCollection([package]),
                ChartPackagesInstalled = CreatePackageCollection([])
            };

            OwnedChartHashIndexVersionedSnapshot initialHashes = library.GetOwnedChartHashIndexSnapshot();
            Assert.AreEqual(1, initialHashes.GetMd5OwnerCount(initialChart.Md5));
            Assert.AreEqual(1, initialHashes.GetSha256OwnerCount(initialChart.Sha256));
            Assert.IsFalse(OwnedChartCollectionTestSupport.IsInstalledPrimaryHashLookupInitialized(library));
            Assert.IsFalse(OwnedChartCollectionTestSupport.IsInstalledChartLookupIndexInitialized(library));

            var table = new BMSTable { playlist_id = 42 };
            table.entries = [PlaylistSummaryAggregationTestSupport.CreateEntry(initialChart.Md5, initialChart.Sha256)];
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
                library.BmsCharts.Count,
                string.Join("|", library.BmsCharts.Select(file => file.Path + ":" + file.Md5)));
            ChartFile installed = library.BmsCharts.Single(file =>
                !string.Equals(initialPath, file.Path, StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(initialChart.Md5, installed.Md5);
            Assert.AreNotEqual(initialPath, installed.Path);
            StringAssert.StartsWith(installed.Path, installRootPath);
            Assert.IsTrue(File.Exists(installed.Path));
            Assert.AreEqual(1, library.ChartPackagesInstalled.Count);

            // 同digestを別配置へ導入した後もprimary所有が維持されることを確認します。
            BMSLibrary.InstalledPrimaryHashWarmupResult updatedPrimary =
                library.WarmInstalledPrimaryHashLookup("hash_only_after_install");
            Assert.AreEqual(1, updatedPrimary.PrimaryHashCount);
            IPrimaryHashLookup currentPrimary =
                OwnedChartCollectionTestSupport.InvokeCreateInstalledChartKeySnapshotExcludingCharts(library, []);
            Assert.IsTrue(currentPrimary.ContainsPrimaryHash(initialChart.Md5));
            using (LR2SongDBExtended verifySongDbBeforeFull = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(2, verifySongDbBeforeFull.Table<LR2SongDB.song>().Count());
                Assert.AreEqual(1, verifySongDbBeforeFull.ExecuteScalar<int>(
                    "SELECT COUNT(1) FROM song WHERE path = ?;",
                    initialPath));
                Assert.AreEqual(1, verifySongDbBeforeFull.ExecuteScalar<int>(
                    "SELECT COUNT(1) FROM song WHERE path = ?;",
                    installed.Path));
            }

            OwnedChartHashIndexVersionedSnapshot updatedHashes = library.GetOwnedChartHashIndexSnapshot();
            PlaylistSummaryCountResult updatedSummary = summaryOwner.GetOrBuildTableCount(
                table,
                updatedHashes,
                CancellationToken.None,
                out bool updatedCacheHit);
            Assert.AreEqual(initialHashes.Version, updatedHashes.Version);
            Assert.AreEqual(2, updatedHashes.GetMd5OwnerCount(initialChart.Md5));
            Assert.AreEqual(2, updatedHashes.GetSha256OwnerCount(initialChart.Sha256));
            Assert.IsTrue(updatedCacheHit);
            Assert.AreEqual(1, updatedSummary.TotalCharts);
            Assert.AreEqual(1, updatedSummary.OwnedCharts);
            Assert.AreEqual(installed.Path, package.ChartEntries.Single().Chart.Path);
        });
    }

    /// <summary>
    /// 自動導入の非同期入口も推定／強制導入と同じ本番catalog入口へ到達し、
    /// 事前に確定した宛先をFS・SQLite・索引・通知へ渡します。通常通知時は短いモデルguardを解放し、
    /// 自動導入の外側Lは保持して新規受付をBusyにし、実Taskの終端後だけ新規受付を許可します。
    /// 背景16件は新規2譜面によるprimary hash更新（最大8件）を上回るため、
    /// 全件列挙なしの実observerを検出できます。背景件数による時間・仕事量比較はこの契約に含めません。
    /// </summary>
    [DataTestMethod]
    [DataRow(16)]
    public void InstallChartPackagesAuto_UsesPreflightDestinationAndWarmDelta(int backgroundCount)
    {
        AssertNormalInstallRouteUsesPreflightDestinationAndWarmDelta("auto", backgroundCount);
    }

    // 推定先導入も同じ実catalog入口と局所差分を通るため、autoと同じ16件の背景で確認します。
    [DataTestMethod]
    [DataRow(16)]
    public void InstallPendingPackagesToEstimatedDestinations_UsesPreflightDestinationAndWarmDelta(int backgroundCount)
    {
        AssertNormalInstallRouteUsesPreflightDestinationAndWarmDelta("estimated", backgroundCount);
    }

    // 強制導入も同じ実catalog入口と局所差分を通るため、autoと同じ16件の背景で確認します。
    [DataTestMethod]
    [DataRow(16)]
    public void ForceInstallPendingPackages_UsesPreflightDestinationAndWarmDelta(int backgroundCount)
    {
        AssertNormalInstallRouteUsesPreflightDestinationAndWarmDelta("force", backgroundCount);
    }

    /// <summary>
    /// 保留中のBMS/BMSONを本番導入入口から導入し、導入前の一時警告を表示した後も
    /// 導入後の計算済み充足値とResourceHealthが新規・通常一覧へ届き、不足継続・解消を正しく投影することを確認します。
    /// </summary>
    [DataTestMethod]
    [DataRow(false, false, false)]
    [DataRow(false, false, true)]
    [DataRow(false, true, false)]
    [DataRow(false, true, true)]
    [DataRow(true, false, false)]
    [DataRow(true, false, true)]
    [DataRow(true, true, false)]
    [DataRow(true, true, true)]
    public void InstallPendingPackages_ProjectsCurrentResourceWarningsInNewAndNormalViews(
        bool bmson,
        bool resourceAlreadyExistsAtDestination,
        bool resourceHealthIndexStartsWarm)
    {

        WithTemporarySongDb((songDbPath, tempRootPath) =>
        {
            string pendingDirectoryPath = Path.Combine(tempRootPath, "pending-resource-projection");
            string destinationRootPath = Path.Combine(tempRootPath, "installed-resource-projection");
            string installedDirectoryPath = Path.Combine(
                destinationRootPath,
                bmson ? "Bmson" : "Pending resource");
            Directory.CreateDirectory(pendingDirectoryPath);
            Directory.CreateDirectory(destinationRootPath);
            string chartPath = Path.Combine(pendingDirectoryPath, bmson ? "chart.bmson" : "chart.bms");
            if (bmson)
            {
                File.WriteAllText(chartPath, CreateBmsonJsonWithSound("missing.wav"));
            }
            else
            {
                File.WriteAllText(
                    chartPath,
                    "#PLAYER 1\r\n#TITLE Pending resource\r\n#WAV01 missing.wav\r\n#00111:01\r\n");
            }
            if (resourceAlreadyExistsAtDestination)
            {
                Directory.CreateDirectory(installedDirectoryPath);
                File.WriteAllBytes(Path.Combine(installedDirectoryPath, "missing.wav"), [1, 2, 3]);
            }
            ChartFile sourceChart = bmson
                ? (ChartTestValues.ReadBmson(chartPath))
                : (BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath)));
            var pendingEntry = PackageChartEntry.FromChart(
                ChartFileProjection.WithPackageState(
                    sourceChart,
                    installedDirectoryPath,
                    "",
                    "",
                    sourceChart.Warnings));
            IReadOnlyList<ChartWarning> pendingWarnings =
                BmsLibraryPackageInstallService.ApplyPendingResourceHealthProjection(pendingEntry);
            Assert.IsTrue(pendingWarnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            StringAssert.Contains(
                ChartWarningCollection.BuildTooltipText(pendingWarnings),
                "WAV");
            var package = ChartPackage.FromChartEntries([pendingEntry]);
            package.path = pendingDirectoryPath;
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
                    BMSInstallDir = destinationRootPath,
                    FolderNameFormat = "%TITLE%",
                    DeletePendingPackageSourceAfterInstall = false,
                    EnableSmartComponentOverwrite = false
                })
            {
                BmsCharts = [],
                BmsonCharts = [],
                ChartPackagesPending = CreatePackageCollection([package]),
                ChartPackagesInstalled = CreatePackageCollection([])
            };
            if (resourceHealthIndexStartsWarm)
            {
                OwnedChartCollectionTestSupport.EnsureCurrentResourceHealthIndex(library);
            }
            bool notificationObserved = false;
            bool notificationObservedReleasedLease = false;
            Exception? notificationFailure = null;
            System.ComponentModel.PropertyChangedEventHandler handler = (_, args) =>
            {
                if (args.PropertyName != nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                {
                    return;
                }
                try
                {
                    ChartFile installed = bmson
                        ? (library.BmsonCharts.Single())
                        : (library.BmsCharts.Single());
                    ResourceHealthIndexSnapshot snapshot = library.TryGetCurrentResourceHealthIndexSnapshotForView();
                    if (resourceHealthIndexStartsWarm)
                    {
                        Assert.AreNotSame(ResourceHealthIndexSnapshot.Empty, snapshot);
                        bool hasMissingWarning = snapshot.GetProjection(
                            installed.Kind,
                            installed.Path,
                            installed.Md5).Warnings.Any(
                                warning => warning.Kind == ChartWarningKind.ResourceWavMissing);
                        Assert.AreEqual(!resourceAlreadyExistsAtDestination, hasMissingWarning);
                    }
                    else
                    {
                        Assert.AreSame(ResourceHealthIndexSnapshot.Empty, snapshot);
                    }
                    using LibraryFileMutationLease probe = library.TryBeginLibraryFileMutation(
                        "resource_health_projection_notification_lease_probe",
                        showMessage: false);
                    notificationObservedReleasedLease |= probe != null;
                    notificationObserved = true;
                }
                catch (Exception exception)
                {
                    notificationFailure = exception;
                }
            };
            library.PropertyChanged += handler;
            try
            {
                LibraryMutationSessionReceipt receipt;
                if (resourceAlreadyExistsAtDestination)
                {
                    // 既存リソースのある導入先を自動採番でずらさないため、
                    // 明示した推定先をそのまま使う本番導入入口を通します。
                    PendingInstallBatchResult estimated = library.InstallPendingPackagesToEstimatedDestinationsWithReceipt(
                        [package]);
                    receipt = estimated.SessionReceipt;
                }
                else
                {
                    receipt = library.ForceInstallPendingPackagesWithReceipt(
                        [package],
                        approveNormalInstallOverride: true,
                        approvedNormalInstallOverridePackages: null);
                }

                Assert.IsTrue(receipt.DurableCommit, receipt.PrimaryFailure?.ToString());
                Assert.IsFalse(receipt.HasRequiredFailure, receipt.PrimaryFailure?.ToString());
                Assert.IsNull(notificationFailure, notificationFailure?.ToString());
                Assert.IsTrue(notificationObserved);
                Assert.IsTrue(notificationObservedReleasedLease);
                Assert.AreEqual(0, library.ChartPackagesPending.Count);
                ChartFile installed = bmson
                    ? (library.BmsonCharts.Single())
                    : (library.BmsCharts.Single());
                Assert.AreEqual(
                    resourceAlreadyExistsAtDestination,
                    File.Exists(Path.Combine(installedDirectoryPath, "missing.wav")));
                ChartPackage installedPackage = library.ChartPackagesInstalled.Single();
                Assert.AreEqual(installedDirectoryPath, installedPackage.path);
                Assert.IsTrue(installedPackage.ChartEntries.Count > 0);
                using (LR2SongDBExtended verifySongDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
                {
                    LR2SongDBExtended.maintenance maintenance = verifySongDb.Table<LR2SongDBExtended.maintenance>()
                        .Single(info => string.Equals(info.path, installed.Path, StringComparison.OrdinalIgnoreCase));
                    Assert.AreEqual(1, maintenance.wav_files_defined);
                    Assert.AreEqual(resourceAlreadyExistsAtDestination ? 1 : 0, maintenance.wav_files_existing);
                    Assert.AreEqual(0, maintenance.bga_files_defined);
                    Assert.IsNull(maintenance.bga_files_existing);
                    Assert.AreEqual(0, maintenance.movie_files_defined);
                    Assert.IsNull(maintenance.movie_files_existing);
                }
                Assert.IsNotNull(installed.Token);
                AssertCalculatedInstallResourceHealth(installed, resourceAlreadyExistsAtDestination);
                foreach (PackageChartEntry installedEntry in installedPackage.ChartEntries)
                {
                    Assert.IsNotNull(installedEntry.Chart);
                    Assert.AreSame(installed.Token, installedEntry.Chart.Token);
                    AssertCalculatedInstallResourceHealth(installedEntry.Chart, resourceAlreadyExistsAtDestination);
                    Assert.IsTrue(string.IsNullOrWhiteSpace(installedEntry.Chart.InstallDestination));
                    Assert.IsFalse((installedEntry.Chart.Warnings ?? []).Any(
                        warning => warning.Category == ChartWarningCategory.ResourceHealth));
                }
                ResourceHealthIndexSnapshot beforeView = library.TryGetCurrentResourceHealthIndexSnapshotForView();
                if (resourceHealthIndexStartsWarm)
                {
                    Assert.AreNotSame(ResourceHealthIndexSnapshot.Empty, beforeView);
                }
                else
                {
                    Assert.AreSame(ResourceHealthIndexSnapshot.Empty, beforeView);
                }

                var table = new MainChartListViewModel();
                PlaylistWorkspaceViewModel workspace = RegularChartListOwnerTestSupport.CreateWorkspaceForOwner(table);
                using RegularChartListOwner owner = RegularChartListOwnerTestSupport.CreateOwner(table, workspace);
                var newlyInstalledRoute = new ChartListRefreshRoute(
                    ChartListRefreshRouteKind.ContinueMainLibrary,
                    MainViewUpdateMode.NewlyInstalledFolderSelected,
                    MainViewUpdateMode.NewlyInstalledFolderSelected,
                    MainViewUpdateMode.NewlyInstalledFolderSelected,
                    isPlaylistTreeActive: false,
                    includeBmsonRows: bmson);
                RegularChartListEntryResult newlyInstalledView = owner.ApplyMainLibraryView(
                    newlyInstalledRoute,
                    library,
                    parameter: null,
                    treeParameter: installedPackage,
                    preserveSummary: false,
                    Stopwatch.StartNew());
                Assert.IsTrue(newlyInstalledView.WasCommitted);
                Assert.AreEqual(RegularChartListEntryRoute.SubsetVirtual, newlyInstalledView.Route);
                Assert.AreEqual(1, table.Rows.Count);
                ResourceHealthIndexSnapshot afterNewlyInstalledView = library.TryGetCurrentResourceHealthIndexSnapshotForView();
                Assert.AreNotSame(ResourceHealthIndexSnapshot.Empty, afterNewlyInstalledView);
                var installedRow = (LibraryChartRow)table.Rows[0]!;
                Assert.AreSame(installed.Token, installedRow.Chart.Token);
                AssertCalculatedInstallResourceHealth(installedRow.Chart, resourceAlreadyExistsAtDestination);
                Assert.AreEqual(installed.WAVHealth, installedRow.WAVHealth);
                Assert.AreEqual(installed.BGAHealth, installedRow.BGAHealth);
                Assert.AreEqual(installed.MovieHealth, installedRow.MovieHealth);
                if (resourceAlreadyExistsAtDestination)
                {
                    Assert.IsFalse(installedRow.WarningDigestText.Contains(
                        Resources.WarningDigest_ResourceMissing,
                        StringComparison.Ordinal));
                    Assert.IsFalse(installedRow.WarningTooltipText.Contains(
                        "WAV",
                        StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    StringAssert.Contains(installedRow.WarningDigestText, Resources.WarningDigest_ResourceMissing);
                    StringAssert.Contains(installedRow.WarningTooltipText, "WAV");
                }

                var normalRoute = new ChartListRefreshRoute(
                    ChartListRefreshRouteKind.ContinueMainLibrary,
                    MainViewUpdateMode.FolderFilterSelected,
                    MainViewUpdateMode.FolderFilterSelected,
                    MainViewUpdateMode.FolderFilterSelected,
                    isPlaylistTreeActive: false,
                    includeBmsonRows: bmson);
                RegularChartListEntryResult normalView = owner.ApplyMainLibraryView(
                    normalRoute,
                    library,
                    parameter: null,
                    treeParameter: null,
                    preserveSummary: false,
                    Stopwatch.StartNew());
                Assert.IsTrue(normalView.WasCommitted);
                Assert.AreEqual(RegularChartListEntryRoute.DefaultVirtual, normalView.Route);
                Assert.AreSame(afterNewlyInstalledView, library.TryGetCurrentResourceHealthIndexSnapshotForView());
                Assert.AreEqual(1, table.Rows.Count);
                var normalRow = (LibraryChartRow)table.Rows[0]!;
                Assert.AreSame(installed.Token, normalRow.Chart.Token);
                AssertCalculatedInstallResourceHealth(normalRow.Chart, resourceAlreadyExistsAtDestination);
                Assert.AreEqual(installed.WAVHealth, normalRow.WAVHealth);
                Assert.AreEqual(installed.BGAHealth, normalRow.BGAHealth);
                Assert.AreEqual(installed.MovieHealth, normalRow.MovieHealth);
                if (resourceAlreadyExistsAtDestination)
                {
                    Assert.IsFalse(normalRow.WarningDigestText.Contains(
                        Resources.WarningDigest_ResourceMissing,
                        StringComparison.Ordinal));
                    Assert.IsFalse(normalRow.WarningTooltipText.Contains(
                        "WAV",
                        StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    StringAssert.Contains(normalRow.WarningDigestText, Resources.WarningDigest_ResourceMissing);
                    StringAssert.Contains(normalRow.WarningTooltipText, "WAV");
                }
            }
            finally
            {
                library.PropertyChanged -= handler;
                library.RequestShutdown("install_resource_health_projection_test_cleanup");
            }
        });
    }

    private static void AssertCalculatedInstallResourceHealth(ChartFile chart, bool wavExists)
    {
        ResourceHealthMaintenanceSnapshot maintenance = chart.ResourceHealthMaintenanceSnapshot;
        Assert.IsNotNull(maintenance);
        Assert.AreEqual(MaintenanceInfoOrigin.Calculated, maintenance.Origin);
        Assert.AreEqual(1, maintenance.WavFilesDefined);
        Assert.AreEqual(wavExists ? 1 : 0, maintenance.WavFilesExisting);
        Assert.AreEqual(0, maintenance.BgaFilesDefined);
        Assert.IsNull(maintenance.BgaFilesExisting);
        Assert.AreEqual(0, maintenance.MovieFilesDefined);
        Assert.IsNull(maintenance.MovieFilesExisting);
        Assert.AreEqual(wavExists ? 100 : 0, chart.WAVHealth);
        Assert.AreEqual(100, chart.BGAHealth);
        Assert.AreEqual(100, chart.MovieHealth);
    }

    private static void AssertNormalInstallRouteUsesPreflightDestinationAndWarmDelta(
        string route,
        int backgroundCount)
    {

        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            bool isAutoRoute = string.Equals(route, "auto", StringComparison.Ordinal);
            bool isEstimatedRoute = string.Equals(route, "estimated", StringComparison.Ordinal);
            bool isForceRoute = string.Equals(route, "force", StringComparison.Ordinal);
            Assert.IsTrue(isAutoRoute || isEstimatedRoute || isForceRoute);
            string installRootPath = Path.Combine(tempRootPath, "AutoInstalled");
            Directory.CreateDirectory(installRootPath);

            (string SourceDirectoryPath, string FirstSourcePath, string SecondSourcePath,
                string FirstResourceName, string SecondResourceName, ChartFile FirstSource,
                ChartFile SecondSource, ChartPackage Package) CreateInstallStep(int step)
            {
                string sourceDirectoryPath = Path.Combine(tempRootPath, "AutoSource" + step);
                string explicitDestinationDirectoryPath = Path.Combine(
                    isForceRoute ? Path.Combine(tempRootPath, "ConfiguredDestinations") : installRootPath,
                    "Explicit" + step);
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
                ChartFile firstSource = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(firstSourcePath));
                ChartFile secondSource = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(secondSourcePath));
                var package = ChartPackage.FromChartEntries([
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

            (string SourceDirectoryPath, string FirstSourcePath, string SecondSourcePath, string FirstResourceName, string SecondResourceName, ChartFile FirstSource, ChartFile SecondSource, ChartPackage Package) firstStep = CreateInstallStep(1);
            (string SourceDirectoryPath, string FirstSourcePath, string SecondSourcePath, string FirstResourceName, string SecondResourceName, ChartFile FirstSource, ChartFile SecondSource, ChartPackage Package) secondStep = CreateInstallStep(2);
            string backgroundDirectoryPath = Path.Combine(tempRootPath, "Background");
            var backgroundFiles = new List<ChartFile>(backgroundCount);
            for (int index = 0; index < backgroundCount; index++)
            {
                string backgroundPath = CreateBmsFile(
                    backgroundDirectoryPath,
                    "background-" + index + ".bms",
                    "#TITLE Background " + index);
                backgroundFiles.Add(BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(backgroundPath)));
            }
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, seedSongDb =>
            {
                foreach (ChartFile backgroundFile in backgroundFiles)
                {
                    seedSongDb.InsertOrReplace(
                        ChartSongStorageMapping.ToBmsRow(backgroundFile),
                        typeof(LR2SongDB.song));
                }
            });

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
            Assert.AreEqual(backgroundCount, library.BmsCharts.Count);
            IPrimaryHashLookup oldPrimary =
                OwnedChartCollectionTestSupport.InvokeCreateInstalledChartKeySnapshotExcludingCharts(library, []);

            List<string> hashWork = [];
            List<string> playlistWork = [];
            List<string> installedWork = [];
            ResourceHealthIndexSnapshot resourceHealthSnapshot =
                library.GetResourceHealthIndexSnapshotForView("preflight_resource_health");
            List<string> resourceHealthWork = [];
            resourceHealthSnapshot.StoreWorkObserver = resourceHealthWork.Add;
            library.OwnedChartHashIndexStoreWorkObserver = hashWork.Add;
            library.PlaylistLibraryResolveIndexStoreWorkObserver = playlistWork.Add;
            library.InstalledChartLookupStoreWorkObserver = installedWork.Add;
            string? expectedSourcePath = null;
            string? expectedSourceHash = null;
            var readOnlySongDbGateway = new BmsLibraryDbGateway(songDbPath);
            int notificationCount = 0;
            Exception? notificationInspectionFailure = null;
            System.ComponentModel.PropertyChangedEventHandler inspectNotification = (_, args) =>
            {
                if (args.PropertyName != nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                {
                    return;
                }
                notificationCount++;
                try
                {
                    ChartFile notifiedFirst = library.BmsCharts.Single(file => file.Md5 == expectedSourceHash);
                    Assert.IsFalse(string.Equals(notifiedFirst.Path, expectedSourcePath, StringComparison.OrdinalIgnoreCase));
                    // 構築しない取得で、公開後の遅延反映を見逃さず現在性を検査します。
                    ResourceHealthIndexSnapshot notifiedResourceHealth = library.TryGetCurrentResourceHealthIndexSnapshotForView();
                    Assert.AreNotSame(ResourceHealthIndexSnapshot.Empty, notifiedResourceHealth);
                    Assert.AreEqual(resourceHealthSnapshot.TargetCount + 2, notifiedResourceHealth.TargetCount);
                    Assert.IsTrue(notifiedResourceHealth.Version > resourceHealthSnapshot.Version);
                    Assert.IsFalse(notifiedResourceHealth.GetProjection((notifiedFirst)).HasIssues);
                    using LibraryFileMutationLease probe = library.TryBeginLibraryFileMutation(
                        "warm_install_notification_lease_probe",
                        showMessage: false);
                    if (isAutoRoute) { Assert.IsNull(probe, "必要な公開中も非同期操作の外側Lを保持します。"); }
                    else { Assert.IsNotNull(probe); }
                    AssertLibraryWriterCanBeAcquired(library, "rwlockBMSFilesInitializedAll");
                    AssertLibraryWriterCanBeAcquired(library, "rwlockPendingInstallCharts");
                    AssertLibraryWriterCanBeAcquired(library, "rwlockBMSFiles");
                    // 通知の観測はSELECTだけなので、writer接続を使わず共有writer lockの保持を避けます。
                    using LR2SongDBExtended notifiedSongDb = readOnlySongDbGateway.OpenSongDbReadOnly();
                    Assert.AreEqual(
                        1,
                        notifiedSongDb.ExecuteScalar<int>(
                            "SELECT COUNT(1) FROM song WHERE path = ?;",
                            notifiedFirst.Path));
                }
                catch (Exception exception)
                {
                    notificationInspectionFailure ??= exception;
                }
            };
            library.PropertyChanged += inspectNotification;

            OwnedChartHashIndexVersionedSnapshot? snapshotAfterFirstHash = null;
            InstalledChartLookupIndexSnapshot? snapshotAfterFirstInstalled = null;
            PlaylistLibraryResolveIndexSnapshot? snapshotAfterFirstPlaylist = null;
            try
            {
                for (int stepIndex = 0; stepIndex < 2; stepIndex++)
                {
                    (string SourceDirectoryPath, string FirstSourcePath, string SecondSourcePath, string FirstResourceName, string SecondResourceName, ChartFile FirstSource, ChartFile SecondSource, ChartPackage Package) step = stepIndex == 0 ? firstStep : secondStep;
                    if (stepIndex > 0 && !isAutoRoute)
                    {
                        library.ChartPackagesPending = CreatePackageCollection([step.Package]);
                    }

                    expectedSourcePath = step.FirstSourcePath;
                    expectedSourceHash = step.FirstSource.Md5;
                    int previousNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
                    int previousHashWorkCount = hashWork.Count;
                    int previousInstalledWorkCount = installedWork.Count;
                    int previousPlaylistWorkCount = playlistWork.Count;
                    int previousResourceHealthWorkCount = resourceHealthWork.Count;
                    int previousResourceHealthVersion = resourceHealthSnapshot.Version;
                    int previousResourceHealthTargetCount = resourceHealthSnapshot.TargetCount;
                    int previousNotificationCount = notificationCount;
                    notificationInspectionFailure = null;

                    LibraryMutationSessionReceipt sessionReceipt;
                    if (isAutoRoute)
                    {
                        PackageInstallCommandResult command = library.InstallChartPackagesAutoWithProgressAsync(
                            [step.SourceDirectoryPath],
                            CancellationToken.None,
                            new RecordingPackageInstallProgressWriter()).GetAwaiter().GetResult();
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
                    using (LibraryFileMutationLease terminalProbe = library.TryBeginLibraryFileMutation("warm_install_terminal_probe", showMessage: false))
                    {
                        Assert.IsNotNull(terminalProbe, "実操作終端後は新規の変更受付を取得できます。");
                    }
                    Assert.AreEqual(stepIndex + 1, library.ChartPackagesInstalled.Count);
                    Assert.AreEqual(0, library.ChartPackagesPending.Count);
                    ChartFile firstInstalled = library.BmsCharts.Single(file => file.Md5 == step.FirstSource.Md5);
                    ChartFile secondInstalled = library.BmsCharts.Single(file => file.Md5 == step.SecondSource.Md5);
                    string firstDestinationPath = firstInstalled.Path;
                    string secondDestinationPath = secondInstalled.Path;
                    Assert.IsFalse(string.Equals(firstDestinationPath, step.FirstSourcePath, StringComparison.OrdinalIgnoreCase));
                    Assert.IsFalse(string.Equals(secondDestinationPath, step.SecondSourcePath, StringComparison.OrdinalIgnoreCase));
                    StringAssert.StartsWith(firstDestinationPath, installRootPath);
                    StringAssert.StartsWith(secondDestinationPath, installRootPath);
                    if (isForceRoute)
                    {
                        string explicitDestination = Path.Combine(tempRootPath, "ConfiguredDestinations", "Explicit" + (stepIndex + 1));
                        Assert.IsFalse(string.Equals(Path.GetDirectoryName(firstDestinationPath), explicitDestination, StringComparison.OrdinalIgnoreCase));
                        Assert.IsFalse(string.Equals(Path.GetDirectoryName(secondDestinationPath), explicitDestination, StringComparison.OrdinalIgnoreCase));
                        Assert.IsFalse(File.Exists(Path.Combine(explicitDestination, Path.GetFileName(step.FirstSourcePath))));
                        Assert.IsFalse(File.Exists(Path.Combine(explicitDestination, Path.GetFileName(step.SecondSourcePath))));
                        foreach (string resourceName in new[] { step.FirstResourceName, step.SecondResourceName })
                        {
                            Assert.IsFalse(File.Exists(Path.Combine(explicitDestination, resourceName + ".wav")));
                            Assert.IsFalse(File.Exists(Path.Combine(explicitDestination, resourceName + ".png")));
                            Assert.IsFalse(File.Exists(Path.Combine(explicitDestination, resourceName + ".mp4")));
                        }
                    }
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
                        .Single(entry => entry.Chart.Md5 == step.FirstSource.Md5);
                    Assert.AreEqual(firstDestinationPath, installedEntry.Chart.Path);

                    // 結果検証もSELECTだけなので、writer接続を使わず同じread-only経路を使います。
                    using (LR2SongDBExtended verifySongDb = readOnlySongDbGateway.OpenSongDbReadOnly())
                    {
                        Assert.AreEqual(1, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ?;", firstDestinationPath));
                        Assert.AreEqual(1, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ?;", secondDestinationPath));
                        Assert.AreEqual(0, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ?;", step.FirstSourcePath));
                        Assert.AreEqual(0, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ?;", step.SecondSourcePath));
                    }

                    OwnedChartHashIndexVersionedSnapshot updatedHash = library.GetOwnedChartHashIndexSnapshot();
                    OwnedChartHashIndexVersionedSnapshot cachedHash = library.GetOwnedChartHashIndexSnapshot();
                    BMSLibrary.InstalledPrimaryHashWarmupResult updatedPrimary =
                        library.WarmInstalledPrimaryHashLookup("preflight_primary_after_install_" + stepIndex);
                    IPrimaryHashLookup currentPrimary =
                        OwnedChartCollectionTestSupport.InvokeCreateInstalledChartKeySnapshotExcludingCharts(library, []);
                    Assert.AreEqual(backgroundCount + ((stepIndex + 1) * 2), updatedPrimary.PrimaryHashCount);
                    Assert.IsTrue(currentPrimary.ContainsPrimaryHash(backgroundFiles[0].Md5));
                    Assert.IsTrue(currentPrimary.ContainsPrimaryHash(firstInstalled.Md5));
                    Assert.IsTrue(currentPrimary.ContainsPrimaryHash(secondInstalled.Md5));
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
                    Assert.IsTrue(updatedPlaylistCacheHit, string.Join(";", playlistWork));
                    Assert.AreEqual(0, updatedPlaylistStaleRetries);
                    Assert.IsTrue(cachedPlaylistCacheHit);
                    Assert.AreEqual(0, cachedPlaylistStaleRetries);
                    Assert.IsTrue(oldHash.ContainsMd5(backgroundFiles[0].Md5));
                    Assert.IsTrue(oldInstalled.ContainsPrimaryHash(backgroundFiles[0].Md5));
                    Assert.IsTrue(oldPrimary.ContainsPrimaryHash(backgroundFiles[0].Md5));
                    Assert.IsFalse(oldPrimary.ContainsPrimaryHash(firstStep.FirstSource.Md5));
                    Assert.IsTrue(oldPlaylist.ContainsCandidate(ChartFileKind.Bms, backgroundFiles[0].Path));
                    Assert.IsTrue(updatedHash.ContainsMd5(backgroundFiles[0].Md5));
                    Assert.IsTrue(updatedHash.ContainsMd5(firstInstalled.Md5));
                    Assert.IsTrue(updatedHash.ContainsMd5(secondInstalled.Md5));
                    Assert.IsTrue(updatedInstalled.ContainsPrimaryHash(firstInstalled.Md5));
                    Assert.IsTrue(updatedInstalled.ContainsPrimaryHash(secondInstalled.Md5));
                    Assert.IsTrue(updatedPlaylist.ContainsCandidate(ChartFileKind.Bms, firstDestinationPath));
                    Assert.IsTrue(updatedPlaylist.ContainsCandidate(ChartFileKind.Bms, secondDestinationPath));
                    Assert.AreEqual(firstInstalled.Mode, updatedPlaylist.ResolveChartSnapshot(LibraryChartRef.FromChartFile(firstInstalled)).Mode);
                    Assert.AreEqual(firstInstalled.Level, updatedPlaylist.ResolveChartSnapshot(LibraryChartRef.FromChartFile(firstInstalled)).Level);

                    if (stepIndex == 0)
                    {
                        snapshotAfterFirstHash = updatedHash;
                        snapshotAfterFirstInstalled = updatedInstalled;
                        snapshotAfterFirstPlaylist = updatedPlaylist;
                    }
                    else
                    {
                        // 2回目のroot更新後も、1回目のimmutable snapshotはその内容を保持します。
                        Assert.IsTrue(snapshotAfterFirstHash!.ContainsMd5(firstStep.FirstSource.Md5));
                        Assert.IsTrue(snapshotAfterFirstInstalled!.ContainsPrimaryHash(firstStep.FirstSource.Md5));
                        Assert.IsTrue(snapshotAfterFirstPlaylist!.ContainsCandidate(
                            ChartFileKind.Bms,
                            library.BmsCharts.Single(file => file.Md5 == firstStep.FirstSource.Md5).Path));
                    }

                    NormalLibraryRefreshNotificationBatch notificationBatch =
                        library.GetNormalLibraryRefreshNotificationsAfter(previousNotificationVersion);
                    Assert.IsTrue(notificationCount > previousNotificationCount);
                    Assert.IsNull(notificationInspectionFailure, notificationInspectionFailure?.ToString());
                    Assert.IsTrue(notificationBatch.HasRefreshNotification);
                    Assert.IsTrue((notificationBatch.ChangedCharts.Count > 0 || notificationBatch.DeletedTokens.Count > 0 || notificationBatch.HasEffect(LibraryChartRefreshEffects.SourceChanged)));
                    Assert.IsTrue(notificationBatch.ChangedCharts.Any(chart => chart.Kind == ChartFileKind.Bms));
                    Assert.IsTrue(notificationBatch.HasEffect(LibraryChartRefreshEffects.SourceChanged));

                    ResourceHealthIndexSnapshot updatedResourceHealth =
                        library.TryGetCurrentResourceHealthIndexSnapshotForView();
                    Assert.AreEqual(previousResourceHealthTargetCount + 2, updatedResourceHealth.TargetCount);
                    Assert.IsTrue(updatedResourceHealth.Version > previousResourceHealthVersion);
                    Assert.IsFalse(updatedResourceHealth.GetProjection(
                        (firstInstalled)).HasIssues);
                    Assert.IsFalse(updatedResourceHealth.GetProjection(
                        (secondInstalled)).HasIssues);
                    string[] operationResourceHealthWork =
                        [.. resourceHealthWork.Skip(previousResourceHealthWorkCount)];
                    Assert.IsTrue(operationResourceHealthWork.Contains("entry_lookup"));
                    Assert.IsFalse(operationResourceHealthWork.Contains("warning_sequence_enumeration"));
                    Assert.IsFalse(operationResourceHealthWork.Contains("warning_sequence_entry_visited"));
                    resourceHealthSnapshot.StoreWorkObserver = null;
                    updatedResourceHealth.StoreWorkObserver = resourceHealthWork.Add;
                    resourceHealthSnapshot = updatedResourceHealth;

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
            }
            finally
            {
                library.PropertyChanged -= inspectNotification;
                resourceHealthSnapshot.StoreWorkObserver = null;
                library.OwnedChartHashIndexStoreWorkObserver = null;
                library.PlaylistLibraryResolveIndexStoreWorkObserver = null;
                library.InstalledChartLookupStoreWorkObserver = null;
            }
        });
    }

    /// <summary>未収束の実非同期入口では導入せず保留・入力を保持し、モデル警告をOperationMessagesへ残します。実配送はowner側へ分担します。</summary>
    [TestMethod]
    public void InstallChartPackagesAuto_UnconvergedCatalogKeepsDiscoveredPackagePendingInsteadOfInstalling()
    {

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
                BmsCharts = [],
                BmsonCharts = [],
                SearchTargets = [installRoot],
                ChartPackagesPending = CreatePackageCollection([]),
                ChartPackagesInstalled = CreatePackageCollection([])
            };
            library.ResetCatalogPathConvergence(CatalogPathConvergenceBlockReason.StartupFileScanDisabled);

            PackageInstallCommandResult installed = library.InstallChartPackagesAutoWithProgressAsync([sourceDirectory], CancellationToken.None, new RecordingPackageInstallProgressWriter()).GetAwaiter().GetResult();

            Assert.AreEqual(0, installed.RegisteredPackages.Count);
            Assert.AreEqual(1, library.ChartPackagesPending.Count);
            Assert.AreEqual(0, library.ChartPackagesInstalled.Count);
            Assert.IsTrue(File.Exists(sourceChartPath));
            Assert.AreEqual(0, dialogService.Messages.Count);
            Assert.AreEqual(1, installed.OperationMessages.Count);
        });
    }

    /// <summary>他の変更が受付を保持するとき実非同期入口は副作用前にBusyで失敗し、空の成功として公開しません。</summary>
    [TestMethod]
    public void InstallChartPackagesAutoWithProgress_WhenAnotherFileMutationOwnsAdmission_FailsInsteadOfPublishingEmptySuccess()
    {

        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string sourceDirectory = Path.Combine(tempRootPath, "busy-auto-install-source");
            Directory.CreateDirectory(sourceDirectory);
            var library = new TestBmsLibrary(songDbPath);
            using LibraryFileMutationLease incumbent = library.TryBeginLibraryFileMutation(
                "test_busy_auto_install");
            Assert.IsNotNull(incumbent);

            Assert.ThrowsException<InvalidOperationException>(
                () => library.InstallChartPackagesAutoWithProgressAsync([sourceDirectory], CancellationToken.None, new RecordingPackageInstallProgressWriter()).GetAwaiter().GetResult());
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
        var lease = PendingEstimatedInstallMutationLease.Acquire(
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
            ChartFile first = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(firstPath));
            ChartFile second = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(secondPath));
            if (scenario == "different_md5_same_sha256")
            {
                // 同一 SHA256 を明示し、BMS の primary identity が MD5 である契約を分離します。
                ChartFile firstWithSharedSha = CreateFile(first.Md5, firstPath);
                ChartFile secondWithSharedSha = CreateFile(second.Md5, secondPath);
                firstWithSharedSha = firstWithSharedSha with { Sha256 = new string('a', 64) };
                secondWithSharedSha = secondWithSharedSha with { Sha256 = new string('a', 64) };
                first = firstWithSharedSha;
                second = secondWithSharedSha;
            }
            PackageChartEntry firstEntry = ChartPackageTestExtensions.CreateEntryWithInstallDestination(
                first, hasBaseline ? string.Empty : target);
            PackageChartEntry secondEntry = ChartPackageTestExtensions.CreateEntryWithInstallDestination(
                second, rejected ? Path.Combine(root, "Installed", "Other") : target);
            var package = ChartPackage.FromChartEntries(scenario == "different_md5_same_sha256"
                ? [secondEntry] : [firstEntry, secondEntry]);
            package.path = source;
            package.delete_parent = false;
            if (hasBaseline)
            {
                using var seed = new LR2SongDBExtended(songDbPath);
                seed.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(first), typeof(LR2SongDB.song));
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
                BmsCharts = hasBaseline ? [first] : [],
                BmsonCharts = [],
                ChartPackagesPending = CreatePackageCollection([package]),
                ChartPackagesInstalled = CreatePackageCollection([])
            };

            PendingInstallBatchResult result = library.InstallPendingPackagesToEstimatedDestinationsWithReceipt([package]);

            Assert.AreEqual(!rejected, result.HasDurableCommit);
            Assert.AreEqual(rejected ? 0 : 1, result.SessionReceipt.ApplyCounts.InstalledTargetApplyCount);
            Assert.AreEqual(0, result.SessionReceipt.ApplyCounts.CatalogApplyCount);
            Assert.AreEqual(rejected ? 0 : 1, result.SessionReceipt.ApplyCounts.RequiredPublicationCount);
            Assert.AreEqual(rejected ? 1 : 0, library.ChartPackagesPending.Count);
            using LR2SongDBExtended readback = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
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

        WithTemporarySongDb((songDbPath, root) =>
        {
            string source = Path.Combine(root, "Pending");
            string target = Path.Combine(root, "Installed");
            Directory.CreateDirectory(source);
            string sourcePath = Path.Combine(source, "chart.bmson");
            File.WriteAllText(sourcePath, CreateBmsonJsonWithSound("sound.wav"));
            File.WriteAllBytes(Path.Combine(source, "sound.wav"), [1, 2, 3]);
            var entry = PackageChartEntry.FromChart((ChartTestValues.ReadBmson(sourcePath)));
            entry.ApplyInstallDestination(target, "Target", "Artist");
            var package = ChartPackage.FromChartEntries([entry]);
            package.path = source;
            package.delete_parent = false;
            var library = new TestBmsLibrary(songDbPath, null, null,
                new RealFileMutationService(), new RecordingDialogService(), new TestUiScheduler(() => null!),
                () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false, BMSInstallDir = target })
            {
                BmsCharts = [],
                BmsonCharts = [],
                ChartPackagesPending = CreatePackageCollection([package]),
                ChartPackagesInstalled = CreatePackageCollection([])
            };

            PendingInstallBatchResult result = library.InstallPendingPackagesToEstimatedDestinationsWithReceipt([package]);

            Assert.IsTrue(result.HasDurableCommit);
            Assert.IsFalse(result.SessionReceipt.HasRequiredFailure);
            Assert.AreEqual(1, result.SessionReceipt.ApplyCounts.InstalledTargetApplyCount);
            Assert.AreEqual(0, library.BmsCharts.Count);
            Assert.AreEqual(1, library.BmsonCharts.Count);
            Assert.AreEqual(0, library.ChartPackagesPending.Count);
            Assert.IsNull(entry.GetBmsChartForTest());
            Assert.IsTrue(string.IsNullOrEmpty(entry.Chart.InstallDestination));
            string installedPath = Path.Combine(target, "chart.bmson");
            Assert.AreEqual(installedPath, library.BmsonCharts.Single().Path);
            Assert.IsTrue(File.Exists(installedPath));
            using LR2SongDBExtended readback = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(0, readback.Table<LR2SongDB.song>().Count());
            Assert.AreEqual(installedPath, readback.Table<LR2SongDBExtended.bmson_song>().Single().path);
        });
    }

    /// <summary>実非同期入口の保留公開時に、先行物理成功だけが重複根拠となり未導入hashを予約しない分類を捕捉します。後続直接推定による警告更新と区別し、終端のThird実導入・FS・DB・一回反映も保持します。</summary>
    [TestMethod]
    public void InstallChartPackagesAuto_OnlyPhysicalSuccessReservesHashesForLaterCandidates()
    {

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
                    OperationModeLR2DB = false,
                    BMSInstallDir = installed,
                    FolderNameFormat = "%TITLE%",
                    KeepInstallablePackagesPending = false
                })
            {
                BmsCharts = [],
                BmsonCharts = [],
                SearchTargets = [installed],
                ChartPackagesPending = CreatePackageCollection([]),
                ChartPackagesInstalled = CreatePackageCollection([])
            };

            bool? matchingInstalledAtPublication = null;
            bool? unmatchedInstalledAtPublication = null;
            System.ComponentModel.PropertyChangedEventHandler captureClassification = (_, args) =>
            {
                if (args.PropertyName != nameof(BMSLibrary.ChartPackagesPending)
                    || matchingInstalledAtPublication.HasValue || library.ChartPackagesPending.Count == 0) { return; }
                ChartPackage published = library.ChartPackagesPending.Single();
                matchingInstalledAtPublication = published.ChartEntries.Single(entry => Path.GetFileName(entry.Chart.Path) == "matching.bms")
                    .Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled);
                unmatchedInstalledAtPublication = published.ChartEntries.Single(entry => Path.GetFileName(entry.Chart.Path) == "unmatched.bms")
                    .Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled);
            };
            library.PropertyChanged += captureClassification;
            PackageInstallCommandResult result;
            try
            {
                result = library.InstallChartPackagesAutoWithProgressAsync(
                    [first, mixed, third], CancellationToken.None, new RecordingPackageInstallProgressWriter()).GetAwaiter().GetResult();
            }
            finally { library.PropertyChanged -= captureClassification; }

            Assert.IsTrue(result.HasDurableCommit);
            Assert.IsFalse(result.HasRequiredFailure);
            Assert.AreEqual(1, result.SessionReceipt.ApplyCounts.InstalledTargetApplyCount);
            Assert.AreEqual(2, library.ChartPackagesInstalled.Count);
            ChartPackage pending = library.ChartPackagesPending.Single();
            Assert.AreEqual(mixed, pending.path);
            PackageChartEntry matching = pending.ChartEntries.Single(entry => Path.GetFileName(entry.Chart.Path) == "matching.bms");
            PackageChartEntry unmatched = pending.ChartEntries.Single(entry => Path.GetFileName(entry.Chart.Path) == "unmatched.bms");
            Assert.AreEqual(true, matchingInstalledAtPublication);
            Assert.AreEqual(false, unmatchedInstalledAtPublication);
            Assert.IsTrue(File.Exists(matching.Chart.Path));
            Assert.IsTrue(File.Exists(unmatched.Chart.Path));
            Assert.IsTrue(File.Exists(Path.Combine(installed, "First", "first.bms")));
            Assert.IsTrue(File.Exists(Path.Combine(installed, "Third", "third.bms")));
            using LR2SongDBExtended readback = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(2, readback.Table<LR2SongDB.song>().Count());
        });
    }

    /// <summary>導入先と確認設定によらず明示的に拒否された package は、入力元・DB・保留を保持します。</summary>
    [DataTestMethod]
    [DataRow(true, true)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(false, false)]
    public void ForceInstallPendingPackages_RejectsUnapprovedPackageWithoutMaterializingBmson(bool hasDestination, bool showNewConfirmation)
    {

        WithTemporarySongDb((songDbPath, root) =>
        {
            string source = Path.Combine(root, "Pending");
            string target = Path.Combine(root, "Installed");
            Directory.CreateDirectory(source);
            string sourcePath = Path.Combine(source, "chart.bmson");
            File.WriteAllText(sourcePath, CreateBmsonJsonWithSound("sound.wav"));
            var entry = PackageChartEntry.FromChart((ChartTestValues.ReadBmson(sourcePath)));
            if (hasDestination)
            {
                entry.ApplyInstallDestination(target, "Target", "Artist");
            }
            var package = ChartPackage.FromChartEntries([entry]);
            package.path = source;
            var library = new TestBmsLibrary(songDbPath, null, null,
                new RealFileMutationService(), new RecordingDialogService(), new TestUiScheduler(() => null!),
                () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false, BMSInstallDir = target, ShowNewPackageInstallConfirmMsg = showNewConfirmation })
            {
                BmsCharts = [],
                BmsonCharts = [],
                ChartPackagesPending = CreatePackageCollection([package]),
                ChartPackagesInstalled = CreatePackageCollection([])
            };

            LibraryMutationSessionReceipt receipt = library.ForceInstallPendingPackagesWithReceipt(
                [package], approveNormalInstallOverride: false, approvedNormalInstallOverridePackages: null);

            Assert.IsFalse(receipt.DurableCommit);
            Assert.AreEqual(default(LibraryMutationSessionApplyCounts), receipt.ApplyCounts);
            Assert.AreSame(package, library.ChartPackagesPending.Single());
            if (hasDestination)
            {
                Assert.AreEqual(target, entry.Chart.InstallDestination);
            }
            else
            {
                Assert.IsTrue(string.IsNullOrWhiteSpace(entry.Chart.InstallDestination));
            }
            Assert.IsNull(entry.GetBmsChartForTest());
            Assert.IsTrue(File.Exists(sourcePath));
            Assert.IsFalse(Directory.Exists(target));
            using LR2SongDBExtended readback = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(0, readback.Table<LR2SongDBExtended.bmson_song>().Count());
        });
    }

    /// <summary>公開入口で確認設定と応答を適用し、拒否時は保留を保持、承認時は新規先へ導入します。</summary>
    [DataTestMethod]
    [DataRow(false, true, (int)MessageBoxResult.No)]
    [DataRow(false, true, (int)MessageBoxResult.None)]
    [DataRow(false, true, (int)MessageBoxResult.Yes)]
    [DataRow(false, false, (int)MessageBoxResult.None)]
    [DataRow(true, false, (int)MessageBoxResult.No)]
    [DataRow(true, false, (int)MessageBoxResult.None)]
    [DataRow(true, false, (int)MessageBoxResult.Yes)]
    public void ForceInstallPendingPackages_PublicEntryUsesConfirmationSettingAndPreservesRejectedInput(
        bool hasDestination, bool showNewConfirmation, int responseValue)
    {
        var response = (MessageBoxResult)responseValue;

        WithTemporarySongDb((songDbPath, root) =>
        {
            string source = Path.Combine(root, "Pending");
            string installationRoot = Path.Combine(root, "NewInstalled");
            string explicitDestination = Path.Combine(root, "Explicit");
            Directory.CreateDirectory(installationRoot);
            string sourcePath = CreateBmsFileWithResources(source, "chart.bms", "#TITLE PublicForceTarget", "pending-resource");
            ChartFile sourceChart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(sourcePath));
            var entry = PackageChartEntry.FromChart((sourceChart));
            if (hasDestination)
            {
                entry.ApplyInstallDestination(explicitDestination, "Existing", "Artist");
            }
            var package = ChartPackage.FromChartEntries([entry]);
            package.path = source;
            package.delete_parent = false;
            string existingPath = CreateBmsFile(explicitDestination, "existing.bms", "#TITLE Existing");
            ChartFile existingChart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(existingPath));
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, db =>
                db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(existingChart), typeof(LR2SongDB.song)));
            Dictionary<string, byte[]> sourceBytes = Directory.GetFiles(source)
                .ToDictionary(path => path, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
            byte[] existingBytes = File.ReadAllBytes(existingPath);
            var dialogs = new PendingInstallConfirmationDialogService(response);
            var library = new TestBmsLibrary(songDbPath, null, null,
                new RealFileMutationService(), dialogs, new TestUiScheduler(() => null!),
                () => new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = false,
                    BMSInstallDir = installationRoot,
                    FolderNameFormat = "%TITLE%",
                    ShowNewPackageInstallConfirmMsg = showNewConfirmation,
                    DeletePendingPackageSourceAfterInstall = false,
                    EnableSmartComponentOverwrite = false,
                    KeepSmartOverwriteProtectedFilesByRenaming = false
                })
            {
                BmsCharts = [existingChart],
                BmsonCharts = [],
                SearchTargets = [installationRoot, explicitDestination],
                ChartPackagesPending = CreatePackageCollection([package]),
                ChartPackagesInstalled = CreatePackageCollection([])
            };

            existingChart = library.BmsCharts.Single();
            library.ForceInstallPendingPackages([package]);

            bool confirmationRequired = hasDestination || showNewConfirmation;
            Assert.AreEqual(confirmationRequired ? 1 : 0, dialogs.Requests.Count);
            if (confirmationRequired)
            {
                (string Text, MessageBoxResult Response) request = dialogs.Requests.Single();
                Assert.AreEqual(hasDestination ? Resources.Confirm_NormalInstallOverride : Resources.Confirm_NewPackageInstall, request.Text);
                Assert.AreEqual(response == MessageBoxResult.None ? MessageBoxResult.No : response, request.Response);
            }
            CollectionAssert.AreEqual(existingBytes, File.ReadAllBytes(existingPath));
            CollectionAssert.AreEqual(new[] { existingPath }, Directory.GetFiles(explicitDestination));
            bool accepted = !confirmationRequired || response == MessageBoxResult.Yes;
            using LR2SongDBExtended readback = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(1, readback.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ? AND hash = ?;", existingPath, existingChart.Md5));
            Assert.AreEqual(0, readback.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ?;", sourcePath));
            if (!accepted)
            {
                Assert.AreSame(package, library.ChartPackagesPending.Single());
                Assert.AreEqual(source, package.path);
                Assert.AreEqual(sourcePath, entry.Chart.Path);
                Assert.AreEqual(hasDestination ? explicitDestination : string.Empty, entry.Chart.InstallDestination ?? string.Empty);
                Assert.AreEqual(0, library.ChartPackagesInstalled.Count);
                Assert.AreSame(existingChart.Token, library.BmsCharts.Single().Token);
                CollectionAssert.AreEquivalent(sourceBytes.Keys.ToArray(), Directory.GetFiles(source));
                foreach ((string path, byte[] bytes) in sourceBytes)
                {
                    CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
                }
                Assert.AreEqual(0, Directory.GetFileSystemEntries(installationRoot, "*", System.IO.SearchOption.AllDirectories).Length);
                Assert.AreEqual(1, readback.Table<LR2SongDB.song>().Count());
                Assert.AreEqual(0, readback.Table<LR2SongDB.folder>().Count());
                Assert.AreEqual(0, readback.Table<LR2SongDBExtended.maintenance>().Count());
                Assert.AreEqual(0, readback.Table<LR2SongDBExtended.bmson_song>().Count());
                return;
            }

            Assert.AreEqual(0, library.ChartPackagesPending.Count);
            Assert.AreEqual(1, library.ChartPackagesInstalled.Count);
            ChartFile installed = library.BmsCharts.Single(chart => chart.Md5 == sourceChart.Md5);
            StringAssert.StartsWith(installed.Path, installationRoot + Path.DirectorySeparatorChar);
            Assert.IsFalse(string.Equals(Path.GetDirectoryName(installed.Path), explicitDestination, StringComparison.OrdinalIgnoreCase));
            CollectionAssert.AreEqual(sourceBytes[sourcePath], File.ReadAllBytes(installed.Path));
            foreach (string resourcePath in sourceBytes.Keys.Where(path => !string.Equals(path, sourcePath, StringComparison.OrdinalIgnoreCase)))
            {
                CollectionAssert.AreEqual(sourceBytes[resourcePath],
                    File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(installed.Path)!, Path.GetFileName(resourcePath))));
            }
            Assert.AreEqual(installed.Path, library.ChartPackagesInstalled.Single().ChartEntries.Single().Chart.Path);
            Assert.AreEqual(2, readback.Table<LR2SongDB.song>().Count());
            Assert.AreEqual(1, readback.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ?;", installed.Path));
        });
    }

    [TestMethod]
    public void BuildComponentMovePlan_SkipsExcludedPaths()
    {

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

        var service = new BmsLibraryPackageInstallService();
        ChartFile keepFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\Pkg1\\keep.bms");
        ChartFile removeFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "C:\\Pending\\Pkg1\\remove.bms");
        ChartFile removeWholePackageFile = CreateFile("cccccccccccccccccccccccccccccccc", "C:\\Pending\\Pkg2\\only.bms");
        ChartPackage keepPackage = ChartPackageTestExtensions.CreatePackage([keepFile, removeFile]);
        keepPackage.path = "C:\\Pending\\Pkg1";
        keepPackage.delete_parent = false;
        ChartPackage removePackage = ChartPackageTestExtensions.CreatePackage([removeWholePackageFile]);
        removePackage.path = "C:\\Pending\\Pkg2";
        removePackage.delete_parent = false;

        PendingPackageMutationDelta delta = service.BuildPendingPackageMutationDelta(
            [keepPackage, removePackage],
            chartPathsToRemove: [removeFile.Path, removeWholePackageFile.Path]);

        Assert.IsTrue(delta.HasChanges);
        Assert.AreEqual(1, delta.RemainingPackages.Count);
        Assert.AreSame(keepPackage, delta.RemainingPackages[0]);
        CollectionAssert.AreEqual(new[] { keepFile, removeFile }, keepPackage.GetBmsChartsForTest());
        Assert.AreEqual(1, delta.EntryMutations.Count);
        Assert.AreSame(keepPackage, delta.EntryMutations[0].Package);
        CollectionAssert.AreEqual(new[] { keepFile.Path }, delta.EntryMutations[0].RemainingEntries.Select(entry => entry.Chart.Path).ToArray());
        CollectionAssert.AreEquivalent(new[] { "C:\\Pending\\Pkg2" }, delta.InstallPathsToDelete);
    }

    [TestMethod]
    public void BuildPendingPackageMutationDelta_RemovesAdapterlessBmsonByChartPathWithoutMaterializing()
    {

        var service = new BmsLibraryPackageInstallService();
        var keepEntry = PackageChartEntry.FromChart((ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Pending\\Pkg\\keep.bmson",
            Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            Sha256 = new string('a', 64)
        }));
        var removeEntry = PackageChartEntry.FromChart((ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Pending\\Pkg\\remove.bmson",
            Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            Sha256 = new string('b', 64)
        }));
        var package = ChartPackage.FromChartEntries([keepEntry, removeEntry]);
        package.path = "C:\\Pending\\Pkg";

        PendingPackageMutationDelta delta = service.BuildPendingPackageMutationDelta(
            [package],
            chartPathsToRemove: [removeEntry.Chart.Path]);

        Assert.IsTrue(delta.HasChanges);
        Assert.AreEqual(1, delta.RemainingPackages.Count);
        Assert.AreSame(package, delta.RemainingPackages[0]);
        Assert.IsNull(keepEntry.GetBmsChartForTest());
        Assert.IsNull(removeEntry.GetBmsChartForTest());
        Assert.AreEqual(2, package.ChartEntries.Count);
        Assert.AreEqual(1, delta.EntryMutations.Count);
        Assert.AreSame(package, delta.EntryMutations[0].Package);
        Assert.AreEqual(keepEntry.Chart.Path, delta.EntryMutations[0].RemainingEntries.Single().Chart.Path);
        Assert.IsNull(package.ChartEntries[0].GetBmsChartForTest());
        Assert.AreEqual(0, delta.InstallPathsToDelete.Count);
    }

    [TestMethod]
    public void BuildEstimatedInstallBatchPlan_FiltersCandidatesWithoutReservingUncommittedHashes()
    {

        var service = new BmsLibraryPackageInstallService();
        string destinationDirectory = "C:\\Installed\\Target";
        ChartFile installedFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Lib\\a.bms");
        ChartFile cleanupInstalledFile = CreateFile("cccccccccccccccccccccccccccccccc", "C:\\Lib\\c.bms");
        ChartFile alreadyInstalledInPackage = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\Pkg1\\a.bms");
        ChartFile newFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "C:\\Pending\\Pkg1\\b.bms");
        ChartFile cleanupOnlyFile = CreateFile("cccccccccccccccccccccccccccccccc", "C:\\Pending\\Pkg2\\c.bms");
        ChartPackage mixedPackage = ChartPackageTestExtensions.CreatePackage(
            ChartPackageTestExtensions.CreateEntryWithInstallDestination(alreadyInstalledInPackage, destinationDirectory),
            ChartPackageTestExtensions.CreateEntryWithInstallDestination(newFile, destinationDirectory));
        mixedPackage.path = "C:\\Pending\\Pkg1";
        mixedPackage.delete_parent = false;
        ChartPackage cleanupOnlyPackage = ChartPackageTestExtensions.CreatePackage(
            ChartPackageTestExtensions.CreateEntryWithInstallDestination(cleanupOnlyFile, destinationDirectory));
        cleanupOnlyPackage.path = "C:\\Pending\\Pkg2";
        cleanupOnlyPackage.delete_parent = false;

        PendingInstallBatchPlan plan = service.BuildEstimatedInstallBatchPlan(
            [mixedPackage, cleanupOnlyPackage],
            [mixedPackage, cleanupOnlyPackage],
            CreateInstalledChartLookup([installedFile, cleanupInstalledFile]));

        Assert.AreEqual(2, plan.SelectedPendingPackages.Count);
        Assert.AreEqual(0, plan.CleanupOnlyCandidates.Count);
        Assert.IsTrue(plan.MoveGuardLookup.ContainsPrimaryHash(installedFile.Md5));
        Assert.IsFalse(plan.MoveGuardLookup.ContainsPrimaryHash(newFile.Md5));
        PackageChartEntry alreadyInstalledEntry = mixedPackage.ChartEntries.Single(entry => entry.Chart.Path == alreadyInstalledInPackage.Path);
        Assert.AreEqual(string.Empty, ChartWarningTestHelpers.BuildDigestText(alreadyInstalledEntry));
        Assert.IsTrue(plan.FilterMs >= 0);
        Assert.IsTrue(plan.PlanBuildMs >= 0);
        Assert.AreEqual(2, plan.SelectedPendingCount);
        Assert.AreEqual(0, plan.CleanupOnlyCandidateCount);
    }

    [TestMethod]
    public void BuildEstimatedInstallBatchPlan_CountsDeferredManualHoldPackagesSeparately()
    {

        var service = new BmsLibraryPackageInstallService();
        ChartFile pendingFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\Pkg1\\a.bms");
        ChartPackage deferredPackage = ChartPackageTestExtensions.CreatePackage([pendingFile]);
        deferredPackage.path = "C:\\Pending\\Pkg1";
        deferredPackage.delete_parent = false;
        deferredPackage.DeferredEstimateReason = PendingEstimateDeferredReason.HealthySourceBaseline;

        PendingInstallBatchPlan plan = service.BuildEstimatedInstallBatchPlan(
            [deferredPackage],
            [deferredPackage],
            CreateInstalledChartLookup([]));

        Assert.AreEqual(0, plan.SelectedPendingPackages.Count);
        Assert.AreEqual(1, plan.DeferredManualHoldCount);
        Assert.AreEqual(string.Empty, ChartWarningCollection.BuildDigestText(pendingFile.Warnings, pendingFile.InstallDestination));
    }

    [TestMethod]
    public void ChartPackage_ClearEntryInstallDestinations_DoesNotMaterializeAdapterlessBmsonEntries()
    {
        ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\Pkg\\a.bms");
        var adapterlessBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.WithPackageState((ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Pending\\Pkg\\adapterless.bmson",
            Md5 = "cccccccccccccccccccccccccccccccc"
        }), "C:\\Installed\\Target", "Installed", "Artist", []));
        PackageChartEntry bmsEntry = ChartPackageTestExtensions.CreateEntryWithInstallDestination(bmsFile, "C:\\Installed\\Target");
        var package = ChartPackage.FromChartEntries(
        [
            bmsEntry,
            adapterlessBmsonEntry
        ]);

        foreach (PackageChartEntry entry in package.ChartEntries)
        {
            entry.ClearInstallDestination();
        }

        Assert.AreEqual(string.Empty, bmsEntry.Chart.InstallDestination);
        Assert.IsNull(adapterlessBmsonEntry.GetBmsChartForTest());
        Assert.AreEqual(string.Empty, adapterlessBmsonEntry.Chart.InstallDestination);
    }

    [TestMethod]
    public void ApplyPendingResourceHealthProjection_BmsonUsesChartResourceSnapshot()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string chartPath = Path.Combine(tempDirectoryPath, "chart.bmson");
            ChartFile song = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = chartPath,
                Folder = tempDirectoryPath,
                RawTitle = "BMSON",
                RawArtist = "Artist",
                Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                Resources = TestChartResources.Create(["missing.wav"])
            };
            var entry = PackageChartEntry.FromChart((song));

            IReadOnlyList<ChartWarning> warnings = BmsLibraryPackageInstallService.ApplyPendingResourceHealthProjection(entry);

            Assert.IsTrue(warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            Assert.IsNull(entry.GetBmsChartForTest());
        });
    }

    [TestMethod]
    public void ApplyPendingResourceHealthProjection_StoresWarningsAndHealthOnPackageEntry()
    {

        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string chartPath = Path.Combine(tempDirectoryPath, "chart.bmson");
            ChartFile song = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = chartPath,
                Folder = tempDirectoryPath,
                RawTitle = "BMSON",
                RawArtist = "Artist",
                Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                Resources = TestChartResources.Create(["missing.wav"], ["missing.png"])
            };
            var entry = PackageChartEntry.FromChart((song));

            IReadOnlyList<ChartWarning> warnings = BmsLibraryPackageInstallService.ApplyPendingResourceHealthProjection(entry);

            Assert.IsTrue(warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            Assert.IsTrue(warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceBgaMissing));
            Assert.IsTrue(entry.Chart.WAVHealth.HasValue);
            Assert.IsTrue(entry.Chart.BGAHealth.HasValue);
            Assert.IsNull(entry.GetBmsChartForTest());
        });
    }

    [TestMethod]
    public void ApplyPendingResourceHealthProjection_BmsUsesChartProjectionWithoutMutatingMaintenance()
    {

        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string chartPath = Path.Combine(tempDirectoryPath, "chart.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n"
                + "#TITLE BMS\r\n"
                + "#WAVAA missing.wav\r\n"
                + "#00111:AA\r\n");
            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
            var entry = PackageChartEntry.FromChart((file));

            IReadOnlyList<ChartWarning> warnings = BmsLibraryPackageInstallService.ApplyPendingResourceHealthProjection(entry);

            Assert.IsTrue(warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            Assert.IsFalse((file.ResourceHealthMaintenanceSnapshot?.Origin is MaintenanceInfoOrigin.DbHydrated or MaintenanceInfoOrigin.Calculated));
            Assert.IsFalse(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
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
            ChartFile file = chartEntry.Chart;
            Assert.IsNotNull(file);
            Assert.IsFalse((file.ResourceHealthMaintenanceSnapshot?.Origin is MaintenanceInfoOrigin.DbHydrated or MaintenanceInfoOrigin.Calculated));
            Assert.IsTrue(chartEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
        });
    }

    [TestMethod]
    public void PrepareAutoInstallWorkflow_ProjectsResourceHealthForAllPackageEntries()
    {

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

        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryPackageInstallService();
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "Pkg1");
            Directory.CreateDirectory(packageDirectoryPath);
            File.WriteAllText(Path.Combine(packageDirectoryPath, "chart.bms"), "#PLAYER 1");
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage(Enumerable.Empty<ChartFile>());
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
            Assert.IsTrue(result.Packages[0].ChartEntries.All(entry => entry.GetBmsChartForTest() == null));
        });
    }

    [TestMethod]
    public void SearchChartPackagesRecursivelyWithMetadata_DetectsRootAndNestedChartsAsOneDirectoryPackage()
    {

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
                result.Packages[0].GetBmsChartsForTest().Select(file => Path.GetFileName(file.Path)).ToArray());
        });
    }

    [TestMethod]
    public void ApplyNestedChartFileWarnings_AddsNestedWarningWithoutMaterializingAdapterlessBmsonEntries()
    {

        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "Pkg");
            string nestedDirectoryPath = Path.Combine(packageDirectoryPath, "sub");
            Directory.CreateDirectory(nestedDirectoryPath);
            string rootBmsonPath = Path.Combine(packageDirectoryPath, "root.bmson");
            string nestedBmsonPath = Path.Combine(nestedDirectoryPath, "nested.bmson");
            File.WriteAllText(rootBmsonPath, CreateBmsonJsonWithSound("root.wav"));
            File.WriteAllText(nestedBmsonPath, CreateBmsonJsonWithSound("nested.wav"));
            var rootEntry = PackageChartEntry.FromChart((ChartTestValues.ReadBmson(rootBmsonPath)));
            var nestedEntry = PackageChartEntry.FromChart((ChartTestValues.ReadBmson(nestedBmsonPath)));
            var package = ChartPackage.FromChartEntries([rootEntry, nestedEntry]);
            package.path = packageDirectoryPath;

            bool applied = BmsLibraryPackageInstallService.ApplyNestedChartFileWarnings(package);

            Assert.IsTrue(applied);
            Assert.IsNull(rootEntry.GetBmsChartForTest());
            Assert.IsNull(nestedEntry.GetBmsChartForTest());
            Assert.IsTrue(nestedEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.NestedChartFileInPackage));
            StringAssert.Contains(ChartWarningCollection.BuildTooltipText(nestedEntry.Chart.Warnings), Resources.Warning_NestedChartFileInPackage);
            var normalizedNestedEntry = PackageChartEntry.FromChart(nestedEntry.Chart);
            Assert.IsNull(normalizedNestedEntry.GetBmsChartForTest());
            Assert.IsTrue(normalizedNestedEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.NestedChartFileInPackage));
            var clearedNormalizedNestedEntry = PackageChartEntry.FromChart(nestedEntry.Chart);
            clearedNormalizedNestedEntry.ClearStructuredWarnings();
            Assert.IsFalse(clearedNormalizedNestedEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.NestedChartFileInPackage));
        });
    }

    [TestMethod]
    public void PrepareAutoInstallWorkflow_DetectsSingleBmsonFileSelection()
    {

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
            Assert.IsNull(chartEntry.GetBmsChartForTest());
            Assert.AreEqual(ChartFileKind.Bmson, chartEntry.Chart.Kind);
            Assert.IsTrue(chartEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            StringAssert.Contains(chartEntry.Chart.Warnings.Single(warning => warning.Kind == ChartWarningKind.ResourceWavMissing).Message, "WAV");
        });
    }

    [TestMethod]
    public void PrepareAutoInstallWorkflow_ChecksInstalledChartsWithoutMaterializingUnmatchedBmsonEntries()
    {

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
            Assert.IsNull(installedEntry.GetBmsChartForTest());
            Assert.IsTrue(installedEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
            Assert.IsNull(unmatchedEntry.GetBmsChartForTest());
        });
    }

    [TestMethod]
    public void PackageChartEntry_ResourceSnapshotUsesAcquiredBmsResourcesWithoutOwner()
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
            ChartFile bmsFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
            ChartFile lightweightChart = (bmsFile);
            var entry = PackageChartEntry.FromChart(ChartFileProjection.ToImmutableSnapshot(lightweightChart));

            Assert.AreEqual(1, entry.ResourceSnapshot.AudioReferenceCount);
            Assert.AreEqual(1, entry.ResourceSnapshot.MovieReferenceCount);
            Assert.AreEqual(1, entry.ResourceSnapshot.OptionalImageReferenceCount);
        });
    }

    [TestMethod]
    public void PackageChartEntry_ResourceSnapshotUsesAcquiredBmsonResourcesWithoutOwner()
    {
        ChartFile song = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = @"D:\BMS\pkg\chart.bmson",
            Md5 = "11111111111111111111111111111111",
            RawTitle = "Resource Owner",
            Resources = TestChartResources.Create(["audio.wav"], ["movie.mpg"], stagefile: "stage.png"),
            Stagefile = "stage.png"
        };
        ChartFile lightweightChart = (song);
        var entry = PackageChartEntry.FromChart(ChartFileProjection.ToImmutableSnapshot(lightweightChart));

        Assert.AreEqual(1, entry.ResourceSnapshot.AudioReferenceCount);
        Assert.AreEqual(1, entry.ResourceSnapshot.MovieReferenceCount);
        Assert.AreEqual(1, entry.ResourceSnapshot.OptionalImageReferenceCount);
    }

    [TestMethod]
    public void PrepareAutoInstallWorkflow_KeepsChartPackagePendingWhenOnlySameStemChartFileExists()
    {

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
            ChartFile chart = chartEntry.Chart;
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
            ChartFile chart = chartEntry.Chart;
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
            ChartFile chart = chartEntry.Chart;
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
            Assert.IsNull(chartEntry.GetBmsChartForTest());
            Assert.AreEqual(ChartFileKind.Bmson, chartEntry.Chart.Kind);
            Assert.AreEqual(1, chartEntry.ResourceSnapshot.AudioReferenceCount);
            Assert.AreEqual(0, chartEntry.Chart.Warnings.Count);
        });
    }

    [TestMethod]
    public void PrepareAutoInstallWorkflow_ClassifiesDetectedDirectoriesAsInstallable()
    {

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
            Assert.IsTrue(result.DiscoveredPackages.All(package => package.GetBmsChartsForTest().Count > 0));
            Assert.IsTrue(result.DiscoveryMs >= 0);
            Assert.IsTrue(result.ClassificationMs >= 0);
            Assert.IsTrue(result.TotalMs >= 0);
        });
    }

    [TestMethod]
    public void PrepareAutoInstallWorkflow_DoesNotPrebuildSourceSurfaceForDiscoveredPackages()
    {

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
                    List<ChartFile> discoveredCharts = [.. package.GetBmsChartsForTest()];
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

        ChartFile source = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\chart.bms");
        source = source with { Mode = 7 };

        var entry = PackageChartEntry.FromChart((source));

        Assert.AreEqual(7, entry.Chart.Mode);
    }

    [TestMethod]
    public void PrepareAutoInstallWorkflow_DoesNotMarkRegroupEligibleSourceDirectoryForPartialFileSelection()
    {

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

        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourceDirectoryPath = Path.Combine(tempDirectoryPath, "PendingPkg");
            string destinationDirectoryPath = Path.Combine(tempDirectoryPath, "Installed", "Pkg");
            Directory.CreateDirectory(sourceDirectoryPath);
            string chartPath = Path.Combine(sourceDirectoryPath, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE Receipt\r\n");
            ChartFile chart = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath);
            ChartPackage package = ChartPackageTestExtensions.CreatePackage([chart]);
            package.path = sourceDirectoryPath;
            package.delete_parent = false;
            var service = new BmsLibraryPackageInstallService();
            bool callbackSawSource = false;
            bool callbackSawDestination = false;
            PackageChartEntry liveEntry = package.ChartEntries.Single();
            int liveNotifications = 0;
            System.ComponentModel.PropertyChangedEventHandler liveChanged = (_, _) => liveNotifications++;
            liveEntry.PropertyChanged += liveChanged;

            try
            {
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
                        Assert.AreNotSame(liveEntry, installResult.AddedEntries.Single(), "mutableな準備entryをlive項目と共有しません。");
                        Assert.AreEqual(chartPath, liveEntry.Chart.Path);
                        Assert.AreEqual(0, liveNotifications);
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
                Assert.AreEqual(chartPath, chart.Path);
                Assert.AreSame(liveEntry, package.ChartEntries.Single());
                Assert.AreEqual(chartPath, liveEntry.Chart.Path);
                Assert.AreEqual(0, liveNotifications);
            }
            finally
            {
                liveEntry.PropertyChanged -= liveChanged;
            }
        });
    }

    [DataTestMethod]
    [DataRow("same")]
    [DataRow("inside")]
    [DataRow("ancestor")]
    public void MovePackageFilesWithReceipt_RejectsOverlappingDirectoryDestinationBeforeMutation(string destinationShape)
    {

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

            ChartFile chart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(sourcePath));
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
                    (
                        BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(sourcePath)))));
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
                    (ChartTestValues.Empty(ChartFileKind.Bmson) with
                    {
                        Path = sourcePath,
                        Folder = Path.GetDirectoryName(sourcePath),
                        RawTitle = "Bmson " + soundName,
                        Md5 = md5
                    })));
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
                     foreach (PackageChartEntry prepared in installResult.AddedEntries)
                     {
                         Assert.IsFalse(package.ChartEntries.Contains(prepared));
                     }
                     CollectionAssert.AreEquivalent(sourcePaths, package.ChartEntries.Select(entry => entry.Chart.Path).ToList());
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
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(sourceChartPath))]);
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
                    BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(firstChartPath)),
                    firstDestinationDirectoryPath));
            ChartPackage conflictPackage = ChartPackageTestExtensions.CreatePackage(
                ChartPackageTestExtensions.CreateEntryWithInstallDestination(
                    BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(conflictChartPath)),
                    conflictDestinationDirectoryPath));
            ChartPackage thirdPackage = ChartPackageTestExtensions.CreatePackage(
                ChartPackageTestExtensions.CreateEntryWithInstallDestination(
                    BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(thirdChartPath)),
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
                BmsCharts = [],
                BmsonCharts = [],
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
            using LR2SongDBExtended verifySongDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
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
                        BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath)),
                        Path.Combine(installationRoot, titles[i])));
                package.path = sources[i];
                package.delete_parent = false;
                packages.Add(package);
            }
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, seedSongDb =>
            {
                seedSongDb.CreateTable<LR2SongDBExtended.install>();
                foreach (ChartPackage package in packages)
                {
                    seedSongDb.InsertOrReplace(package, typeof(LR2SongDBExtended.install));
                }
            });
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
                BmsCharts = [],
                BmsonCharts = [],
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

            using LR2SongDBExtended verifySongDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
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

        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourceDirectoryPath = Path.Combine(tempDirectoryPath, "PendingPkg");
            string destinationPath = Path.Combine(tempDirectoryPath, "Installed");
            string sourceChartPath = CreateBmsFile(sourceDirectoryPath, "chart.bms", "#TITLE Root conflict");
            File.WriteAllText(destinationPath, "existing destination file");
            byte[] sourceBytes = File.ReadAllBytes(sourceChartPath);
            int durableCallbackCount = 0;
            ChartPackage package = ChartPackageTestExtensions.CreatePackage(
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(sourceChartPath)));
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

        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourceDirectoryPath = Path.Combine(tempDirectoryPath, "PendingPkg");
            string destinationDirectoryPath = Path.Combine(tempDirectoryPath, "Installed", "Pkg");
            Directory.CreateDirectory(sourceDirectoryPath);
            string chartPath = Path.Combine(sourceDirectoryPath, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE Receipt success\r\n");
            ChartFile chart = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", chartPath);
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
            Assert.AreEqual(chartPath, chart.Path);
            Assert.AreEqual(Path.Combine(destinationDirectoryPath, "chart.bms"), package.ChartEntries.Single().Chart.Path);
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
            ChartFile firstChart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(firstChartPath));
            ChartFile secondChart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(secondChartPath));
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
            library.BmsCharts = [firstChart, secondChart];
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

            using LR2SongDBExtended verifySongDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            List<LR2SongDB.song> installedRows = [.. verifySongDb.Table<LR2SongDB.song>()];
            Assert.IsTrue(package.ChartEntries.All(entry =>
                installedRows.Any(row => string.Equals(row.path, entry.Chart.Path, StringComparison.OrdinalIgnoreCase))));
        });
    }

    /// <summary>
    /// 実非同期入口で全パッケージの物理準備が成功しても正本の確定が失敗した場合、
    /// DB・パッケージ・リソースを部分公開せず、準備済み宛先をセッションの確認候補に保持します。
    /// </summary>
    [TestMethod]
    public void InstallChartPackagesAutoWithProgress_CanonicalApplyFailurePublishesNoPartialPackageState()
    {

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
            library.BmsCharts = [];
            Directory.CreateDirectory(installRootPath);
            library.SearchTargets = [installRootPath];

            PackageInstallCommandResult commandResult = library.InstallChartPackagesAutoWithProgressAsync(
                [firstSourceDirectoryPath, secondSourceDirectoryPath, thirdSourceDirectoryPath],
                CancellationToken.None,
                new RecordingPackageInstallProgressWriter()).GetAwaiter().GetResult();

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

            using LR2SongDBExtended verifySongDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
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
            ChartFile firstChart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(firstChartPath));
            ChartFile secondChart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(secondChartPath));
            ChartFile thirdChart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(thirdChartPath));
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
            library.BmsCharts = [firstChart, secondChart, thirdChart];
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

            using LR2SongDBExtended verifySongDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
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
            ChartPackage package = ChartPackageTestExtensions.CreatePackage(Enumerable.Empty<ChartFile>());
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

        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourcePath = Path.Combine(tempDirectoryPath, "single.bms");
            string destinationDirectoryPath = Path.Combine(tempDirectoryPath, "InstalledSingle");
            File.WriteAllText(sourcePath, "#PLAYER 1\r\n#TITLE Single");
            ChartFile chart = CreateFile("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", sourcePath);
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
            Assert.AreEqual(sourcePath, chart.Path);
            Assert.AreEqual(Path.Combine(destinationDirectoryPath, "single.bms"), package.ChartEntries.Single().Chart.Path);
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
            ChartFile chart = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath);
            ChartPackage package = ChartPackageTestExtensions.CreatePackage([chart]);
            package.path = packageDirectoryPath;
            package.delete_parent = false;

            PendingFileDeletionResult result = service.DeletePendingCharts(
                [(chart)],
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

        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryPackageInstallService();
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "PendingBmsonPkg");
            Directory.CreateDirectory(packageDirectoryPath);
            string bmsonPath = Path.Combine(packageDirectoryPath, "chart.bmson");
            File.WriteAllText(bmsonPath, CreateBmsonJsonWithSound("sound.wav"));
            File.WriteAllText(Path.Combine(packageDirectoryPath, "sound.wav"), "resource");
            ChartFile bmsonSong = ChartTestValues.ReadBmson(bmsonPath);
            var adapterlessBmsonEntry = PackageChartEntry.FromChart((bmsonSong));
            var package = ChartPackage.FromChartEntries([adapterlessBmsonEntry]);
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
            Assert.IsNull(adapterlessBmsonEntry.GetBmsChartForTest());
            Assert.IsFalse(Directory.Exists(packageDirectoryPath));
        });
    }

    [TestMethod]
    public void DeletePendingCharts_DeletesAdapterlessBmsonChartByPathWithoutMaterializing()
    {

        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryPackageInstallService();
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "PendingBmsonPkg");
            Directory.CreateDirectory(packageDirectoryPath);
            string bmsonPath = Path.Combine(packageDirectoryPath, "chart.bmson");
            File.WriteAllText(bmsonPath, CreateBmsonJsonWithSound("sound.wav"));
            File.WriteAllText(Path.Combine(packageDirectoryPath, "sound.wav"), "resource");
            ChartFile bmsonSong = ChartTestValues.ReadBmson(bmsonPath);
            var adapterlessBmsonEntry = PackageChartEntry.FromChart((bmsonSong));
            var package = ChartPackage.FromChartEntries([adapterlessBmsonEntry]);
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
            Assert.IsNull(adapterlessBmsonEntry.GetBmsChartForTest());
            Assert.IsFalse(File.Exists(bmsonPath));
        });
    }

    [TestMethod]
    public void DeletePendingCharts_DeletesWholeAdapterlessBmsonPackageDirectoryByPathWithoutMaterializing()
    {

        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryPackageInstallService();
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "PendingBmsonPkg");
            Directory.CreateDirectory(packageDirectoryPath);
            string bmsonPath = Path.Combine(packageDirectoryPath, "chart.bmson");
            File.WriteAllText(bmsonPath, CreateBmsonJsonWithSound("sound.wav"));
            File.WriteAllText(Path.Combine(packageDirectoryPath, "sound.wav"), "resource");
            ChartFile bmsonSong = ChartTestValues.ReadBmson(bmsonPath);
            var adapterlessBmsonEntry = PackageChartEntry.FromChart((bmsonSong));
            var package = ChartPackage.FromChartEntries([adapterlessBmsonEntry]);
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
            Assert.IsNull(adapterlessBmsonEntry.GetBmsChartForTest());
            Assert.IsFalse(Directory.Exists(packageDirectoryPath));
        });
    }

    [TestMethod]
    public void DeletePendingCharts_FailedPackageFolderDeleteDoesNotMaterializeAdapterlessBmsonEntries()
    {

        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryPackageInstallService();
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "PendingBmsonPkg");
            Directory.CreateDirectory(packageDirectoryPath);
            string bmsonPath = Path.Combine(packageDirectoryPath, "chart.bmson");
            File.WriteAllText(bmsonPath, CreateBmsonJsonWithSound("sound.wav"));
            File.WriteAllText(Path.Combine(packageDirectoryPath, "sound.wav"), "resource");
            ChartFile bmsonSong = ChartTestValues.ReadBmson(bmsonPath);
            var adapterlessBmsonEntry = PackageChartEntry.FromChart((bmsonSong));
            var package = ChartPackage.FromChartEntries([adapterlessBmsonEntry]);
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
            Assert.IsNull(adapterlessBmsonEntry.GetBmsChartForTest());
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
            ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(tempDirectoryPath, "chart.bms"));
            var adapterlessBmsonEntry = PackageChartEntry.FromChart((ChartTestValues.ReadBmson(bmsonPath)));
            var plainBmsonPathEntry = PackageChartEntry.FromChart((ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = Path.Combine(tempDirectoryPath, "plain.bmson"),
                Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
            }));
            var package = ChartPackage.FromChartEntries([PackageChartEntry.FromChart((bmsFile)), adapterlessBmsonEntry, plainBmsonPathEntry]);
            var adapterlessBmsonPackage = ChartPackage.FromChartEntries([adapterlessBmsonEntry]);

            List<ChartFile> result = service.GetPendingBmsFormatChartFilesSnapshot([package, adapterlessBmsonPackage]);

            CollectionAssert.AreEqual(new[] { bmsFile }, result.ToArray());
            Assert.IsNull(adapterlessBmsonEntry.GetBmsChartForTest());
        });
    }

    [TestMethod]
    public void GetPendingBmsFormatChartFilesSnapshot_PreservesCapturedCommonValue()
    {
        var service = new BmsLibraryPackageInstallService();
        ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\chart.bms");
        var bmsEntry = PackageChartEntry.FromChart((bmsFile));
        var package = ChartPackage.FromChartEntries([bmsEntry]);

        List<ChartFile> result = service.GetPendingBmsFormatChartFilesSnapshot([package]);

        CollectionAssert.AreEqual(new[] { bmsFile }, result.ToArray());
        Assert.AreSame(bmsFile, bmsEntry.GetBmsChartForTest());
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
            ChartFile renameFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", renameSourcePath);
            ChartFile duplicateFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", duplicateSourcePath);
            ChartFile failureFile = CreateFile("cccccccccccccccccccccccccccccccc", failureSourcePath);
            duplicateFile = duplicateFile with { Md5 = fileOperationService.TryComputeFileMd5ForPath(duplicateSourcePath) };

            PendingExtensionRenameResult result = service.RenamePendingBmsFormatChartFileExtensions(
                [
                    (renameFile),
                    (duplicateFile),
                    (failureFile)
                ],
                ".bme",
                delegate (ChartFile file, string requestedPath)
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
            CollectionAssert.AreEquivalent(new[] { renameFile.Path, duplicateFile.Path }, result.ChartPathsToRemove);
            Assert.AreEqual(1, result.Failures.Count);
            Assert.AreSame(failureFile, result.Failures[0].File);
            Assert.IsTrue(File.Exists(bmsonSourcePath));
        });
    }

    [TestMethod]
    public void MovePackageFilesWithReceipt_DeletesParentDirectory_WhenRemainingFilesAreEmpty()
    {

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

        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            SafeDeleteMoveSetup setup = CreateSingleChartParentDeleteSetup(tempDirectoryPath, "install-target", "#TITLE Installed");
            string remainingChartPath = CreateBmsFile(setup.ParentDirectoryPath, "remain-installed.bms", "#TITLE Remain Installed");

            string independentlyOwnedPath = Path.Combine(tempDirectoryPath, "Installed", "Owned", "remain-installed.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(independentlyOwnedPath)!);
            File.Copy(remainingChartPath, independentlyOwnedPath);
            bool moved = ExecuteSingleChartParentDeleteMoveWithReceipt(
                setup,
                CreateInstalledChartLookup([BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(independentlyOwnedPath))]));

            Assert.IsTrue(moved);
            Assert.IsFalse(Directory.Exists(setup.ParentDirectoryPath));
            Assert.IsTrue(File.Exists(Path.Combine(setup.DestinationDirectoryPath, "install-target.bms")));
        });
    }

    [TestMethod]
    public void MovePackageFilesWithReceipt_KeepsParentDirectory_WhenOwnedChartRemainsInsideCleanupBoundary()
    {

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
                    BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(insideOwnedPath)),
                    BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(outsideOwnedPath))]));

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
                CreateInstalledChartLookup([BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(safeResidualOwnedPath))]));

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
                CreateInstalledChartLookup([BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(safeResidualOwnedPath))]));

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
                CreateInstalledChartLookup([BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(independentlyOwnedPath))]));

            Assert.IsTrue(moved);
            Assert.IsFalse(Directory.Exists(setup.ParentDirectoryPath));
            Assert.IsTrue(File.Exists(Path.Combine(setup.DestinationDirectoryPath, "install-target.bms")));
        });
    }

    [TestMethod]
    public void MovePackageFilesWithReceipt_DeletesParentDirectory_WhenRemainingBmsonChartIsInstalled()
    {

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
                    [ChartTestValues.ReadBmson(independentlyOwnedPath)]));

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

    private static ChartFile CreateFile(string hash, string path)
    {
        ChartFile file = ChartTestValues.Empty() with
        {
            Path = path
        };
        file = file with { Md5 = hash };
        return file;
    }

    private static InstalledChartLookupIndexSnapshot CreateInstalledChartLookup(IEnumerable<ChartFile> installedFiles)
    {
        return CreateInstalledChartLookup(installedFiles, []);
    }

    private static InstalledChartLookupIndexSnapshot CreateInstalledChartLookup(
        IEnumerable<ChartFile> installedFiles,
        IEnumerable<ChartFile> installedBmsonSongs)
    {
        var state = new InstalledChartLookupIndexState();
        foreach (ChartFile file in installedFiles ?? [])
        {
            if (file != null)
            {
                state.AddChart(file.Path, file.Md5, file.Sha256);
            }
        }
        foreach (ChartFile song in installedBmsonSongs ?? [])
        {
            if (song != null)
            {
                state.AddChart(song.Path, song.Md5, song.Sha256);
            }
        }
        return state.CreateSnapshot();
    }

    private static List<ChartFile> GetAddedBmsFiles(PackageInstallExecutionResult result)
    {
        return [.. (result?.AddedCharts ?? [])
            .Where(ChartFileKindResolver.IsBmsChartFile)];
    }

    private static List<ChartFile> GetAddedBmsonSongs(PackageInstallExecutionResult result)
    {
        return [.. (result?.AddedCharts ?? [])
            .Where(song => song?.Kind == ChartFileKind.Bmson && !string.IsNullOrWhiteSpace(song.Path))];
    }

    private static PackageInstallEstimationSnapshot BuildPackageSnapshot(ChartPackage package, IEnumerable<ChartFile> targetFiles)
    {
        List<ChartFile> targetFileList = [.. (targetFiles ?? []).Where(file => file != null)];
        List<PackageChartEntry> targetEntries = targetFileList.Count == 0
            ? package.ChartEntries
            : ResolvePackageEntries(package, targetFileList);
        return package.GetOrBuildInstallEstimationSnapshotFromEntries(targetEntries);
    }

    private static List<PackageChartEntry> ResolvePackageEntries(ChartPackage package, IEnumerable<ChartFile> targetFiles)
    {
        List<PackageChartEntry> packageEntries = package.ChartEntries;
        var result = new List<PackageChartEntry>();
        foreach (ChartFile targetFile in (targetFiles ?? []).Where(file => file != null))
        {
            PackageChartEntry? packageEntry = packageEntries.FirstOrDefault(entry => IsSamePackageChartTarget(entry, targetFile));
            result.Add(packageEntry ?? PackageChartEntry.FromChart((targetFile)));
        }
        return [.. result.Where(entry => entry?.Chart != null)];
    }

    private static bool IsSamePackageChartTarget(PackageChartEntry entry, ChartFile targetFile)
    {
        if (entry?.Chart == null || targetFile == null)
        {
            return false;
        }
        if (ReferenceEquals(entry.GetBmsChartForTest(), targetFile))
        {
            return true;
        }
        return !string.IsNullOrWhiteSpace(entry.Chart.Path)
            && !string.IsNullOrWhiteSpace(targetFile.Path)
            && entry.Chart.Path.Equals(targetFile.Path, StringComparison.OrdinalIgnoreCase);
    }

    private static SafeDeleteMoveSetup CreateSingleChartParentDeleteSetup(string tempDirectoryPath, string chartBaseName, string chartBody)
    {
        string parentDirectoryPath = Path.Combine(tempDirectoryPath, "Pending", "Parent");
        string destinationDirectoryPath = Path.Combine(tempDirectoryPath, "Installed", "Package");
        Directory.CreateDirectory(parentDirectoryPath);
        string sourceChartPath = CreateBmsFile(parentDirectoryPath, chartBaseName + ".bms", chartBody);
        ChartFile sourceChart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(sourceChartPath));
        ChartPackage package = ChartPackageTestExtensions.CreatePackage([sourceChart]);
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
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDBExtended.bmson_song>();
            });
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

    /// <summary>新規導入確認の役割、回数と返した応答を観測します。</summary>
    private sealed class PendingInstallConfirmationDialogService(MessageBoxResult response) : IBmsLibraryDialogService
    {
        internal List<(string Text, MessageBoxResult Response)> Requests { get; } = [];

        public MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button,
            MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.None)
        {
            // None は未選択閉鎖を表し、実表示部品と同じ要求値の正規化・結果変換を通します。
            MessageBoxResult result = response == MessageBoxResult.None
                ? UiDialogResult.ClosedByUser(ThemedMessageBox.NormalizeDefaultResult(
                    UiDialogPresentationAdapter.ToWpf(button),
                    UiDialogPresentationAdapter.ToWpf(defaultResult))).DefaultResult
                : response;
            Requests.Add((messageBoxText, result));
            return result;
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
