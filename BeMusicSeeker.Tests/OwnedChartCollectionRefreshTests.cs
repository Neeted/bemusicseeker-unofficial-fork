using System;
using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
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
            string chartDirectory = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Installed");
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
    public void ApplyFileScanCatalogResidual_UpdatesInstallDestinationProjectionThroughTypedFacts()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string chartPath = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Installed", "scan-residual.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(chartPath)!);
            File.WriteAllText(chartPath, "#PLAYER 1");
            BMSFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath);
            var library = new TestBmsLibrary(songDbPath);
            SetLibraryFilesWithoutNotification(library, [bmsFile]);
            SetLibraryBmsonSongsWithoutNotification(library, []);
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;

            ChartFile residualChart = ChartFileProjection.WithPackageState(
                ChartFileProjection.FromBmsFile(
                    bmsFile,
                    includeWarningSnapshot: false,
                    includeResourceReferences: false),
                Path.Combine(Path.GetDirectoryName(chartPath)!, "Overlay"),
                string.Empty,
                string.Empty,
                []);
            Action postLeaseEffects = library.ApplyFileScanCatalogResidualForScan(
                FileScanCatalogResidualEvent.Create([residualChart], "residual_test"));
            postLeaseEffects?.Invoke();

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
    public void ApplyFileScanCatalogResidual_EmptyFactsDoNotAddMutationOrNotification()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string chartPath = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Installed", "scan-residual-empty.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(chartPath)!);
            File.WriteAllText(chartPath, "#PLAYER 1");
            BMSFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath);
            var library = new TestBmsLibrary(songDbPath);
            SetLibraryFilesWithoutNotification(library, [bmsFile]);

            OwnedChartHashIndexVersionedSnapshot beforeHash = library.GetOwnedChartHashIndexSnapshot();
            InstalledChartLookupIndexSnapshot beforeInstalled =
                library.CreateInstalledChartLookupSnapshotForDiagnostics();
            PlaylistLibraryResolveIndexSnapshot beforePlaylist = library.GetPlaylistLibraryResolveIndexSnapshot(
                System.Threading.CancellationToken.None,
                out bool beforePlaylistCacheHit,
                out int beforePlaylistStaleRetries);
            Assert.IsFalse(beforePlaylistCacheHit);
            Assert.AreEqual(0, beforePlaylistStaleRetries);
            LibraryResourceIndexOwner resourceIndexOwner = LibraryResourceIndexTestSupport.GetOwner(library);
            LibraryResourceIndexSnapshot beforeResourceIndex = resourceIndexOwner.CaptureSnapshot();
            ResourceHealthIndexSnapshot beforeResourceHealth =
                library.TryGetCurrentResourceHealthIndexSnapshotForView();
            int beforeOwnedCollectionVersion = library.OwnedChartCollectionVersion;
            int beforeRefreshVersion = library.NormalLibraryRefreshNotificationVersion;
            int beforeDuplicateInvalidationVersion = library.DuplicateChartGroupsInvalidationVersion;

            Action postLeaseEffects = library.ApplyFileScanCatalogResidualForScan(
                FileScanCatalogResidualEvent.Create([], "empty_residual_test"));

            Assert.IsNull(postLeaseEffects);
            Assert.AreSame(beforeHash, library.GetOwnedChartHashIndexSnapshot());
            Assert.AreEqual(beforeInstalled.HashCount,
                library.CreateInstalledChartLookupSnapshotForDiagnostics().HashCount);
            PlaylistLibraryResolveIndexSnapshot afterPlaylist = library.GetPlaylistLibraryResolveIndexSnapshot(
                System.Threading.CancellationToken.None,
                out bool afterPlaylistCacheHit,
                out int afterPlaylistStaleRetries);
            Assert.AreSame(beforePlaylist, afterPlaylist);
            Assert.IsTrue(afterPlaylistCacheHit);
            Assert.AreEqual(0, afterPlaylistStaleRetries);
            LibraryResourceIndexSnapshot afterResourceIndex = resourceIndexOwner.CaptureSnapshot();
            Assert.AreEqual(beforeResourceIndex.Generation, afterResourceIndex.Generation);
            Assert.AreSame(beforeResourceIndex.Index, afterResourceIndex.Index);
            Assert.AreSame(beforeResourceIndex.DirectoryLookupCache, afterResourceIndex.DirectoryLookupCache);
            Assert.AreSame(beforeResourceHealth, library.TryGetCurrentResourceHealthIndexSnapshotForView());
            Assert.AreEqual(beforeOwnedCollectionVersion, library.OwnedChartCollectionVersion);
            Assert.AreEqual(beforeRefreshVersion, library.NormalLibraryRefreshNotificationVersion);
            Assert.AreEqual(beforeDuplicateInvalidationVersion, library.DuplicateChartGroupsInvalidationVersion);
        });
    }

    [TestMethod]
    public void NormalLibraryRefreshNotificationBatch_DoesNotHideOverlayRefreshInSameStorageRowNotification()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string chartDirectory = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Installed");
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
