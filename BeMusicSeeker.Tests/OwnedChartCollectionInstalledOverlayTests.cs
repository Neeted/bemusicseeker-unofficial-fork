using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualBasic.FileIO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using static BeMusicSeeker.Tests.OwnedChartCollectionTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class OwnedChartCollectionInstalledOverlayTests
{
    [TestMethod]
    public void ApplyInstalledChartStorageTargets_UpsertsOwnedCollectionWithoutRebuild()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string replacedBmsPath = Path.Combine("C:\\Installed", "Bms", "replace.bms");
            string replacedBmsonPath = Path.Combine("C:\\Installed", "Bmson", "replace.bmson");
            var keptBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "keep.bms"));
            var replacedBms = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", replacedBmsPath);
            var newBms = CreateFile("cccccccccccccccccccccccccccccccc", replacedBmsPath);
            var keptBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "aaa.bmson"), "dddddddddddddddddddddddddddddddd");
            var replacedBmson = CreateBmsonSong(replacedBmsonPath, "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
            var newBmson = CreateBmsonSong(replacedBmsonPath, "ffffffffffffffffffffffffffffffff");
            var library = new TestBmsLibrary(songDbPath);
            SetLibraryFilesWithoutNotification(library, [keptBms, replacedBms]);
            SetLibraryBmsonSongsWithoutNotification(library, [replacedBmson, keptBmson]);
            InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            EnsureCurrentResourceHealthIndex(library);
            Assert.AreEqual(4, library.TryGetCurrentResourceHealthIndexSnapshotForView().TargetCount);
            int baselineOwnedCollectionVersion = library.OwnedChartCollectionVersion;
            int baselineParentFolderVersion = library.BMSParentFolderListCacheVersion;
            int ownedCollectionVersionChanged = 0;
            int parentFolderVersionChanged = 0;
            int bmsFilesChanged = 0;
            int bmsonSongsChanged = 0;
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            library.PropertyChanged += delegate (object _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == "OwnedChartCollectionVersion")
                {
                    ownedCollectionVersionChanged++;
                }
                if (args.PropertyName == "BMSParentFolderListCacheVersion")
                {
                    parentFolderVersionChanged++;
                }
                if (args.PropertyName == nameof(BMSLibrary.BMSFiles))
                {
                    bmsFilesChanged++;
                }
                if (args.PropertyName == nameof(BMSLibrary.BmsonSongs))
                {
                    bmsonSongsChanged++;
                }
            };
            SetDuplicateChartGroupsWithoutNotification(library, []);

            InvokeApplyInstalledChartStorageTargets(library, ChartStorageTargetSet.FromRows([newBms], [newBmson]));
            List<ChartFile> snapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);

            Assert.AreEqual(baselineOwnedCollectionVersion + 1, library.OwnedChartCollectionVersion);
            Assert.AreEqual(1, ownedCollectionVersionChanged);
            Assert.AreEqual(baselineParentFolderVersion + 1, library.BMSParentFolderListCacheVersion);
            Assert.AreEqual(1, parentFolderVersionChanged);
            Assert.AreEqual(0, bmsFilesChanged);
            Assert.AreEqual(0, bmsonSongsChanged);
            Assert.IsTrue(batch.NotifiesStorageRows);
            Assert.IsTrue(batch.NotifiesBmsFiles);
            Assert.IsTrue(batch.NotifiesBmsonSongs);
            Assert.IsNull(library.DuplicateChartGroups);
            Assert.AreEqual(4, library.TryGetCurrentResourceHealthIndexSnapshotForView().TargetCount);
            Assert.IsFalse(HasNoCurrentResourceHealthIndex(library));
            Assert.AreEqual(4, snapshot.Count);
            Assert.AreSame(keptBms, snapshot[0].GetBmsStorageOwner());
            Assert.AreSame(newBms, snapshot[1].GetBmsStorageOwner());
            Assert.AreSame(keptBmson, snapshot[2].GetBmsonStorageOwner());
            Assert.AreSame(newBmson, snapshot[3].GetBmsonStorageOwner());
        });
    }

    [TestMethod]
    public void ApplyInstalledChartStorageTargets_CurrentResourceHealthIndexUsesDeltaWithoutFullRebuild()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var keptBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "keep.bms"));
            var addedBms = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Bms", "added.bms"));
            var library = new TestBmsLibrary(songDbPath);
            SetLibraryFilesWithoutNotification(library, [keptBms]);
            SetLibraryBmsonSongsWithoutNotification(library, []);
            EnsureCurrentResourceHealthIndex(library);

            InvokeApplyInstalledChartStorageTargets(library, ChartStorageTargetSet.FromRows([addedBms], []));

            Assert.AreEqual(2, library.TryGetCurrentResourceHealthIndexSnapshotForView().TargetCount);
        });
    }

    [TestMethod]
    public void ApplyInstalledChartStorageTargets_BuiltLookupUpsertUsesOwnedPathExactView()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string bmsPath = Path.Combine("C:\\Installed", "Shared", "chart.bms");
            string canonicalVariantBmsPath = Path.Combine("C:\\Installed", "Shared", ".", "chart.bms");
            string bmsonPath = Path.Combine("C:\\Installed", "Shared", "chart.bmson");
            var oldBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", bmsPath);
            var newBms = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", canonicalVariantBmsPath);
            var bmsonSong = CreateBmsonSong(bmsonPath, "cccccccccccccccccccccccccccccccc");
            var library = new TestBmsLibrary(songDbPath);
            SetLibraryFilesWithoutNotification(library, [oldBms]);
            SetLibraryBmsonSongsWithoutNotification(library, [bmsonSong]);
            InstalledChartLookupIndexSnapshot initialLookup = InvokeCreateInstalledChartLookupSnapshot(library);

            Assert.IsTrue(IsInstalledChartLookupIndexInitialized(library));
            Assert.IsTrue(initialLookup.ContainsPrimaryHash(oldBms.hash));
            Assert.IsTrue(initialLookup.ContainsPrimaryHash(bmsonSong.md5));

            InvokeApplyInstalledChartStorageTargets(library, ChartStorageTargetSet.FromRows([newBms], []));
            InstalledChartLookupIndexSnapshot updatedLookup = InvokeCreateInstalledChartLookupSnapshot(library);
            List<ChartFile> snapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);

            Assert.IsTrue(IsInstalledChartLookupIndexInitialized(library));
            Assert.IsFalse(updatedLookup.ContainsPrimaryHash(oldBms.hash));
            Assert.IsTrue(updatedLookup.ContainsPrimaryHash(newBms.hash));
            Assert.IsTrue(updatedLookup.ContainsPrimaryHash(bmsonSong.md5));
            Assert.AreEqual(1, updatedLookup.GetPrimaryHashCount(newBms.hash));
            Assert.AreEqual(2, snapshot.Count);
            Assert.AreSame(newBms, snapshot.Single(chart => chart.Kind == ChartFileKind.Bms).GetBmsStorageOwner());
            Assert.AreSame(bmsonSong, snapshot.Single(chart => chart.Kind == ChartFileKind.Bmson).GetBmsonStorageOwner());
        });
    }

    [TestMethod]
    public void AutoRenameAllChartFolders_RootOnlyChartsAreNotActionableTargets()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string rootPath = Path.Combine(Path.GetDirectoryName(songDbPath), "LibraryRoot");
            Directory.CreateDirectory(rootPath);
            string chartPath = Path.Combine(rootPath, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1");
            var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath);
            var library = new TestBmsLibrary(songDbPath)
            {
                SearchTargets = [rootPath]
            };
            SetLibraryFilesWithoutNotification(library, [bmsFile]);
            SetLibraryBmsonSongsWithoutNotification(library, []);

            Assert.IsFalse(library.HasAutoRenameAllChartFolderTargets(rootPath));
            Assert.IsFalse(library.AutoRenameAllChartFolders(rootPath));
        });
    }

    [TestMethod]
    public void ApplyInstalledChartStorageTargets_InvalidatesOwnedCollectionOnFailure()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var keptBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "keep.bms"));
            var staleOverlayBms = CreateFile("ffffffffffffffffffffffffffffffff", Path.Combine("C:\\Installed", "Bms", "stale.bms"));
            var addedBms = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Bms", "added.bms"));
            var duplicateAddedBms = CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine("C:\\Installed", "Bms", ".", "added.bms"));
            var addedBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "added.bmson"), "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = [keptBms],
                BmsonSongs = []
            };
            InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            SetDuplicateChartGroupsWithoutNotification(library, []);
            int baselineParentFolderVersion = library.BMSParentFolderListCacheVersion;
            int parentFolderVersionChanged = 0;
            library.PropertyChanged += delegate (object _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == "BMSParentFolderListCacheVersion")
                {
                    parentFolderVersionChanged++;
                }
            };
            ApplyInstallDestinationChange(library, staleOverlayBms, Path.Combine("C:\\Install", "Stale"));
            Assert.IsTrue(GetInstallDestinationRuntimeStateCount(library) > 0);

            InvalidOperationException exception = Assert.ThrowsException<InvalidOperationException>(() =>
                InvokeApplyInstalledChartStorageTargets(library, ChartStorageTargetSet.FromRows([addedBms, duplicateAddedBms], [addedBmson])));

            Assert.IsNotNull(exception);
            Assert.AreEqual(baselineParentFolderVersion + 1, library.BMSParentFolderListCacheVersion);
            Assert.AreEqual(1, parentFolderVersionChanged);
            Assert.IsNull(library.DuplicateChartGroups);
            Assert.AreEqual(0, GetInstallDestinationRuntimeStateCount(library));
        });
    }

    [TestMethod]
    public void ApplyInstalledChartStorageTargets_ForceInvalidatesResourceHealthIndexOnFailureDuringSuppression()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var keptBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "keep.bms"));
            var addedBms = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Bms", "added.bms"));
            var duplicateAddedBms = CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine("C:\\Installed", "Bms", ".", "added.bms"));
            var addedBmson = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "added.bmson"), "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = [keptBms],
                BmsonSongs = []
            };
            InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            EnsureCurrentResourceHealthIndex(library);
            Assert.AreEqual(1, library.TryGetCurrentResourceHealthIndexSnapshotForView().TargetCount);

            InvalidOperationException exception = Assert.ThrowsException<InvalidOperationException>(() =>
                InvokeApplyInstalledChartStorageTargets(library, ChartStorageTargetSet.FromRows([addedBms, duplicateAddedBms], [addedBmson])));

            Assert.IsNotNull(exception);
            Assert.AreEqual(0, library.TryGetCurrentResourceHealthIndexSnapshotForView().TargetCount);
        });
    }

    [TestMethod]
    public void CreateInstallDestinationOverlayChartRefSnapshotUnsafe_DeduplicatesRuntimeStateKeys()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Library", "Bms", "chart.bms"), new string('b', 64));
            var library = new TestBmsLibrary(songDbPath);
            SetLibraryFilesWithoutNotification(library, [bmsFile]);
            SetLibraryBmsonSongsWithoutNotification(library, []);
            var delta = new LibraryMutationDelta();
            delta.UpdatedInstallDestinations.Add(new LibraryInstallDestinationChange
            {
                Chart = ChartFileProjection.FromBmsFile(bmsFile, includeWarningSnapshot: false, includeResourceReferences: false),
                NewInstallDestination = Path.Combine("C:\\Install", "Bms")
            });
            InvokeApplyLibraryMutationDelta(library, delta);

            InstallDestinationOverlayChartRefSnapshot snapshot = InvokeCreateInstallDestinationOverlayChartRefSnapshot(library);
            List<LibraryChartRef> refs = snapshot.GetChartRefsUnderInstallDestination(Path.Combine("C:\\Install", "Bms"));

            Assert.AreEqual(1, refs.Count);
            Assert.AreSame(bmsFile, refs[0].GetBmsStorageOwner());
            Assert.AreEqual(Path.Combine("C:\\Install", "Bms"), refs[0].ToChartFile().InstallDestination);
        });
    }

    [TestMethod]
    public void CreateInstallDestinationOverlayChartRefSnapshotUnsafe_InvalidatesCachedSnapshotOnOverlayUpdate()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Library", "Bms", "chart.bms"), new string('b', 64));
            var library = new TestBmsLibrary(songDbPath);
            SetLibraryFilesWithoutNotification(library, [bmsFile]);
            SetLibraryBmsonSongsWithoutNotification(library, []);
            string oldInstallDestination = Path.Combine("C:\\Install", "Old");
            string newInstallDestination = Path.Combine("C:\\Install", "New");
            ApplyInstallDestinationChange(library, bmsFile, oldInstallDestination);
            InstallDestinationOverlayChartRefSnapshot oldSnapshot = InvokeCreateInstallDestinationOverlayChartRefSnapshot(library);
            Assert.AreSame(oldSnapshot, InvokeCreateInstallDestinationOverlayChartRefSnapshot(library));

            ApplyInstallDestinationChange(library, bmsFile, newInstallDestination);
            InstallDestinationOverlayChartRefSnapshot newSnapshot = InvokeCreateInstallDestinationOverlayChartRefSnapshot(library);

            Assert.AreNotSame(oldSnapshot, newSnapshot);
            Assert.AreEqual(0, newSnapshot.GetChartRefsUnderInstallDestination(oldInstallDestination).Count);
            List<LibraryChartRef> refs = newSnapshot.GetChartRefsUnderInstallDestination(newInstallDestination);
            Assert.AreEqual(1, refs.Count);
            Assert.AreSame(bmsFile, refs[0].GetBmsStorageOwner());
        });
    }

    [TestMethod]
    public void WarmInstallDestinationOverlaySnapshot_BuildsAndReusesOverlaySnapshot()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Library", "Bms", "chart.bms"), new string('b', 64));
            var library = new TestBmsLibrary(songDbPath);
            SetLibraryFilesWithoutNotification(library, [bmsFile]);
            string installDestination = Path.Combine("C:\\Install", "WarmOverlay");
            ApplyInstallDestinationChange(library, bmsFile, installDestination);

            BMSLibrary.OwnedAdjacentIndexWarmupResult first = library.WarmInstallDestinationOverlaySnapshot("test");
            BMSLibrary.OwnedAdjacentIndexWarmupResult second = library.WarmInstallDestinationOverlaySnapshot("test");
            InstallDestinationOverlayChartRefSnapshot snapshot = InvokeCreateInstallDestinationOverlayChartRefSnapshot(library);

            Assert.AreEqual("install_destination_overlay", first.IndexName);
            Assert.AreEqual("built", first.Status);
            Assert.AreEqual(1, first.ChartRefCount);
            Assert.AreEqual(1, first.DirectoryCount);
            Assert.AreEqual("cached", second.Status);
            Assert.AreEqual(first.ChartRefCount, second.ChartRefCount);
            Assert.AreEqual(1, snapshot.GetChartRefsUnderInstallDestination(installDestination).Count);
        });
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_UnregisterDoesNotPruneSamePathDifferentOwner()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string sharedPath = Path.Combine("C:\\Library", "Bms", "chart.bms");
            var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", sharedPath, new string('b', 64));
            var duplicateOwner = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", sharedPath, new string('c', 64));
            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = [bmsFile, duplicateOwner],
                BmsonSongs = []
            };
            string installDestination = Path.Combine("C:\\Install", "Bms");
            ApplyInstallDestinationChange(library, duplicateOwner, installDestination);
            InstallDestinationOverlayChartRefSnapshot initialSnapshot = InvokeCreateInstallDestinationOverlayChartRefSnapshot(library);
            Assert.AreEqual(1, initialSnapshot.GetChartRefsUnderInstallDestination(installDestination).Count);

            var delta = new LibraryMutationDelta();
            delta.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromOwnerReference(bmsFile));
            InvokeApplyLibraryMutationDelta(library, delta);
            InstallDestinationOverlayChartRefSnapshot afterSnapshot = InvokeCreateInstallDestinationOverlayChartRefSnapshot(library);

            Assert.AreEqual(1, afterSnapshot.GetChartRefsUnderInstallDestination(installDestination).Count);
            Assert.AreEqual(1, library.BMSFiles.Count);
            Assert.AreSame(duplicateOwner, library.BMSFiles[0]);
        });
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_UnregisterBmsonDoesNotPruneSamePathDifferentOwner()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string sharedPath = Path.Combine("C:\\Library", "Bmson", "chart.bmson");
            var bmsonSong = CreateBmsonSong(sharedPath, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var duplicateOwner = CreateBmsonSong(sharedPath, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = [],
                BmsonSongs = [bmsonSong, duplicateOwner]
            };

            var delta = new LibraryMutationDelta();
            delta.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromOwnerReference(bmsonSong));
            InvokeApplyLibraryMutationDelta(library, delta);

            Assert.AreEqual(1, library.BmsonSongs.Count);
            Assert.AreSame(duplicateOwner, library.BmsonSongs[0]);
        });
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_OverlayOnlyClearsMetadataCacheAndPublishesOverlayRefreshWithoutLookupRebuild()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string chartDirectory = Path.Combine(Path.GetDirectoryName(songDbPath), "Installed");
            Directory.CreateDirectory(chartDirectory);
            string chartPath = Path.Combine(chartDirectory, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1");
            var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath, new string('b', 64));
            var library = new TestBmsLibrary(songDbPath);
            SetLibraryFilesWithoutNotification(library, [bmsFile]);
            InstalledChartLookupIndexSnapshot initialLookup = InvokeCreateInstalledChartLookupSnapshot(library);
            Assert.IsTrue(initialLookup.ContainsPrimaryHash(bmsFile.hash));
            Assert.IsTrue(IsInstalledChartLookupIndexInitialized(library));
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            int bmsFilesChanged = 0;
            int normalLibraryRefreshNotifications = 0;
            int ownedCollectionVersionChanged = 0;
            library.PropertyChanged += delegate (object _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == "BMSFiles")
                {
                    bmsFilesChanged++;
                }
                if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                {
                    normalLibraryRefreshNotifications++;
                }
                if (args.PropertyName == "OwnedChartCollectionVersion")
                {
                    ownedCollectionVersionChanged++;
                }
            };
            var delta = new LibraryMutationDelta
            {
                InvalidateInstalledDirectoryIndex = true
            };
            delta.UpdatedInstallDestinations.Add(new LibraryInstallDestinationChange
            {
                Chart = ChartFileProjection.FromBmsFile(bmsFile, includeWarningSnapshot: false, includeResourceReferences: false),
                NewInstallDestination = Path.Combine(chartDirectory, "Overlay")
            });

            InvokeApplyLibraryMutationDelta(library, delta);
            InstalledChartLookupIndexSnapshot updatedLookup = InvokeCreateInstalledChartLookupSnapshot(library);

            Assert.IsTrue(IsInstalledChartLookupIndexInitialized(library));
            Assert.IsTrue(updatedLookup.ContainsPrimaryHash(bmsFile.hash));
            Assert.AreSame(initialLookup, updatedLookup);
            CollectionAssert.AreEqual(initialLookup.Md5Directories[bmsFile.hash].ToArray(), updatedLookup.Md5Directories[bmsFile.hash].ToArray());
            Assert.AreEqual(0, bmsFilesChanged);
            Assert.AreEqual(1, normalLibraryRefreshNotifications);
            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);
            Assert.IsFalse(batch.NotifiesStorageRows);
            Assert.IsFalse(batch.NotifiesBmsFiles);
            Assert.IsFalse(batch.NotifiesBmsonSongs);
            Assert.IsFalse(batch.HasEffect(LibraryChartRefreshEffects.SourceChanged));
            Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.InstallDestinationOverlayChanged));
            Assert.AreEqual(0, ownedCollectionVersionChanged);
        });
    }

    [TestMethod]
    public void SearchEstimatedInstallationDirectory_OverlayMutationRefreshesMetadataProfileThroughRealRoute()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.GetDirectoryName(songDbPath);
            string libraryRootPath = ResolveExistingDataFixtureDirectory();
            string candidateDirectoryPath = libraryRootPath;
            string candidatePath = Path.Combine(candidateDirectoryPath, "fixture.bms");
            string pendingDirectoryPath = Path.Combine(tempRootPath, "MetadataProfilePending");
            Directory.CreateDirectory(pendingDirectoryPath);
            string pendingPath = Path.Combine(pendingDirectoryPath, "pending.bms");
            File.WriteAllText(
                pendingPath,
                "#PLAYER 1\r\n#TITLE Pending Target\r\n#ARTIST Pending Artist\r\n#00111:01\r\n",
                System.Text.Encoding.ASCII);

            ChartFileSnapshot candidateSnapshot = ChartFileContentReader.ReadSnapshot(candidatePath);
            TestableBmsFile candidate = CreateFile(candidateSnapshot.Md5, candidatePath, candidateSnapshot.Sha256);
            candidate.SetTitle("E1 Fixture Song");
            candidate.SetArtist("BeMusicSeeker");
            candidate.date = Lr2SongRowEnricher.ToLr2UnixSeconds(File.GetLastWriteTimeUtc(candidatePath));
            var library = new TestBmsLibrary(
                songDbPath,
                getLR2Config: null,
                _lr2ScoreDB: null,
                startupRequiredFileScanReason: null,
                optionsSnapshotProvider: () => new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = false
                })
            {
                SearchTargets = [libraryRootPath]
            };
            SetLibraryFilesWithoutNotification(library, [candidate]);

            BMSFile pending = BMSFile.CreateBMSFileFromFile(pendingPath);
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([candidate, pending]);
            pendingPackage.path = pendingDirectoryPath;
            pendingPackage.delete_parent = false;
            library.ChartPackagesPending = new System.Collections.ObjectModel.ObservableCollection<ChartPackage>([pendingPackage]);

            // The mixed-package route resolves the already-installed candidate through
            // the public estimation entry point and warms the destination metadata profile.
            library.SearchEstimatedInstallationDirectory(pendingPackage);
            PackageChartEntry firstEntry = pendingPackage.ChartEntries.Single(entry => entry.Chart.Path == pendingPath);

            Assert.AreEqual("E1 Fixture Song", firstEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual("BeMusicSeeker", firstEntry.Chart.InstallDestinationArtist);

            candidate.SetTitle("Changed Candidate");
            candidate.SetArtist("Changed Artist");
            var overlayDelta = new LibraryMutationDelta
            {
                InvalidateInstalledDirectoryIndex = true
            };
            overlayDelta.UpdatedInstallDestinations.Add(new LibraryInstallDestinationChange
            {
                Chart = ChartFileProjection.FromBmsFile(
                    candidate,
                    includeWarningSnapshot: false,
                    includeResourceReferences: false),
                NewInstallDestination = candidateDirectoryPath
            });
            InvokeApplyLibraryMutationDelta(library, overlayDelta);

            library.SearchEstimatedInstallationDirectory(pendingPackage);
            PackageChartEntry refreshedEntry = pendingPackage.ChartEntries.Single(entry => entry.Chart.Path == pendingPath);

            Assert.AreEqual("Changed Candidate", refreshedEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Changed Artist", refreshedEntry.Chart.InstallDestinationArtist);
        });
    }

    [TestMethod]
    public void CreateInstalledChartKeySnapshotExcludingCharts_BuildsPrimaryLookupWithoutFullDirectoryLookup()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"));
            var bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = [bmsFile],
                BmsonSongs = [bmsonSong]
            };

            IPrimaryHashLookup lookup = InvokeCreateInstalledChartKeySnapshotExcludingCharts(library, []);

            Assert.IsTrue(lookup.ContainsPrimaryHash(bmsFile.hash));
            Assert.IsTrue(lookup.ContainsPrimaryHash(bmsonSong.md5));
            Assert.IsTrue(IsInstalledPrimaryHashLookupInitialized(library));
            Assert.IsFalse(IsInstalledChartLookupIndexInitialized(library));
        });
    }

    [TestMethod]
    public void CreateInstalledChartKeySnapshotExcludingCharts_ExcludesOnlyPrimaryHashCounts()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var firstBmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "First", "chart.bms"));
            var secondBmsFile = CreateFile(firstBmsFile.hash, Path.Combine("C:\\Installed", "Second", "chart.bms"));
            var otherBmsFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Other", "chart.bms"));
            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = [firstBmsFile, secondBmsFile, otherBmsFile],
                BmsonSongs = []
            };

            IPrimaryHashLookup excludingOne = InvokeCreateInstalledChartKeySnapshotExcludingCharts(
                library,
                [ChartFileProjection.FromBmsFile(firstBmsFile, includeWarningSnapshot: false, includeResourceReferences: false)]);
            IPrimaryHashLookup excludingBoth = InvokeCreateInstalledChartKeySnapshotExcludingCharts(
                library,
                [
                    ChartFileProjection.FromBmsFile(firstBmsFile, includeWarningSnapshot: false, includeResourceReferences: false),
                    ChartFileProjection.FromBmsFile(secondBmsFile, includeWarningSnapshot: false, includeResourceReferences: false)
                ]);

            Assert.AreEqual(1, excludingOne.GetPrimaryHashCount(firstBmsFile.hash));
            Assert.IsTrue(excludingOne.ContainsPrimaryHash(firstBmsFile.hash));
            Assert.AreEqual(0, excludingBoth.GetPrimaryHashCount(firstBmsFile.hash));
            Assert.IsFalse(excludingBoth.ContainsPrimaryHash(firstBmsFile.hash));
            Assert.IsTrue(excludingBoth.ContainsPrimaryHash(otherBmsFile.hash));
            Assert.IsFalse(IsInstalledChartLookupIndexInitialized(library));
        });
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_UpdatesPrimaryLookupWithoutFullDirectoryLookup()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var removedBmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Removed", "chart.bms"));
            var keptBmsFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Kept", "chart.bms"));
            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = [removedBmsFile, keptBmsFile],
                BmsonSongs = []
            };
            IPrimaryHashLookup initialLookup = InvokeCreateInstalledChartKeySnapshotExcludingCharts(library, []);
            Assert.IsTrue(initialLookup.ContainsPrimaryHash(removedBmsFile.hash));
            Assert.IsFalse(IsInstalledChartLookupIndexInitialized(library));
            var delta = new LibraryMutationDelta();
            delta.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromOwnerReference(removedBmsFile));

            InvokeApplyLibraryMutationDelta(library, delta);
            IPrimaryHashLookup updatedLookup = InvokeCreateInstalledChartKeySnapshotExcludingCharts(library, []);

            Assert.IsFalse(updatedLookup.ContainsPrimaryHash(removedBmsFile.hash));
            Assert.IsTrue(updatedLookup.ContainsPrimaryHash(keptBmsFile.hash));
            Assert.IsTrue(IsInstalledPrimaryHashLookupInitialized(library));
            Assert.IsFalse(IsInstalledChartLookupIndexInitialized(library));
        });
    }

    [TestMethod]
    public void ApplyInstalledChartStorageTargets_UpdatesPrimaryLookupWithoutBuildingOwnedRefIndex()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var initialBmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Initial", "chart.bms"));
            var addedBmsFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Added", "chart.bms"));
            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = [initialBmsFile],
                BmsonSongs = []
            };
            IPrimaryHashLookup initialLookup = InvokeCreateInstalledChartKeySnapshotExcludingCharts(library, []);
            Assert.IsTrue(initialLookup.ContainsPrimaryHash(initialBmsFile.hash));
            Assert.IsFalse(IsInstalledChartLookupIndexInitialized(library));

            InvokeApplyInstalledChartStorageTargets(library, ChartStorageTargetSet.FromRows([addedBmsFile], []));
            IPrimaryHashLookup updatedLookup = InvokeCreateInstalledChartKeySnapshotExcludingCharts(library, []);

            Assert.IsTrue(updatedLookup.ContainsPrimaryHash(initialBmsFile.hash));
            Assert.IsTrue(updatedLookup.ContainsPrimaryHash(addedBmsFile.hash));
            Assert.IsTrue(IsInstalledPrimaryHashLookupInitialized(library));
            Assert.IsFalse(IsInstalledChartLookupIndexInitialized(library));
        });
    }

    [TestMethod]
    public void WarmOwnedRealPathDirectoryView_BuildsAndReusesOwnedRefIndex()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string root = Path.Combine("C:\\Installed", "Warmup");
            var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(root, "Bms", "chart.bms"));
            var bmsonSong = CreateBmsonSong(Path.Combine(root, "Bmson", "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = [bmsFile],
                BmsonSongs = [bmsonSong]
            };
            BMSLibrary.OwnedAdjacentIndexWarmupResult first = library.WarmOwnedRealPathDirectoryView("test");
            BMSLibrary.OwnedAdjacentIndexWarmupResult second = library.WarmOwnedRealPathDirectoryView("test");

            Assert.AreEqual("real_path", first.IndexName);
            Assert.AreEqual("built", first.Status);
            Assert.AreEqual(2, first.ChartRefCount);
            Assert.IsTrue(first.DirectDirectoryCount >= 2);
            Assert.IsTrue(first.SubtreeDirectoryCount >= 1);
            Assert.AreEqual("cached", second.Status);
            Assert.AreEqual(first.ChartRefCount, second.ChartRefCount);
        });
    }

    [TestMethod]
    public void WarmInstalledPrimaryHashLookup_BuildsWithoutFullDirectoryLookup()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "PrimaryWarmup", "Bms", "chart.bms"));
            var bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "PrimaryWarmup", "Bmson", "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = [bmsFile],
                BmsonSongs = [bmsonSong]
            };
            Assert.IsFalse(IsInstalledPrimaryHashLookupInitialized(library));
            Assert.IsFalse(IsInstalledChartLookupIndexInitialized(library));

            BMSLibrary.InstalledPrimaryHashWarmupResult first = library.WarmInstalledPrimaryHashLookup("test");
            BMSLibrary.InstalledPrimaryHashWarmupResult second = library.WarmInstalledPrimaryHashLookup("test");

            Assert.AreEqual("installed_primary_hash", first.IndexName);
            Assert.AreEqual("built", first.Status);
            Assert.AreEqual(2, first.PrimaryHashCount);
            Assert.AreEqual(1, first.BmsCount);
            Assert.AreEqual(1, first.BmsonCount);
            Assert.IsFalse(first.FullDirectoryLookupInitialized);
            Assert.IsTrue(IsInstalledPrimaryHashLookupInitialized(library));
            Assert.IsFalse(IsInstalledChartLookupIndexInitialized(library));
            Assert.AreEqual("cached", second.Status);
            Assert.AreEqual(first.PrimaryHashCount, second.PrimaryHashCount);
            Assert.AreEqual(0, second.BmsCount);
            Assert.AreEqual(0, second.BmsonCount);
            Assert.IsFalse(second.FullDirectoryLookupInitialized);
        });
    }


}
