using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
public sealed class RegularChartViewBuildAndOrderingTests
{

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
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
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
    public void ApplyMainLibraryView_PreparesColdResourceHealthBeforeVirtualRows()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string chartPath = Path.Combine(Path.GetDirectoryName(songDbPath)!, "resource-health-warning.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n#TITLE Resource health warning\r\n#WAV01 missing.wav\r\n#00111:01\r\n");
            var file = BMSFile.CreateBMSFileFromFile(chartPath);
            file.SetMaintenanceInfo(new BMSFileMaintenanceInfo(file)
            {
                hash = file.hash,
                wav_files_defined = 1,
                wav_files_existing = 0
            }, suppressPropertyChanged: true);
            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                new OwnedChartCollectionTestSupport.TestFileMutationService(),
                new PlaylistSummaryAggregationTestSupport.RecordingDialogService());
            OwnedChartCollectionTestSupport.SetLibraryFilesWithoutNotification(library, [file]);
            Assert.IsTrue(OwnedChartCollectionTestSupport.HasNoCurrentResourceHealthIndex(library));

            var table = new MainChartListViewModel();
            PlaylistWorkspaceViewModel workspace = CreateWorkspaceForOwner(table);
            using RegularChartListOwner owner = CreateOwner(table, workspace);
            var route = new ChartListRefreshRoute(
                ChartListRefreshRouteKind.ContinueMainLibrary,
                MainViewUpdateMode.FolderFilterSelected,
                MainViewUpdateMode.FolderFilterSelected,
                MainViewUpdateMode.FolderFilterSelected,
                isPlaylistTreeActive: false,
                includeBmsonRows: false);

            RegularChartListEntryResult result = owner.ApplyMainLibraryView(
                route,
                library,
                parameter: null,
                treeParameter: null,
                preserveSummary: false,
                Stopwatch.StartNew());

            Assert.IsTrue(result.WasCommitted);
            Assert.IsFalse(OwnedChartCollectionTestSupport.HasNoCurrentResourceHealthIndex(library));
            Assert.AreEqual(1, table.Rows.Count);
            var row = (LibraryChartRow)table.Rows[0]!;
            StringAssert.Contains(row.WarningDigestText, Resources.WarningDigest_ResourceMissing);
        });
    }


    [TestMethod]
    public void ApplyMainLibraryView_PreparesResourceHealthBeforeWarningSortAndReusesRows()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string missingPath = Path.Combine(root, "warning-sort-missing.bms");
            string healthyPath = Path.Combine(root, "warning-sort-healthy.bms");
            File.WriteAllText(
                missingPath,
                "#PLAYER 1\r\n#TITLE Zeta healthy-name\r\n#WAV01 missing.wav\r\n#00111:01\r\n");
            File.WriteAllText(
                healthyPath,
                "#PLAYER 1\r\n#TITLE Alpha warning-name\r\n#WAV01 present.wav\r\n#00111:01\r\n");
            File.WriteAllBytes(Path.Combine(root, "present.wav"), [1, 2, 3]);
            var missing = BMSFile.CreateBMSFileFromFile(missingPath);
            missing.SetMaintenanceInfo(new BMSFileMaintenanceInfo(missing)
            {
                hash = missing.hash,
                wav_files_defined = 1,
                wav_files_existing = 0
            }, suppressPropertyChanged: true);
            var healthy = BMSFile.CreateBMSFileFromFile(healthyPath);
            healthy.SetMaintenanceInfo(new BMSFileMaintenanceInfo(healthy)
            {
                hash = healthy.hash,
                wav_files_defined = 1,
                wav_files_existing = 1
            }, suppressPropertyChanged: true);
            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                new OwnedChartCollectionTestSupport.TestFileMutationService(),
                new PlaylistSummaryAggregationTestSupport.RecordingDialogService());
            OwnedChartCollectionTestSupport.SetLibraryFilesWithoutNotification(library, [missing, healthy]);
            Assert.IsTrue(OwnedChartCollectionTestSupport.HasNoCurrentResourceHealthIndex(library));

            var table = new MainChartListViewModel();
            PlaylistWorkspaceViewModel workspace = CreateWorkspaceForOwner(table);
            using RegularChartListOwner owner = CreateOwner(table, workspace);
            owner.QueueSort(new MainChartListSortRequestedEventArgs(
                nameof(LibraryChartRow.WarningDigestText),
                ListSortDirection.Descending,
                MainChartListSortTarget.Regular));
            var route = new ChartListRefreshRoute(
                ChartListRefreshRouteKind.ContinueMainLibrary,
                MainViewUpdateMode.FolderFilterSelected,
                MainViewUpdateMode.FolderFilterSelected,
                MainViewUpdateMode.FolderFilterSelected,
                isPlaylistTreeActive: false,
                includeBmsonRows: false);

            RegularChartListEntryResult first = owner.ApplyMainLibraryView(
                route,
                library,
                parameter: null,
                treeParameter: null,
                preserveSummary: false,
                Stopwatch.StartNew());

            Assert.IsTrue(first.WasCommitted);
            Assert.IsFalse(OwnedChartCollectionTestSupport.HasNoCurrentResourceHealthIndex(library));
            Assert.AreEqual(2, table.Rows.Count);
            var firstRow = (LibraryChartRow)table.Rows[0]!;
            var secondRow = (LibraryChartRow)table.Rows[1]!;
            Assert.IsTrue(firstRow.WarningDigestText.Length > secondRow.WarningDigestText.Length);
            StringAssert.Contains(firstRow.WarningDigestText, Resources.WarningDigest_ResourceMissing);
            Assert.IsFalse(secondRow.WarningDigestText.Contains(
                Resources.WarningDigest_ResourceMissing,
                StringComparison.Ordinal));
            ResourceHealthIndexSnapshot firstSnapshot =
                library.TryGetCurrentResourceHealthIndexSnapshotForView();
            Assert.IsTrue(firstSnapshot.GetProjection(
                ChartFileProjection.FromBmsFile(missing)).HasIssues);

            var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
            RegularVirtualNormalLibraryApplyResult second = owner.TryApplyVirtualNormalLibrary(new RegularVirtualNormalLibraryApplyRequest
            {
                Library = library,
                IncludeBmsonRows = false,
                TreeFilter = null,
                KeywordFilter = string.Empty,
                ModeFilter = ChartModeFilter.All,
                SortColumnName = nameof(LibraryChartRow.WarningDigestText),
                SortDirection = ListSortDirection.Descending,
                ExternalVersions = new RegularChartListExternalVersions(
                    library.ScoreSnapshotVersion,
                    library.ChartInfoIndexVersion,
                    library.MaintenanceHydrationCompletedVersion),
                ColumnSelection = new MainChartListColumnSelection(
                    settings,
                    reused: false,
                    elapsedMs: 0L,
                    MainViewUpdateMode.FolderFilterSelected,
                    Visibility.Collapsed,
                    new PlaylistSummaryColumnSettings()),
                Mode = MainViewUpdateMode.FolderFilterSelected,
                Stopwatch = Stopwatch.StartNew(),
                RetireDetailSource = true,
                Reason = "warning_sort_cached"
            });

            Assert.IsTrue(second.WasCommitted);
            Assert.IsTrue(second.SourceRowsCacheHit);
            Assert.IsTrue(second.SortCacheHit);
            Assert.AreSame(second.RowsView, table.Rows);
            Assert.AreSame(firstSnapshot, library.TryGetCurrentResourceHealthIndexSnapshotForView());
            StringAssert.Contains(((LibraryChartRow)table.Rows[0]!).WarningDigestText, Resources.WarningDigest_ResourceMissing);
        });
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
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
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
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
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
        Assert.AreEqual("Alpha", ((LibraryChartRow)table.Rows[0]!).Title);
        Assert.AreEqual("Beta", ((LibraryChartRow)table.Rows[1]!).Title);
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
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
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
        Assert.AreEqual("nested.bms", ((LibraryChartRow)table.Rows[0]!).Title);
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
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
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
        var sourceRow = ChartListSourceRow.FromChartFile(ChartFileProjection.FromBmsFile(file));

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
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
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
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
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
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
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
        var firstLibrary = (BMSLibrary)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BMSLibrary));
        var secondLibrary = (BMSLibrary)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BMSLibrary));
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
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
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
        var firstLibrary = (BMSLibrary)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BMSLibrary));
        var secondLibrary = (BMSLibrary)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BMSLibrary));
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
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
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
        var firstLibrary = (BMSLibrary)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BMSLibrary));
        var secondLibrary = (BMSLibrary)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BMSLibrary));
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
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
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
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
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
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
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
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
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
        Assert.AreEqual("Alpha", ((LibraryChartRow)table.Rows[0]!).Title);
        Assert.AreEqual("Bravo", ((LibraryChartRow)table.Rows[1]!).Title);
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
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
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
        Assert.AreEqual("Alpha", ((LibraryChartRow)table.Rows[0]!).Title);
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
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
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
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
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
        Assert.AreEqual("Seven", ((LibraryChartRow)table.Rows[0]!).Title);
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
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
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
        var library = (BMSLibrary)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BMSLibrary));
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
        DuplicateGroup[] groups = new[]
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
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
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
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
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
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
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
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
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
}
