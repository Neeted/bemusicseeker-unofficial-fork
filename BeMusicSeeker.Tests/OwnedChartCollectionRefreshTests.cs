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
public sealed class OwnedChartCollectionRefreshTests
{
    [TestMethod]
    public void NormalLibraryRefreshNotificationBatch_DoesNotHideOverlayOnlyRefreshBehindOtherStorageRowNotifications()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string chartDirectory = Path.Combine(Path.GetDirectoryName(songDbPath), "Installed");
            Directory.CreateDirectory(chartDirectory);
            string overlayChartPath = Path.Combine(chartDirectory, "overlay.bms");
            string unregisterChartPath = Path.Combine(chartDirectory, "unregister.bms");
            File.WriteAllText(overlayChartPath, "#PLAYER 1");
            File.WriteAllText(unregisterChartPath, "#PLAYER 1");
            var overlayBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", overlayChartPath);
            var unregisterBms = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", unregisterChartPath);
            var library = new TestBmsLibrary(songDbPath);
            SetLibraryFilesWithoutNotification(library, [overlayBms, unregisterBms]);
            SetLibraryBmsonSongsWithoutNotification(library, []);
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            var overlayDelta = new LibraryMutationDelta();
            overlayDelta.UpdatedInstallDestinations.Add(new LibraryInstallDestinationChange
            {
                Chart = ChartFileProjection.FromBmsFile(overlayBms, includeWarningSnapshot: false, includeResourceReferences: false),
                NewInstallDestination = Path.Combine(chartDirectory, "Overlay")
            });
            var unregisterDelta = new LibraryMutationDelta();
            unregisterDelta.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromOwnerReference(unregisterBms));

            InvokeApplyLibraryMutationDelta(library, overlayDelta);
            InvokeApplyLibraryMutationDelta(library, unregisterDelta);

            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);
            Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.InstallDestinationOverlayChanged));
            Assert.IsTrue(batch.NotifiesStorageRows);
            Assert.IsTrue(batch.NotifiesBmsFiles);
            Assert.IsFalse(batch.NotifiesBmsonSongs);
        });
    }

    [TestMethod]
    public void ApplyFileScanCatalogResidual_UpdatesInstallDestinationProjectionWithoutGenericMutation()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string chartPath = Path.Combine(Path.GetDirectoryName(songDbPath), "Installed", "scan-residual.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(chartPath));
            File.WriteAllText(chartPath, "#PLAYER 1");
            BMSFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath);
            var library = new TestBmsLibrary(songDbPath);
            SetLibraryFilesWithoutNotification(library, [bmsFile]);
            SetLibraryBmsonSongsWithoutNotification(library, []);
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;

            var delta = new LibraryMutationDelta
            {
                InvalidateInstalledDirectoryIndex = true,
                ClearDuplicatedCache = true
            };
            delta.UpdatedInstallDestinations.Add(new LibraryInstallDestinationChange
            {
                Chart = ChartFileProjection.FromBmsFile(
                    bmsFile,
                    includeWarningSnapshot: false,
                    includeResourceReferences: false),
                NewInstallDestination = Path.Combine(Path.GetDirectoryName(chartPath), "Overlay")
            });

            library.ApplyFileScanCatalogResidual(
                FileScanCatalogResidualEvent.Create(delta, "residual_test"));

            InstallDestinationOverlayChartRefSnapshot overlay =
                InvokeCreateInstallDestinationOverlayChartRefSnapshot(library);
            Assert.AreEqual(1, overlay.ChartCount);
            NormalLibraryRefreshNotificationBatch batch =
                library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);
            Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.InstallDestinationOverlayChanged));
            Assert.IsFalse(batch.NotifiesStorageRows);
            Assert.IsFalse(batch.NotifiesBmsFiles);
            Assert.IsFalse(batch.NotifiesBmsonSongs);
        });
    }

    [TestMethod]
    public void ReloadFileDiff_ReplacesOwnedStorageRowsAndPublishesResourceHealthInvalidation()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string libraryRootPath = ResolveExistingDataFixtureDirectory();
            string keptPath = Path.Combine(libraryRootPath, "fixture.bms");
            string removedPath = Path.Combine(libraryRootPath, "removed-from-catalog.bms");
            string addedPath = keptPath;
            BMSFile kept = BMSFile.CreateBMSFileFromFile(keptPath);
            BMSFile removed = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", removedPath);
            kept.date = Lr2SongRowEnricher.ToLr2UnixSeconds(File.GetLastWriteTimeUtc(keptPath));
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
            SetLibraryFilesWithoutNotification(library, [removed]);
            InstalledChartLookupIndexSnapshot initialLookup = InvokeCreateInstalledChartLookupSnapshot(library);
            Assert.IsTrue(initialLookup.ContainsPrimaryHash(removed.hash));
            EnsureCurrentResourceHealthIndex(library);
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;

            library.ReloadFileDiff();

            Assert.IsTrue(library.BMSFiles.Any(file => file.path == keptPath));
            Assert.IsTrue(library.BMSFiles.Any(file => file.path == addedPath));
            Assert.IsFalse(library.BMSFiles.Any(file => file.path == removedPath));
            InstalledChartLookupIndexSnapshot updatedLookup = InvokeCreateInstalledChartLookupSnapshot(library);
            Assert.IsFalse(updatedLookup.ContainsPrimaryHash(removed.hash));
            Assert.IsTrue(updatedLookup.ContainsPrimaryHash(kept.hash));
            Assert.IsTrue(HasNoCurrentResourceHealthIndex(library));
            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);
            Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.SourceChanged));
            Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged));
            Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.MaintenancePresentationChanged));
            Assert.IsTrue(batch.NotifiesStorageRows);
            Assert.IsTrue(batch.NotifiesBmsFiles);
        });
    }

    [TestMethod]
    public void ReloadFileDiff_NoDiffPublishesResourceHealthRefreshThroughRealScanRoute()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string libraryRootPath = ResolveExistingDataFixtureDirectory();
            string chartPath = Path.Combine(libraryRootPath, "fixture.bms");
            BMSFile chart = BMSFile.CreateBMSFileFromFile(chartPath);
            chart.date = Lr2SongRowEnricher.ToLr2UnixSeconds(File.GetLastWriteTimeUtc(chartPath));
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
            SetLibraryFilesWithoutNotification(library, [chart]);
            EnsureCurrentResourceHealthIndex(library);
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;

            library.ReloadFileDiff();

            Assert.AreEqual(1, library.BMSFiles.Count);
            Assert.AreSame(chart, library.BMSFiles[0]);
            Assert.IsTrue(HasNoCurrentResourceHealthIndex(library));
            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);
            Assert.IsTrue(batch.HasRefreshNotification);
            Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged));
            Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.MaintenancePresentationChanged));
            Assert.IsFalse(batch.HasEffect(LibraryChartRefreshEffects.SourceChanged));
            Assert.IsFalse(batch.NotifiesStorageRows);
            Assert.IsFalse(batch.NotifiesBmsFiles);
        });
    }

    [TestMethod]
    public void NormalLibraryRefreshNotificationBatch_DoesNotHideOverlayRefreshInSameStorageRowNotification()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string chartDirectory = Path.Combine(Path.GetDirectoryName(songDbPath), "Installed");
            Directory.CreateDirectory(chartDirectory);
            string overlayChartPath = Path.Combine(chartDirectory, "overlay.bms");
            string movedChartPath = Path.Combine(chartDirectory, "moved.bms");
            string oldMovedChartPath = Path.Combine(chartDirectory, "old", "moved.bms");
            File.WriteAllText(overlayChartPath, "#PLAYER 1");
            File.WriteAllText(movedChartPath, "#PLAYER 1");
            var overlayBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", overlayChartPath);
            var movedBms = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", movedChartPath);
            var library = new TestBmsLibrary(songDbPath);
            SetLibraryFilesWithoutNotification(library, [overlayBms, movedBms]);
            SetLibraryBmsonSongsWithoutNotification(library, []);
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            var delta = new LibraryMutationDelta
            {
                NotifyStorageRowPathChanges = true
            };
            delta.UpdatedInstallDestinations.Add(new LibraryInstallDestinationChange
            {
                Chart = ChartFileProjection.FromBmsFile(overlayBms, includeWarningSnapshot: false, includeResourceReferences: false),
                NewInstallDestination = Path.Combine(chartDirectory, "Overlay")
            });
            delta.ChartPathChanges.Add(new LibraryChartPathChange
            {
                Chart = ChartFileProjection.FromBmsFile(movedBms, includeWarningSnapshot: false, includeResourceReferences: false),
                OldPath = oldMovedChartPath,
                NewPath = movedChartPath
            });

            InvokeApplyLibraryMutationDelta(library, delta);

            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);
            Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.InstallDestinationOverlayChanged));
            Assert.IsTrue(batch.NotifiesStorageRows);
            Assert.IsTrue(batch.NotifiesBmsFiles);
            Assert.IsFalse(batch.NotifiesBmsonSongs);
        });
    }


}
