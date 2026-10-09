using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.RegularChartListOwnerTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class RegularChartNormalLibraryRefreshTests
{
    /// <summary>実保守の文字コード再読解が同tokenの基本値・集合版・局所playlist差分と、接続済み一覧の順序・絞込みへ届くことを確認します。</summary>
    [TestMethod]
    public void MaintenanceMetadataChange_ReachesAttachedViewAndWarmPlaylistDeltaOnce()
    {

        WithTemporarySongDb(songDbPath => RunAsync(songDbPath).GetAwaiter().GetResult());

        static async Task RunAsync(string songDbPath)
        {
            string root = Path.GetDirectoryName(songDbPath) ?? throw new InvalidOperationException();
            string firstPath = Path.Combine(root, "first.bms");
            string secondPath = Path.Combine(root, "second.bms");
            string backgroundPath = Path.Combine(root, "background.bms");
            File.WriteAllText(firstPath, "#PLAYER 1\r\n#TITLE Zあ\r\n#ARTIST 作者あ\r\n#GENRE genre\r\n", new System.Text.UTF8Encoding(false));
            File.WriteAllText(secondPath, "#PLAYER 1\r\n#TITLE Bあ\r\n#ARTIST 別作者あ\r\n#GENRE genre\r\n", new System.Text.UTF8Encoding(false));
            File.WriteAllText(backgroundPath, "#PLAYER 1\r\n#TITLE M before\r\n", System.Text.Encoding.ASCII);
            ChartFile first = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(firstPath)) with
            {
                Title = "A before",
                RawTitle = "A before",
                Artist = "old first",
                RawArtist = "old first"
            };
            ChartFile second = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(secondPath)) with
            {
                Title = "C before",
                RawTitle = "C before",
                Artist = "old second",
                RawArtist = "old second"
            };
            ChartFile background = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(backgroundPath));
            new BmsLibraryDbGateway(songDbPath).UpsertSongs([first, second, background]);
            var library = new TestBmsLibrary(songDbPath) { BmsCharts = [first, second, background] };
            OwnedChartCollectionState collection = OwnedChartCollectionTestSupport.GetOwnedCollectionOwner(library).Collection;
            first = collection.ResolveCurrentChart(LibraryChartRef.FromPath(ChartFileKind.Bms, firstPath, null, null));
            second = collection.ResolveCurrentChart(LibraryChartRef.FromPath(ChartFileKind.Bms, secondPath, null, null));
            background = collection.ResolveCurrentChart(LibraryChartRef.FromPath(ChartFileKind.Bms, backgroundPath, null, null));
            var table = new MainChartListViewModel();
            using RegularChartListOwner receiver = CreateOwner(table, CreateWorkspaceForOwner(table));
            var request = new RegularVirtualNormalLibraryApplyRequest
            {
                Library = library,
                KeywordFilter = "before",
                ModeFilter = ChartModeFilter.All,
                SortColumnName = nameof(LibraryChartRow.Title),
                SortDirection = System.ComponentModel.ListSortDirection.Ascending,
                ColumnSelection = new MainChartListColumnSelection(new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD), false, 0,
                    MainViewUpdateMode.FullScanAllChartsFilterSelected, System.Windows.Visibility.Collapsed, new PlaylistSummaryColumnSettings()),
                Mode = MainViewUpdateMode.FullScanAllChartsFilterSelected,
                Stopwatch = System.Diagnostics.Stopwatch.StartNew(),
                Reason = "maintenance_metadata_connection"
            };
            var applied = new TaskCompletionSource<NormalLibraryRefreshNotificationBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<NormalLibraryRefreshAppliedEventArgs> handler = (_, args) =>
            {
                try
                {
                    MainViewRefreshAction action = receiver.ApplyNotificationPresentation(library, args.NotificationBatch,
                        MainViewUpdateMode.FullScanAllChartsFilterSelected, null, new ChartListFilterSnapshot(request.KeywordFilter, ChartModeFilter.All), false);
                    if (action == MainViewRefreshAction.Refresh)
                    {
                        request.Stopwatch = System.Diagnostics.Stopwatch.StartNew();
                        Assert.IsTrue(receiver.TryApplyVirtualNormalLibrary(request).WasCommitted);
                    }
                    applied.TrySetResult(args.NotificationBatch);
                }
                catch (Exception exception)
                {
                    applied.TrySetException(exception);
                }
            };
            Task<MaintenanceWorkflowResult>? operation = null;
            try
            {
                receiver.AttachNormalLibraryRefreshSource(library);
                Assert.IsTrue(receiver.TryApplyVirtualNormalLibrary(request).WasCommitted);
                CollectionAssert.AreEqual(new[] { "A before", "C before", "M before" }, table.Rows.Cast<LibraryChartRow>().Select(row => row.Title).ToArray());
                receiver.NormalLibraryRefreshApplied += handler;
                var storeWork = new List<string>();
                library.PlaylistLibraryResolveIndexStoreWorkObserver = storeWork.Add;
                PlaylistLibraryResolveIndexSnapshot beforeIndex = library.GetPlaylistLibraryResolveIndexSnapshot(CancellationToken.None, out _, out _);
                storeWork.Clear();
                int version = library.OwnedCollectionVersion;
                operation = Task.Run(() => library.RescanResourceHealthCharts([first, second]));
                await Task.WhenAny(operation, applied.Task);
                MaintenanceWorkflowResult result = await operation;
                NormalLibraryRefreshNotificationBatch batch = await applied.Task;
                Assert.IsTrue(result.HasUpdates);
                Assert.AreEqual(2, result.ReloadedSongCount);
                Assert.AreEqual(version + 1, library.OwnedCollectionVersion);
                Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.SourceChanged));
                Assert.AreEqual(version + 1, batch.OwnedCollectionVersion);
                Assert.IsTrue(batch.ChangedCharts.Any(chart => chart.Token == first.Token));
                Assert.IsTrue(batch.ChangedCharts.Any(chart => chart.Token == second.Token));
                ChartFile currentFirst = collection.ResolveCurrentChart(LibraryChartRef.FromChartFile(first));
                ChartFile currentSecond = collection.ResolveCurrentChart(LibraryChartRef.FromChartFile(second));
                Assert.AreSame(first.Token, currentFirst.Token);
                Assert.AreSame(second.Token, currentSecond.Token);
                Assert.AreEqual("Zあ", currentFirst.Title);
                Assert.AreEqual("作者あ", currentFirst.Artist);
                Assert.AreEqual("Bあ", currentSecond.Title);
                Assert.AreEqual("別作者あ", currentSecond.Artist);
                Assert.AreEqual("A before", first.Title);
                Assert.AreEqual("old second", second.Artist);
                Assert.AreSame(background, collection.ResolveCurrentChart(LibraryChartRef.FromChartFile(background)));
                CollectionAssert.AreEqual(new[] { "M before" }, table.Rows.Cast<LibraryChartRow>().Select(row => row.Title).ToArray());
                PlaylistLibraryResolveIndexSnapshot afterIndex = library.GetPlaylistLibraryResolveIndexSnapshot(CancellationToken.None, out bool cacheHit, out _);
                Assert.IsTrue(cacheHit);
                Assert.AreEqual("A before", beforeIndex.ResolveChartSnapshot(beforeIndex.ResolveChartForPlaylistHash(first.Md5, first.Sha256)).Title);
                Assert.AreEqual("Zあ", afterIndex.ResolveChartSnapshot(afterIndex.ResolveChartForPlaylistHash(first.Md5, first.Sha256)).Title);
                Assert.AreEqual("Bあ", afterIndex.ResolveChartSnapshot(afterIndex.ResolveChartForPlaylistHash(second.Md5, second.Sha256)).Title);
                Assert.IsTrue(storeWork.Contains("playlist_resolve_delta_apply"));
                Assert.IsFalse(storeWork.Contains("playlist_resolve_source_entry_visited"));
                Assert.IsFalse(storeWork.Contains("playlist_resolve_source_enumeration"));
                Assert.IsFalse(storeWork.Contains("playlist_resolve_full_root_enumeration"));
                using (LR2SongDBExtended songDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
                {
                    LR2SongDB.song firstRow = songDb.Query<LR2SongDB.song>("SELECT * FROM song WHERE path = ?", firstPath).Single();
                    LR2SongDB.song secondRow = songDb.Query<LR2SongDB.song>("SELECT * FROM song WHERE path = ?", secondPath).Single();
                    Assert.AreEqual("Zあ", firstRow.title);
                    Assert.AreEqual("作者あ", firstRow.artist);
                    Assert.AreEqual("Bあ", secondRow.title);
                    Assert.AreEqual("別作者あ", secondRow.artist);
                }
                request.KeywordFilter = "";
                request.Stopwatch = System.Diagnostics.Stopwatch.StartNew();
                Assert.IsTrue(receiver.TryApplyVirtualNormalLibrary(request).WasCommitted);
                CollectionAssert.AreEqual(new[] { "Bあ", "M before", "Zあ" }, table.Rows.Cast<LibraryChartRow>().Select(row => row.Title).ToArray());
                applied = new(TaskCreationOptions.RunContinuationsAsynchronously);
                operation = Task.Run(() => library.RescanResourceHealthCharts([currentFirst, currentSecond]));
                await Task.WhenAny(operation, applied.Task);
                MaintenanceWorkflowResult repeated = await operation;
                NormalLibraryRefreshNotificationBatch repeatedBatch = await applied.Task;
                Assert.AreEqual(2, repeated.ReloadedSongCount);
                Assert.AreEqual(version + 1, library.OwnedCollectionVersion);
                Assert.IsFalse(repeatedBatch.HasEffect(LibraryChartRefreshEffects.SourceChanged));
                CollectionAssert.AreEqual(new[] { "Bあ", "M before", "Zあ" }, table.Rows.Cast<LibraryChartRow>().Select(row => row.Title).ToArray());
            }
            finally
            {
                receiver.NormalLibraryRefreshApplied -= handler;
                await receiver.StopAsync();
                library.PlaylistLibraryResolveIndexStoreWorkObserver = null;
                library.RequestShutdown("maintenance_metadata_connection_test");
                if (operation != null)
                {
                    await operation;
                }
            }
        }
    }

    [DataTestMethod]
    [DataRow("Title", "", false, false, false, false, false)]
    [DataRow("path", "", false, false, false, false, true)]
    [DataRow("Title", "before", false, false, false, false, true)]
    [DataRow("Title", "", true, false, false, false, true)]
    [DataRow("Title", "", false, true, false, false, false)]
    [DataRow("Title", "", false, false, true, false, true)]
    [DataRow("Title", "", false, false, false, true, true)]
    public void InstalledBatch_ResolvesCurrentViewDependenciesOnce(string sort, string keyword,
        bool modeChanged, bool unrelatedPackage, bool reset, bool informationMissing, bool rebuild)
    {
        ChartFile before = ChartTestValues.Empty(ChartFileKind.Bms) with
        {
            Token = new OwnedChartToken(),
            Path = @"C:\before\chart.bms",
            Title = "A",
            Mode = 7,
            Md5 = new string('a', 32)
        };
        ChartFile current = before with { Path = @"C:\after\chart.bms", Mode = modeChanged ? 14 : 7 };
        var entry = PackageChartEntry.FromChart(current);
        var package = ChartPackage.FromChartEntries([entry]);
        ChartPackage selected = unrelatedPackage ? ChartPackage.FromChartEntries([PackageChartEntry.FromChart(before with { Token = new OwnedChartToken() })]) : package;
        var batch = new NormalLibraryRefreshNotificationBatch(1, 1,
            LibraryChartRefreshEffects.SourceChanged | LibraryChartRefreshEffects.WarningPresentationChanged
                | LibraryChartRefreshEffects.MaintenancePresentationChanged | LibraryChartRefreshEffects.InstallDestinationOverlayChanged,
            [], reset, changedCharts: [current],
            installedChartChanges: informationMissing ? [] : [new(entry, [package], before, current)]);
        MainViewRefreshAction action = MainViewRefreshDecisionService.BuildNotificationBatch(batch,
            MainViewUpdateMode.NewlyInstalledFolderSelected, selected, false, keyword,
            modeChanged ? ChartModeFilter._7KEYS : ChartModeFilter.All, sort, false);
        Assert.AreEqual(rebuild ? MainViewRefreshAction.Refresh : MainViewRefreshAction.RefreshDisplay, action);
    }

    [TestMethod]
    public void InstalledBatch_AllPresentationEffectsIssueOneDisplayRequestAndKeepRows()
    {
        WithTemporarySongDb(songDbPath =>
        {
            var library = new TestBmsLibrary(songDbPath);
            var table = new MainChartListViewModel();
            using RegularChartListOwner owner = CreateOwner(table, CreateWorkspaceForOwner(table));
            ChartFile before = ChartTestValues.Empty(ChartFileKind.Bmson) with { Token = new OwnedChartToken(), Path = @"C:\before.bmson", Title = "A" };
            var entry = PackageChartEntry.FromChart(before);
            var package = ChartPackage.FromChartEntries([entry]);
            ChartFile current = before with { Path = @"C:\after.bmson" };
            entry.ApplyCurrentChart(current);
            IListRowsForNotification(table, entry);
            System.Collections.IList rows = table.Rows;
            int replacing = 0;
            int display = 0;
            table.RowsReplacing += (_, _) => replacing++;
            table.DisplayRefreshRequested += (_, _) => display++;
            var batch = new NormalLibraryRefreshNotificationBatch(1, 1,
                LibraryChartRefreshEffects.SourceChanged | LibraryChartRefreshEffects.WarningPresentationChanged
                    | LibraryChartRefreshEffects.MaintenancePresentationChanged | LibraryChartRefreshEffects.InstallDestinationOverlayChanged,
                [], false, changedCharts: [current], changedDetailMd5s: ["detail"],
                installedChartChanges: [new(entry, [package], before, current)]);
            Assert.AreEqual(MainViewRefreshAction.RefreshDisplay,
                owner.ApplyNotificationPresentation(library, batch, MainViewUpdateMode.NewlyInstalledFolderSelected,
                    package, ChartListFilterSnapshot.Default, false));
            Assert.AreSame(rows, table.Rows);
            Assert.AreEqual(0, replacing);
            Assert.AreEqual(1, display);
        });
    }

    [TestMethod]
    public void InstalledTitleChangeThroughOwnerFactsRebuildsFinalTitleOrder()
    {
        WithTemporarySongDb(songDbPath =>
        {
            var library = new TestBmsLibrary(songDbPath);
            var table = new MainChartListViewModel();
            using RegularChartListOwner receiver = CreateOwner(table, CreateWorkspaceForOwner(table));
            var producer = new PackageLifecycleOwner(new BmsLibraryDbGateway(songDbPath),
                new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher), _ => { },
                packages => new System.Collections.ObjectModel.ObservableCollection<ChartPackage>(packages ?? []), () => { }, _ => { });
            ChartFile before = ChartTestValues.Empty(ChartFileKind.Bms) with
            {
                Token = new OwnedChartToken(),
                Path = @"C:\Charts\a.bms",
                Title = "A",
                RawTitle = "A"
            };
            var first = PackageChartEntry.FromChart(before);
            var second = PackageChartEntry.FromChart(before with { Token = new OwnedChartToken(), Path = @"C:\Charts\m.bms", Title = "M", RawTitle = "M" });
            var package = ChartPackage.FromChartEntries([first, second]);
            producer.ReplaceInstalledPackages([package]);
            var request = new RegularVirtualChartSubsetApplyRequest
            {
                Library = library,
                SourceEntries = [first, second],
                TreeMode = MainViewUpdateMode.NewlyInstalledFolderSelected,
                SubsetName = "installed_title_connection",
                KeywordFilter = "",
                ModeFilter = ChartModeFilter.All,
                SortColumnName = nameof(LibraryChartRow.Title),
                SortDirection = System.ComponentModel.ListSortDirection.Ascending,
                ColumnSelection = new MainChartListColumnSelection(new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD), false, 0,
                    MainViewUpdateMode.NewlyInstalledFolderSelected, System.Windows.Visibility.Collapsed, new PlaylistSummaryColumnSettings()),
                Mode = MainViewUpdateMode.NewlyInstalledFolderSelected,
                Stopwatch = System.Diagnostics.Stopwatch.StartNew()
            };
            try
            {
                Assert.IsTrue(receiver.TryApplyVirtualChartSubset(request).WasCommitted);
                LibraryChartRow[] initialRows = table.Rows.Cast<LibraryChartRow>().ToArray();
                Assert.AreEqual("A", initialRows[0].Title);
                var changes = new List<InstalledChartCurrentChange>();
                ChartFile current = before with { Title = "Z", RawTitle = "Z" };
                producer.PrepareCommittedChartApplication([current], changes)();
                var publisher = new NormalLibraryRefreshPublisher();
                publisher.Publish(new NormalLibraryRefreshPublishRequest
                {
                    Effects = LibraryChartRefreshEffects.SourceChanged,
                    ChangedCharts = [current],
                    InstalledChartChanges = changes
                });
                NormalLibraryRefreshNotificationBatch batch = publisher.GetNotificationsAfter(0);
                Assert.AreEqual(MainViewRefreshAction.Refresh, receiver.ApplyNotificationPresentation(library, batch,
                    MainViewUpdateMode.NewlyInstalledFolderSelected, package, ChartListFilterSnapshot.Default, false));
                request.Stopwatch = System.Diagnostics.Stopwatch.StartNew();
                Assert.IsTrue(receiver.TryApplyVirtualChartSubset(request).WasCommitted);
                LibraryChartRow[] finalRows = table.Rows.Cast<LibraryChartRow>().ToArray();
                Assert.AreEqual("M", finalRows[0].Title);
                Assert.AreEqual("Z", finalRows[1].Title);
                Assert.AreSame(first, finalRows[1].PackageEntry);
                Assert.AreEqual("A", before.Title);
            }
            finally
            {
                producer.ClearInstalledPackages();
            }
        });
    }

    private static void IListRowsForNotification(MainChartListViewModel table, PackageChartEntry entry)
    {
        table.ApplyRows(new MainChartListRowsApplyRequest
        {
            Rows = new System.Collections.Generic.List<LibraryChartRow> { LibraryChartRow.FromPackageChartEntry(entry) },
            ColumnsSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD),
            SelectionPolicy = MainChartListSelectionPolicy.Reset,
            Summary = MainChartListSummaryUpdate.NormalCounts(1, 1),
            Stopwatch = System.Diagnostics.Stopwatch.StartNew()
        });
    }

    [TestMethod]
    public void AttachNormalLibraryRefreshSource_CatchesUpOnceAndSuppressesDuplicate()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath);
            library.BmsCharts = [CreateTestableBmsFile("C:\\Charts\\catch-up.bms")];
            RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), CreateWorkspaceForOwner());
            RegularMaterializedChartListApplyResult materialized = owner.TryApplyMaterialized(
                CreateMaterializedApplyRequest(
                [
                    LibraryChartRow.FromChartFile(CreateSourceRow("Old", "old.bms").Chart)
                ]));
            Assert.IsTrue(materialized.WasCommitted);
            Assert.IsTrue(owner.HasFolderRows);
            var applied = new List<NormalLibraryRefreshAppliedEventArgs>();
            owner.NormalLibraryRefreshApplied += (_, args) => applied.Add(args);
            try
            {
                owner.AttachNormalLibraryRefreshSource(library);

                Assert.AreEqual(1, applied.Count);
                Assert.AreEqual("normal_library_refresh", applied[0].Reason);
                Assert.IsTrue(applied[0].NotificationBatch.ResetsPriorNotifications);
                Assert.IsTrue(applied[0].NotificationBatch.HasEffect(LibraryChartRefreshEffects.SourceChanged));
                Assert.IsTrue(owner.SourceGeneration > 0);
                Assert.AreEqual(
                    applied[0].NotificationBatch.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged),
                    owner.WarningGeneration > 0);
                Assert.AreEqual(
                    applied[0].NotificationBatch.HasEffect(LibraryChartRefreshEffects.InstallDestinationOverlayChanged)
                        && !applied[0].NotificationBatch.HasEffect(LibraryChartRefreshEffects.SourceChanged),
                    owner.InstallDestinationGeneration > 0);
                Assert.IsFalse(owner.HasFolderRows);
                Assert.AreEqual(1, applied.Count);
            }
            finally
            {
                owner.Dispose();
            }
        });
    }


    [TestMethod]
    public void AttachedNormalLibraryRefreshSource_AppliesPublishedStorageReplacement()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath);
            RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), CreateWorkspaceForOwner());
            using var applied = new ManualResetEventSlim();
            int appliedCount = 0;
            owner.NormalLibraryRefreshApplied += (_, args) =>
            {
                if (args.NotificationBatch.ResetsPriorNotifications)
                {
                    Interlocked.Increment(ref appliedCount);
                    applied.Set();
                }
            };
            try
            {
                owner.AttachNormalLibraryRefreshSource(library);
                library.BmsCharts = [CreateTestableBmsFile("C:\\Charts\\published.bms")];

                applied.Wait();
                Assert.AreEqual(1, Volatile.Read(ref appliedCount));
                Assert.AreEqual(1, Volatile.Read(ref appliedCount));
            }
            finally
            {
                owner.Dispose();
            }
        });
    }


    [TestMethod]
    public void StopAsync_DrainsInFlightNormalLibraryRefreshApplication()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath);
            RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), CreateWorkspaceForOwner());
            owner.AttachNormalLibraryRefreshSource(library);
            using var applyEntered = new ManualResetEventSlim();
            using var releaseApply = new ManualResetEventSlim();
            using var stopStarted = new ManualResetEventSlim();
            using var stopCompleted = new ManualResetEventSlim();
            int appliedCount = 0;
            owner.NormalLibraryRefreshApplied += (_, args) =>
            {
                if (!args.NotificationBatch.ResetsPriorNotifications)
                {
                    return;
                }
                Interlocked.Increment(ref appliedCount);
                applyEntered.Set();
                releaseApply.Wait();
            };

            Task mutationTask = StartLongRunning(() =>
            {
                library.BmsCharts = [CreateTestableBmsFile("C:\\Charts\\in-flight.bms")];
            });
            applyEntered.Wait();

            Task stopTask = StartLongRunningAsync(async delegate
            {
                stopStarted.Set();
                await owner.StopAsync();
                stopCompleted.Set();
            });
            stopStarted.Wait();
            Assert.IsFalse(stopCompleted.IsSet);

            releaseApply.Set();
            Task.WaitAll(mutationTask, stopTask);
            Assert.AreEqual(1, Volatile.Read(ref appliedCount));
        });
    }


    [TestMethod]
    public void AttachedNormalLibraryRefreshSource_InvalidatesMaintenanceDependency()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string chartPath = Path.Combine(Path.GetDirectoryName(songDbPath)!, "maintenance.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE maintenance\r\n");
            ChartFile file = CreateTestableBmsFile(chartPath);
            file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(new LR2SongDBExtended.maintenance { path = file.Path, hash = file.Md5, wav_files_defined = 2, wav_files_existing = 1 }));
            var library = new TestBmsLibrary(songDbPath)
            {
                BmsCharts = [file]
            };
            RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), CreateWorkspaceForOwner());
            owner.AttachNormalLibraryRefreshSource(library);
            long maintenanceGeneration = owner.MaintenanceGeneration;
            using var applied = new ManualResetEventSlim();
            owner.NormalLibraryRefreshApplied += (_, args) =>
            {
                if (args.NotificationBatch.HasEffect(LibraryChartRefreshEffects.MaintenancePresentationChanged))
                {
                    applied.Set();
                }
            };
            try
            {
                MaintenanceWorkflowResult result = library.RescanResourceHealthCharts(
                    [(file)]);

                Assert.IsTrue(result.HasUpdates);
                applied.Wait();
                Assert.AreEqual(maintenanceGeneration + 1, owner.MaintenanceGeneration);
            }
            finally
            {
                owner.Dispose();
            }
        });
    }


    [TestMethod]
    public void AttachedNormalLibraryRefreshSource_MarshalsApplyToExecutionLane()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath);
            library.BmsCharts = [CreateTestableBmsFile("C:\\Charts\\queued.bms")];
            var pendingActions = new Queue<Action>();
            RegularChartListOwner owner = CreateOwner(
                new MainChartListViewModel(),
                CreateWorkspaceForOwner(),
                action => action(),
                new ActionQueueUiScheduler(pendingActions.Enqueue));
            int appliedCount = 0;
            owner.NormalLibraryRefreshApplied += (_, _) => appliedCount++;
            try
            {
                owner.AttachNormalLibraryRefreshSource(library);

                Assert.AreEqual(1, pendingActions.Count);
                Assert.AreEqual(0, appliedCount);
                Assert.AreEqual(0L, owner.SourceGeneration);

                pendingActions.Dequeue()();

                Assert.AreEqual(1, appliedCount);
                Assert.IsTrue(owner.SourceGeneration > 0);
            }
            finally
            {
                owner.Dispose();
            }
        });
    }


    [TestMethod]
    public void AttachedNormalLibraryRefreshSource_DoesNotWaitForUiWhileCatalogWriterHeld()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath);
            var uiScheduler = new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher);
            using var notificationPublished = new ManualResetEventSlim();
            using var applied = new ManualResetEventSlim();

            RegularChartListOwner owner = CreateOwner(
                new MainChartListViewModel(),
                CreateWorkspaceForOwner(),
                action => action(),
                uiScheduler);
            owner.AttachNormalLibraryRefreshSource(library);
            owner.NormalLibraryRefreshApplied += (_, args) =>
            {
                if (args.NotificationBatch.ResetsPriorNotifications)
                {
                    // 表示担当が通常一覧の正式な捕捉入口を読む間も、通知生産側はUIの完了を待ちません。
                    library.CreateNormalLibrarySourceChartView();
                    applied.Set();
                }
            };

            ReaderWriterLockSlimWrapper catalogWriteGate = GetCatalogStorageRowsWriteGate(library);
            Task producer;
            bool notificationPublishedWhileWriterHeld;
            bool uiReachedCatalogReader;
            using (catalogWriteGate.GetWriterGuard())
            {
                producer = StartLongRunning(() =>
                {
                    PublishNormalLibraryRefreshResetNotification(
                        library,
                        notifiesBmsFiles: true,
                        notifiesBmsonSongs: false);
                    notificationPublished.Set();
                });
                notificationPublishedWhileWriterHeld =
                    notificationPublished.Wait(TimeSpan.FromSeconds(10));
                uiReachedCatalogReader = SpinWait.SpinUntil(
                    () => catalogWriteGate.WaitingReadCount > 0,
                    TimeSpan.FromSeconds(10));
            }
            try
            {
                producer.Wait();
                Assert.IsTrue(
                    notificationPublishedWhileWriterHeld,
                    "The producer waited synchronously for the UI lane while a catalog writer was held.");
                Assert.IsTrue(
                    uiReachedCatalogReader,
                    "The dedicated UI lane did not reach the catalog snapshot reader.");
                producer.GetAwaiter().GetResult();
                applied.Wait();
                owner.StopAsync().GetAwaiter().GetResult();
            }
            finally
            {
                owner.Dispose();
            }
        });
    }


    [TestMethod]
    public void StopAsync_QueuedNormalLibraryRefreshBecomesNoOp()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath);
            library.BmsCharts = [CreateTestableBmsFile("C:\\Charts\\queued-stop.bms")];
            var pendingActions = new Queue<Action>();
            RegularChartListOwner owner = CreateOwner(
                new MainChartListViewModel(),
                CreateWorkspaceForOwner(),
                action => action(),
                new ActionQueueUiScheduler(pendingActions.Enqueue));
            int appliedCount = 0;
            owner.NormalLibraryRefreshApplied += (_, _) => appliedCount++;

            owner.AttachNormalLibraryRefreshSource(library);
            Assert.AreEqual(1, pendingActions.Count);

            Task stopTask = owner.StopAsync();
            Assert.IsFalse(stopTask.IsCompleted);
            pendingActions.Dequeue()();
            stopTask.GetAwaiter().GetResult();

            Assert.AreEqual(0, appliedCount);
            Assert.AreEqual(0L, owner.SourceGeneration);
        });
    }


    [TestMethod]
    public void StopAsync_AcceptedButAbortedNormalLibraryRefreshDoesNotHang()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath)
            {
                BmsCharts = [CreateTestableBmsFile("C:\\Charts\\aborted-refresh.bms")]
            };
            RegularChartListOwner owner = CreateOwner(
                new MainChartListViewModel(),
                CreateWorkspaceForOwner(),
                action => action(),
                new AbortingUiScheduler());

            owner.AttachNormalLibraryRefreshSource(library);

            owner.StopAsync().Wait();
        });
    }


    [TestMethod]
    public void AttachedNormalLibraryRefreshSource_ApplyFailureDoesNotBlockLaterVersion()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath);
            using RegularChartListOwner owner = CreateOwner(
                new MainChartListViewModel(),
                CreateWorkspaceForOwner());
            owner.AttachNormalLibraryRefreshSource(library);
            EventHandler<NormalLibraryRefreshAppliedEventArgs> failingHandler =
                (_, _) => throw new InvalidOperationException("injected refresh apply failure");
            owner.NormalLibraryRefreshApplied += failingHandler;

            library.BmsCharts = [CreateTestableBmsFile("C:\\Charts\\failed-refresh.bms")];
            owner.NormalLibraryRefreshApplied -= failingHandler;

            int appliedCount = 0;
            owner.NormalLibraryRefreshApplied += (_, args) =>
            {
                if (args.NotificationBatch.ResetsPriorNotifications)
                {
                    appliedCount++;
                }
            };
            library.BmsCharts = [CreateTestableBmsFile("C:\\Charts\\recovered-refresh.bms")];

            Assert.AreEqual(1, appliedCount);
        });
    }
}
