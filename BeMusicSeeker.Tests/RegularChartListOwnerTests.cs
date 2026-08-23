using System;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class RegularChartListOwnerTests
{
    [TestMethod]
    public async Task InstlDstCellEdit_UsesPendingOwnerWithExactChartTargetAndText()
    {
        var table = new MainChartListViewModel(action => action());
        PlaylistWorkspaceViewModel workspace = CreateWorkspaceForOwner(table);
        var store = new PendingPackageWorkflowOwnerTests.RecordingStore([]);
        BMSLibrary library = (BMSLibrary)FormatterServices.GetUninitializedObject(typeof(BMSLibrary));
        var pendingOwner = new PendingPackageWorkflowOwner(
            () => library,
            new ChartFileOperationSynchronizer(),
            new ChartMutationActivityOwner(),
            new NoOpPendingPackageMutationPlaybackPort(),
            new TestUiDialogService(),
            () => new InstallDestinationWorkflowSettingsSnapshot(
                showManualInstallConfirmation: false,
                deletePendingPackageSourceAfterInstall: false),
            ExternalShellGatewayPolicy.Current,
            store);
        var workflowCompletion = new TaskCompletionSource<PendingPackageMutationAppliedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        pendingOwner.WorkflowChanged += (_, change) =>
        {
            if (change is PendingPackageMutationAppliedEventArgs mutation)
            {
                workflowCompletion.TrySetResult(mutation);
            }
        };

        using RegularChartListOwner owner = CreateOwner(
            table,
            workspace,
            action => action(),
            pendingPackageWorkflow: pendingOwner);
        MainChartListCellEditContext? beginning = null;
        table.CellEditBeginningRequested += (_, request) =>
        {
            beginning = request.Context;
            request.Accepted = owner.CanBeginCellEdit(request.Context);
        };
        table.CellEditEndedRequested += (_, request) => owner.CompleteCellEdit(request);

        var file = new BMSFile
        {
            path = @"C:\wave6e-owner-chain\pending.bms",
            hash = "ffffffffffffffffffffffffffffffff",
            title = "Owner chain chart"
        };
        PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(file));
        store.SetResult = entry.Chart;
        LibraryChartRow row = LibraryChartRow.FromPackageChartEntry(entry);
        const string destination = @"C:\wave6e-owner-chain\destination";

        table.SetOperationContext(MainViewUpdateMode.PendingInstallFolderSelected);
        Assert.IsTrue(table.TryBeginCellEdit(row, "instl_dst"));
        Assert.IsNotNull(beginning);
        Assert.AreSame(row, beginning.Row);
        Assert.AreEqual(MainViewOperationSection.InstallPending, beginning.OperationSection);
        Assert.AreEqual(ChartOperationSourceScope.PendingPackage, beginning.SourceScope);

        table.NotifyCellEditStarted(row, "instl_dst");
        table.RequestCellEditEnded(row, "instl_dst", destination, commit: true);
        PendingPackageMutationAppliedEventArgs applied = await workflowCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsNotNull(store.LastPendingRequest);
        Assert.AreSame(entry, store.LastPendingRequest!.PackageEntry);
        Assert.AreSame(entry, store.LastPendingRequest.GetOrCreateChartEntry());
        Assert.AreEqual(destination, store.LastDestinationDirectory);
        Assert.AreEqual(1, applied.ChangedCharts.Count);
        Assert.AreSame(file, applied.ChangedCharts[0].GetBmsStorageOwner());
    }

    [TestMethod]
    public void NewOwner_DerivedCachesAreInvalidUntilFirstCommit()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));

        Assert.IsFalse(owner.HasFolderRows);
        Assert.IsFalse(owner.HasKeywordRows);
        Assert.IsFalse(owner.HasModeRows);
    }

    [TestMethod]
    public void NavigateTree_OwnsFilterIdentitySummaryTransitionAndRefreshRequest()
    {
        PlaylistWorkspaceViewModel workspace = CreateWorkspaceForOwner();
        using RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), workspace);
        var presentations = new List<(string? FilterIdentity, bool KeywordRefresh, MainViewUpdateMode RefreshMode)>();
        owner.TreeNavigationPresentationRequested += (_, request) => presentations.Add((
            owner.CaptureTreeFilter(enabled: true)?.Identity,
            request.KeywordPresentationRefreshRequired,
            request.RefreshMode));

        Assert.IsTrue(owner.NavigateTree(
            RegularChartFolderFilterKind.Directory,
            @"C:\Charts\Folder A"));
        Assert.AreEqual(1, presentations.Count);
        Assert.AreEqual(
            "directory:" + @"C:\Charts\Folder A" + Path.DirectorySeparatorChar,
            presentations[0].FilterIdentity);
        Assert.IsFalse(presentations[0].KeywordRefresh);
        Assert.AreEqual(MainViewUpdateMode.FolderFilterSelected, presentations[0].RefreshMode);

        workspace.SetPlaylistSummaryMode(enabled: true);
        Assert.IsTrue(owner.NavigateTree(RegularChartFolderFilterKind.Artist, "Artist A"));
        Assert.AreEqual("artist:Artist A", presentations[1].FilterIdentity);
        Assert.IsTrue(presentations[1].KeywordRefresh);
        Assert.IsTrue(workspace.IsPlaylistSummaryMode);
        Assert.IsFalse(workspace.IsPlaylistSummaryModeRequested);

        Assert.IsTrue(owner.NavigateTree(filterKind: null));
        Assert.IsNull(presentations[2].FilterIdentity);
        Assert.IsTrue(presentations[2].KeywordRefresh);

        workspace.SetPlaylistSummaryMode(enabled: true);
        Assert.IsFalse(owner.NavigateTree((RegularChartFolderFilterKind)999, "invalid"));
        Assert.AreEqual(3, presentations.Count);
        Assert.IsTrue(workspace.IsPlaylistSummaryMode);
        Assert.IsNull(owner.CaptureTreeFilter(enabled: true));
    }

    [TestMethod]
    public void NavigateMaintenance_OwnsSummaryTransitionAndAllMaintenanceRefreshRequests()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath)
            {
                DuplicateChartGroups = []
            };
            PlaylistWorkspaceViewModel workspace = CreateWorkspaceForOwner();
            using RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), workspace);
            owner.AttachNormalLibraryRefreshSource(library);
            var presentations = new List<RegularChartMaintenanceNavigationPresentationRequestedEventArgs>();
            owner.MaintenanceNavigationPresentationRequested += (_, request) => presentations.Add(request);
            MainViewUpdateMode[] modes =
            [
                MainViewUpdateMode.FullScanAllChartsFilterSelected,
                MainViewUpdateMode.FileMissingFilterSelected,
                MainViewUpdateMode.FileMissingIgnoredFilterSelected,
                MainViewUpdateMode.DuplicateFilterSelected,
                MainViewUpdateMode.GarbledFilterSelected,
                MainViewUpdateMode.GarbleFixedFilterSelected,
                MainViewUpdateMode.UnregisteredFilterSelected,
                MainViewUpdateMode.ZeroNoteFilterSelected,
                MainViewUpdateMode.ChartInfoParseErrorFilterSelected
            ];

            workspace.SetPlaylistSummaryMode(enabled: true);
            for (int index = 0; index < modes.Length; index++)
            {
                object? parameter = index == 3 ? "duplicate-folder" : null;
                int presentationCountBefore = presentations.Count;

                Assert.IsTrue(owner.NavigateMaintenance(modes[index], parameter, "test_navigation"));

                RegularChartMaintenanceNavigationPresentationRequestedEventArgs request = presentations[^1];
                Assert.AreEqual(modes[index], request.Mode);
                Assert.AreSame(parameter, request.Parameter);
                Assert.AreEqual(index == 3 ? 2 : 1, presentations.Count - presentationCountBefore);
                Assert.AreEqual(index != 3, request.KeywordPresentationRefreshRequired);
                Assert.IsTrue(request.RefreshRequested);
            }
            Assert.IsTrue(workspace.IsPlaylistSummaryMode);
            Assert.IsFalse(workspace.IsPlaylistSummaryModeRequested);

            int presentationCount = presentations.Count;
            workspace.SetPlaylistSummaryMode(enabled: true);
            Assert.IsFalse(owner.NavigateMaintenance(MainViewUpdateMode.SortUpdated));
            Assert.AreEqual(presentationCount, presentations.Count);
            Assert.IsTrue(workspace.IsPlaylistSummaryMode);
        });
    }

    [TestMethod]
    public void NavigateMaintenance_WithoutLibraryStillPublishesOwnedSummaryTransition()
    {
        PlaylistWorkspaceViewModel workspace = CreateWorkspaceForOwner();
        using RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), workspace);
        var presentations = new List<RegularChartMaintenanceNavigationPresentationRequestedEventArgs>();
        owner.MaintenanceNavigationPresentationRequested += (_, request) => presentations.Add(request);
        workspace.SetPlaylistSummaryMode(enabled: true);

        Assert.IsTrue(owner.NavigateMaintenance(MainViewUpdateMode.FileMissingFilterSelected));

        Assert.AreEqual(1, presentations.Count);
        Assert.AreEqual(MainViewUpdateMode.FileMissingFilterSelected, presentations[0].Mode);
        Assert.IsTrue(presentations[0].KeywordPresentationRefreshRequired);
        Assert.IsFalse(presentations[0].RefreshRequested);
        Assert.IsTrue(workspace.IsPlaylistSummaryMode);
        Assert.IsFalse(workspace.IsPlaylistSummaryModeRequested);

        Assert.IsTrue(owner.NavigateMaintenance(MainViewUpdateMode.ZeroNoteFilterSelected));
        Assert.AreEqual(2, presentations.Count);
        Assert.IsTrue(presentations[1].KeywordPresentationRefreshRequired);
        Assert.IsFalse(presentations[1].RefreshRequested);
    }

    [TestMethod]
    public void NavigateMaintenance_DuplicateModeBuildsCacheBeforeRefreshPresentation()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles =
                [
                    CreateTestableBmsFile(@"C:\Charts\duplicate-a.bms"),
                    CreateTestableBmsFile(@"C:\Charts\duplicate-b.bms")
                ]
            };
            using RegularChartListOwner owner = CreateOwner(
                new MainChartListViewModel(),
                CreateWorkspaceForOwner());
            owner.AttachNormalLibraryRefreshSource(library);
            var cacheWasReadyAtPresentation = new List<bool>();
            owner.MaintenanceNavigationPresentationRequested += (_, request) =>
            {
                if (request.RefreshRequested)
                {
                    cacheWasReadyAtPresentation.Add(library.DuplicateChartGroups != null);
                }
            };

            Assert.IsTrue(owner.NavigateMaintenance(
                MainViewUpdateMode.DuplicateFilterSelected,
                parameter: "duplicate-folder",
                reason: "test_duplicate_navigation"));

            Assert.IsNotNull(library.DuplicateChartGroups);
            Assert.AreEqual(1, library.DuplicateChartGroups.Count);
            CollectionAssert.AreEqual(new[] { true }, cacheWasReadyAtPresentation);
        });
    }

    [TestMethod]
    public void NavigateInstall_OwnsSummaryTransitionAndPackageRefreshRequests()
    {
        PlaylistWorkspaceViewModel workspace = CreateWorkspaceForOwner();
        using RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), workspace);
        var presentations = new List<RegularChartInstallNavigationPresentationRequestedEventArgs>();
        owner.InstallNavigationPresentationRequested += (_, request) => presentations.Add(request);
        var installedPackage = new ChartPackage { path = @"C:\Installed\Package" };
        var pendingPackage = new ChartPackage { path = @"C:\Pending\Package" };

        workspace.SetPlaylistSummaryMode(enabled: true);
        Assert.IsTrue(owner.NavigateInstall(MainViewUpdateMode.NewlyInstalledFolderSelected));
        Assert.AreEqual(MainViewUpdateMode.NewlyInstalledFolderSelected, presentations[0].Mode);
        Assert.IsNull(presentations[0].Parameter);
        Assert.IsTrue(presentations[0].KeywordPresentationRefreshRequired);

        Assert.IsTrue(owner.NavigateInstall(
            MainViewUpdateMode.NewlyInstalledFolderSelected,
            installedPackage));
        Assert.AreSame(installedPackage, presentations[1].Parameter);
        Assert.IsTrue(presentations[1].KeywordPresentationRefreshRequired);

        Assert.IsTrue(owner.NavigateInstall(MainViewUpdateMode.PendingInstallFolderSelected));
        Assert.AreEqual(MainViewUpdateMode.PendingInstallFolderSelected, presentations[2].Mode);
        Assert.IsNull(presentations[2].Parameter);

        Assert.IsTrue(owner.NavigateInstall(
            MainViewUpdateMode.PendingInstallFolderSelected,
            pendingPackage));
        Assert.AreSame(pendingPackage, presentations[3].Parameter);

        int presentationCount = presentations.Count;
        workspace.SetPlaylistSummaryMode(enabled: true);
        Assert.IsFalse(owner.NavigateInstall(MainViewUpdateMode.FileMissingFilterSelected));
        Assert.AreEqual(presentationCount, presentations.Count);
        Assert.IsTrue(workspace.IsPlaylistSummaryMode);
    }

    [TestMethod]
    public void AttachNormalLibraryRefreshSource_CatchesUpOnceAndSuppressesDuplicate()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath);
            library.BMSFiles = [CreateTestableBmsFile("C:\\Charts\\catch-up.bms")];
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
                Assert.IsTrue(applied[0].NotificationBatch.NotifiesBmsFiles);
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
                if (args.NotificationBatch.NotifiesBmsFiles)
                {
                    Interlocked.Increment(ref appliedCount);
                    applied.Set();
                }
            };
            try
            {
                owner.AttachNormalLibraryRefreshSource(library);
                library.BMSFiles = [CreateTestableBmsFile("C:\\Charts\\published.bms")];

                Assert.IsTrue(applied.Wait(TimeSpan.FromSeconds(10)));
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
                if (!args.NotificationBatch.NotifiesBmsFiles)
                {
                    return;
                }
                Interlocked.Increment(ref appliedCount);
                applyEntered.Set();
                Assert.IsTrue(releaseApply.Wait(TimeSpan.FromSeconds(10)));
            };

            Task mutationTask = StartLongRunning(() =>
            {
                library.BMSFiles = [CreateTestableBmsFile("C:\\Charts\\in-flight.bms")];
            });
            Assert.IsTrue(applyEntered.Wait(TimeSpan.FromSeconds(10)));

            Task stopTask = StartLongRunningAsync(async delegate
            {
                stopStarted.Set();
                await owner.StopAsync();
                stopCompleted.Set();
            });
            Assert.IsTrue(stopStarted.Wait(TimeSpan.FromSeconds(10)));
            Assert.IsFalse(stopCompleted.IsSet);

            releaseApply.Set();
            Task.WaitAll(mutationTask, stopTask);
            Assert.AreEqual(1, Volatile.Read(ref appliedCount));
        });
    }

    [TestMethod]
    public void StopAsync_DrainsInFlightFolderRename()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string libraryRoot = Path.GetDirectoryName(songDbPath)!;
            string sourceDirectory = Path.Combine(libraryRoot, "rename-source");
            string chartPath = Path.Combine(sourceDirectory, "chart.bms");
            Directory.CreateDirectory(sourceDirectory);
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE shutdown\r\n");
            var file = CreateTestableBmsFile(chartPath);
            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = [file]
            };
            ChartFile chart = ChartFileProjection.FromBmsFile(file);
            var target = new ChartOperationTarget(
                chart,
                playlistEntry: null,
                ChartOperationSourceScope.Library,
                isOwned: true,
                isPending: false,
                isPlaylistMissing: false,
                ChartOperationCapabilities.MoveInLibrary);
            Assert.IsTrue(RenameChartFolderRequest.TryCreate(target, out RenameChartFolderRequest request));

            int appliedCount = 0;
            using RegularChartListOwner owner = CreateOwner(
                new MainChartListViewModel(),
                CreateWorkspaceForOwner(),
                action => action());
            owner.AttachNormalLibraryRefreshSource(library);
            owner.NormalLibraryRefreshApplied += (_, args) =>
            {
                if (args.NotificationBatch.HasRefreshNotification)
                {
                    Interlocked.Increment(ref appliedCount);
                }
            };
            using var suppressionEntered = new ManualResetEventSlim();
            using var releaseSuppression = new ManualResetEventSlim();
            owner.RefreshSuppressionChanged += (_, args) =>
            {
                if (!args.IsSuppressed)
                {
                    suppressionEntered.Set();
                    releaseSuppression.Wait(TimeSpan.FromSeconds(10));
                }
            };

            owner.RenameChartFolderAsync(request, "rename-destination");
            Assert.IsTrue(suppressionEntered.Wait(TimeSpan.FromSeconds(10)));

            using var stopStarted = new ManualResetEventSlim();
            Task stopTask = StartLongRunningAsync(async delegate
            {
                stopStarted.Set();
                await owner.StopAsync();
            });
            Assert.IsTrue(stopStarted.Wait(TimeSpan.FromSeconds(10)));
            Assert.IsFalse(stopTask.Wait(TimeSpan.FromMilliseconds(250)));

            releaseSuppression.Set();
            stopTask.GetAwaiter().GetResult();
            Assert.IsFalse(Directory.Exists(sourceDirectory));
            Assert.IsTrue(Directory.Exists(Path.Combine(libraryRoot, "rename-destination")));
            Assert.AreEqual(1, Volatile.Read(ref appliedCount), "The mutation notification must apply once after the gate is released.");
        });
    }

    [TestMethod]
    public void FolderRenames_SerializeMutationsWithoutWaitingForRefreshDrain()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string libraryRoot = Path.GetDirectoryName(songDbPath)!;
            string firstSourceDirectory = Path.Combine(libraryRoot, "first-source");
            string secondSourceDirectory = Path.Combine(libraryRoot, "second-source");
            Directory.CreateDirectory(firstSourceDirectory);
            Directory.CreateDirectory(secondSourceDirectory);
            string firstChartPath = Path.Combine(firstSourceDirectory, "first.bms");
            string secondChartPath = Path.Combine(secondSourceDirectory, "second.bms");
            File.WriteAllText(firstChartPath, "#PLAYER 1\r\n#TITLE first\r\n");
            File.WriteAllText(secondChartPath, "#PLAYER 1\r\n#TITLE second\r\n");
            var firstFile = CreateTestableBmsFile(firstChartPath);
            var secondFile = CreateTestableBmsFile(secondChartPath);
            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = [firstFile, secondFile]
            };
            RenameChartFolderRequest firstRequest = CreateRenameRequest(firstFile);
            RenameChartFolderRequest secondRequest = CreateRenameRequest(secondFile);
            var pendingActions = new Queue<Action>();
            using var actionQueued = new ManualResetEventSlim();
            using RegularChartListOwner owner = CreateOwner(
                new MainChartListViewModel(),
                CreateWorkspaceForOwner(),
                action => action(),
                new ActionQueueUiScheduler(action =>
                {
                    lock (pendingActions)
                    {
                        pendingActions.Enqueue(action);
                    }
                    actionQueued.Set();
                }));
            owner.AttachNormalLibraryRefreshSource(library);
            Assert.IsTrue(actionQueued.Wait(TimeSpan.FromSeconds(10)));
            Action catchUp;
            lock (pendingActions)
            {
                Assert.AreEqual(1, pendingActions.Count);
                catchUp = pendingActions.Dequeue();
            }
            catchUp();
            actionQueued.Reset();

            Task firstRename = owner.RenameChartFolderAsync(firstRequest, "first-destination");
            Assert.IsTrue(actionQueued.Wait(TimeSpan.FromSeconds(10)));
            Task secondRename = owner.RenameChartFolderAsync(secondRequest, "second-destination");
            Assert.IsTrue(firstRename.Wait(TimeSpan.FromSeconds(10)));
            Assert.IsTrue(secondRename.Wait(TimeSpan.FromSeconds(10)));
            firstRename.GetAwaiter().GetResult();
            secondRename.GetAwaiter().GetResult();

            Assert.IsTrue(Directory.Exists(Path.Combine(libraryRoot, "first-destination")));
            Assert.IsTrue(Directory.Exists(Path.Combine(libraryRoot, "second-destination")));
            Action coalescedRefresh;
            lock (pendingActions)
            {
                Assert.AreEqual(1, pendingActions.Count);
                coalescedRefresh = pendingActions.Dequeue();
            }
            coalescedRefresh();
            owner.StopAsync().GetAwaiter().GetResult();
        });
    }

    [TestMethod]
    public void FolderRename_StopAsyncDrainsQueuedRefresh()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string libraryRoot = Path.GetDirectoryName(songDbPath)!;
            string sourceDirectory = Path.Combine(libraryRoot, "queued-source");
            string chartPath = Path.Combine(sourceDirectory, "queued.bms");
            Directory.CreateDirectory(sourceDirectory);
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE queued\r\n");
            var file = CreateTestableBmsFile(chartPath);
            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = [file]
            };
            RenameChartFolderRequest request = CreateRenameRequest(file);
            var pendingActions = new Queue<Action>();
            using var actionQueued = new ManualResetEventSlim();
            using RegularChartListOwner owner = CreateOwner(
                new MainChartListViewModel(),
                CreateWorkspaceForOwner(),
                action => action(),
                new ActionQueueUiScheduler(action =>
                {
                    lock (pendingActions)
                    {
                        pendingActions.Enqueue(action);
                    }
                    actionQueued.Set();
                }));
            owner.AttachNormalLibraryRefreshSource(library);
            Assert.IsTrue(actionQueued.Wait(TimeSpan.FromSeconds(10)));
            Action catchUp;
            lock (pendingActions)
            {
                Assert.AreEqual(1, pendingActions.Count);
                catchUp = pendingActions.Dequeue();
            }
            catchUp();
            actionQueued.Reset();

            Task renameTask = owner.RenameChartFolderAsync(request, "queued-destination");
            Assert.IsTrue(actionQueued.Wait(TimeSpan.FromSeconds(10)));
            Task stopTask = owner.StopAsync();
            Assert.IsFalse(stopTask.Wait(TimeSpan.FromMilliseconds(250)));

            Action pendingAction;
            lock (pendingActions)
            {
                Assert.AreEqual(1, pendingActions.Count);
                pendingAction = pendingActions.Dequeue();
            }
            pendingAction();
            stopTask.GetAwaiter().GetResult();
            renameTask.GetAwaiter().GetResult();
            Assert.IsTrue(Directory.Exists(Path.Combine(libraryRoot, "queued-destination")));
        });
    }

    [TestMethod]
    public void AttachedNormalLibraryRefreshSource_InvalidatesMaintenanceDependency()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string chartPath = Path.Combine(Path.GetDirectoryName(songDbPath)!, "maintenance.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE maintenance\r\n");
            TestableBmsFile file = CreateTestableBmsFile(chartPath);
            file.SetMaintenanceInfo(new BMSFileMaintenanceInfo(file)
            {
                hash = file.hash,
                wav_files_defined = 2,
                wav_files_existing = 1
            });
            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = [file]
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
                    [ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false)]);

                Assert.IsTrue(result.HasUpdates);
                Assert.IsTrue(applied.Wait(TimeSpan.FromSeconds(10)));
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
            library.BMSFiles = [CreateTestableBmsFile("C:\\Charts\\queued.bms")];
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
                if (args.NotificationBatch.NotifiesBmsFiles)
                {
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
                Assert.IsTrue(producer.Wait(TimeSpan.FromSeconds(10)));
                Assert.IsTrue(
                    notificationPublishedWhileWriterHeld,
                    "The producer waited synchronously for the UI lane while a catalog writer was held.");
                Assert.IsTrue(
                    uiReachedCatalogReader,
                    "The dedicated UI lane did not reach the catalog snapshot reader.");
                producer.GetAwaiter().GetResult();
                Assert.IsTrue(applied.Wait(TimeSpan.FromSeconds(10)));
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
            library.BMSFiles = [CreateTestableBmsFile("C:\\Charts\\queued-stop.bms")];
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
                BMSFiles = [CreateTestableBmsFile("C:\\Charts\\aborted-refresh.bms")]
            };
            RegularChartListOwner owner = CreateOwner(
                new MainChartListViewModel(),
                CreateWorkspaceForOwner(),
                action => action(),
                new AbortingUiScheduler());

            owner.AttachNormalLibraryRefreshSource(library);

            Assert.IsTrue(owner.StopAsync().Wait(TimeSpan.FromSeconds(10)));
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

            library.BMSFiles = [CreateTestableBmsFile("C:\\Charts\\failed-refresh.bms")];
            owner.NormalLibraryRefreshApplied -= failingHandler;

            int appliedCount = 0;
            owner.NormalLibraryRefreshApplied += (_, args) =>
            {
                if (args.NotificationBatch.NotifiesBmsFiles)
                {
                    appliedCount++;
                }
            };
            library.BMSFiles = [CreateTestableBmsFile("C:\\Charts\\recovered-refresh.bms")];

            Assert.AreEqual(1, appliedCount);
        });
    }

    [TestMethod]
    public void ApplyRegularView_NestedNewerRequestKeepsLatestRetirementReceipt()
    {
        var table = new MainChartListViewModel();
        var buildState = new PlaylistDetailBuildState
        {
            RequestVersion = 1,
            PendingRequest = new PlaylistBuildRequest(),
            CurrentBuildRequest = new PlaylistBuildRequest(),
            CurrentBuildCancellation = new CancellationTokenSource()
        };
        var sourceRows = new List<PlaylistDetailSourceRow> { CreatePlaylistSourceRow("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa") };
        var viewRows = new List<object> { new PlaylistDetailRow(sourceRows[0]) };
        var viewState = new PlaylistDetailViewState();
        viewState.Source.Rows = sourceRows;
        viewState.View.Rows = viewRows;
        var logs = new List<string>();
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            table,
            buildState,
            viewState,
            logs.Add,
            logs.Add,
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
        workspace.IsPlaylistDetailViewActive = true;
        var owner = new RegularChartListOwner(
            table,
            workspace,
            logs.Add,
            action => action(),
            logs.Add,
            CreatePendingPackageWorkflowOwner(),
            new ChartFileOperationSynchronizer(),
            new ChartMutationActivityOwner(),
            new NoOpFolderAutoRenamePlaybackPort(),
            new TestUiScheduler(() => null!));
        int? sourceClearVersionAtRowsNotification = null;
        bool? detailActiveAtRowsNotification = null;
        bool sourceWasRetainedAtPreparation = false;
        bool nestedStarted = false;
        RegularChartListEntryResult nestedResult = default;
        table.RowsReplacing += (_, _) =>
        {
            sourceWasRetainedAtPreparation =
                ReferenceEquals(sourceRows, viewState.Source.Rows)
                && ReferenceEquals(viewRows, viewState.View.Rows);
            if (!nestedStarted)
            {
                nestedStarted = true;
                nestedResult = owner.ApplyRegularView(
                    CreateEntryRequest(MainViewUpdateMode.FolderFilterSelected));
            }
        };
        table.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainChartListViewModel.Rows))
            {
                if (viewState.Source.Rows.Count == 0)
                {
                    sourceClearVersionAtRowsNotification = buildState.RequestVersion;
                }
                detailActiveAtRowsNotification = workspace.IsPlaylistDetailViewActive;
            }
        };

        RegularChartListEntryResult result = owner.ApplyRegularView(CreateEntryRequest(MainViewUpdateMode.FolderFilterSelected));

        Assert.IsFalse(result.WasCommitted);
        Assert.IsTrue(nestedResult.WasCommitted);
        Assert.IsTrue(sourceWasRetainedAtPreparation);
        Assert.AreEqual(3, buildState.RequestVersion);
        Assert.AreEqual(0, viewState.Source.Rows.Count);
        Assert.AreEqual(0, viewState.View.Rows.Count);
        Assert.IsNull(buildState.PendingRequest);
        Assert.IsNull(buildState.CurrentBuildRequest);
        Assert.AreEqual(MainViewUpdateMode.FolderFilterSelected, table.LastAppliedColumnMode);
        Assert.AreEqual(3, sourceClearVersionAtRowsNotification);
        Assert.AreEqual(false, detailActiveAtRowsNotification);
        Assert.IsFalse(workspace.IsPlaylistDetailViewActive);
        Assert.IsTrue(logs.Any(log => log.Contains("playlist_source_replace action=clear")));
    }

    [TestMethod]
    public void ApplyRegularView_RetentionLogFailureStillPublishesRegularModeBeforeRetirement()
    {
        var table = new MainChartListViewModel();
        var buildState = new PlaylistDetailBuildState { RequestVersion = 1 };
        var sourceRows = new List<PlaylistDetailSourceRow>
        {
            CreatePlaylistSourceRow("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
        };
        var viewState = new PlaylistDetailViewState();
        viewState.Source.Rows = sourceRows;
        viewState.View.Rows = new List<object> { new PlaylistDetailRow(sourceRows[0]) };
        var publicationOrder = new List<string>();
        PlaylistWorkspaceViewModel workspace = CreateWorkspaceForOwner(
            table,
            buildState,
            viewState,
            _ =>
            {
                publicationOrder.Add("retirement-log");
                throw new InvalidOperationException("retirement log failed");
            });
        workspace.IsPlaylistDetailViewActive = true;
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaylistWorkspaceViewModel.IsPlaylistDetailViewActive))
            {
                publicationOrder.Add("mode");
            }
        };
        using RegularChartListOwner owner = CreateOwner(table, workspace);

        Assert.ThrowsException<RegularChartListTerminalPublishException>(
            () => owner.ApplyRegularView(CreateEntryRequest(MainViewUpdateMode.FolderFilterSelected)));

        CollectionAssert.AreEqual(
            new[] { "mode", "retirement-log", "retirement-log" },
            publicationOrder);
        Assert.IsFalse(workspace.IsPlaylistDetailViewActive);
        Assert.AreEqual(0, viewState.Source.Rows.Count);
        Assert.AreEqual(0, viewState.View.Rows.Count);
    }

    [TestMethod]
    public void ApplyMainLibraryView_DelegatesMainLibraryRouteToRegularPipeline()
    {
        var seededRows = new List<object>
        {
            LibraryChartRow.FromChartFile(CreateSourceRow("Seed", "seed.bms").Chart)
        };
        var table = new MainChartListViewModel { Rows = seededRows };
        PlaylistWorkspaceViewModel workspace = CreateWorkspaceForOwner();
        using RegularChartListOwner owner = CreateOwner(table, workspace);
        var route = new ChartListRefreshRoute(
            ChartListRefreshRouteKind.ContinueMainLibrary,
            MainViewUpdateMode.FolderFilterSelected,
            MainViewUpdateMode.FolderFilterSelected,
            MainViewUpdateMode.FolderFilterSelected,
            isPlaylistTreeActive: false,
            includeBmsonRows: false);
        table.UpdateSummaryText(0, 0);
        string expectedSummary = table.SummaryText;
        table.UpdateSummaryText(9, 3);
        workspace.SetPlaylistSummaryMode(enabled: true);
        workspace.RequestPlaylistSummaryMode(enabled: false);
        RegularChartListRequestLease staleLease = owner.BeginRequest();
        RegularChartListBuildResult staleBuild = Build(
            owner,
            staleLease,
            [LibraryChartRow.FromChartFile(CreateSourceRow("Stale", "stale.bms").Chart)]);
        int rowsPropertyNotificationCount = 0;
        table.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainChartListViewModel.Rows))
            {
                rowsPropertyNotificationCount++;
            }
        };

        owner.PrepareForMainViewRefresh();

        RegularChartListTerminalResult staleCommit = owner.TryCommit(
            staleLease,
            CreateTerminalInput(staleBuild));

        Assert.IsFalse(staleCommit.WasCommitted);
        Assert.AreSame(seededRows, table.Rows);

        RegularChartListEntryResult result = owner.ApplyMainLibraryView(
            route,
            library: null,
            parameter: null,
            treeParameter: null,
            preserveSummary: workspace.IsPlaylistSummaryModeRequested,
            Stopwatch.StartNew());

        Assert.IsTrue(result.WasCommitted);
        Assert.AreEqual(RegularChartListEntryRoute.DefaultVirtual, result.Route);
        Assert.IsFalse(result.SortWasReset);
        Assert.AreEqual(1, rowsPropertyNotificationCount);
        Assert.AreNotSame(seededRows, table.Rows);
        Assert.AreEqual(0, table.Rows.Count);
        Assert.AreEqual(MainViewUpdateMode.FolderFilterSelected, table.LastAppliedColumnMode);
        Assert.AreEqual(expectedSummary, table.SummaryText);
    }

    [TestMethod]
    public void TryApplyMaterialized_TransientSortRetainsResolvedPendingOperationContext()
    {
        var table = new MainChartListViewModel();
        PlaylistWorkspaceViewModel workspace = CreateWorkspaceForOwner();
        using RegularChartListOwner owner = CreateOwner(table, workspace);

        RegularMaterializedChartListApplyResult result = owner.TryApplyMaterialized(
            CreateMaterializedApplyRequest(
                [LibraryChartRow.FromChartFile(CreateSourceRow("Pending", "Alpha").Chart)],
                MainViewUpdateMode.PendingInstallFolderSelected));

        Assert.IsTrue(result.WasCommitted);
        Assert.AreEqual(
            MainViewOperationSection.InstallPending,
            table.CurrentOperationContext.OperationSection);
        Assert.AreEqual(
            ChartOperationSourceScope.PendingPackage,
            table.CurrentOperationContext.SourceScope);
    }

    [TestMethod]
    public void ApplyMainLibraryView_RejectsNonMainLibraryRoute()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        var route = new ChartListRefreshRoute(
            ChartListRefreshRouteKind.ApplyPlayHistoryView,
            MainViewUpdateMode.PlayHistorySelected,
            MainViewUpdateMode.PlayHistorySelected,
            MainViewUpdateMode.PlayHistorySelected,
            isPlaylistTreeActive: false,
            includeBmsonRows: false);

        Assert.ThrowsException<ArgumentException>(() => owner.ApplyMainLibraryView(
            route,
            library: null,
            parameter: null,
            treeParameter: null,
            preserveSummary: false,
            Stopwatch.StartNew()));
    }

    [TestMethod]
    public void MaterializedApply_OwnsBuildAndTerminalPipeline()
    {
        var table = new MainChartListViewModel();
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        List<LibraryChartRow> rows =
        [
            LibraryChartRow.FromChartFile(CreateSourceRow("Folder A", "Beta").Chart),
            LibraryChartRow.FromChartFile(CreateSourceRow("Folder A", "Alpha").Chart)
        ];
        var refresh = new RegularChartListRefreshRequest(
            MainViewUpdateMode.SortUpdated,
            MainViewUpdateMode.TreeViewFilterNotChanged,
            parameter: null,
            MainViewUpdateMode.FolderFilterSelected,
            treeParameter: null,
            includeBmsonRows: false,
            virtualSubsetRequiredFailure: false,
            keywordFilter: string.Empty,
            ChartModeFilter.All,
            ChartListSortSpecification.Create(nameof(LibraryChartRow.Title), ListSortDirection.Ascending, hasValue: true));
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);

        RegularMaterializedChartListApplyResult result = owner.TryApplyMaterialized(
            new RegularMaterializedChartListApplyRequest
            {
                RefreshRequest = refresh,
                HasFolderRowsOverride = true,
                FolderRowsOverride = rows,
                ExternalVersions = new RegularChartListExternalVersions(0, 0, 0),
                ColumnSelection = new MainChartListColumnSelection(
                    settings,
                    reused: false,
                    elapsedMs: 0L,
                    MainViewUpdateMode.FolderFilterSelected,
                    Visibility.Collapsed,
                    new PlaylistSummaryColumnSettings()),
                Mode = MainViewUpdateMode.SortUpdated,
                Stopwatch = Stopwatch.StartNew()
            });

        Assert.IsTrue(result.WasCommitted);
        Assert.AreEqual("Alpha", ((LibraryChartRow)table.Rows[0]).Title);
        Assert.AreEqual("Beta", ((LibraryChartRow)table.Rows[1]).Title);
        Assert.AreEqual(2, result.FolderCount);
        Assert.AreEqual(2, result.ModeCount);
    }

    [TestMethod]
    public void MaterializedApply_NestedNewerRequestRejectsOuterTerminal()
    {
        var table = new MainChartListViewModel();
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        List<LibraryChartRow> outerRows =
        [
            LibraryChartRow.FromChartFile(CreateSourceRow("Outer", "outer.bms").Chart)
        ];
        List<LibraryChartRow> nestedRows =
        [
            LibraryChartRow.FromChartFile(CreateSourceRow("Nested", "nested.bms").Chart)
        ];
        bool nestedStarted = false;
        RegularMaterializedChartListApplyResult nestedResult = default;
        table.RowsReplacing += (_, _) =>
        {
            if (nestedStarted)
            {
                return;
            }
            nestedStarted = true;
            nestedResult = owner.TryApplyMaterialized(CreateMaterializedApplyRequest(nestedRows));
        };

        RegularMaterializedChartListApplyResult outerResult = owner.TryApplyMaterialized(
            CreateMaterializedApplyRequest(outerRows));

        Assert.IsTrue(nestedResult.WasCommitted);
        Assert.IsFalse(outerResult.WasCommitted);
        Assert.AreEqual("nested.bms", ((LibraryChartRow)table.Rows[0]).Title);
    }

    [TestMethod]
    public void VirtualRowCache_ReusesAndRemovesBmsOwnerRowsThroughRegularOwner()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        var file = new BMSFile
        {
            path = @"C:\Charts\Owner\chart.bms",
        };
        ChartListSourceRow sourceRow = ChartListSourceRow.FromChartFile(ChartFileProjection.FromBmsFile(file));

        LibraryChartRow first = owner.CreateVirtualRow(null, sourceRow);
        LibraryChartRow second = owner.CreateVirtualRow(null, sourceRow);

        Assert.AreSame(first, second);
        Assert.AreEqual(1, owner.SnapshotRows().Count);
        Assert.AreEqual(1, owner.RemoveBmsRows([file]));
        Assert.AreEqual(0, owner.SnapshotRows().Count);
    }

    [TestMethod]
    public void SourceInvalidation_ConsumesEachPositiveOwnedCollectionVersionOnce()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));

        Assert.IsTrue(owner.TryInvalidateSourceForOwnedCollectionVersion(0, out _));
        Assert.AreEqual(1L, owner.SourceGeneration);
        Assert.IsTrue(owner.TryInvalidateSourceForOwnedCollectionVersion(2, out _));
        Assert.AreEqual(2L, owner.SourceGeneration);
        Assert.IsFalse(owner.TryInvalidateSourceForOwnedCollectionVersion(2, out _));
        Assert.AreEqual(2L, owner.SourceGeneration);
    }

    [TestMethod]
    public void VirtualSourceRows_StaleLookupCannotRepopulateInvalidatedCache()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        RegularVirtualSourceRowsLookup staleLookup = owner.LookupVirtualSourceRows(null, includeBmsonRows: false);
        var rows = new List<ChartListSourceRow> { CreateSourceRow("Folder A", "a.bms") };

        owner.InvalidateSource();
        owner.TryPublishVirtualSourceRows(staleLookup, rows);
        Assert.IsFalse(owner.LookupVirtualSourceRows(null, includeBmsonRows: false).CacheHit);

        RegularVirtualSourceRowsLookup currentLookup = owner.LookupVirtualSourceRows(null, includeBmsonRows: false);
        owner.TryPublishVirtualSourceRows(currentLookup, rows);
        RegularVirtualSourceRowsLookup cached = owner.LookupVirtualSourceRows(null, includeBmsonRows: false);
        Assert.IsTrue(cached.CacheHit);
        Assert.AreSame(rows, cached.Rows);

        RegularVirtualSourceRowsLookup staleAfterSortChange = owner.LookupVirtualSourceRows(null, includeBmsonRows: false);
        owner.InvalidateIdentitySortKeys(clearSourceRows: true);
        owner.TryPublishVirtualSourceRows(staleAfterSortChange, rows);
        Assert.IsFalse(owner.LookupVirtualSourceRows(null, includeBmsonRows: false).CacheHit);
    }

    [TestMethod]
    public void VirtualSourceRows_LibraryIdentityChangeInvalidatesDerivedOrders()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        var firstLibrary = (BMSLibrary)FormatterServices.GetUninitializedObject(typeof(BMSLibrary));
        var secondLibrary = (BMSLibrary)FormatterServices.GetUninitializedObject(typeof(BMSLibrary));
        List<ChartListSourceRow> rows = [CreateSourceRow("Folder A", "a.bms")];
        Assert.IsTrue(owner.TryBeginVirtualRequest(firstLibrary, out RegularChartListRequestLease firstLease));
        RegularVirtualSourceRowsLookup firstLookup = owner.LookupVirtualSourceRows(firstLibrary, includeBmsonRows: false);
        owner.TryPublishVirtualSourceRows(firstLookup, rows);
        NormalLibrarySortCacheKey orderKey = owner.CreateVirtualOrderKey(
            firstLookup.SourceGeneration,
            firstLookup.SortKeyGeneration,
            nameof(LibraryChartRow.Title),
            ListSortDirection.Ascending,
            rows.Count,
            new RegularChartListExternalVersions(0, 0, 0));
        Assert.IsTrue(owner.TryPublishVirtualOrder(
            orderKey,
            CreateOrder(rows[0]),
            new RegularChartListExternalVersions(0, 0, 0)));

        Assert.IsTrue(owner.TryBeginVirtualOrderPrewarm(secondLibrary, out RegularChartListPrewarmLease secondLease));
        RegularVirtualSourceRowsLookup secondLookup = owner.LookupVirtualSourceRows(secondLibrary, includeBmsonRows: false);

        Assert.IsFalse(secondLookup.CacheHit);
        Assert.IsTrue(secondLookup.SourceGeneration > firstLookup.SourceGeneration);
        Assert.IsFalse(owner.TryGetVirtualOrder(orderKey, out _));
        Assert.IsTrue(firstLease.Token.IsCancellationRequested);
        secondLease.Dispose();
    }

    [TestMethod]
    public void VirtualSourceRows_ConcurrentInitialLibraryMissRejectsStalePublisher()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        var firstLibrary = (BMSLibrary)FormatterServices.GetUninitializedObject(typeof(BMSLibrary));
        var secondLibrary = (BMSLibrary)FormatterServices.GetUninitializedObject(typeof(BMSLibrary));
        Assert.IsTrue(owner.TryBeginVirtualRequest(firstLibrary, out RegularChartListRequestLease firstLease));
        RegularVirtualSourceRowsLookup firstLookup = owner.LookupVirtualSourceRows(firstLibrary, includeBmsonRows: false);

        Assert.IsTrue(owner.TryBeginVirtualOrderPrewarm(secondLibrary, out RegularChartListPrewarmLease secondLease));
        RegularVirtualSourceRowsLookup secondLookup = owner.LookupVirtualSourceRows(secondLibrary, includeBmsonRows: false);
        owner.TryPublishVirtualSourceRows(firstLookup, [CreateSourceRow("Folder A", "a.bms")]);
        Assert.IsFalse(owner.LookupVirtualSourceRows(secondLibrary, includeBmsonRows: false).CacheHit);
        List<ChartListSourceRow> secondRows = [CreateSourceRow("Folder B", "b.bms")];
        owner.TryPublishVirtualSourceRows(secondLookup, secondRows);

        RegularVirtualSourceRowsLookup cached = owner.LookupVirtualSourceRows(secondLibrary, includeBmsonRows: false);
        Assert.IsTrue(firstLease.Token.IsCancellationRequested);
        Assert.IsTrue(cached.CacheHit);
        Assert.AreSame(secondRows, cached.Rows);
        secondLease.Dispose();
    }

    [TestMethod]
    public void VirtualOrderPrewarm_LibrarySwitchCancelsRunningDifferentLibraryLease()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        var firstLibrary = (BMSLibrary)FormatterServices.GetUninitializedObject(typeof(BMSLibrary));
        var secondLibrary = (BMSLibrary)FormatterServices.GetUninitializedObject(typeof(BMSLibrary));
        Assert.IsTrue(owner.TryBeginVirtualOrderPrewarm(firstLibrary, out RegularChartListPrewarmLease firstLease));

        Assert.IsFalse(owner.TryBeginVirtualOrderPrewarm(secondLibrary, out _));

        Assert.IsTrue(firstLease.Token.IsCancellationRequested);
        firstLease.Dispose();
    }

    [TestMethod]
    public void VirtualOrderPrewarm_BuildsOwnedOrderAndCompletesLease()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        List<ChartListSourceRow> rows =
        [
            CreateSourceRow("Folder A", "b.bms"),
            CreateSourceRow("Folder A", "a.bms")
        ];
        RegularVirtualSourceRowsLookup lookup = owner.LookupVirtualSourceRows(null, includeBmsonRows: false);
        owner.TryPublishVirtualSourceRows(lookup, rows);
        Assert.IsTrue(owner.TryBeginVirtualOrderPrewarm(null, out RegularChartListPrewarmLease lease));

        using (lease)
        {
            owner.RunVirtualOrderPrewarm(
                lease,
                library: null,
                includeBmsonRows: false,
                [new VirtualNormalLibrarySortDescriptor(nameof(LibraryChartRow.Title), ListSortDirection.Ascending, 1)],
                "test");
        }

        NormalLibrarySortCacheKey key = owner.CreateVirtualOrderKey(
            lookup.SourceGeneration,
            lookup.SortKeyGeneration,
            nameof(LibraryChartRow.Title),
            ListSortDirection.Ascending,
            rows.Count,
            new RegularChartListExternalVersions(0, 0, 0));
        Assert.IsTrue(owner.TryGetVirtualOrder(key, out ChartListOrder order));
        CollectionAssert.AreEqual(new[] { 1, 0 }, order.Indexes.ToArray());
        Assert.IsTrue(lease.Completion.IsCompleted);
    }

    [TestMethod]
    public void VirtualNormalLibraryApply_OwnsSourceOrderFilterAndTerminalReuse()
    {
        var table = new MainChartListViewModel();
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        List<ChartListSourceRow> sourceRows =
        [
            CreateSourceRow("Folder A", "Alpha"),
            CreateSourceRow("Folder B", "Bravo"),
            CreateSourceRow("Folder A", "Charlie")
        ];
        RegularVirtualSourceRowsLookup lookup = owner.LookupVirtualSourceRows(null, includeBmsonRows: false);
        owner.TryPublishVirtualSourceRows(lookup, sourceRows);
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
        var request = new RegularVirtualNormalLibraryApplyRequest
        {
            IncludeBmsonRows = false,
            TreeFilter = RegularNormalLibraryTreeFilter.Create(RegularChartFolderFilterKind.Directory, @"C:\Charts\Folder A"),
            KeywordFilter = string.Empty,
            ModeFilter = ChartModeFilter.All,
            SortColumnName = nameof(LibraryChartRow.Title),
            SortDirection = ListSortDirection.Descending,
            ExternalVersions = new RegularChartListExternalVersions(0, 0, 0),
            ColumnSelection = new MainChartListColumnSelection(
                settings,
                reused: false,
                elapsedMs: 0L,
                MainViewUpdateMode.FolderFilterSelected,
                Visibility.Collapsed,
                new PlaylistSummaryColumnSettings()),
            Mode = MainViewUpdateMode.FolderFilterSelected,
            Stopwatch = Stopwatch.StartNew(),
            Reason = "test"
        };

        RegularVirtualNormalLibraryApplyResult first = owner.TryApplyVirtualNormalLibrary(request);
        RegularVirtualNormalLibraryApplyResult second = owner.TryApplyVirtualNormalLibrary(request);

        Assert.IsTrue(first.WasCommitted);
        Assert.IsTrue(second.WasCommitted);
        Assert.AreSame(second.RowsView, table.Rows);
        Assert.AreEqual(2, second.FolderCount);
        Assert.AreEqual(2, second.RowsView.Count);
        Assert.AreEqual("Charlie", ((LibraryChartRow)second.RowsView[0]).Title);
        Assert.AreEqual("Alpha", ((LibraryChartRow)second.RowsView[1]).Title);
        Assert.IsTrue(second.SourceRowsCacheHit);
        Assert.IsTrue(second.SortCacheHit);
    }

    [TestMethod]
    public void VirtualChartSubsetApply_OwnsProjectionOrderFilterAndTerminalReuse()
    {
        var table = new MainChartListViewModel();
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        List<ChartFile> charts =
        [
            CreateSourceRow("Folder A", "Alpha").Chart,
            CreateSourceRow("Folder B", "Bravo").Chart,
            CreateSourceRow("Folder A", "Charlie").Chart
        ];
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
        var request = new RegularVirtualChartSubsetApplyRequest
        {
            SourceCharts = charts,
            SourceProjectionMode = ChartListSourceProjectionMode.PreserveSourceProjection,
            TreeMode = MainViewUpdateMode.FileMissingFilterSelected,
            SubsetName = "file_missing",
            KeywordFilter = string.Empty,
            ModeFilter = ChartModeFilter.All,
            SortColumnName = nameof(LibraryChartRow.Title),
            SortDirection = ListSortDirection.Descending,
            ExternalVersions = new RegularChartListExternalVersions(0, 0, 0),
            ColumnSelection = new MainChartListColumnSelection(
                settings,
                reused: false,
                elapsedMs: 0L,
                MainViewUpdateMode.FileMissingFilterSelected,
                Visibility.Collapsed,
                new PlaylistSummaryColumnSettings()),
            Mode = MainViewUpdateMode.FileMissingFilterSelected,
            Stopwatch = Stopwatch.StartNew()
        };

        RegularVirtualChartSubsetApplyResult first = owner.TryApplyVirtualChartSubset(request);
        RegularVirtualChartSubsetApplyResult second = owner.TryApplyVirtualChartSubset(request);

        Assert.IsTrue(first.WasCommitted);
        Assert.IsTrue(second.WasCommitted);
        Assert.AreSame(second.RowsView, table.Rows);
        Assert.AreEqual(3, second.RowsView.Count);
        Assert.AreEqual("Charlie", ((LibraryChartRow)second.RowsView[0]).Title);
        Assert.AreEqual("Bravo", ((LibraryChartRow)second.RowsView[1]).Title);
        Assert.AreEqual("Alpha", ((LibraryChartRow)second.RowsView[2]).Title);
        Assert.IsTrue(second.SortCacheHit);
        Assert.AreEqual(2, second.DistinctFolderCount);
    }

    [TestMethod]
    public void RegularEntry_SelectsDefaultVirtualAndResetsUnsupportedSort()
    {
        var table = new MainChartListViewModel();
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        RegularVirtualSourceRowsLookup lookup = owner.LookupVirtualSourceRows(null, includeBmsonRows: false);
        owner.TryPublishVirtualSourceRows(
            lookup,
            [CreateSourceRow("Folder B", "Bravo"), CreateSourceRow("Folder A", "Alpha")]);
        owner.QueueSort(new MainChartListSortRequestedEventArgs(
            "UnsupportedColumn",
            ListSortDirection.Descending,
            MainChartListSortTarget.Regular));

        RegularChartListEntryResult result = owner.ApplyMainLibraryView(
            new ChartListRefreshRoute(
                ChartListRefreshRouteKind.ContinueMainLibrary,
                MainViewUpdateMode.FolderFilterSelected,
                MainViewUpdateMode.FolderFilterSelected,
                MainViewUpdateMode.FolderFilterSelected,
                isPlaylistTreeActive: false,
                includeBmsonRows: false),
            library: null,
            parameter: null,
            treeParameter: null,
            preserveSummary: false,
            Stopwatch.StartNew());

        Assert.IsTrue(result.WasCommitted);
        Assert.AreEqual(RegularChartListEntryRoute.DefaultVirtual, result.Route);
        Assert.IsTrue(result.SortWasReset);
        Assert.IsNull(owner.CaptureSortParameters());
        Assert.AreEqual("Alpha", ((LibraryChartRow)table.Rows[0]).Title);
        Assert.AreEqual("Bravo", ((LibraryChartRow)table.Rows[1]).Title);
    }

    [TestMethod]
    public void RegularEntry_AppliesOwnerFilterState()
    {
        var table = new MainChartListViewModel();
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        RegularVirtualSourceRowsLookup lookup = owner.LookupVirtualSourceRows(null, includeBmsonRows: false);
        owner.TryPublishVirtualSourceRows(
            lookup,
            [CreateSourceRow("Folder B", "Bravo"), CreateSourceRow("Folder A", "Alpha")]);
        RegularChartListEntryResult result = owner.ApplyRegularView(
            CreateEntryRequest(MainViewUpdateMode.FolderFilterSelected, "Alpha", ChartModeFilter.All));

        Assert.IsTrue(result.WasCommitted);
        Assert.AreEqual(1, table.Rows.Count);
        Assert.AreEqual("Alpha", ((LibraryChartRow)table.Rows[0]).Title);
    }

    [TestMethod]
    public void PlayHistoryColumnModeCommit_CancelsPendingRegularRequest()
    {
        var table = new MainChartListViewModel();
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        RegularChartListRequestLease pending = owner.BeginRequest();

        table.CommitAppliedColumnMode(MainViewUpdateMode.PlayHistorySelected);

        Assert.IsTrue(pending.Token.IsCancellationRequested);
        owner.InvalidatePendingRequest();
    }

    [TestMethod]
    public void RegularEntry_AppliesOwnerModeFilterState()
    {
        var table = new MainChartListViewModel();
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        RegularVirtualSourceRowsLookup lookup = owner.LookupVirtualSourceRows(null, includeBmsonRows: false);
        owner.TryPublishVirtualSourceRows(
            lookup,
            [CreateSourceRow("Folder A", "Seven", mode: 7), CreateSourceRow("Folder B", "Nine", mode: 9)]);
        RegularChartListEntryResult result = owner.ApplyRegularView(
            CreateEntryRequest(MainViewUpdateMode.FolderFilterSelected, string.Empty, ChartModeFilter._7KEYS));

        Assert.IsTrue(result.WasCommitted);
        Assert.AreEqual(1, table.Rows.Count);
        Assert.AreEqual("Seven", ((LibraryChartRow)table.Rows[0]).Title);
    }

    [TestMethod]
    public void RegularEntry_SelectsDuplicateSubsetFromLibraryOwner()
    {
        var table = new MainChartListViewModel();
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        ChartFile bravo = CreateSourceRow("Folder B", "Bravo").Chart;
        ChartFile alpha = CreateSourceRow("Folder A", "Alpha").Chart;
        var library = (BMSLibrary)FormatterServices.GetUninitializedObject(typeof(BMSLibrary));
        typeof(BMSLibrary)
            .GetField("catalogChartInfoOwner", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(library, new CatalogChartInfoOwner(_ => { }, () => false, (_, _) => false, () => null, _ => { }));
        typeof(BMSLibrary)
            .GetField("_DuplicateChartGroups", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(library, new List<DuplicateGroup> { new([bravo, alpha], [bravo.Folder, alpha.Folder]) });
        RegularChartListEntryRequest request = CreateEntryRequest(MainViewUpdateMode.DuplicateFilterSelected);
        request.Library = library;

        RegularChartListEntryResult result = owner.ApplyRegularView(request);

        Assert.IsTrue(result.WasCommitted);
        Assert.AreEqual(RegularChartListEntryRoute.SubsetVirtual, result.Route);
        Assert.IsFalse(result.SortWasReset);
        Assert.AreEqual(2, table.Rows.Count);
    }

    [TestMethod]
    public void RegularEntry_DuplicateFolderContextSelectsOnlyMatchingCharts()
    {
        ChartFile matching = CreateSourceRow("Folder A", "Alpha").Chart;
        ChartFile other = CreateSourceRow("Folder B", "Bravo").Chart;
        var groups = new[]
        {
            new DuplicateGroup([matching, other], [matching.Folder, other.Folder]) { Header = "Group" }
        };

        Assert.IsTrue(RegularChartListOwner.TryResolveDuplicateSource(
            groups,
            DuplicateViewContext.ForFolder(System.IO.Path.Combine(@"C:\Charts", "Folder A")),
            out RegularChartListSubsetSource source));
        groups[0].ChartFiles.Clear();

        CollectionAssert.AreEqual(new[] { matching }, source.SourceCharts.ToArray());
        Assert.AreEqual("duplicate_folder", source.Name);
    }

    [TestMethod]
    public void DependencyInvalidation_PrunesDefaultAndSubsetOrderCachesTogether()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        var versions = new RegularChartListExternalVersions(score: 1, chartInfo: 0, maintenanceHydration: 0);
        ChartListOrder order = CreateOrder(CreateSourceRow("Folder A", "a.bms"));
        NormalLibrarySortCacheKey defaultKey = owner.CreateVirtualOrderKey(
            owner.SourceGeneration,
            owner.SortKeyGeneration,
            nameof(LibraryChartRow.rateDouble),
            ListSortDirection.Ascending,
            rowCount: 1,
            externalVersions: versions);
        VirtualChartSubsetSortCacheKey subsetKey = owner.CreateVirtualSubsetOrderKey(
            owner.SourceGeneration,
            owner.SortKeyGeneration,
            treeMode: (int)MainViewUpdateMode.FileMissingFilterSelected,
            subsetName: "missing",
            sourceRowsSignature: 1,
            nameof(LibraryChartRow.rateDouble),
            ListSortDirection.Ascending,
            rowCount: 1,
            externalVersions: versions);
        Assert.IsTrue(owner.TryPublishVirtualOrder(defaultKey, order, versions));
        Assert.IsTrue(owner.TryPublishVirtualSubsetOrder(subsetKey, order, versions));

        int removed = owner.InvalidateSortCacheByDependency(MainViewDataDependency.Score, out int cacheCount);

        Assert.AreEqual(2, cacheCount);
        Assert.AreEqual(2, removed);
        Assert.IsFalse(owner.TryGetVirtualOrder(defaultKey, out _));
        Assert.IsFalse(owner.TryGetVirtualSubsetOrder(subsetKey, out _));
    }

    [TestMethod]
    public void VirtualOrder_ExternalVersionChangeRejectsStalePublish()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        var initialVersions = new RegularChartListExternalVersions(score: 1, chartInfo: 0, maintenanceHydration: 0);
        NormalLibrarySortCacheKey key = owner.CreateVirtualOrderKey(
            owner.SourceGeneration,
            owner.SortKeyGeneration,
            nameof(LibraryChartRow.rateDouble),
            ListSortDirection.Ascending,
            rowCount: 1,
            externalVersions: initialVersions);
        ChartListOrder order = CreateOrder(CreateSourceRow("Folder A", "a.bms"));

        bool published = owner.TryPublishVirtualOrder(
            key,
            order,
            new RegularChartListExternalVersions(score: 2, chartInfo: 0, maintenanceHydration: 0));

        Assert.IsFalse(published);
        Assert.IsFalse(owner.TryGetVirtualOrder(key, out _));
    }

    [TestMethod]
    public void VirtualSubsetOrder_ExternalVersionChangeRejectsStalePublish()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        var initialVersions = new RegularChartListExternalVersions(score: 0, chartInfo: 1, maintenanceHydration: 0);
        VirtualChartSubsetSortCacheKey key = owner.CreateVirtualSubsetOrderKey(
            owner.SourceGeneration,
            owner.SortKeyGeneration,
            treeMode: (int)MainViewUpdateMode.FileMissingFilterSelected,
            subsetName: "missing",
            sourceRowsSignature: 1,
            nameof(LibraryChartRow.ChartNotes),
            ListSortDirection.Ascending,
            rowCount: 1,
            externalVersions: initialVersions);
        ChartListOrder order = CreateOrder(CreateSourceRow("Folder A", "a.bms"));

        bool published = owner.TryPublishVirtualSubsetOrder(
            key,
            order,
            new RegularChartListExternalVersions(score: 0, chartInfo: 2, maintenanceHydration: 0));

        Assert.IsFalse(published);
        Assert.IsFalse(owner.TryGetVirtualSubsetOrder(key, out _));
    }

    [TestMethod]
    public void WarningOrderCache_IsPrunedByInstallDestinationAndMaintenanceChanges()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        var versions = new RegularChartListExternalVersions(score: 0, chartInfo: 0, maintenanceHydration: 1);
        ChartListOrder order = CreateOrder(CreateSourceRow("Folder A", "a.bms"));
        NormalLibrarySortCacheKey installKey = owner.CreateVirtualOrderKey(
            owner.SourceGeneration,
            owner.SortKeyGeneration,
            nameof(LibraryChartRow.WarningDigestText),
            ListSortDirection.Ascending,
            rowCount: 1,
            externalVersions: versions);
        Assert.IsTrue(owner.TryPublishVirtualOrder(installKey, order, versions));

        Assert.AreEqual(1, owner.InvalidateSortCacheByDependency(MainViewDataDependency.InstallDestination, out _));
        Assert.IsFalse(owner.TryGetVirtualOrder(installKey, out _));

        NormalLibrarySortCacheKey maintenanceKey = owner.CreateVirtualOrderKey(
            owner.SourceGeneration,
            owner.SortKeyGeneration,
            nameof(LibraryChartRow.WarningDigestText),
            ListSortDirection.Ascending,
            rowCount: 1,
            externalVersions: versions);
        Assert.IsTrue(owner.TryPublishVirtualOrder(maintenanceKey, order, versions));
        Assert.AreEqual(1, owner.InvalidateSortCacheByDependency(MainViewDataDependency.Maintenance, out _));
        Assert.IsFalse(owner.TryGetVirtualOrder(maintenanceKey, out _));
    }

    [TestMethod]
    public void TryCommit_RowsReplacingNestedRequest_LatestRequestWins()
    {
        var table = new MainChartListViewModel();
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
        RegularChartListOwner owner = CreateOwner(table, workspace);
        RegularChartListRequestLease firstLease = owner.BeginRequest();
        RegularChartListBuildResult firstBuild = Build(owner, firstLease, new List<LibraryChartRow>());
        RegularChartListBuildResult nestedBuild = null!;
        RegularChartListTerminalResult nestedTerminal = default;
        long nestedRequestId = 0L;
        bool nested = false;
        table.RowsReplacing += (_, _) =>
        {
            if (nested)
            {
                return;
            }
            nested = true;
            RegularChartListRequestLease nestedLease = owner.BeginRequest();
            nestedRequestId = nestedLease.RequestId;
            nestedBuild = Build(owner, nestedLease, new List<LibraryChartRow>());
            nestedTerminal = owner.TryCommit(nestedLease, CreateTerminalInput(nestedBuild));
        };

        RegularChartListTerminalResult firstTerminal = owner.TryCommit(firstLease, CreateTerminalInput(firstBuild));

        Assert.IsTrue(nestedTerminal.WasCommitted);
        Assert.IsFalse(firstTerminal.WasCommitted);
        Assert.AreSame(nestedBuild.Sort.RowsView, table.Rows);
        Assert.AreEqual(nestedRequestId, table.LastCompletion.RequestId);
        Assert.IsTrue(nestedRequestId > firstLease.RequestId);
    }

    [TestMethod]
    public void TryCommit_RequestInvalidatedDuringPrepare_DoesNotReplaceRows()
    {
        var originalRows = new List<object>();
        var table = new MainChartListViewModel { Rows = originalRows };
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        RegularChartListRequestLease lease = owner.BeginRequest();
        RegularChartListBuildResult build = Build(owner, lease, new List<LibraryChartRow>());
        int canceled = 0;
        table.RowsReplacing += (_, _) => owner.InvalidatePendingRequest();
        table.RowsReplacementCanceled += (_, _) =>
        {
            canceled++;
            Task lockProbe = StartLongRunning(owner.InvalidatePendingRequest);
            Assert.IsTrue(lockProbe.Wait(TimeSpan.FromSeconds(5)), "RowsReplacementCanceled must run after the regular owner lock is released.");
        };

        RegularChartListTerminalResult terminal = owner.TryCommit(lease, CreateTerminalInput(build));

        Assert.IsFalse(terminal.WasCommitted);
        Assert.AreSame(originalRows, table.Rows);
        Assert.AreEqual(1, canceled);
    }

    [TestMethod]
    public void TryCommit_RejectsPresentationOfDifferentKind()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        RegularChartListRequestLease lease = owner.BeginRequest();
        RegularChartListBuildResult build = Build(owner, lease, new List<LibraryChartRow>());

        Assert.ThrowsException<ArgumentException>(() => owner.TryCommit(lease, CreateVirtualTerminalInput(new List<object>())));
        Assert.ThrowsException<ArgumentException>(() => owner.TryCommitVirtual(lease, CreateTerminalInput(build)));
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
        var stopwatch = Stopwatch.StartNew();
        Assert.ThrowsException<ArgumentException>(() => RegularChartListPresentationResult.ForMaterialized(
            build,
            new MainChartListRowsApplyRequest
            {
                Rows = new List<object>(),
                ColumnsSettings = settings,
                SelectionPolicy = MainChartListSelectionPolicy.Preserve,
                Summary = MainChartListSummaryUpdate.Preserve(),
                Stopwatch = stopwatch
            },
            new MainChartListColumnSelection(
                settings,
                reused: false,
                elapsedMs: 0L,
                MainViewUpdateMode.UpdatedNone,
                Visibility.Collapsed,
                new PlaylistSummaryColumnSettings()),
            MainViewUpdateMode.UpdatedNone,
            stopwatch));
    }

    [TestMethod]
    public void PresentationResult_SnapshotsRowsApplyRequest()
    {
        var table = new MainChartListViewModel();
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        RegularChartListRequestLease lease = owner.BeginRequest();
        RegularChartListBuildResult build = Build(owner, lease, new List<LibraryChartRow>());
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
        var stopwatch = Stopwatch.StartNew();
        var request = new MainChartListRowsApplyRequest
        {
            Rows = build.Sort.RowsView,
            ColumnsSettings = settings,
            SelectionPolicy = MainChartListSelectionPolicy.Preserve,
            Summary = MainChartListSummaryUpdate.NormalRows(build.Sort.RowsView),
            Stopwatch = stopwatch
        };
        RegularChartListPresentationResult presentation = RegularChartListPresentationResult.ForMaterialized(
            build,
            request,
            new MainChartListColumnSelection(
                settings,
                reused: false,
                elapsedMs: 0L,
                MainViewUpdateMode.UpdatedNone,
                Visibility.Collapsed,
                new PlaylistSummaryColumnSettings()),
            MainViewUpdateMode.UpdatedNone,
            stopwatch);

        request.Rows = new List<object>();
        request.Summary = MainChartListSummaryUpdate.Explicit("mutated");

        Assert.IsTrue(owner.TryCommit(lease, presentation).WasCommitted);
        Assert.AreSame(build.Sort.RowsView, table.Rows);
    }

    [TestMethod]
    public void TryCommitVirtual_NewerRequestPreventsStaleRowsFromReplacingCurrentRows()
    {
        var table = new MainChartListViewModel();
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        RegularChartListRequestLease staleLease = owner.BeginRequest();
        var staleRows = new List<object> { new() };
        RegularChartListRequestLease currentLease = owner.BeginRequest();
        var currentRows = new List<object> { new(), new() };

        RegularChartListTerminalResult current = owner.TryCommitVirtual(
            currentLease,
            CreateVirtualTerminalInput(currentRows));
        int stalePrepareCount = 0;
        table.RowsReplacing += (_, _) => stalePrepareCount++;
        RegularChartListTerminalResult stale = owner.TryCommitVirtual(
            staleLease,
            CreateVirtualTerminalInput(staleRows));

        Assert.IsTrue(current.WasCommitted);
        Assert.IsFalse(stale.WasCommitted);
        Assert.AreEqual(0, stalePrepareCount);
        Assert.AreSame(currentRows, table.Rows);
        Assert.AreEqual(currentLease.RequestId, table.LastCompletion.RequestId);
    }

    [TestMethod]
    public void VirtualSummary_CurrentRequestUpdatesCommittedRows()
    {
        var table = new MainChartListViewModel();
        Action pendingUiAction = null!;
        using var uiActionQueued = new ManualResetEventSlim();
        var owner = new RegularChartListOwner(
            table,
            new PlaylistWorkspaceViewModel(
                action => action(),
                new MainChartListViewModel(action => action()),
                new PlaylistDetailBuildState(),
                new PlaylistDetailViewState(),
                _ => { },
                _ => { },
                () => new CustomFolderOutputSettingsSnapshot(),
                PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
                PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
                PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
                PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
                PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
                PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
                PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
                PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
                PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
                PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
                PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
                PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
                PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
                () => null!,
                () => null!,
                _ => { },
                new ObservableCollection<BMSTable>(),
                (_, _) => false,
                () => true,
                () => MainViewUpdateMode.FolderFilterSelected,
                () => Task.CompletedTask,
                () => false,
                () => { },
                _ => { },
                (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck),
            _ => { },
            action =>
            {
                pendingUiAction = action;
                uiActionQueued.Set();
            },
            _ => { },
            CreatePendingPackageWorkflowOwner(),
            new ChartFileOperationSynchronizer(),
            new ChartMutationActivityOwner(),
            new NoOpFolderAutoRenamePlaybackPort(),
            new TestUiScheduler(() => null!));
        RegularChartListRequestLease lease = owner.BeginRequest();
        var rows = new List<object> { new(), new() };
        Assert.IsTrue(owner.TryCommitVirtual(lease, CreateVirtualTerminalInput(rows)).WasCommitted);
        var key = new MainViewSummaryCacheKey(1, 1, rows.Count, includeBmsonRows: false, "test");
        ChartListSourceRow[] sourceRows =
        [
            CreateSourceRow("Folder A", "a.bms"),
            CreateSourceRow("Folder B", "b.bms")
        ];

        owner.ScheduleVirtualSummary(
            lease,
            key,
            sourceRows,
            Enumerable.Range(0, sourceRows.Length).ToArray(),
            rows,
            "test");

        Assert.IsTrue(uiActionQueued.Wait(TimeSpan.FromSeconds(5)));
        pendingUiAction();
        Assert.AreEqual("[2" + BeMusicSeeker.Properties.Resources.Num_songs + " / 2" + BeMusicSeeker.Properties.Resources.Num_folders + "]", table.SummaryText);
    }

    [TestMethod]
    public void VirtualSummary_StaleRequestCannotUpdateNewerRows()
    {
        var table = new MainChartListViewModel();
        Action pendingUiAction = null!;
        using var uiActionQueued = new ManualResetEventSlim();
        var owner = new RegularChartListOwner(
            table,
            new PlaylistWorkspaceViewModel(
                action => action(),
                new MainChartListViewModel(action => action()),
                new PlaylistDetailBuildState(),
                new PlaylistDetailViewState(),
                _ => { },
                _ => { },
                () => new CustomFolderOutputSettingsSnapshot(),
                PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
                PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
                PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
                PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
                PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
                PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
                PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
                PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
                PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
                PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
                PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
                PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
                PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
                () => null!,
                () => null!,
                _ => { },
                new ObservableCollection<BMSTable>(),
                (_, _) => false,
                () => true,
                () => MainViewUpdateMode.FolderFilterSelected,
                () => Task.CompletedTask,
                () => false,
                () => { },
                _ => { },
                (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck),
            _ => { },
            action =>
            {
                pendingUiAction = action;
                uiActionQueued.Set();
            },
            _ => { },
            CreatePendingPackageWorkflowOwner(),
            new ChartFileOperationSynchronizer(),
            new ChartMutationActivityOwner(),
            new NoOpFolderAutoRenamePlaybackPort(),
            new TestUiScheduler(() => null!));
        RegularChartListRequestLease staleLease = owner.BeginRequest();
        var staleRows = new List<object> { new(), new() };
        Assert.IsTrue(owner.TryCommitVirtual(staleLease, CreateVirtualTerminalInput(staleRows)).WasCommitted);
        var key = new MainViewSummaryCacheKey(1, 1, staleRows.Count, includeBmsonRows: false, "test");
        owner.ScheduleVirtualSummary(
            staleLease,
            key,
            [CreateSourceRow("Folder A", "a.bms"), CreateSourceRow("Folder B", "b.bms")],
            [0, 1],
            staleRows,
            "test");
        Assert.IsTrue(uiActionQueued.Wait(TimeSpan.FromSeconds(5)));

        RegularChartListRequestLease currentLease = owner.BeginRequest();
        var currentRows = new List<object> { new() };
        Assert.IsTrue(owner.TryCommitVirtual(currentLease, CreateVirtualTerminalInput(currentRows)).WasCommitted);
        string currentSummary = table.SummaryText;
        pendingUiAction();

        Assert.AreSame(currentRows, table.Rows);
        Assert.AreEqual(currentSummary, table.SummaryText);
    }

    [TestMethod]
    public void VirtualSummary_SameKeyRefreshSharesRunningScanWithLatestRows()
    {
        var table = new MainChartListViewModel();
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        var sourceRows = new BlockingIndexedSourceRows(
            CreateSourceRow("Folder A", "a.bms"),
            CreateSourceRow("Folder B", "b.bms"));
        try
        {
            var key = new MainViewSummaryCacheKey(1, 1, 2, includeBmsonRows: false, "test");
            RegularChartListRequestLease firstLease = owner.BeginRequest();
            var firstRows = new List<object> { new(), new() };
            Assert.IsTrue(owner.TryCommitVirtual(firstLease, CreateVirtualTerminalInput(firstRows)).WasCommitted);
            owner.ScheduleVirtualSummary(firstLease, key, sourceRows, [0, 1], firstRows, "first");
            Assert.IsTrue(sourceRows.IndexReadStarted.Wait(TimeSpan.FromSeconds(5)));

            RegularChartListRequestLease currentLease = owner.BeginRequest();
            var currentRows = new List<object> { new(), new() };
            Assert.IsTrue(owner.TryCommitVirtual(currentLease, CreateVirtualTerminalInput(currentRows)).WasCommitted);
            owner.ScheduleVirtualSummary(currentLease, key, sourceRows, [0, 1], currentRows, "current");

            string expectedSummary = "[2" + BeMusicSeeker.Properties.Resources.Num_songs + " / 2" + BeMusicSeeker.Properties.Resources.Num_folders + "]";
            using var summaryUpdated = new ManualResetEventSlim();
            PropertyChangedEventHandler summaryHandler = (_, args) =>
            {
                if (args.PropertyName == nameof(MainChartListViewModel.SummaryText)
                    && string.Equals(table.SummaryText, expectedSummary, StringComparison.Ordinal))
                {
                    summaryUpdated.Set();
                }
            };
            table.PropertyChanged += summaryHandler;
            try
            {
                sourceRows.ReleaseIndexRead.Set();
                Assert.IsTrue(summaryUpdated.Wait(TimeSpan.FromSeconds(5)));
            }
            finally
            {
                table.PropertyChanged -= summaryHandler;
            }
            Assert.AreSame(currentRows, table.Rows);
            Assert.AreEqual(2, sourceRows.IndexReadCount);
        }
        finally
        {
            sourceRows.ReleaseIndexRead.Set();
            sourceRows.Dispose();
        }
    }

    [TestMethod]
    public void VirtualSummary_StaleStopRetiresWorkBeforeLatestSameKeyRequestJoins()
    {
        var table = new MainChartListViewModel();
        var sourceRows = new BlockingIndexedSourceRows(
            CreateSourceRow("Folder A", "a.bms"),
            CreateSourceRow("Folder B", "b.bms"));
        RegularChartListOwner owner = null!;
        RegularChartListRequestLease latestLease = null!;
        IList latestRows = null!;
        IReadOnlyList<int> indexes = Enumerable.Range(0, 5_000)
            .Select(index => index & 1)
            .ToArray();
        var key = new MainViewSummaryCacheKey(1, 1, indexes.Count, includeBmsonRows: false, "terminal-race");
        int rescheduleCount = 0;
        var messages = new ConcurrentQueue<string>();
        owner = CreateOwner(
            table,
            CreateWorkspaceForOwner(),
            action => action(),
            log: message =>
            {
                messages.Enqueue(message);
                if (message.Contains("stale_stopped", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref rescheduleCount);
                    owner.ScheduleVirtualSummary(
                        latestLease,
                        key,
                        sourceRows,
                        indexes,
                        latestRows,
                        "latest_from_terminal");
                }
            });
        try
        {
            RegularChartListRequestLease staleLease = owner.BeginRequest();
            var staleRows = new List<object> { new() };
            Assert.IsTrue(owner.TryCommitVirtual(staleLease, CreateVirtualTerminalInput(staleRows)).WasCommitted);
            owner.ScheduleVirtualSummary(staleLease, key, sourceRows, indexes, staleRows, "stale");
            Assert.IsTrue(sourceRows.IndexReadStarted.Wait(TimeSpan.FromSeconds(5)));

            latestLease = owner.BeginRequest();
            latestRows = Enumerable.Repeat<object>(new(), indexes.Count).ToList();
            Assert.IsTrue(owner.TryCommitVirtual(latestLease, CreateVirtualTerminalInput(latestRows)).WasCommitted);

            string expectedSummary = "[" + indexes.Count + BeMusicSeeker.Properties.Resources.Num_songs
                + " / 2" + BeMusicSeeker.Properties.Resources.Num_folders + "]";
            using var summaryUpdated = new ManualResetEventSlim();
            PropertyChangedEventHandler summaryHandler = (_, args) =>
            {
                if (args.PropertyName == nameof(MainChartListViewModel.SummaryText)
                    && string.Equals(table.SummaryText, expectedSummary, StringComparison.Ordinal))
                {
                    summaryUpdated.Set();
                }
            };
            table.PropertyChanged += summaryHandler;
            try
            {
                sourceRows.ReleaseIndexRead.Set();
                Assert.IsTrue(summaryUpdated.Wait(TimeSpan.FromSeconds(5)),
                    "Actual summary: " + table.SummaryText
                    + Environment.NewLine + string.Join(Environment.NewLine, messages));
            }
            finally
            {
                table.PropertyChanged -= summaryHandler;
            }
            Assert.AreEqual(1, Volatile.Read(ref rescheduleCount));
            Assert.AreSame(latestRows, table.Rows);
        }
        finally
        {
            sourceRows.ReleaseIndexRead.Set();
            sourceRows.Dispose();
        }
    }

    [TestMethod]
    public void VirtualSummary_IndexedSnapshotCountsDistinctFoldersAndStopsAtChunkBoundary()
    {
        ChartListSourceRow[] sourceRows =
        [
            CreateSourceRow("Folder A", "a.bms"),
            CreateSourceRow("folder a", "b.bms"),
            CreateSourceRow("Folder B", "c.bms"),
            CreateSourceRow(string.Empty, "d.bms"),
            CreateSourceRow("Folder C", "filtered-out.bms")
        ];
        int[] indexes = [2, 0, 1, 3, -1, sourceRows.Length];

        int indexed = RegularChartListOwner.CountDistinctFoldersByIndex(
            sourceRows,
            indexes,
            shouldStop: null,
            out int scanned,
            out bool stopped);

        Assert.AreEqual(2, indexed);
        Assert.AreEqual(indexes.Length, scanned);
        Assert.IsFalse(stopped);

        int[] largeIndexes = Enumerable.Repeat(0, 5_000).ToArray();
        _ = RegularChartListOwner.CountDistinctFoldersByIndex(
            sourceRows,
            largeIndexes,
            () => true,
            out int cancelledScanCount,
            out bool cancelled);

        Assert.IsTrue(cancelled);
        Assert.AreEqual(1_024, cancelledScanCount);
    }

    [TestMethod]
    public void VirtualSummary_OrderExposesReadOnlyIndexSnapshot()
    {
        ChartListOrder order = CreateOrder(
            CreateSourceRow("Folder A", "a.bms"),
            CreateSourceRow("Folder B", "b.bms"));

        Assert.IsFalse(order.Indexes is int[]);
        Assert.IsTrue(order.Indexes is IList<int> { IsReadOnly: true });
        Assert.ThrowsException<NotSupportedException>(
            () => ((IList<int>)order.Indexes)[0] = 1);
    }

    [TestMethod]
    public void VirtualSummary_OrderOwnsIndexSnapshotIndependentOfProducerArray()
    {
        int[] producerIndexes = [1, 0];
        ChartListOrder order = CreateOrder(
                CreateSourceRow("Folder A", "a.bms"),
                CreateSourceRow("Folder B", "b.bms"))
            .WithIndexes(producerIndexes);

        producerIndexes[0] = 0;
        producerIndexes[1] = 1;

        CollectionAssert.AreEqual(new[] { 1, 0 }, order.Indexes.ToArray());
    }

    [TestMethod]
    public async Task StopAsync_CancelsAndDrainsVirtualSummaryWithoutWaitingForUi()
    {
        var table = new MainChartListViewModel();
        var owner = CreateOwner(table, CreateWorkspaceForOwner());
        var sourceRows = new BlockingIndexedSourceRows(
            CreateSourceRow("Folder A", "a.bms"),
            CreateSourceRow("Folder B", "b.bms"));
        try
        {
            RegularChartListRequestLease lease = owner.BeginRequest();
            var rows = new List<object> { new(), new() };
            Assert.IsTrue(owner.TryCommitVirtual(lease, CreateVirtualTerminalInput(rows)).WasCommitted);
            var key = new MainViewSummaryCacheKey(1, 1, 2, includeBmsonRows: false, "shutdown");
            owner.ScheduleVirtualSummary(lease, key, sourceRows, [0, 1], rows, "shutdown");
            Assert.IsTrue(sourceRows.IndexReadStarted.Wait(TimeSpan.FromSeconds(5)));

            Task stop = owner.StopAsync();

            Assert.IsFalse(stop.IsCompleted);
            sourceRows.ReleaseIndexRead.Set();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            sourceRows.ReleaseIndexRead.Set();
            sourceRows.Dispose();
        }
    }

    [TestMethod]
    public void Build_AfterCommittedSortCache_ReusesOwnedCache()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        var source = new List<LibraryChartRow>();
        RegularChartListRequestLease firstLease = owner.BeginRequest();
        RegularChartListBuildResult first = Build(owner, firstLease, source, MainViewUpdateMode.FolderFilterSelected);
        Assert.IsTrue(owner.TryCommit(firstLease, CreateTerminalInput(first, MainViewUpdateMode.FolderFilterSelected)).WasCommitted);
        Assert.AreEqual(1, owner.CacheCount);

        RegularChartListRequestLease secondLease = owner.BeginRequest();
        RegularChartListBuildResult second = Build(owner, secondLease, source, MainViewUpdateMode.FolderFilterSelected);

        Assert.IsTrue(second.Sort.SortReuse);
        Assert.AreSame(first.Sort.RowsView, second.Sort.RowsView);
    }

    [TestMethod]
    public void InvalidatePendingRequest_DisablesNormalSummaryFreshness()
    {
        var table = new MainChartListViewModel();
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        RegularChartListRequestLease lease = owner.BeginRequest();
        RegularChartListBuildResult build = Build(owner, lease, new List<LibraryChartRow>());
        Assert.IsTrue(owner.TryCommit(lease, CreateTerminalInput(build)).WasCommitted);
        Assert.IsTrue(owner.IsCurrentRegularRows(table.Rows));

        owner.InvalidatePendingRequest();

        Assert.IsFalse(owner.IsCurrentRegularRows(table.Rows));
    }

    [TestMethod]
    public void TryCommit_RowsNotificationObservesCommittedCompletionAndColumnMode()
    {
        var table = new MainChartListViewModel();
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        RegularChartListRequestLease lease = owner.BeginRequest();
        RegularChartListBuildResult build = Build(owner, lease, new List<LibraryChartRow>());
        long completionAtNotification = 0L;
        MainViewUpdateMode? columnModeAtNotification = null;
        table.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainChartListViewModel.Rows))
            {
                completionAtNotification = table.LastCompletion.RequestId;
                columnModeAtNotification = table.LastAppliedColumnMode;
            }
        };

        Assert.IsTrue(owner.TryCommit(lease, CreateTerminalInput(build)).WasCommitted);

        Assert.AreEqual(lease.RequestId, completionAtNotification);
        Assert.AreEqual(MainViewUpdateMode.UpdatedNone, columnModeAtNotification);
    }

    [TestMethod]
    public void TryCommit_RowsPublishNestedRequest_StopsRemainingOuterNotifications()
    {
        var table = new MainChartListViewModel();
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        RegularChartListRequestLease outerLease = owner.BeginRequest();
        RegularChartListBuildResult outerBuild = Build(owner, outerLease, new List<LibraryChartRow>());
        RegularChartListBuildResult nestedBuild = null!;
        long nestedRequestId = 0L;
        int summaryNotifications = 0;
        bool nested = false;
        table.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainChartListViewModel.SummaryText))
            {
                summaryNotifications++;
            }
            if (e.PropertyName != nameof(MainChartListViewModel.Rows) || nested)
            {
                return;
            }
            nested = true;
            RegularChartListRequestLease nestedLease = owner.BeginRequest();
            nestedRequestId = nestedLease.RequestId;
            nestedBuild = Build(owner, nestedLease, new List<LibraryChartRow>());
            Assert.IsTrue(owner.TryCommit(nestedLease, CreateTerminalInput(nestedBuild)).WasCommitted);
        };

        Assert.IsTrue(owner.TryCommit(outerLease, CreateTerminalInput(outerBuild)).WasCommitted);

        Assert.AreSame(nestedBuild.Sort.RowsView, table.Rows);
        Assert.AreEqual(nestedRequestId, table.LastCompletion.RequestId);
        Assert.AreEqual(0, summaryNotifications);
    }

    [TestMethod]
    public void TryCommit_WorkspacePublishNestedRequest_StopsRemainingOuterNotifications()
    {
        var table = new MainChartListViewModel();
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
        RegularChartListOwner owner = CreateOwner(table, workspace);
        RegularChartListRequestLease outerLease = owner.BeginRequest();
        RegularChartListBuildResult outerBuild = Build(owner, outerLease, new List<LibraryChartRow>());
        PlaylistSummaryColumnSettings nestedSummarySettings = null!;
        int summaryNotifications = 0;
        bool nested = false;
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaylistWorkspaceViewModel.PlaylistSummaryColumnsSettings))
            {
                summaryNotifications++;
            }
            if (e.PropertyName != nameof(PlaylistWorkspaceViewModel.ColumnSettingsVisibilityForPlaylist) || nested)
            {
                return;
            }
            nested = true;
            RegularChartListRequestLease nestedLease = owner.BeginRequest();
            RegularChartListBuildResult nestedBuild = Build(owner, nestedLease, new List<LibraryChartRow>());
            nestedSummarySettings = new PlaylistSummaryColumnSettings();
            RegularChartListPresentationResult nestedInput = CreateTerminalInput(
                nestedBuild,
                MainViewUpdateMode.UpdatedNone,
                Visibility.Collapsed,
                nestedSummarySettings);
            Assert.IsTrue(owner.TryCommit(nestedLease, nestedInput).WasCommitted);
        };
        RegularChartListPresentationResult outerInput = CreateTerminalInput(
            outerBuild,
            MainViewUpdateMode.UpdatedNone,
            Visibility.Visible,
            new PlaylistSummaryColumnSettings());

        Assert.IsTrue(owner.TryCommit(outerLease, outerInput).WasCommitted);

        Assert.IsTrue(nested);
        Assert.AreSame(nestedSummarySettings, workspace.PlaylistSummaryColumnsSettings);
        Assert.AreEqual(1, summaryNotifications);
    }

    [TestMethod]
    public void TryCommit_NoOpNestedPresentation_DoesNotSuppressOuterNotifications()
    {
        var table = new MainChartListViewModel();
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
        RegularChartListOwner owner = CreateOwner(table, workspace);
        RegularChartListRequestLease outerLease = owner.BeginRequest();
        RegularChartListBuildResult outerBuild = Build(owner, outerLease, new List<LibraryChartRow>());
        var sharedSummarySettings = new PlaylistSummaryColumnSettings();
        int visibilityNotifications = 0;
        int summaryNotifications = 0;
        bool nested = false;
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaylistWorkspaceViewModel.ColumnSettingsVisibilityForPlaylist))
            {
                visibilityNotifications++;
            }
            if (e.PropertyName == nameof(PlaylistWorkspaceViewModel.PlaylistSummaryColumnsSettings))
            {
                summaryNotifications++;
            }
        };
        table.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MainChartListViewModel.Rows) || nested)
            {
                return;
            }
            nested = true;
            RegularChartListRequestLease nestedLease = owner.BeginRequest();
            RegularChartListBuildResult nestedBuild = Build(owner, nestedLease, new List<LibraryChartRow>());
            RegularChartListPresentationResult nestedInput = CreateTerminalInput(
                nestedBuild,
                MainViewUpdateMode.UpdatedNone,
                Visibility.Visible,
                sharedSummarySettings);
            Assert.IsTrue(owner.TryCommit(nestedLease, nestedInput).WasCommitted);
        };
        RegularChartListPresentationResult outerInput = CreateTerminalInput(
            outerBuild,
            MainViewUpdateMode.UpdatedNone,
            Visibility.Visible,
            sharedSummarySettings);

        Assert.IsTrue(owner.TryCommit(outerLease, outerInput).WasCommitted);

        Assert.IsTrue(nested);
        Assert.AreEqual(1, visibilityNotifications);
        Assert.AreEqual(1, summaryNotifications);
    }

    [TestMethod]
    public void Dispose_CancelsCurrentRequestAndRejectsNewRequests()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        RegularChartListRequestLease lease = owner.BeginRequest();

        owner.Dispose();
        owner.Dispose();

        Assert.IsTrue(lease.Token.IsCancellationRequested);
        Assert.IsFalse(owner.TryBeginRequest(out _));
        Assert.ThrowsException<ObjectDisposedException>(() => owner.BeginRequest());
    }

    [TestMethod]
    public void VirtualOrderPrewarm_AllowsOneRunAndCompletionAllowsNextRun()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));

        Assert.IsTrue(owner.TryBeginVirtualOrderPrewarm(null, out RegularChartListPrewarmLease first));
        Assert.IsTrue(owner.IsVirtualOrderPrewarmRunning);
        Assert.IsFalse(owner.TryBeginVirtualOrderPrewarm(null, out _));

        using var staleCancellation = new CancellationTokenSource();
        using var staleLease = new RegularChartListPrewarmLease(-1, staleCancellation.Token, null);
        Assert.IsFalse(owner.CancelVirtualOrderPrewarm(staleLease));
        Assert.IsFalse(first.Token.IsCancellationRequested);
        Assert.IsTrue(owner.CancelVirtualOrderPrewarm(first));
        Assert.IsTrue(first.Token.IsCancellationRequested);

        Assert.IsFalse(owner.IsVirtualOrderPrewarmRunning);
        Assert.IsTrue(owner.TryBeginVirtualOrderPrewarm(null, out RegularChartListPrewarmLease second));
        Assert.AreEqual(first.RunId + 1, second.RunId);
        second.Dispose();
        owner.Dispose();
    }

    [TestMethod]
    public async Task StopAsync_CancelsAndDrainsVirtualOrderPrewarm()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        Assert.IsTrue(owner.TryBeginVirtualOrderPrewarm(null, out RegularChartListPrewarmLease lease));

        Task stopTask = owner.StopAsync();

        Assert.IsTrue(lease.Token.IsCancellationRequested);
        Assert.IsFalse(stopTask.IsCompleted);
        lease.Dispose();
        await stopTask;
        Assert.IsFalse(owner.TryBeginVirtualOrderPrewarm(null, out _));
        Assert.IsFalse(owner.TryBeginRequest(out _));
    }

    [TestMethod]
    public void SortKeyInvalidation_CancelsVirtualOrderPrewarm()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        Assert.IsTrue(owner.TryBeginVirtualOrderPrewarm(null, out RegularChartListPrewarmLease lease));

        owner.InvalidateIdentitySortKeys(clearSourceRows: false);

        Assert.IsTrue(lease.Token.IsCancellationRequested);
        lease.Dispose();
        owner.Dispose();
    }

    [TestMethod]
    public void ClearSortCache_CancelsPrewarmAndRejectsItsLatePublication()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        var versions = new RegularChartListExternalVersions(score: 0, chartInfo: 0, maintenanceHydration: 0);
        NormalLibrarySortCacheKey staleKey = owner.CreateVirtualOrderKey(
            owner.SourceGeneration,
            owner.SortKeyGeneration,
            nameof(LibraryChartRow.Title),
            ListSortDirection.Ascending,
            rowCount: 1,
            externalVersions: versions);
        Assert.IsTrue(owner.TryBeginVirtualOrderPrewarm(null, out RegularChartListPrewarmLease lease));

        owner.ClearSortCache();

        Assert.IsTrue(lease.Token.IsCancellationRequested);
        Assert.IsFalse(owner.TryPublishVirtualOrder(
            staleKey,
            CreateOrder(CreateSourceRow("Folder A", "a.bms")),
            versions));
        lease.Dispose();
        owner.Dispose();
    }

    [TestMethod]
    public async Task StopAsync_RejectsLateVirtualCachePublication()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        RegularVirtualSourceRowsLookup sourceLookup = owner.LookupVirtualSourceRows(null, includeBmsonRows: false);
        var rows = new List<ChartListSourceRow> { CreateSourceRow("Folder A", "a.bms") };
        var versions = new RegularChartListExternalVersions(score: 0, chartInfo: 0, maintenanceHydration: 0);
        NormalLibrarySortCacheKey orderKey = owner.CreateVirtualOrderKey(
            owner.SourceGeneration,
            owner.SortKeyGeneration,
            nameof(LibraryChartRow.Title),
            ListSortDirection.Ascending,
            rowCount: 1,
            externalVersions: versions);

        await owner.StopAsync();

        owner.TryPublishVirtualSourceRows(sourceLookup, rows);
        Assert.IsFalse(owner.LookupVirtualSourceRows(null, includeBmsonRows: false).CacheHit);
        Assert.IsFalse(owner.TryPublishVirtualOrder(orderKey, CreateOrder(rows[0]), versions));
        Assert.IsFalse(owner.IsCurrentVirtualGeneration(orderKey.SourceGeneration, orderKey.SortKeyGeneration));
    }

    [TestMethod]
    public void PrepareRowsApply_RowsReplacingThrows_CancelsPreparation()
    {
        var table = new MainChartListViewModel { Rows = new List<object>() };
        int canceled = 0;
        table.RowsReplacing += (_, _) => throw new InvalidOperationException("prepare failed");
        table.RowsReplacementCanceled += (_, _) => canceled++;

        Assert.ThrowsException<InvalidOperationException>(() => table.ApplyRows(new MainChartListRowsApplyRequest
        {
            Rows = new List<object>(),
            ColumnsSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD),
            SelectionPolicy = MainChartListSelectionPolicy.Preserve,
            Summary = MainChartListSummaryUpdate.Preserve(),
            Stopwatch = Stopwatch.StartNew()
        }));
        Assert.AreEqual(1, canceled);
    }

    [TestMethod]
    public void TryCommit_MainRowsPublishThrows_StillPublishesColumnPresentation()
    {
        var table = new MainChartListViewModel();
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
        RegularChartListOwner owner = CreateOwner(table, workspace);
        RegularChartListRequestLease lease = owner.BeginRequest();
        RegularChartListBuildResult build = Build(owner, lease, new List<LibraryChartRow>());
        int workspaceNotifications = 0;
        table.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainChartListViewModel.Rows))
            {
                throw new InvalidOperationException("rows publish failed");
            }
        };
        workspace.PropertyChanged += (_, _) => workspaceNotifications++;
        RegularChartListPresentationResult input = CreateTerminalInput(
            build,
            MainViewUpdateMode.FolderFilterSelected,
            Visibility.Visible,
            new PlaylistSummaryColumnSettings());

        Assert.ThrowsException<RegularChartListTerminalPublishException>(() => owner.TryCommit(lease, input));
        Assert.AreSame(build.Sort.RowsView, table.Rows);
        Assert.IsTrue(workspaceNotifications > 0);
    }

    [TestMethod]
    [DoNotParallelize]
    public void MainChartListColumnPresentation_LoadCommitAndReuseOwnsMainAndWorkspacePresentation()
    {
        CustomTableColumnSettings previousStandard = Settings.Default.StandardCustomTableColumnSettings;
        PlaylistSummaryColumnSettings previousSummary = Settings.Default.PlaylistSummaryColumnsSettings;
        try
        {
            var table = new MainChartListViewModel(
                action => action(),
                _ => { },
                new SettingsMainChartColumnSettingsStore(() => Settings.Default));
            var workspace = new PlaylistWorkspaceViewModel(
                action => action(),
                new MainChartListViewModel(action => action()),
                new PlaylistDetailBuildState(),
                new PlaylistDetailViewState(),
                _ => { },
                _ => { },
                () => new CustomFolderOutputSettingsSnapshot(),
                PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
                PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
                PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
                PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
                PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
                PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
                PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
                PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
                PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
                PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
                PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
                PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
                PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
                () => null!,
                () => null!,
                _ => { },
                new ObservableCollection<BMSTable>(),
                (_, _) => false,
                () => true,
                () => MainViewUpdateMode.FolderFilterSelected,
                () => Task.CompletedTask,
                () => false,
                () => { },
                _ => { },
                (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
            Settings.Default.StandardCustomTableColumnSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
            Settings.Default.PlaylistSummaryColumnsSettings = new PlaylistSummaryColumnSettings();
            RegularChartListOwner owner = CreateOwner(table, workspace);

            owner.InitializeColumnPresentation(MainViewUpdateMode.FolderFilterSelected);
            MainChartListColumnSelection second = table.ResolveColumnSettingForViewUpdate(
                MainViewUpdateMode.SortUpdated,
                MainViewUpdateMode.FolderFilterSelected);

            Assert.IsTrue(second.Reused);
            Assert.AreEqual(MainViewUpdateMode.FolderFilterSelected, second.AppliedMode);
            Assert.AreSame(table.ColumnsSettings, second.ColumnsSettings);
            Assert.AreSame(Settings.Default.PlaylistSummaryColumnsSettings, workspace.PlaylistSummaryColumnsSettings);
            Assert.AreEqual(Visibility.Collapsed, workspace.ColumnSettingsVisibilityForPlaylist);

            CustomTableColumnSettings beforeInit = Settings.Default.StandardCustomTableColumnSettings;
            owner.InitializeColumnPresentation(MainViewUpdateMode.FolderFilterSelected);

            Assert.AreNotSame(beforeInit, Settings.Default.StandardCustomTableColumnSettings);
            Assert.AreSame(Settings.Default.StandardCustomTableColumnSettings, table.ColumnsSettings);
            owner.Dispose();
        }
        finally
        {
            Settings.Default.StandardCustomTableColumnSettings = previousStandard;
            Settings.Default.PlaylistSummaryColumnsSettings = previousSummary;
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public void ResetCurrentColumnPresentation_ResetsAppliedModeAndPublishesRelatedState()
    {
        CustomTableColumnSettings previousPlayHistory = Settings.Default.PlayHistoryCustomTableColumnSettings;
        PlaylistSummaryColumnSettings previousSummary = Settings.Default.PlaylistSummaryColumnsSettings;
        try
        {
            Settings.Default.PlayHistoryCustomTableColumnSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAY_HISTORY);
            Settings.Default.PlaylistSummaryColumnsSettings = new PlaylistSummaryColumnSettings();
            var table = new MainChartListViewModel(
                action => action(),
                _ => { },
                new SettingsMainChartColumnSettingsStore(() => Settings.Default));
            var workspace = CreateWorkspaceForOwner();
            var owner = CreateOwner(table, workspace);
            var notifications = new List<string>();
            table.PropertyChanged += (_, e) => notifications.Add("table:" + e.PropertyName);
            workspace.PropertyChanged += (_, e) => notifications.Add("workspace:" + e.PropertyName);

            owner.InitializeColumnPresentation(MainViewUpdateMode.PlayHistorySelected);
            Settings.Default.PlayHistoryCustomTableColumnSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAY_HISTORY);
            CustomTableColumnSettings persistedBeforeReset = Settings.Default.PlayHistoryCustomTableColumnSettings;
            Settings.Default.PlaylistSummaryColumnsSettings = new PlaylistSummaryColumnSettings();
            workspace.ColumnSettingsVisibilityForPlaylist = Visibility.Visible;
            notifications.Clear();

            owner.ResetCurrentColumnPresentation();

            Assert.AreNotSame(persistedBeforeReset, table.ColumnsSettings);
            Assert.AreSame(Settings.Default.PlayHistoryCustomTableColumnSettings, table.ColumnsSettings);
            Assert.AreEqual(MainViewUpdateMode.PlayHistorySelected, table.LastAppliedColumnMode);
            Assert.AreEqual(Visibility.Collapsed, workspace.ColumnSettingsVisibilityForPlaylist);
            Assert.AreSame(Settings.Default.PlaylistSummaryColumnsSettings, workspace.PlaylistSummaryColumnsSettings);
            int tableColumnsIndex = notifications.IndexOf("table:ColumnsSettings");
            int visibilityIndex = notifications.IndexOf("workspace:ColumnSettingsVisibilityForPlaylist");
            int summaryIndex = notifications.IndexOf("workspace:PlaylistSummaryColumnsSettings");
            Assert.IsTrue(tableColumnsIndex >= 0);
            Assert.IsTrue(visibilityIndex > tableColumnsIndex);
            Assert.IsTrue(summaryIndex > visibilityIndex);
            owner.Dispose();
        }
        finally
        {
            Settings.Default.PlayHistoryCustomTableColumnSettings = previousPlayHistory;
            Settings.Default.PlaylistSummaryColumnsSettings = previousSummary;
        }
    }

    [TestMethod]
    public void ResetCurrentColumnPresentation_WithoutInitializationFailsWithoutMutation()
    {
        var table = new MainChartListViewModel(action => action());
        var workspace = CreateWorkspaceForOwner();
        var owner = CreateOwner(table, workspace);
        CustomTableColumnSettings columnsBefore = table.ColumnsSettings;
        PlaylistSummaryColumnSettings summaryBefore = workspace.PlaylistSummaryColumnsSettings;
        Visibility visibilityBefore = workspace.ColumnSettingsVisibilityForPlaylist;

        Assert.ThrowsException<InvalidOperationException>(() => owner.ResetCurrentColumnPresentation());

        Assert.AreSame(columnsBefore, table.ColumnsSettings);
        Assert.AreSame(summaryBefore, workspace.PlaylistSummaryColumnsSettings);
        Assert.AreEqual(visibilityBefore, workspace.ColumnSettingsVisibilityForPlaylist);
        Assert.IsNull(table.LastAppliedColumnMode);
        owner.Dispose();
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task PlaylistSummaryColumnSettings_ResetCreatesDefaultSettingsAndPublishesWorkspace()
    {
        PlaylistSummaryColumnSettings previousSummary = Settings.Default.PlaylistSummaryColumnsSettings;
        try
        {
            var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService
            {
                ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
            };
            var workspace = new PlaylistWorkspaceViewModel(
                action => action(),
                new MainChartListViewModel(action => action()),
                new PlaylistDetailBuildState(),
                new PlaylistDetailViewState(),
                _ => { },
                _ => { },
                () => new CustomFolderOutputSettingsSnapshot(),
                PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
                PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
                PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
                PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
                PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
                PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
                PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
                PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
                PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
                PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
                PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
                PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
                PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
                () => null!,
                () => null!,
                _ => { },
                new ObservableCollection<BMSTable>(),
                (_, _) => false,
                () => true,
                () => MainViewUpdateMode.FolderFilterSelected,
                () => Task.CompletedTask,
                () => false,
                () => { },
                _ => { },
                (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck, dialogs);
            var oldSettings = new PlaylistSummaryColumnSettings();
            Settings.Default.PlaylistSummaryColumnsSettings = oldSettings;
            workspace.CommitColumnPresentationWithoutNotification(
                workspace.ColumnSettingsVisibilityForPlaylist,
                oldSettings);
            int notificationCount = 0;
            workspace.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PlaylistWorkspaceViewModel.PlaylistSummaryColumnsSettings))
                {
                    notificationCount++;
                }
            };

            await workspace.ResetPlaylistSummaryColumnsToDefaultAsync();

            Assert.AreNotSame(oldSettings, Settings.Default.PlaylistSummaryColumnsSettings);
            Assert.AreSame(Settings.Default.PlaylistSummaryColumnsSettings, workspace.PlaylistSummaryColumnsSettings);
            Assert.IsTrue(notificationCount > 0);
        }
        finally
        {
            Settings.Default.PlaylistSummaryColumnsSettings = previousSummary;
        }
    }

    private static RegularChartListOwner CreateOwner(
        MainChartListViewModel table,
        PlaylistWorkspaceViewModel workspace)
    {
        return CreateOwner(table, workspace, action => action());
    }

    private static RegularChartListOwner CreateOwner(
        MainChartListViewModel table,
        PlaylistWorkspaceViewModel workspace,
        Action<Action> dispatchToUi,
        IUiScheduler? normalLibraryRefreshUiScheduler = null,
        Action<string>? log = null,
        PendingPackageWorkflowOwner? pendingPackageWorkflow = null)
    {
        return new RegularChartListOwner(
            table,
            workspace,
            log ?? (_ => { }),
            dispatchToUi,
            _ => { },
            pendingPackageWorkflow ?? CreatePendingPackageWorkflowOwner(),
            new ChartFileOperationSynchronizer(),
            new ChartMutationActivityOwner(),
            new NoOpFolderAutoRenamePlaybackPort(),
            normalLibraryRefreshUiScheduler ?? new TestUiScheduler(() => null!));
    }

    private static ReaderWriterLockSlimWrapper GetCatalogStorageRowsWriteGate(BMSLibrary library)
    {
        FieldInfo storageOwnerField = typeof(BMSLibrary).GetField(
            "catalogStorageRowsOwner",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var storageOwner = (CatalogStorageRowsOwner)storageOwnerField.GetValue(library)!;
        return storageOwner.WriteGate;
    }

    private static void PublishNormalLibraryRefreshResetNotification(
        BMSLibrary library,
        bool notifiesBmsFiles,
        bool notifiesBmsonSongs)
    {
        MethodInfo publishMethod = typeof(BMSLibrary).GetMethod(
            "PublishNormalLibraryRefreshResetNotification",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        publishMethod.Invoke(library, [notifiesBmsFiles, notifiesBmsonSongs]);
    }

    private static PendingPackageWorkflowOwner CreatePendingPackageWorkflowOwner()
    {
        return new PendingPackageWorkflowOwner(
              () => null!,
              new ChartFileOperationSynchronizer(),
              new ChartMutationActivityOwner(),
              new NoOpPendingPackageMutationPlaybackPort(),
              new TestUiDialogService(),
              () => new InstallDestinationWorkflowSettingsSnapshot(
                  showManualInstallConfirmation: false,
                  deletePendingPackageSourceAfterInstall: false),
              ExternalShellGatewayPolicy.Current);
    }

    private static PlaylistWorkspaceViewModel CreateWorkspaceForOwner(
        MainChartListViewModel? table = null,
        PlaylistDetailBuildState? buildState = null,
        PlaylistDetailViewState? viewState = null,
        Action<string>? detailRetentionLog = null)
    {
        return new PlaylistWorkspaceViewModel(
            action => action(),
            table ?? new MainChartListViewModel(action => action()),
            buildState ?? new PlaylistDetailBuildState(),
            viewState ?? new PlaylistDetailViewState(),
            _ => { },
            detailRetentionLog ?? (_ => { }),
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { },
            (_, _) => false,
            (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
    }

    private static TestableBmsFile CreateTestableBmsFile(string path)
    {
        var file = new TestableBmsFile { path = path };
        file.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        return file;
    }

    private static RenameChartFolderRequest CreateRenameRequest(BMSFile file)
    {
        ChartFile chart = ChartFileProjection.FromBmsFile(file);
        var target = new ChartOperationTarget(
            chart,
            playlistEntry: null,
            ChartOperationSourceScope.Library,
            isOwned: true,
            isPending: false,
            isPlaylistMissing: false,
            ChartOperationCapabilities.MoveInLibrary);
        Assert.IsTrue(RenameChartFolderRequest.TryCreate(target, out RenameChartFolderRequest request));
        return request;
    }

    private static void WithTemporarySongDb(Action<string> testAction)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_RegularOwner_" + Guid.NewGuid().ToString("N"));
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
            }
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

    private sealed class NoOpFolderAutoRenamePlaybackPort : IFolderAutoRenamePlaybackPort
    {
        public void StopPlaybackForCharts(IReadOnlyList<ChartFile> charts)
        {
        }

        public void StopPlaybackForFolderMutation()
        {
        }
    }

    private sealed class TestableBmsFile : BMSFile
    {
        internal void SetHash(string value)
        {
            hash = value;
        }

        internal void SetMaintenanceInfo(BMSFileMaintenanceInfo value)
        {
            SetMaintenanceInfo(value, suppressPropertyChanged: true);
        }
    }

    private static RegularChartListEntryRequest CreateEntryRequest(
        MainViewUpdateMode mode,
        string keywordFilter = "",
        ChartModeFilter modeFilter = ChartModeFilter.All)
    {
        return new RegularChartListEntryRequest
        {
            Filters = new ChartListFilterSnapshot(keywordFilter, modeFilter),
            Mode = mode,
            RequestedMode = mode,
            CurrentTreeMode = mode,
            IncludeBmsonRows = false,
            Stopwatch = Stopwatch.StartNew()
        };
    }

    private static ChartListSourceRow CreateSourceRow(string folder, string fileName, int? mode = null)
    {
        string path = string.IsNullOrEmpty(folder)
            ? fileName
            : System.IO.Path.Combine(@"C:\Charts", folder, fileName);
        var chart = new ChartFile(
            ChartFileKind.Bms,
            path,
            md5: fileName,
            sha256: null,
            title: fileName,
            rawTitle: fileName,
            artist: string.Empty,
            genre: string.Empty,
            folder: folder,
            tag: string.Empty,
            levelText: string.Empty,
            level: null,
            mode: mode,
            chartInfo: null,
            bmsFile: null,
            bmsonSong: null);
        return ChartListSourceRow.FromChartFile(chart);
    }

    private static PlaylistDetailSourceRow CreatePlaylistSourceRow(string md5)
    {
        ChartFile chart = CreateSourceRow("Playlist", md5).Chart;
        return new PlaylistDetailSourceRow(
            new BMSTableEntry(chart),
            chart);
    }

    private static ChartListOrder CreateOrder(params ChartListSourceRow[] sourceRows)
    {
        Assert.IsTrue(ChartListOrder.TryCreate(
            sourceRows,
            nameof(LibraryChartRow.Title),
            ListSortDirection.Ascending,
            out ChartListOrder order));
        return order;
    }

    private static RegularChartListBuildResult Build(
        RegularChartListOwner owner,
        RegularChartListRequestLease lease,
        IEnumerable<LibraryChartRow> rows,
        MainViewUpdateMode mode = MainViewUpdateMode.UpdatedNone)
    {
        var request = new RegularChartListRefreshRequest(
            mode,
            MainViewUpdateMode.TreeViewFilterNotChanged,
            parameter: null,
            MainViewUpdateMode.FolderFilterSelected,
            treeParameter: null,
            includeBmsonRows: true,
            virtualSubsetRequiredFailure: false,
            keywordFilter: string.Empty,
            ChartModeFilter.All,
            default);
        return owner.Build(lease, request, new RegularChartListBuildInput
        {
            CurrentTreeMode = MainViewUpdateMode.FolderFilterSelected,
            HasFolderRowsOverride = true,
            FolderRowsOverride = rows,
            SortCacheGeneration = new NormalLibrarySortCacheGenerationSnapshot(1, 1, 0, 0, 0, 0, 0, 0),
            Stopwatch = Stopwatch.StartNew()
        });
    }

    private static RegularMaterializedChartListApplyRequest CreateMaterializedApplyRequest(
        IEnumerable<LibraryChartRow> rows,
        MainViewUpdateMode appliedMode = MainViewUpdateMode.FolderFilterSelected)
    {
        var refresh = new RegularChartListRefreshRequest(
            MainViewUpdateMode.SortUpdated,
            MainViewUpdateMode.TreeViewFilterNotChanged,
            parameter: null,
            appliedMode,
            treeParameter: null,
            includeBmsonRows: false,
            virtualSubsetRequiredFailure: false,
            keywordFilter: string.Empty,
            ChartModeFilter.All,
            ChartListSortSpecification.Create(nameof(LibraryChartRow.Title), ListSortDirection.Ascending, hasValue: true));
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
        return new RegularMaterializedChartListApplyRequest
        {
            RefreshRequest = refresh,
            HasFolderRowsOverride = true,
            FolderRowsOverride = rows,
            ExternalVersions = new RegularChartListExternalVersions(0, 0, 0),
            ColumnSelection = new MainChartListColumnSelection(
                settings,
                reused: false,
                elapsedMs: 0L,
                appliedMode,
                Visibility.Collapsed,
                new PlaylistSummaryColumnSettings()),
            Mode = MainViewUpdateMode.SortUpdated,
            Stopwatch = Stopwatch.StartNew()
        };
    }

    private static RegularChartListPresentationResult CreateTerminalInput(
        RegularChartListBuildResult build,
        MainViewUpdateMode mode = MainViewUpdateMode.UpdatedNone,
        Visibility visibility = Visibility.Collapsed,
        PlaylistSummaryColumnSettings? summarySettings = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
        return RegularChartListPresentationResult.ForMaterialized(
            build,
            new MainChartListRowsApplyRequest
            {
                Rows = build.Sort.RowsView,
                ColumnsSettings = settings,
                SelectionPolicy = MainChartListSelectionPolicy.Preserve,
                Summary = MainChartListSummaryUpdate.NormalRows(build.Sort.RowsView),
                Stopwatch = stopwatch
            },
            new MainChartListColumnSelection(
                settings,
                reused: false,
                elapsedMs: 0L,
                mode,
                visibility,
                summarySettings ?? new PlaylistSummaryColumnSettings()),
            mode,
            stopwatch);
    }

    private static RegularChartListPresentationResult CreateVirtualTerminalInput(IList rows)
    {
        var stopwatch = Stopwatch.StartNew();
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
        return RegularChartListPresentationResult.ForVirtual(
            new MainChartListRowsApplyRequest
            {
                Rows = rows,
                ColumnsSettings = settings,
                SelectionPolicy = MainChartListSelectionPolicy.Preserve,
                Summary = MainChartListSummaryUpdate.NormalCounts(rows.Count, distinctFolderCount: -1),
                Stopwatch = stopwatch
            },
            new MainChartListColumnSelection(
                settings,
                reused: false,
                elapsedMs: 0L,
                MainViewUpdateMode.FolderFilterSelected,
                Visibility.Collapsed,
                new PlaylistSummaryColumnSettings()),
            MainViewUpdateMode.FolderFilterSelected,
            stopwatch);
    }

    private static Task StartLongRunning(Action action)
    {
        return Task.Factory.StartNew(
            action,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private static Task StartLongRunningAsync(Func<Task> action)
    {
        return Task.Factory.StartNew(
                action,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default)
            .Unwrap();
    }

    private sealed class ActionQueueUiScheduler : IUiScheduler
    {
        private readonly Action<Action> enqueue;

        internal ActionQueueUiScheduler(Action<Action> enqueue)
        {
            this.enqueue = enqueue ?? throw new ArgumentNullException(nameof(enqueue));
        }

        public bool IsAvailable => true;

        public bool CanExecuteInline => false;

        public bool CheckAccess() => false;

        public IUiScheduledOperation Schedule(
            Action action,
            UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            var operation = new ActionQueueUiScheduledOperation();
            enqueue(() => operation.Execute(action));
            return operation;
        }

        public void Invoke(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            action();
        }

        public T Invoke<T>(Func<T> action, UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            return action();
        }

        public async Task InvokeAsync(
            Action action,
            UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            await Schedule(action, priority).Completion.ConfigureAwait(false);
        }

        public async Task InvokeAsync(
            Func<Task> action,
            UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            await action().ConfigureAwait(false);
        }
    }

    private sealed class ActionQueueUiScheduledOperation : IUiScheduledOperation
    {
        private readonly TaskCompletionSource<bool> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsAccepted => true;

        public bool IsCompleted => completion.Task.IsCompleted;

        public bool IsAborted => false;

        public string RejectionReason => string.Empty;

        public Task Completion => completion.Task;

        public void Abort()
        {
        }

        internal void Execute(Action action)
        {
            try
            {
                action();
                completion.TrySetResult(true);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
                throw;
            }
        }
    }

    private sealed class AbortingUiScheduler : IUiScheduler
    {
        public bool IsAvailable => true;

        public bool CanExecuteInline => false;

        public bool CheckAccess() => false;

        public IUiScheduledOperation Schedule(
            Action action,
            UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            return new AbortedUiScheduledOperation();
        }

        public void Invoke(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            throw new InvalidOperationException("Synchronous invoke is not supported.");
        }

        public T Invoke<T>(Func<T> action, UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            throw new InvalidOperationException("Synchronous invoke is not supported.");
        }

        public Task InvokeAsync(
            Action action,
            UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            return Task.FromException(new InvalidOperationException("UI operation was aborted."));
        }

        public Task InvokeAsync(
            Func<Task> action,
            UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            return Task.FromException(new InvalidOperationException("UI operation was aborted."));
        }
    }

    private sealed class AbortedUiScheduledOperation : IUiScheduledOperation
    {
        private static readonly Task AbortedCompletion = Task.FromCanceled(
            new CancellationToken(canceled: true));

        public bool IsAccepted => true;

        public bool IsCompleted => true;

        public bool IsAborted => true;

        public string RejectionReason => "UI operation was aborted.";

        public Task Completion => AbortedCompletion;

        public void Abort()
        {
        }
    }

    private sealed class BlockingIndexedSourceRows : IReadOnlyList<ChartListSourceRow>, IDisposable
    {
        private readonly IReadOnlyList<ChartListSourceRow> rows;
        private int indexReadCount;

        internal BlockingIndexedSourceRows(params ChartListSourceRow[] rows)
        {
            this.rows = rows;
        }

        internal ManualResetEventSlim IndexReadStarted { get; } = new();

        internal ManualResetEventSlim ReleaseIndexRead { get; } = new();

        internal int IndexReadCount => Volatile.Read(ref indexReadCount);

        public int Count => rows.Count;

        public ChartListSourceRow this[int index]
        {
            get
            {
                Interlocked.Increment(ref indexReadCount);
                IndexReadStarted.Set();
                if (!ReleaseIndexRead.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("Summary index read was not released.");
                }
                return rows[index];
            }
        }

        public IEnumerator<ChartListSourceRow> GetEnumerator()
        {
            return rows.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public void Dispose()
        {
            IndexReadStarted.Dispose();
            ReleaseIndexRead.Dispose();
        }
    }
}
