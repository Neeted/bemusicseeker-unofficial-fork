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
using static BeMusicSeeker.Tests.RegularChartListOwnerTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class RegularChartNavigationTests
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
}
