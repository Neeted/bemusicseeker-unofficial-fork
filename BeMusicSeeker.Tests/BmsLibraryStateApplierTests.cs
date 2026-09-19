using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.BmsLibraryStateApplierTestSupport;
using PackageStateMutationApplier = BeMusicSeeker.Models.BmsLibraryInternal.PackageLifecycleOwner.PackageStateMutationApplier;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryStateApplierTests
{
    [TestMethod]
    public void LibraryInitializationProgress_CoalescesPendingReportsIntoOneTypedUiCommit()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var scheduler = new QueuedProgressUiScheduler();
            var library = new TestBmsLibrary(
                songDbPath,
                getLR2Config: null!,
                _lr2ScoreDB: null,
                fileMutationService: null!,
                dialogService: null!,
                scheduler);
            int versionNotifications = 0;
            library.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(BMSLibrary.LibraryInitializationProgressVersion))
                {
                    versionNotifications++;
                }
            };
            MethodInfo report = typeof(BMSLibrary).GetMethod(
                "ReportLibraryInitializationProgress",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            InvokeProgress(report, library, BMSLibrary.LibraryInitializationProgressStage.FileEnumeration, 12, 1, "first");
            InvokeProgress(report, library, BMSLibrary.LibraryInitializationProgressStage.FileDiff, 12, 12, "last");

            Assert.AreEqual(1, scheduler.PendingCount);
            Assert.AreEqual(0L, library.LibraryInitializationProgressVersion);

            scheduler.ExecuteNext();

            BMSLibrary.LibraryInitializationProgressSnapshot snapshot =
                library.GetLibraryInitializationProgressSnapshot();
            Assert.AreEqual(1, versionNotifications);
            Assert.AreEqual(2L, snapshot.Version);
            Assert.AreEqual(BMSLibrary.LibraryInitializationProgressStage.FileDiff, snapshot.Stage);
            Assert.AreEqual(12, snapshot.TotalCount);
            Assert.AreEqual(12, snapshot.ProcessedCount);
            Assert.AreEqual("last", snapshot.CurrentPath);
            Assert.AreEqual(0, scheduler.PendingCount);
        });
    }

    private static void InvokeProgress(
        MethodInfo report,
        BMSLibrary library,
        BMSLibrary.LibraryInitializationProgressStage stage,
        int total,
        int processed,
        string path)
    {
        report.Invoke(
            library,
            [stage, "managed", total, processed, path, true]);
    }


    [TestMethod]
    public void ApplyPackageReferenceFacts_FullClearBuildsChartSnapshotWithoutMutatingBmsOwner()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var file = new TestableBmsFile
            {
                path = @"C:\Library\chart.bms"
            };
            file.SetWarning(ChartWarningKind.InstallEstimationAmbiguous, "ambiguous");
            List<BMSFile> libraryFiles = [file];
            List<LR2SongDBExtended.bmson_song> bmsonSongs = [];
            ObservableCollection<ChartPackage> pendingPackages = CreatePackageCollection([]);
            ObservableCollection<ChartPackage> installedPackages = CreatePackageCollection([]);
            var callbacks = new TrackingCallbacks();
            PackageStateMutationApplier applier = CreateStateApplier(songDbPath, callbacks, () => pendingPackages, packages => pendingPackages = packages, () => installedPackages, packages => installedPackages = packages);
            LibraryInstallDestinationChange installDestinationChange = new()
            {
                Chart = ChartFileProjection.WithPackageState(
                    ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: true),
                    @"C:\Deleted",
                    "Deleted title",
                    "Deleted artist",
                    [@"C:\Deleted", @"C:\Other"],
                    file.Warnings.ToStructuredList()),
                NewInstallDestination = null,
                ClearInstallDestinationState = true
            };
            LibraryPackageReferenceFacts packageFacts = new([installDestinationChange], []);

            ApplyCommittedMutation(applier, LibraryCatalogMutationFacts.Empty, packageFacts);

            Assert.IsTrue(file.Warnings.ToStructuredList().Any(warning => warning.Category == ChartWarningCategory.InstallEstimation));
            ChartFile appliedChart = packageFacts.CreateAppliedInstallDestinationChartSnapshots().Single();
            Assert.AreSame(file, appliedChart.GetBmsStorageOwner());
            Assert.AreEqual(string.Empty, appliedChart.InstallDestination);
            Assert.AreEqual(string.Empty, appliedChart.InstallDestinationTitle);
            Assert.AreEqual(string.Empty, appliedChart.InstallDestinationArtist);
            Assert.AreEqual(0, appliedChart.InstallDestinationSuggestions.Count);
            Assert.IsFalse(appliedChart.Warnings.Any(warning => warning.Category == ChartWarningCategory.InstallEstimation));
        });
    }

    [TestMethod]
    public void ApplyPackageReferenceFacts_PathOnlyNullBuildsChartSnapshotWithoutMutatingBmsOwner()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var file = new TestableBmsFile
            {
                path = @"C:\Library\chart.bms"
            };
            file.SetWarning(ChartWarningKind.InstallEstimationAmbiguous, "ambiguous");
            List<BMSFile> libraryFiles = [file];
            List<LR2SongDBExtended.bmson_song> bmsonSongs = [];
            ObservableCollection<ChartPackage> pendingPackages = CreatePackageCollection([]);
            ObservableCollection<ChartPackage> installedPackages = CreatePackageCollection([]);
            var callbacks = new TrackingCallbacks();
            PackageStateMutationApplier applier = CreateStateApplier(songDbPath, callbacks, () => pendingPackages, packages => pendingPackages = packages, () => installedPackages, packages => installedPackages = packages);
            LibraryInstallDestinationChange installDestinationChange = new()
            {
                Chart = ChartFileProjection.WithPackageState(
                    ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: true),
                    @"C:\Installed",
                    "Candidate title",
                    "Candidate artist",
                    [@"C:\Installed", @"C:\Other"],
                    file.Warnings.ToStructuredList()),
                NewInstallDestination = null,
                ClearInstallDestinationState = false
            };
            LibraryPackageReferenceFacts packageFacts = new([installDestinationChange], []);

            ApplyCommittedMutation(applier, LibraryCatalogMutationFacts.Empty, packageFacts);

            Assert.IsTrue(file.Warnings.ToStructuredList().Any(warning => warning.Category == ChartWarningCategory.InstallEstimation));
            ChartFile appliedChart = packageFacts.CreateAppliedInstallDestinationChartSnapshots().Single();
            Assert.AreSame(file, appliedChart.GetBmsStorageOwner());
            Assert.AreEqual(string.Empty, appliedChart.InstallDestination);
            Assert.AreEqual("Candidate title", appliedChart.InstallDestinationTitle);
            Assert.AreEqual("Candidate artist", appliedChart.InstallDestinationArtist);
            CollectionAssert.AreEqual(new[] { @"C:\Installed", @"C:\Other" }, appliedChart.InstallDestinationSuggestions.ToArray());
        });
    }

    [TestMethod]
    public void LibraryPackageReferenceFacts_CreateAppliedSnapshotsDoesNotRequireStateApplierWriteback()
    {
        var file = new TestableBmsFile
        {
            path = @"C:\Library\chart.bms"
        };
        file.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        LibraryInstallDestinationChange installDestinationChange = new()
        {
            Chart = ChartFileProjection.WithPackageState(
                ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false),
                @"C:\Old",
                string.Empty,
                string.Empty,
                []),
            NewInstallDestination = @"C:\New"
        };
        LibraryPackageReferenceFacts packageFacts = new([installDestinationChange], []);

        ChartFile appliedChart = packageFacts.CreateAppliedInstallDestinationChartSnapshots().Single();

        Assert.AreEqual(@"C:\New", appliedChart.InstallDestination);
    }

    [TestMethod]
    public void ApplyPackageReferenceFacts_UnregisterPrunesInstalledPackagesWithoutStorageCollectionWriteback()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var removedFile = new TestableBmsFile
            {
                path = "C:\\Library\\remove.bms"
            };
            removedFile.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var keptFile = new TestableBmsFile
            {
                path = "C:\\Library\\keep.bms"
            };
            keptFile.SetHash("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            ChartPackage removedPackage = ChartPackageTestExtensions.CreatePackage([removedFile]);
            removedPackage.path = "C:\\Installed\\RemovePkg";
            removedPackage.delete_parent = false;
            ChartPackage keptPackage = ChartPackageTestExtensions.CreatePackage([keptFile]);
            keptPackage.path = "C:\\Installed\\KeepPkg";
            keptPackage.delete_parent = false;
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.InsertOrReplace(removedFile, typeof(LR2SongDB.song));
                songDb.InsertOrReplace(keptFile, typeof(LR2SongDB.song));
                songDb.InsertOrReplace(new BMSFileMaintenanceInfo { path = removedFile.path }, typeof(LR2SongDBExtended.maintenance));
                songDb.InsertOrReplace(new BMSFileMaintenanceInfo { path = keptFile.path }, typeof(LR2SongDBExtended.maintenance));
            }

            List<BMSFile> libraryFiles = [removedFile, keptFile];
            List<LR2SongDBExtended.bmson_song> bmsonSongs = [];
            ObservableCollection<ChartPackage> pendingPackages = CreatePackageCollection([]);
            ObservableCollection<ChartPackage> installedPackages = CreatePackageCollection([removedPackage, keptPackage]);
            var callbacks = new TrackingCallbacks();
            PackageStateMutationApplier applier = CreateStateApplier(songDbPath, callbacks, () => pendingPackages, packages => pendingPackages = packages, () => installedPackages, packages => installedPackages = packages);

            ApplyCommittedMutation(
                applier,
                CreateUnregisterFacts([ChartFileProjection.FromBmsStorageOwnerIdentity(removedFile)]));

            Assert.AreEqual(2, libraryFiles.Count);
            Assert.AreEqual(1, installedPackages.Count);
            Assert.AreSame(keptPackage, installedPackages.Single());
            Assert.AreEqual(1, callbacks.InstalledPackagesChangedCount);
            int uiThreadId = TestUiDispatcherHost.Dispatcher.Invoke(() => Environment.CurrentManagedThreadId);
            Assert.AreEqual(uiThreadId, callbacks.InstalledPackagesChangedThreadId);
        });
    }

    /// <summary>
    /// 確定したexact削除集合をDB・storage・owned・packageへ同じ意味で反映し、移動先と非対象行を保護します。
    /// </summary>
    [DataTestMethod]
    [DataRow("CHART", false, false, false)]
    [DataRow("other", false, false, false)]
    [DataRow(".\\chart", false, false, false)]
    [DataRow("CHART", false, true, false)]
    [DataRow("other", false, true, false)]
    [DataRow("CHART", true, false, false)]
    [DataRow("other", true, false, false)]
    [DataRow("CHART", false, false, true)]
    [DataRow("other", false, false, true)]
    public void ApplyPackageReferenceFacts_ExactRemovalKeepsOtherRowsAndPackages(
        string removedName, bool relocate, bool removeBoth, bool ownerReference)
    {
        WithTemporarySongDb(songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string keptBmsPath = Path.Combine(root, "chart.bms");
            string keptBmsonPath = Path.Combine(root, "chart.bmson");
            var keptBms = new TestableBmsFile
            {
                path = relocate ? Path.Combine(root, "old.bms") : keptBmsPath,
                favorite = 7,
                tag = "kept-user-tag",
                adddate = 12345
            };
            keptBms.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var removedBms = new TestableBmsFile { path = Path.Combine(root, removedName + ".bms") };
            removedBms.SetHash("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
            var keptBmson = new LR2SongDBExtended.bmson_song
            {
                path = relocate ? Path.Combine(root, "old.bmson") : keptBmsonPath,
                md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                title = "kept-bmson"
            };
            var removedBmson = new LR2SongDBExtended.bmson_song
            {
                path = Path.Combine(root, removedName + ".bmson"),
                md5 = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB",
                title = "removed-bmson"
            };
            keptBms.SetMaintenanceInfo(new BMSFileMaintenanceInfo { path = keptBms.path, hash = keptBms.hash }, suppressPropertyChanged: true);
            keptBmson.MaintenanceInfo = new BMSFileMaintenanceInfo { path = keptBmson.path, hash = keptBmson.md5 };
            File.WriteAllText(keptBmsPath, "#PLAYER 1\r\n#TITLE exact\r\n");
            File.WriteAllText(keptBmsonPath, "{}");
            string missingPath = Path.Combine(root, "missing.bms");
            string keptMissingPath = Path.Combine(root, removedName == "other" ? "other-missing.bms" : "MISSING.bms");
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.song>();
                db.CreateTable<LR2SongDBExtended.bmson_song>();
                db.CreateTable<LR2SongDBExtended.maintenance>();
                db.InsertOrReplace(keptBms, typeof(LR2SongDB.song));
                db.InsertOrReplace(removedBms, typeof(LR2SongDB.song));
                db.InsertOrReplace(keptBmson, typeof(LR2SongDBExtended.bmson_song));
                db.InsertOrReplace(removedBmson, typeof(LR2SongDBExtended.bmson_song));
                foreach (string path in new[] { keptBms.path, removedBms.path, keptBmson.path, removedBmson.path, missingPath, keptMissingPath })
                {
                    db.InsertOrReplace(new BMSFileMaintenanceInfo { path = path }, typeof(LR2SongDBExtended.maintenance));
                }
            }

            var storage = new CatalogStorageRowsOwner();
            CatalogStorageRowsSnapshot initial = storage.ReplaceRowsAndCaptureSnapshot([keptBms, removedBms], [keptBmson, removedBmson]);
            var owned = new CatalogOwnedCollectionOwner();
            Assert.IsTrue(owned.ApplyBuiltCollection(
                OwnedChartCollectionState.FromStorageRows([keptBms, removedBms], [keptBmson, removedBmson]),
                initial.BmsRowsVersion, initial.BmsonRowsVersion));
            Assert.AreEqual(4, owned.Collection.CreatePathSnapshot().Count, "旧DBの別exact keyをloaded ownerで取り落とさない。");
            var keptPackage = ChartPackage.FromChartEntries(
            [
                PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(keptBms)),
                PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(keptBmson))
            ]);
            var removedPackage = ChartPackage.FromChartEntries(
            [
                PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(removedBms)),
                PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(removedBmson))
            ]);
            ObservableCollection<ChartPackage> pending = CreatePackageCollection([]);
            ObservableCollection<ChartPackage> installed = CreatePackageCollection([keptPackage, removedPackage]);
            PackageStateMutationApplier applier = CreateStateApplier(songDbPath, new TrackingCallbacks(),
                () => pending, value => pending = value, () => installed, value => installed = value);
            var removeRequests = new List<OwnedChartRemoveRequest>();
            removeRequests.Add(ownerReference
                ? OwnedChartRemoveRequest.FromOwnerReference(removedBms)
                : OwnedChartRemoveRequest.FromPathCleanup(ChartFileKind.Bms, removedBms.path));
            removeRequests.Add(ownerReference
                ? OwnedChartRemoveRequest.FromOwnerReference(removedBmson)
                : OwnedChartRemoveRequest.FromPathCleanup(ChartFileKind.Bmson, removedBmson.path));
            removeRequests.Add(OwnedChartRemoveRequest.FromPathCleanup(ChartFileKind.Bms, missingPath));
            if (removeBoth || relocate)
            {
                removeRequests.Add(OwnedChartRemoveRequest.FromPathCleanup(ChartFileKind.Bms, keptBmsPath));
                removeRequests.Add(OwnedChartRemoveRequest.FromPathCleanup(ChartFileKind.Bmson, keptBmsonPath));
            }
            var pathChanges = new List<LibraryChartPathChange>();
            if (relocate)
            {
                pathChanges.Add(new LibraryChartPathChange
                {
                    Chart = ChartFileProjection.FromBmsStorageOwnerIdentity(keptBms),
                    OldPath = keptBms.path,
                    NewPath = keptBmsPath
                });
                pathChanges.Add(new LibraryChartPathChange
                {
                    Chart = ChartFileProjection.FromBmsonStorageOwnerIdentity(keptBmson),
                    OldPath = keptBmson.path,
                    NewPath = keptBmsonPath
                });
            }
            LibraryCatalogMutationFacts catalogFacts = new(removeRequests, pathChanges, []);

            CatalogMutationReceipt receipt = new CatalogMutationOwner(storage, owned, new BmsLibraryDbGateway(songDbPath))
                .ApplyCatalogMutation(catalogFacts);
            applier.ApplyPackageReferenceFacts(
                LibraryPackageReferenceFacts.Empty,
                receipt.RemovedCharts,
                receipt.PathFacts);

            string[] expectedBms = removeBoth ? [] : [keptBmsPath];
            string[] expectedBmson = removeBoth ? [] : [keptBmsonPath];
            CollectionAssert.AreEquivalent(expectedBms, storage.BmsRows.Select(row => row.path).ToArray());
            CollectionAssert.AreEquivalent(expectedBmson, storage.BmsonRows.Select(row => row.path).ToArray());
            CollectionAssert.AreEquivalent(expectedBms.Concat(expectedBmson).ToArray(), owned.Collection.CreatePathSnapshot());
            CollectionAssert.AreEquivalent(expectedBms.Concat(expectedBmson).ToArray(),
                installed.SelectMany(package => package.ChartEntries).Select(entry => entry.Chart.Path).ToArray());
            using var readback = new LR2SongDBExtended(songDbPath);
            CollectionAssert.AreEquivalent(expectedBms, readback.Table<LR2SongDB.song>().Select(row => row.path).ToArray());
            CollectionAssert.AreEquivalent(expectedBmson, readback.Table<LR2SongDBExtended.bmson_song>().Select(row => row.path).ToArray());
            CollectionAssert.AreEquivalent(expectedBms.Concat(expectedBmson).Append(keptMissingPath).ToArray(),
                readback.Table<LR2SongDBExtended.maintenance>().Select(row => row.path).ToArray());
            if (!removeBoth)
            {
                Assert.AreSame(keptBms, storage.BmsRows.Single());
                Assert.AreSame(keptBmson, storage.BmsonRows.Single());
                LR2SongDB.song kept = readback.Find<LR2SongDB.song>(keptBmsPath);
                Assert.AreEqual(7, kept.favorite);
                Assert.AreEqual(12345, kept.adddate);
                Assert.AreEqual("kept-user-tag", kept.tag);
                Assert.AreEqual("kept-bmson", readback.Find<LR2SongDBExtended.bmson_song>(keptBmsonPath).title);
            }
        });
    }

    [TestMethod]
    public void ApplyPackageReferenceFacts_PathCleanupPrunesInstalledPackagesByPath()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var canonicalFile = new TestableBmsFile
            {
                path = "C:\\Library\\remove.bms"
            };
            canonicalFile.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var keptFile = new TestableBmsFile
            {
                path = "C:\\Library\\keep.bms"
            };
            keptFile.SetHash("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            ChartPackage removedPackage = ChartPackageTestExtensions.CreatePackage([canonicalFile]);
            removedPackage.path = "C:\\Installed\\RemovePkg";
            removedPackage.delete_parent = false;
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.InsertOrReplace(canonicalFile, typeof(LR2SongDB.song));
                songDb.InsertOrReplace(keptFile, typeof(LR2SongDB.song));
                songDb.InsertOrReplace(new BMSFileMaintenanceInfo { path = canonicalFile.path }, typeof(LR2SongDBExtended.maintenance));
            }

            List<BMSFile> libraryFiles = [canonicalFile, keptFile];
            List<LR2SongDBExtended.bmson_song> bmsonSongs = [];
            ObservableCollection<ChartPackage> pendingPackages = CreatePackageCollection([]);
            ObservableCollection<ChartPackage> installedPackages = CreatePackageCollection([removedPackage]);
            var callbacks = new TrackingCallbacks();
            PackageStateMutationApplier applier = CreateStateApplier(songDbPath, callbacks, () => pendingPackages, packages => pendingPackages = packages, () => installedPackages, packages => installedPackages = packages);

            LibraryCatalogMutationFacts catalogFacts = new(
                [OwnedChartRemoveRequest.FromPathCleanup(ChartFileKind.Bms, canonicalFile.path)],
                [],
                []);
            ApplyCommittedMutation(applier, catalogFacts);

            Assert.AreEqual(2, libraryFiles.Count);
            Assert.AreEqual(0, installedPackages.Count);
        });
    }

    [TestMethod]
    public void ApplyPackageReferenceFacts_PathCleanupDoesNotPruneRelocatedDestination()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var relocatedFile = new TestableBmsFile
            {
                path = "C:\\Library\\new.bms"
            };
            relocatedFile.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            ChartPackage relocatedPackage = ChartPackageTestExtensions.CreatePackage([relocatedFile]);
            relocatedPackage.path = "C:\\Installed\\RelocatedPkg";
            relocatedPackage.delete_parent = false;
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.InsertOrReplace(relocatedFile, typeof(LR2SongDB.song));
            }

            ObservableCollection<ChartPackage> pendingPackages = CreatePackageCollection([]);
            ObservableCollection<ChartPackage> installedPackages = CreatePackageCollection([relocatedPackage]);
            var callbacks = new TrackingCallbacks();
            PackageStateMutationApplier applier = CreateStateApplier(
                songDbPath,
                callbacks,
                () => pendingPackages,
                packages => pendingPackages = packages,
                () => installedPackages,
                packages => installedPackages = packages);
            LibraryCatalogMutationFacts catalogFacts = new(
                [OwnedChartRemoveRequest.FromPathCleanup(
                    ChartFileKind.Bms,
                    relocatedFile.path)],
                [],
                []);

            ApplyCommittedMutation(
                applier,
                catalogFacts,
                protectedPathFacts: [new CatalogRelocationPathFact(
                    ChartFileKind.Bms,
                    "C:\\Library\\old.bms",
                    relocatedFile.path)]);

            Assert.AreEqual(1, installedPackages.Count);
            Assert.AreSame(relocatedPackage, installedPackages.Single());
            Assert.AreEqual(0, callbacks.InstalledPackagesChangedCount);
        });
    }

    [TestMethod]
    public void ApplyPackageReferenceFacts_UnregisterDoesNotMaterializeUnmatchedAdapterlessBmsonInstalledPackageEntry()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var removedFile = new TestableBmsFile
            {
                path = "C:\\Library\\remove.bms"
            };
            removedFile.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var unmatchedBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(new LR2SongDBExtended.bmson_song
            {
                path = "C:\\Library\\keep.bmson",
                md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
            }));
            var mixedPackage = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(removedFile)), unmatchedBmsonEntry]);
            mixedPackage.path = "C:\\Installed\\MixedPkg";
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.InsertOrReplace(removedFile, typeof(LR2SongDB.song));
            }

            List<BMSFile> libraryFiles = [removedFile];
            List<LR2SongDBExtended.bmson_song> bmsonSongs = [];
            ObservableCollection<ChartPackage> pendingPackages = CreatePackageCollection([]);
            ObservableCollection<ChartPackage> installedPackages = CreatePackageCollection([mixedPackage]);
            var callbacks = new TrackingCallbacks();
            PackageStateMutationApplier applier = CreateStateApplier(songDbPath, callbacks, () => pendingPackages, packages => pendingPackages = packages, () => installedPackages, packages => installedPackages = packages);

            ApplyCommittedMutation(
                applier,
                CreateUnregisterFacts([ChartFileProjection.FromBmsStorageOwnerIdentity(removedFile)]));

            Assert.AreEqual(1, libraryFiles.Count);
            Assert.AreEqual(1, installedPackages.Count);
            Assert.AreSame(mixedPackage, installedPackages.Single());
            Assert.AreEqual(1, mixedPackage.ChartEntries.Count);
            Assert.AreEqual(unmatchedBmsonEntry.Chart.Path, mixedPackage.ChartEntries.Single().Chart.Path);
            Assert.IsNull(unmatchedBmsonEntry.GetBmsOwnerForTest());
        });
    }

    [TestMethod]
    public void ApplyPackageReferenceFacts_UnregisterLeavesBmsonStorageToCatalogOwner()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var removedSong = new LR2SongDBExtended.bmson_song
            {
                path = "C:\\Library\\remove.bmson",
                folder = "C:\\Library"
            };
            var keptSong = new LR2SongDBExtended.bmson_song
            {
                path = "C:\\Library\\keep.bmson",
                folder = "C:\\Library"
            };
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.bmson_song>();
                songDb.InsertOrReplace(removedSong, typeof(LR2SongDBExtended.bmson_song));
                songDb.InsertOrReplace(keptSong, typeof(LR2SongDBExtended.bmson_song));
            }

            List<BMSFile> libraryFiles = [];
            List<LR2SongDBExtended.bmson_song> bmsonSongs = [removedSong, keptSong];
            ObservableCollection<ChartPackage> pendingPackages = CreatePackageCollection([]);
            ObservableCollection<ChartPackage> installedPackages = CreatePackageCollection([]);
            var callbacks = new TrackingCallbacks();
            PackageStateMutationApplier applier = CreateStateApplier(songDbPath, callbacks, () => pendingPackages, packages => pendingPackages = packages, () => installedPackages, packages => installedPackages = packages);

            ApplyCommittedMutation(
                applier,
                CreateUnregisterFacts([ChartFileProjection.FromBmsonStorageOwnerIdentity(removedSong)]));

            Assert.AreEqual(2, bmsonSongs.Count);
        });
    }

    [TestMethod]
    public void ApplyPackageReferenceFacts_UnregisterRemovesBmsonRowsFromInstalledPackages()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var removedSong = new LR2SongDBExtended.bmson_song
            {
                path = "C:\\Library\\remove.bmson",
                folder = "C:\\Library",
                md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            };
            var keptSong = new LR2SongDBExtended.bmson_song
            {
                path = "C:\\Library\\keep.bmson",
                folder = "C:\\Library",
                md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
            };
            var removedPackage = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(removedSong))]);
            removedPackage.path = "C:\\Installed\\RemovePkg";
            removedPackage.delete_parent = false;
            var keptPackage = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(keptSong))]);
            keptPackage.path = "C:\\Installed\\KeepPkg";
            keptPackage.delete_parent = false;
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.bmson_song>();
                songDb.InsertOrReplace(removedSong, typeof(LR2SongDBExtended.bmson_song));
                songDb.InsertOrReplace(keptSong, typeof(LR2SongDBExtended.bmson_song));
            }

            List<BMSFile> libraryFiles = [];
            List<LR2SongDBExtended.bmson_song> bmsonSongs = [removedSong, keptSong];
            ObservableCollection<ChartPackage> pendingPackages = CreatePackageCollection([]);
            ObservableCollection<ChartPackage> installedPackages = CreatePackageCollection([removedPackage, keptPackage]);
            var callbacks = new TrackingCallbacks();
            PackageStateMutationApplier applier = CreateStateApplier(songDbPath, callbacks, () => pendingPackages, packages => pendingPackages = packages, () => installedPackages, packages => installedPackages = packages);

            ApplyCommittedMutation(
                applier,
                CreateUnregisterFacts([ChartFileProjection.FromBmsonStorageOwnerIdentity(removedSong)]));

            Assert.AreEqual(2, bmsonSongs.Count);
            Assert.AreEqual(1, installedPackages.Count);
            Assert.AreSame(keptPackage, installedPackages.Single());
            Assert.AreEqual(1, callbacks.InstalledPackagesSetCount);
            Assert.AreEqual(1, callbacks.InstalledPackagesChangedCount);
        });
    }

    [TestMethod]
    public void ApplyPackageReferenceFacts_UnregisterRemovesAdapterlessBmsonInstalledPackageEntryWithoutMaterializing()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var removedSong = new LR2SongDBExtended.bmson_song
            {
                path = "C:\\Library\\remove.bmson",
                folder = "C:\\Library",
                md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            };
            var keptSong = new LR2SongDBExtended.bmson_song
            {
                path = "C:\\Library\\keep.bmson",
                folder = "C:\\Library",
                md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
            };
            var removedEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(removedSong));
            var keptEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(keptSong));
            var package = ChartPackage.FromChartEntries([removedEntry, keptEntry]);
            package.path = "C:\\Installed\\MixedPkg";
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.bmson_song>();
                songDb.InsertOrReplace(removedSong, typeof(LR2SongDBExtended.bmson_song));
                songDb.InsertOrReplace(keptSong, typeof(LR2SongDBExtended.bmson_song));
            }

            List<BMSFile> libraryFiles = [];
            List<LR2SongDBExtended.bmson_song> bmsonSongs = [removedSong, keptSong];
            ObservableCollection<ChartPackage> pendingPackages = CreatePackageCollection([]);
            ObservableCollection<ChartPackage> installedPackages = CreatePackageCollection([package]);
            var callbacks = new TrackingCallbacks();
            PackageStateMutationApplier applier = CreateStateApplier(songDbPath, callbacks, () => pendingPackages, packages => pendingPackages = packages, () => installedPackages, packages => installedPackages = packages);

            ApplyCommittedMutation(
                applier,
                CreateUnregisterFacts([ChartFileProjection.FromBmsonStorageOwnerIdentity(removedSong)]));

            Assert.AreEqual(2, bmsonSongs.Count);
            Assert.AreEqual(1, installedPackages.Count);
            Assert.AreSame(package, installedPackages.Single());
            Assert.AreEqual(1, package.ChartEntries.Count);
            Assert.AreEqual(keptSong.path, package.ChartEntries.Single().Chart.Path);
            Assert.IsNull(removedEntry.GetBmsOwnerForTest());
            Assert.IsNull(keptEntry.GetBmsOwnerForTest());
            Assert.AreEqual(1, callbacks.InstalledPackagesChangedCount);
        });
    }

    [TestMethod]
    public void ApplyPackageReferenceFacts_UnregisterDoesNotWriteBmsonRows()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var removedSong = new LR2SongDBExtended.bmson_song
            {
                path = "C:\\Library\\remove.bmson",
                folder = "C:\\Library"
            };
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.bmson_song>();
                songDb.InsertOrReplace(removedSong, typeof(LR2SongDBExtended.bmson_song));
            }

            List<BMSFile> libraryFiles = [];
            List<LR2SongDBExtended.bmson_song> bmsonSongs = [removedSong];
            ObservableCollection<ChartPackage> pendingPackages = CreatePackageCollection([]);
            ObservableCollection<ChartPackage> installedPackages = CreatePackageCollection([]);
            var callbacks = new TrackingCallbacks();
            PackageStateMutationApplier applier = CreateStateApplier(songDbPath, callbacks, () => pendingPackages, packages => pendingPackages = packages, () => installedPackages, packages => installedPackages = packages);
            LibraryCatalogMutationFacts catalogFacts = new(
                [OwnedChartRemoveRequest.FromOwnerReference(removedSong)],
                [],
                []);

            ApplyCommittedMutation(applier, catalogFacts);

            Assert.AreEqual(1, bmsonSongs.Count);
        });
    }

    [TestMethod]
    public void ApplyPackageReferenceFacts_MoveAndRemoveSameOwnerPrunesRelocatedBmsAndBmsonPackages()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_StateApplierMoveRemove_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRootPath);
            string oldBmsPath = Path.Combine(tempRootPath, "old.bms");
            string newBmsPath = Path.Combine(tempRootPath, "new.bms");
            string oldBmsonPath = Path.Combine(tempRootPath, "old.bmson");
            string newBmsonPath = Path.Combine(tempRootPath, "new.bmson");
            try
            {
                File.WriteAllText(oldBmsPath, "#PLAYER 1");
                File.WriteAllText(newBmsPath, "#PLAYER 1");
                File.WriteAllText(oldBmsonPath, "{}");
                File.WriteAllText(newBmsonPath, "{}");
                var bmsFile = new TestableBmsFile { path = oldBmsPath };
                bmsFile.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                var bmsonSong = new LR2SongDBExtended.bmson_song
                {
                    path = oldBmsonPath,
                    folder = tempRootPath,
                    md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                    sha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
                };
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.CreateTable<LR2SongDB.song>();
                    songDb.CreateTable<LR2SongDBExtended.bmson_song>();
                    songDb.CreateTable<LR2SongDBExtended.maintenance>();
                    songDb.InsertOrReplace(bmsFile, typeof(LR2SongDB.song));
                    songDb.InsertOrReplace(bmsonSong, typeof(LR2SongDBExtended.bmson_song));
                }

                ChartPackage bmsPackage = ChartPackageTestExtensions.CreatePackage(bmsFile);
                var bmsonPackage = ChartPackage.FromChartEntries(
                [
                    PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmsonSong))
                ]);
                ObservableCollection<ChartPackage> pendingPackages = CreatePackageCollection([]);
                ObservableCollection<ChartPackage> installedPackages = CreatePackageCollection([bmsPackage, bmsonPackage]);
                var callbacks = new TrackingCallbacks();
                PackageStateMutationApplier applier = CreateStateApplier(
                    songDbPath,
                    callbacks,
                    () => pendingPackages,
                    packages => pendingPackages = packages,
                    () => installedPackages,
                    packages => installedPackages = packages);

                var pathChanges = new List<LibraryChartPathChange>();
                pathChanges.Add(new LibraryChartPathChange
                {
                    Chart = ChartFileProjection.FromBmsFile(bmsFile),
                    OldPath = oldBmsPath,
                    NewPath = newBmsPath
                });
                pathChanges.Add(new LibraryChartPathChange
                {
                    Chart = ChartFileProjection.FromBmsonSong(bmsonSong),
                    OldPath = oldBmsonPath,
                    NewPath = newBmsonPath
                });
                var removeRequests = new List<OwnedChartRemoveRequest>
                {
                    OwnedChartRemoveRequest.FromOwnerReference(bmsFile),
                    OwnedChartRemoveRequest.FromOwnerReference(bmsonSong)
                };
                LibraryCatalogMutationFacts catalogFacts = new(removeRequests, pathChanges, []);

                var catalogOwner = new CatalogMutationOwner(
                    new CatalogStorageRowsOwner(),
                    new CatalogOwnedCollectionOwner(),
                    new BmsLibraryDbGateway(songDbPath));
                CatalogMutationReceipt receipt = catalogOwner.ApplyCatalogMutation(
                    catalogFacts);

                Assert.IsTrue(receipt.Applied);
                Assert.AreEqual(2, receipt.RemovedCharts.Count);
                Assert.AreEqual(2, receipt.MovedCharts.Count);
                Assert.AreEqual(
                    bmsonSong.sha256,
                    receipt.RemovedCharts.Single(fact => fact.Kind == ChartFileKind.Bmson).Sha256);

                applier.ApplyPackageReferenceFacts(
                    LibraryPackageReferenceFacts.Empty,
                    receipt.RemovedCharts,
                    receipt.PathFacts);

                Assert.AreEqual(0, installedPackages.Count);
                Assert.AreEqual(2, callbacks.InstalledPackagesChangedCount);
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

}

internal static class BmsLibraryStateApplierTestSupport
{
    internal static PackageStateMutationApplier CreateStateApplier(
        string songDbPath,
        TrackingCallbacks callbacks,
        Func<ObservableCollection<ChartPackage>> getPendingPackages,
        Action<ObservableCollection<ChartPackage>> setPendingPackages,
        Func<ObservableCollection<ChartPackage>> getInstalledPackages,
        Action<ObservableCollection<ChartPackage>> setInstalledPackages)
    {
        return new PackageStateMutationApplier(
            new BmsLibraryDbGateway(songDbPath),
            getPendingPackages,
            delegate (ObservableCollection<ChartPackage> packages)
            {
                callbacks.PendingPackagesSetCount++;
                setPendingPackages(packages);
            },
            getInstalledPackages,
            delegate (ObservableCollection<ChartPackage> packages)
            {
                callbacks.InstalledPackagesSetCount++;
                setInstalledPackages(packages);
            },
            () =>
            {
                callbacks.InstalledPackagesChangedCount++;
                callbacks.InstalledPackagesChangedThreadId = Environment.CurrentManagedThreadId;
            },
            packages => new ObservableCollection<ChartPackage>(packages),
            new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
            _ => false,
            mutation => mutation());
    }

    internal static CatalogMutationReceipt ApplyCatalogRelocation(
        string songDbPath,
        LibraryCatalogMutationFacts catalogFacts,
        TrackingCallbacks callbacks)
    {
        var owner = new CatalogMutationOwner(
            new CatalogStorageRowsOwner(),
            new CatalogOwnedCollectionOwner(),
            new BmsLibraryDbGateway(songDbPath));
        owner.CatalogWriteFailurePublished += delegate (object? sender, CatalogWriteFailureFact fact)
        {
            callbacks.SongDbWriteFailureCount++;
            callbacks.LastSongDbWriteFailureStage = fact.Stage;
            callbacks.LastSongDbWriteFailure = fact.Exception;
        };
        return owner.ApplyCatalogMutation(catalogFacts);
    }

    internal static ObservableCollection<ChartPackage> CreatePackageCollection(IEnumerable<ChartPackage> packages)
    {
        return new ObservableCollection<ChartPackage>([.. (packages ?? [])]);
    }

    internal static LibraryCatalogMutationFacts CreateUnregisterFacts(IEnumerable<ChartFile> charts)
    {
        List<OwnedChartRemoveRequest> removeRequests = [.. (charts ?? [])
            .Select(OwnedChartRemoveRequest.FromOwnerReferenceChart)
            .Where(request => request != null)];
        return new LibraryCatalogMutationFacts(removeRequests, [], []);
    }

    internal static void ApplyCommittedMutation(
        PackageStateMutationApplier applier,
        LibraryCatalogMutationFacts catalogFacts,
        LibraryPackageReferenceFacts? packageReferenceFacts = null,
        IEnumerable<CatalogRelocationPathFact> protectedPathFacts = null!)
    {
        applier.ApplyPackageReferenceFacts(
            packageReferenceFacts ?? LibraryPackageReferenceFacts.Empty,
            CatalogChartMutationFact.CreateRemovalFacts(catalogFacts?.ChartRemoveRequests),
            protectedPathFacts ?? []);
    }

    internal static void WithTemporarySongDb(Action<string> testAction)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_StateApplierTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            testAction(songDbPath);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    internal sealed class TrackingCallbacks
    {
        public int PendingPackagesSetCount { get; set; }

        public int InstalledPackagesSetCount { get; set; }

        public int InstalledPackagesChangedCount { get; set; }

        public int InstalledPackagesChangedThreadId { get; set; }

        public int SongDbWriteFailureCount { get; set; }

        public string LastSongDbWriteFailureStage { get; set; } = string.Empty;

        public Exception LastSongDbWriteFailure { get; set; } = null!;
    }

    internal sealed class CanceledScheduleUiScheduler : IUiScheduler
    {
        public bool IsAvailable => true;

        public bool CanExecuteInline => false;

        public bool CheckAccess() => false;

        public IUiScheduledOperation Schedule(
            Action action,
            UiSchedulePriority priority = UiSchedulePriority.Normal)
            => CanceledUiScheduledOperation.Instance;

        public void Invoke(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
            => throw new NotSupportedException();

        public T Invoke<T>(Func<T> action, UiSchedulePriority priority = UiSchedulePriority.Normal)
            => throw new NotSupportedException();

        public Task InvokeAsync(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
            => throw new NotSupportedException();

        public Task InvokeAsync(Func<Task> action, UiSchedulePriority priority = UiSchedulePriority.Normal)
            => throw new NotSupportedException();
    }

    internal sealed class QueuedProgressUiScheduler : IUiScheduler
    {
        private readonly Queue<Action> actions = [];

        internal int PendingCount => actions.Count;

        public bool IsAvailable => true;

        public bool CanExecuteInline => false;

        public bool CheckAccess() => false;

        public IUiScheduledOperation Schedule(
            Action action,
            UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            actions.Enqueue(action);
            return new CompletedUiScheduledOperation();
        }

        public void Invoke(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal) =>
            action();

        public T Invoke<T>(Func<T> action, UiSchedulePriority priority = UiSchedulePriority.Normal) =>
            action();

        public Task InvokeAsync(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            action();
            return Task.CompletedTask;
        }

        public Task InvokeAsync(Func<Task> action, UiSchedulePriority priority = UiSchedulePriority.Normal) =>
            action();

        internal void ExecuteNext()
        {
            actions.Dequeue()();
        }
    }

    internal sealed class CompletedUiScheduledOperation : IUiScheduledOperation
    {
        public bool IsAccepted => true;

        public bool IsCompleted => true;

        public bool IsAborted => false;

        public string RejectionReason => string.Empty;

        public Task Completion => Task.CompletedTask;

        public void Abort()
        {
        }
    }

    internal sealed class CanceledUiScheduledOperation : IUiScheduledOperation
    {
        private static readonly CancellationToken CanceledToken = new(canceled: true);

        internal static CanceledUiScheduledOperation Instance { get; } = new();

        public bool IsAccepted => true;

        public bool IsCompleted => true;

        public bool IsAborted => true;

        public string RejectionReason => string.Empty;

        public Task Completion { get; } = Task.FromCanceled(CanceledToken);

        public void Abort()
        {
        }
    }

    internal sealed class TestableBmsFile : BMSFile
    {
        public void SetHash(string value)
        {
            hash = value;
        }

        public void SetSha256(string value)
        {
            sha256 = value;
        }
    }
}
