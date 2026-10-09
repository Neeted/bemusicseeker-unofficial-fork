using System;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.OwnedChartCollectionTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class OwnedChartCollectionRefreshTests
{
    [TestMethod]
    public void Publisher_PreservesEarlierTitleDependencyAndFinalCurrentInOneBatch()
    {
        WithTemporarySongDb(songDbPath =>
        {
            var owner = new PackageLifecycleOwner(new BmsLibraryDbGateway(songDbPath),
                new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher), _ => { },
                packages => new System.Collections.ObjectModel.ObservableCollection<ChartPackage>(packages ?? []), () => { }, _ => { });
            ChartFile before = ChartTestValues.Empty(ChartFileKind.Bms) with
            {
                Token = new OwnedChartToken(),
                Path = @"C:\Charts\before.bms",
                Md5 = new string('a', 32),
                Sha256 = new string('a', 64),
                Title = "A",
                RawTitle = "A",
                Mode = 7
            };
            var entry = PackageChartEntry.FromChart(before);
            var package = ChartPackage.FromChartEntries([entry]);
            owner.ReplaceInstalledPackages([package]);
            var publisher = new NormalLibraryRefreshPublisher();
            var pending = new System.Collections.Generic.Queue<Action>();
            var scheduler = new RegularChartListOwnerTestSupport.ActionQueueUiScheduler(pending.Enqueue);
            var scheduled = new System.Collections.Generic.List<IUiScheduledOperation>();
            try
            {
                foreach (ChartFile current in new[] { before with { Title = "Z", RawTitle = "Z" }, before with { Title = "Z", RawTitle = "Z", Path = @"C:\Charts\after.bms" } })
                {
                    var changes = new System.Collections.Generic.List<InstalledChartCurrentChange>();
                    Action publish = owner.PrepareCommittedChartApplication([current], changes);
                    scheduled.Add(scheduler.Schedule(() =>
                    {
                        publish();
                        publisher.Publish(new NormalLibraryRefreshPublishRequest
                        {
                            Effects = LibraryChartRefreshEffects.SourceChanged,
                            ChangedCharts = [current],
                            InstalledChartChanges = changes
                        });
                    }));
                }
                while (pending.TryDequeue(out Action? publication))
                {
                    publication();
                }
                foreach (IUiScheduledOperation operation in scheduled)
                {
                    operation.Completion.GetAwaiter().GetResult();
                }
                NormalLibraryRefreshNotificationBatch batch = publisher.GetNotificationsAfter(0);
                Assert.AreEqual(1, batch.ChangedCharts.Count);
                Assert.AreEqual(@"C:\Charts\after.bms", batch.ChangedCharts.Single().Path);
                Assert.AreEqual(2, batch.InstalledChartChanges.Count);
                Assert.AreEqual("A", batch.InstalledChartChanges[0].Before.Title);
                Assert.AreEqual("Z", batch.InstalledChartChanges[0].Current.Title);
                Assert.AreEqual("Z", batch.InstalledChartChanges[1].Before.Title);
                Assert.AreSame(entry, batch.InstalledChartChanges[0].Entry);
                Assert.AreSame(package, batch.InstalledChartChanges[1].Packages.Single());
                Assert.AreEqual(BeMusicSeeker.ViewModels.MainViewRefreshAction.Refresh,
                    BeMusicSeeker.ViewModels.MainViewRefreshDecisionService.BuildNotificationBatch(batch,
                        BeMusicSeeker.ViewModels.MainViewUpdateMode.NewlyInstalledFolderSelected, package, false, "",
                        BeMusicSeeker.ViewModels.ChartModeFilter.All, "Title", false));
            }
            finally
            {
                foreach (IUiScheduledOperation operation in scheduled)
                {
                    if (!operation.IsCompleted)
                    {
                        operation.Abort();
                    }
                }
                pending.Clear();
                owner.ClearInstalledPackages();
            }
        });
    }

    [TestMethod]
    public void ApplyFileScanCatalogResidual_UpdatesInstallDestinationProjectionThroughTypedFacts()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string chartPath = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Installed", "scan-residual.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(chartPath)!);
            File.WriteAllText(chartPath, "#PLAYER 1");
            ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath);
            var library = new TestBmsLibrary(songDbPath);
            SetLibraryFilesWithoutNotification(library, [bmsFile]);
            SetLibraryBmsonSongsWithoutNotification(library, []);
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;

            ChartFile residualChart = ChartFileProjection.WithPackageState(
                (bmsFile),
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
            Assert.IsFalse(batch.HasEffect(LibraryChartRefreshEffects.SourceChanged));
            Assert.AreEqual(0, batch.DeletedTokens.Count);
            Assert.AreSame(bmsFile.Token, batch.ChangedCharts.Single().Token);
        });
    }

    [TestMethod]
    public void ApplyFileScanCatalogResidual_EmptyFactsDoNotAddMutationOrNotification()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string chartPath = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Installed", "scan-residual-empty.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(chartPath)!);
            File.WriteAllText(chartPath, "#PLAYER 1");
            ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath);
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
            int beforeOwnedCollectionVersion = library.OwnedCollectionVersion;
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
            Assert.AreEqual(beforeOwnedCollectionVersion, library.OwnedCollectionVersion);
            Assert.AreEqual(beforeRefreshVersion, library.NormalLibraryRefreshNotificationVersion);
            Assert.AreEqual(beforeDuplicateInvalidationVersion, library.DuplicateChartGroupsInvalidationVersion);
        });
    }

}
