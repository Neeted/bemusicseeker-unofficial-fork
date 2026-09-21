using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using BeMusicSeeker.Diagnostics;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using static BeMusicSeeker.Tests.PlaylistWorkspaceFixtureFactory;
using static BeMusicSeeker.Tests.PlaylistWorkspaceTestDataSupport;
using PlaylistWorkspaceViewModelTests = BeMusicSeeker.Tests.PlaylistWorkspaceExternalSourceTests;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaylistWorkspacePresentationStateTests
{
    [TestInitialize]
    public void TestInitialize()
    {
        TestResourceInitializer.EnsureJapaneseResources();
    }

    [TestMethod]
    public void PlaylistSummaryConfiguration_IsOwnedByPlaylistWorkspace()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        var columns = new PlaylistSummaryColumnSettings();
        var propertyNames = new List<string>();
        var rootPropertyNames = new List<string>();
        viewModel.PlaylistWorkspace.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName!);
        viewModel.PropertyChanged += (_, e) => rootPropertyNames.Add(e.PropertyName!);

        PlaylistColumnPresentationCommit columnCommit =
            viewModel.PlaylistWorkspace.CommitColumnPresentationWithoutNotification(
                Visibility.Collapsed,
                columns);
        viewModel.PlaylistWorkspace.PublishColumnPresentation(columnCommit);
        viewModel.PlaylistWorkspace.ColumnSettingsVisibilityForPlaylist = Visibility.Visible;
        viewModel.PlaylistWorkspace.GridHeaderText = "Playlist summary";
        viewModel.PlaylistWorkspace.PlaylistSummaryKeywordFilter = "title:test";
        viewModel.PlaylistWorkspace.PlaylistSummaryOwnedFilter = PlaylistOwnedFilter.OwnedComplete;

        Assert.AreSame(columns, viewModel.PlaylistWorkspace.PlaylistSummaryColumnsSettings);
        Assert.AreEqual(Visibility.Visible, viewModel.PlaylistWorkspace.ColumnSettingsVisibilityForPlaylist);
        Assert.AreEqual("Playlist summary", viewModel.PlaylistWorkspace.GridHeaderText);
        Assert.AreEqual("title:test", viewModel.PlaylistWorkspace.PlaylistSummaryKeywordFilter);
        Assert.AreEqual(PlaylistOwnedFilter.OwnedComplete, viewModel.PlaylistWorkspace.PlaylistSummaryOwnedFilter);
        CollectionAssert.Contains(propertyNames, nameof(PlaylistWorkspaceViewModel.PlaylistSummaryColumnsSettings));
        CollectionAssert.Contains(propertyNames, nameof(PlaylistWorkspaceViewModel.ColumnSettingsVisibilityForPlaylist));
        CollectionAssert.DoesNotContain(rootPropertyNames, nameof(PlaylistWorkspaceViewModel.PlaylistSummaryColumnsSettings));
        CollectionAssert.DoesNotContain(rootPropertyNames, nameof(PlaylistWorkspaceViewModel.ColumnSettingsVisibilityForPlaylist));
    }

    [TestMethod]
    public void PlaylistSummaryKeywordSearchAssistance_IsFieldsOnlyAndIncludesOutput()
    {
        PlaylistWorkspaceViewModel workspace = MainWindowViewModelTestFactory.Create().PlaylistWorkspace;

        KeywordSearchPresentationState state = workspace.FocusPlaylistSummaryKeywordSearch("ou", 2);

        Assert.IsTrue(state.IsOpen);
        Assert.AreEqual(GridKeywordSearchContext.PlaylistSummary, state.Context);
        Assert.AreEqual(KeywordSearchPresentationSectionKind.Fields, state.Sections.Single().Kind);
        Assert.IsTrue(state.VisibleItems.Any(item => item.DisplayText == "output:"));
        Assert.IsFalse(state.VisibleItems.Any(item => item.Kind == KeywordSearchPresentationItemKind.Value));

        KeywordSearchPresentationState blurred = workspace.BlurPlaylistSummaryKeywordSearch();
        Assert.IsFalse(blurred.IsOpen);
    }

    [TestMethod]
    public void MainTablePresentationCommit_CombinesWorkspaceStateBeforePublishingNotifications()
    {
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
        var columns = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
        var summaryColumns = new PlaylistSummaryColumnSettings();
        var selection = new MainChartListColumnSelection(
            columns,
            reused: false,
            elapsedMs: 0L,
            MainViewUpdateMode.FolderFilterSelected,
            Visibility.Visible,
            summaryColumns);
        var propertyNames = new List<string>();
        workspace.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName!);

        PlaylistMainTablePresentationCommit commit = workspace.CommitMainTablePresentationWithoutNotification(
            selection,
            playlistDetailActive: true,
            playlistSummaryActive: false);

        Assert.AreEqual(Visibility.Visible, workspace.ColumnSettingsVisibilityForPlaylist);
        Assert.AreSame(summaryColumns, workspace.PlaylistSummaryColumnsSettings);
        Assert.IsTrue(workspace.IsPlaylistDetailViewActive);
        Assert.AreEqual(0, propertyNames.Count);

        workspace.PublishMainTablePresentation(commit);

        CollectionAssert.Contains(propertyNames, nameof(PlaylistWorkspaceViewModel.ColumnSettingsVisibilityForPlaylist));
        CollectionAssert.Contains(propertyNames, nameof(PlaylistWorkspaceViewModel.PlaylistSummaryColumnsSettings));
        CollectionAssert.Contains(propertyNames, nameof(PlaylistWorkspaceViewModel.IsPlaylistDetailViewActive));
    }

    [TestMethod]
    public void PlaylistWorkspaceSummarySortRequestDrainsOwnerPresentationState()
    {
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
        workspace.PlaylistPresentationRefreshRequested += (_, request) =>
        {
            if (request.Kind == PlaylistPresentationRefreshKind.SummaryData)
            {
                workspace.ApplyPlaylistSummaryDataRefresh(deferred: false, request.RebuildAsync);
            }
            else if (request.Kind == PlaylistPresentationRefreshKind.SummaryPresentation)
            {
                workspace.ApplyPlaylistSummaryPresentationRefresh(deferred: false);
            }
        };
        workspace.IsPlaylistSummaryMode = true;
        long dataGeneration = workspace.BeginPlaylistSummaryDataRebuildGeneration();
        Assert.IsTrue(workspace.TrySetPlaylistSummaryRowsCache(
            new[]
            {
                new PlaylistSummaryRow { TotalCharts = 1 },
                new PlaylistSummaryRow { TotalCharts = 3 }
            },
            dataGeneration));
        long presentationGenerationBefore = workspace.CurrentPlaylistSummaryPresentationGeneration;
        int callerThreadId = Thread.CurrentThread.ManagedThreadId;
        int propertyChangedCount = 0;
        int propertyChangedThreadId = 0;
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaylistWorkspaceViewModel.PlaylistSummarySortParameters))
            {
                propertyChangedCount++;
                propertyChangedThreadId = Thread.CurrentThread.ManagedThreadId;
            }
        };

        workspace.RequestPlaylistSummarySort(
            nameof(PlaylistSummaryRow.TotalCharts),
            System.ComponentModel.ListSortDirection.Descending);

        Assert.AreEqual(1, propertyChangedCount);
        Assert.AreEqual(callerThreadId, propertyChangedThreadId);
        Assert.AreEqual(nameof(PlaylistSummaryRow.TotalCharts), workspace.PlaylistSummarySortParameters.ColumnsName);
        Assert.AreEqual(System.ComponentModel.ListSortDirection.Descending, workspace.PlaylistSummarySortParameters.Direction);
        Assert.IsTrue(workspace.CurrentPlaylistSummaryPresentationGeneration > presentationGenerationBefore);
        Assert.AreEqual(2, workspace.PlaylistSummaryView.Count);
        Assert.AreEqual(3, workspace.PlaylistSummaryView[0].TotalCharts);
    }

    [TestMethod]
    public void PlaylistWorkspaceSummaryFiltersDrainVisiblePresentationAndStayDeferredWhenHidden()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        workspace.IsPlaylistSummaryMode = true;
        long dataGeneration = workspace.BeginPlaylistSummaryDataRebuildGeneration();
        Assert.IsTrue(workspace.TrySetPlaylistSummaryRowsCache(
            new[]
            {
                new PlaylistSummaryRow { Name = "alpha", TotalCharts = 2, OwnedCharts = 2 },
                new PlaylistSummaryRow { Name = "beta", TotalCharts = 3, OwnedCharts = 1 }
            },
            dataGeneration));

        workspace.RequestPlaylistSummaryPresentationRefresh();
        Assert.AreEqual(2, workspace.PlaylistSummaryView.Count);
        long initialPresentationGeneration = workspace.CurrentPlaylistSummaryPresentationGeneration;
        workspace.PlaylistSummaryKeywordFilter = "alpha";
        long keywordPresentationGeneration = workspace.CurrentPlaylistSummaryPresentationGeneration;
        Assert.IsTrue(keywordPresentationGeneration > initialPresentationGeneration);
        Assert.AreEqual(1, workspace.PlaylistSummaryView.Count);
        Assert.AreEqual("alpha", workspace.PlaylistSummaryView[0].Name);
        Assert.AreEqual(
            string.Format(
                BeMusicSeeker.Properties.Resources.Playlist_summary_format,
                2,
                1),
            workspace.PlaylistSummaryText);

        workspace.PlaylistSummaryKeywordFilter = string.Empty;
        Assert.AreEqual(2, workspace.PlaylistSummaryView.Count);
        workspace.PlaylistSummaryOwnedFilter = PlaylistOwnedFilter.OwnedComplete;
        long ownedPresentationGeneration = workspace.CurrentPlaylistSummaryPresentationGeneration;
        Assert.IsTrue(ownedPresentationGeneration > keywordPresentationGeneration);
        Assert.AreEqual(1, workspace.PlaylistSummaryView.Count);
        Assert.AreEqual("alpha", workspace.PlaylistSummaryView[0].Name);

        Assert.IsTrue(workspace.SetPlaylistSummaryMode(enabled: false));
        long hiddenPresentationGeneration = workspace.CurrentPlaylistSummaryPresentationGeneration;
        workspace.PlaylistSummaryKeywordFilter = string.Empty;
        workspace.PlaylistSummaryOwnedFilter = PlaylistOwnedFilter.All;
        Assert.AreEqual(hiddenPresentationGeneration, workspace.CurrentPlaylistSummaryPresentationGeneration);
    }

    [TestMethod]
    public void PlaylistWorkspaceSummaryPresentationRefreshRespectsShellSuppressionGate()
    {
        bool suppressed = true;
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
            out _,
            presentationRefreshDeferredProvider: request =>
                request.Kind == PlaylistPresentationRefreshKind.SummaryPresentation && suppressed);
        workspace.IsPlaylistSummaryMode = true;
        long dataGeneration = workspace.BeginPlaylistSummaryDataRebuildGeneration();
        Assert.IsTrue(workspace.TrySetPlaylistSummaryRowsCache(
            new[]
            {
                new PlaylistSummaryRow { Name = "alpha", TotalCharts = 2, OwnedCharts = 2 },
                new PlaylistSummaryRow { Name = "beta", TotalCharts = 3, OwnedCharts = 1 }
            },
            dataGeneration));

        suppressed = false;
        workspace.RequestPlaylistSummaryPresentationRefresh();
        suppressed = true;
        long initialPresentationGeneration = workspace.CurrentPlaylistSummaryPresentationGeneration;
        workspace.PlaylistSummaryKeywordFilter = "alpha";
        Assert.AreEqual(initialPresentationGeneration, workspace.CurrentPlaylistSummaryPresentationGeneration);
        Assert.AreEqual(2, workspace.PlaylistSummaryView.Count);
        Assert.IsTrue(workspace.HasDeferredPlaylistSummaryPresentationRefresh());

        suppressed = false;
        workspace.PlaylistSummaryKeywordFilter = "beta";
        Assert.IsTrue(workspace.CurrentPlaylistSummaryPresentationGeneration > initialPresentationGeneration);
        Assert.AreEqual(1, workspace.PlaylistSummaryView.Count);
        Assert.AreEqual("beta", workspace.PlaylistSummaryView[0].Name);
        Assert.IsFalse(workspace.HasDeferredPlaylistSummaryPresentationRefresh());
    }

    [TestMethod]
    public void PlaylistWorkspaceDetailSortRequestCommitsOwnerStateBeforeEvent()
    {
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
        int raisedCount = 0;
        MainChartListSortRequestedEventArgs? observedRequest = null;
        workspace.PlaylistDetailSortChanged += (_, request) =>
        {
            raisedCount++;
            observedRequest = request;
            Assert.AreEqual(nameof(PlaylistDetailRow.Level), workspace.PlaylistDetailSortParameters.ColumnsName);
            Assert.AreEqual(System.ComponentModel.ListSortDirection.Descending, workspace.PlaylistDetailSortParameters.Direction);
        };

        workspace.RequestPlaylistDetailSort(
            nameof(PlaylistDetailRow.Level),
            System.ComponentModel.ListSortDirection.Descending);

        Assert.AreEqual(1, raisedCount);
        Assert.IsNotNull(observedRequest);
        Assert.AreEqual(MainChartListSortTarget.Regular, observedRequest.Target);
        Assert.AreEqual(1L, observedRequest.OwnerRevision);

        workspace.RequestPlaylistDetailSort(
            nameof(PlaylistDetailRow.Level),
            System.ComponentModel.ListSortDirection.Descending);

        Assert.AreEqual(1, raisedCount);
    }

    [TestMethod]
    public void PlaylistWorkspaceRoutesSharedDetailSortOnlyWhileDetailIsActive()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        workspace.InitializePlaylistDetailSort(new ChartListSortParameters
        {
            ColumnsName = nameof(PlaylistDetailRow.Title),
            Direction = System.ComponentModel.ListSortDirection.Ascending
        });
        int raisedCount = 0;
        workspace.PlaylistDetailSortChanged += (_, _) => raisedCount++;

        Assert.IsFalse(workspace.TryRequestPlaylistDetailSort(
            nameof(PlaylistDetailRow.Level),
            System.ComponentModel.ListSortDirection.Descending));
        workspace.IsPlaylistDetailViewActive = true;
        Assert.IsTrue(workspace.TryRequestPlaylistDetailSort(
            nameof(PlaylistDetailRow.Level),
            System.ComponentModel.ListSortDirection.Descending));
        Assert.AreEqual(1, raisedCount);
        Assert.AreEqual(nameof(PlaylistDetailRow.Level), workspace.PlaylistDetailSortParameters.ColumnsName);

        workspace.IsPlaylistSummaryMode = true;
        Assert.IsFalse(workspace.TryRequestPlaylistDetailSort(
            nameof(PlaylistDetailRow.Artist),
            System.ComponentModel.ListSortDirection.Ascending));
        Assert.AreEqual(nameof(PlaylistDetailRow.Level), workspace.PlaylistDetailSortParameters.ColumnsName);

        workspace.IsPlaylistSummaryMode = false;
        workspace.IsPlaylistDetailViewActive = false;
        Assert.IsFalse(workspace.TryRequestPlaylistDetailSort(
            nameof(PlaylistDetailRow.Artist),
            System.ComponentModel.ListSortDirection.Ascending));
    }

    [TestMethod]
    public void PlaylistWorkspaceDetailSortInitializationUsesFallbackOnlyOnce()
    {
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
        var initialSort = new ChartListSortParameters
        {
            ColumnsName = nameof(PlaylistDetailRow.Level),
            Direction = System.ComponentModel.ListSortDirection.Descending
        };

        workspace.InitializePlaylistDetailSort(initialSort);

        Assert.AreEqual(nameof(PlaylistDetailRow.Level), workspace.PlaylistDetailSortParameters.ColumnsName);
        Assert.AreEqual(System.ComponentModel.ListSortDirection.Descending, workspace.PlaylistDetailSortParameters.Direction);
        Assert.AreNotSame(initialSort, workspace.PlaylistDetailSortParameters);

        workspace.InitializePlaylistDetailSort(new ChartListSortParameters
        {
            ColumnsName = nameof(PlaylistDetailRow.Title),
            Direction = System.ComponentModel.ListSortDirection.Ascending
        });

        Assert.AreEqual(nameof(PlaylistDetailRow.Level), workspace.PlaylistDetailSortParameters.ColumnsName);
        Assert.AreEqual(System.ComponentModel.ListSortDirection.Descending, workspace.PlaylistDetailSortParameters.Direction);
    }

    [TestMethod]
    public void PlaylistWorkspaceDetailFilterRequestCommitsOwnerStateBeforeEventAndRejectsNone()
    {
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
        workspace.InitializePlaylistDetailFilter(
            new ChartListFilterSnapshot("  title:Alpha  ", ChartModeFilter.All));
        int raisedCount = 0;
        PlaylistDetailFilterChangedEventArgs? firstRequest = null;
        PlaylistDetailFilterChangedEventArgs? secondRequest = null;
        workspace.PlaylistDetailFilterChanged += (_, request) =>
        {
            raisedCount++;
            if (raisedCount == 1)
            {
                firstRequest = request;
            }
            else
            {
                secondRequest = request;
            }
            Assert.AreEqual("  title:Beta  ", workspace.PlaylistDetailFilterSnapshot.KeywordFilter);
        };

        workspace.RequestPlaylistDetailFilter(
            MainViewUpdateMode.KeywordFilterUpdated,
            new ChartListFilterSnapshot("  title:Beta  ", ChartModeFilter.All));

        Assert.AreEqual(1, raisedCount);
        Assert.IsNotNull(firstRequest);
        Assert.AreEqual(MainViewUpdateMode.KeywordFilterUpdated, firstRequest.UpdateMode);
        Assert.AreEqual(1L, firstRequest.OwnerRevision);
        Assert.IsTrue(workspace.IsCurrentPlaylistDetailFilterRequest(firstRequest));

        workspace.RequestPlaylistDetailFilter(
            MainViewUpdateMode.KeywordFilterUpdated,
            new ChartListFilterSnapshot("  title:Beta  ", ChartModeFilter.All));
        Assert.AreEqual(1, raisedCount);

        workspace.RequestPlaylistDetailFilter(
            MainViewUpdateMode.ModeFilterUpdated,
            new ChartListFilterSnapshot("  title:Beta  ", ChartModeFilter._7KEYS));

        Assert.AreEqual(2, raisedCount);
        Assert.IsNotNull(secondRequest);
        Assert.AreEqual(MainViewUpdateMode.ModeFilterUpdated, secondRequest.UpdateMode);
        Assert.AreEqual(2L, secondRequest.OwnerRevision);
        Assert.IsFalse(workspace.IsCurrentPlaylistDetailFilterRequest(firstRequest));
        Assert.IsTrue(workspace.IsCurrentPlaylistDetailFilterRequest(secondRequest));
        Assert.AreEqual(ChartModeFilter._7KEYS, workspace.PlaylistDetailFilterSnapshot.ModeFilter);

        workspace.RequestPlaylistDetailFilter(
            MainViewUpdateMode.ModeFilterUpdated,
            new ChartListFilterSnapshot("  title:Beta  ", ChartModeFilter.None));

        Assert.AreEqual(2, raisedCount);
        Assert.AreEqual(ChartModeFilter._7KEYS, workspace.PlaylistDetailFilterSnapshot.ModeFilter);
    }

    [TestMethod]
    public void PlaylistWorkspaceRoutesSharedDetailFilterOnlyWhileDetailIsActive()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        workspace.InitializePlaylistDetailFilter(new ChartListFilterSnapshot("initial", ChartModeFilter.All));
        int raisedCount = 0;
        workspace.PlaylistDetailFilterChanged += (_, _) => raisedCount++;

        Assert.IsFalse(workspace.TryRequestPlaylistDetailFilter(
            MainViewUpdateMode.KeywordFilterUpdated,
            new ChartListFilterSnapshot("before-detail", ChartModeFilter.All)));
        Assert.AreEqual("initial", workspace.PlaylistDetailFilterSnapshot.KeywordFilter);

        workspace.IsPlaylistDetailViewActive = true;
        Assert.IsTrue(workspace.TryRequestPlaylistDetailFilter(
            MainViewUpdateMode.KeywordFilterUpdated,
            new ChartListFilterSnapshot("detail", ChartModeFilter.All)));
        Assert.AreEqual(1, raisedCount);
        Assert.AreEqual("detail", workspace.PlaylistDetailFilterSnapshot.KeywordFilter);

        Assert.IsTrue(workspace.TryRequestPlaylistDetailFilter(
            MainViewUpdateMode.ModeFilterUpdated,
            new ChartListFilterSnapshot("detail", ChartModeFilter.None)));
        Assert.AreEqual(1, raisedCount);
        Assert.AreEqual(ChartModeFilter.All, workspace.PlaylistDetailFilterSnapshot.ModeFilter);

        workspace.IsPlaylistSummaryMode = true;
        Assert.IsFalse(workspace.TryRequestPlaylistDetailFilter(
            MainViewUpdateMode.KeywordFilterUpdated,
            new ChartListFilterSnapshot("summary", ChartModeFilter.All)));
        Assert.AreEqual("detail", workspace.PlaylistDetailFilterSnapshot.KeywordFilter);

        workspace.IsPlaylistSummaryMode = false;
        workspace.IsPlaylistDetailViewActive = false;
        Assert.IsFalse(workspace.TryRequestPlaylistDetailFilter(
            MainViewUpdateMode.KeywordFilterUpdated,
            new ChartListFilterSnapshot("after-detail", ChartModeFilter.All)));
    }

    [TestMethod]
    public void PlaylistWorkspaceDetailFilterInitializationResynchronizesAcrossDetailEntries()
    {
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

        int raisedCount = 0;
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(workspace.PlaylistDetailFilterSnapshot))
            {
                raisedCount++;
            }
        };

        workspace.InitializePlaylistDetailFilter(
            new ChartListFilterSnapshot("title:Alpha", ChartModeFilter.All));
        workspace.InitializePlaylistDetailFilter(
            new ChartListFilterSnapshot("title:Beta", ChartModeFilter._7KEYS));
        workspace.InitializePlaylistDetailFilter(
            new ChartListFilterSnapshot("title:Beta", ChartModeFilter._7KEYS));

        Assert.AreEqual(2, raisedCount);
        Assert.AreEqual("title:Beta", workspace.PlaylistDetailFilterSnapshot.KeywordFilter);
        Assert.AreEqual(ChartModeFilter._7KEYS, workspace.PlaylistDetailFilterSnapshot.ModeFilter);
    }

    [TestMethod]
    public void PlaylistWorkspaceKeywordWarning_IsOwnedByPlaylistWorkspace()
    {
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
        var propertyNames = new List<string>();
        workspace.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName!);

        workspace.PlaylistSummaryKeywordFilter = "memo:warning";

        StringAssert.Contains(workspace.PlaylistSummaryKeywordSearchWarningText, "memo");
        Assert.IsTrue(workspace.HasPlaylistSummaryKeywordSearchWarning);
        CollectionAssert.Contains(propertyNames, nameof(PlaylistWorkspaceViewModel.PlaylistSummaryKeywordSearchWarningText));
        CollectionAssert.Contains(propertyNames, nameof(PlaylistWorkspaceViewModel.HasPlaylistSummaryKeywordSearchWarning));
    }

    [TestMethod]
    public void PlaylistWorkspaceSummaryModeTransitionOwnsHeaderTextAndCancellation()
    {
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
        var propertyNames = new List<string>();
        workspace.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName!);
        workspace.GridHeaderText = "stale header";
        workspace.PlaylistSummaryText = "stale summary";

        Assert.IsTrue(workspace.SetPlaylistSummaryMode(enabled: true));
        Assert.IsTrue(workspace.IsPlaylistSummaryMode);
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Playlist_summary_header, workspace.GridHeaderText);
        Assert.AreEqual("stale summary", workspace.PlaylistSummaryText);
        CollectionAssert.Contains(propertyNames, nameof(PlaylistWorkspaceViewModel.IsPlaylistSummaryMode));
        CollectionAssert.Contains(propertyNames, nameof(PlaylistWorkspaceViewModel.GridHeaderText));

        Assert.IsTrue(workspace.TryBeginPlaylistSummaryDataBuild(out PlaylistSummaryDataBuildRequest request));
        propertyNames.Clear();

        Assert.IsTrue(workspace.SetPlaylistSummaryMode(enabled: false));
        Assert.IsTrue(request.CancellationToken.IsCancellationRequested);
        Assert.IsFalse(workspace.IsPlaylistSummaryMode);
        Assert.AreEqual(string.Empty, workspace.GridHeaderText);
        Assert.AreEqual(string.Empty, workspace.PlaylistSummaryText);
        CollectionAssert.AreEqual(
            new[]
            {
                nameof(PlaylistWorkspaceViewModel.IsPlaylistSummaryMode),
                nameof(PlaylistWorkspaceViewModel.GridHeaderText),
                nameof(PlaylistWorkspaceViewModel.PlaylistSummaryText)
            },
            propertyNames);

        Assert.IsFalse(workspace.SetPlaylistSummaryMode(enabled: false));
        workspace.CompletePlaylistSummaryDataBuild(request);
    }

    [TestMethod]
    public void PlaylistWorkspaceSummaryApplyCommitsRowsAndTextWithoutSelectionRestore()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        PlaylistWorkspaceViewModel workspace = viewModel.PlaylistWorkspace;
        workspace.IsPlaylistSummaryMode = true;
        long dataGeneration = workspace.BeginPlaylistSummaryDataRebuildGeneration();
        long presentationGeneration = workspace.BeginPlaylistSummaryPresentationGeneration();
        workspace.InvalidatePlaylistSummaryCache();
        long cacheGeneration = workspace.CurrentPlaylistSummaryRowsCacheGeneration;
        var rows = new ObservableCollection<PlaylistSummaryRow> { new() { TotalCharts = 3 } };
        var notifications = new List<string>();
        workspace.PropertyChanged += (_, e) => notifications.Add(e.PropertyName!);

        bool applied = workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
        {
            Rows = rows,
            SummaryText = "3 charts / 1 playlist",
            PresentationGeneration = presentationGeneration,
            DataRebuildGeneration = dataGeneration,
            CacheGeneration = cacheGeneration
        });

        Assert.IsTrue(applied);
        CollectionAssert.AreEqual(
            new[] { nameof(PlaylistWorkspaceViewModel.PlaylistSummaryView), nameof(PlaylistWorkspaceViewModel.PlaylistSummaryText) },
            notifications);
        CollectionAssert.AreEqual(rows, workspace.PlaylistSummaryView);
        Assert.AreEqual("3 charts / 1 playlist", workspace.PlaylistSummaryText);
        Assert.IsTrue(
            workspace.TryGetAppliedPlaylistSummaryPerformanceInteraction(
                out PerformanceInteraction interaction));
        Assert.AreEqual(dataGeneration, interaction.InteractionId);
        Assert.AreEqual(presentationGeneration, interaction.Generation);

        workspace.BeginPlaylistSummaryPresentationGeneration();

        Assert.IsTrue(
            workspace.TryGetAppliedPlaylistSummaryPerformanceInteraction(
                out PerformanceInteraction retainedInteraction));
        Assert.AreEqual(interaction, retainedInteraction);
    }

    [TestMethod]
    public void PlaylistWorkspaceSummaryApplyRejectsStaleGenerationAndInactiveMode()
    {
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
        workspace.IsPlaylistSummaryMode = true;
        long dataGeneration = workspace.BeginPlaylistSummaryDataRebuildGeneration();
        long presentationGeneration = workspace.BeginPlaylistSummaryPresentationGeneration();
        workspace.InvalidatePlaylistSummaryCache();
        long cacheGeneration = workspace.CurrentPlaylistSummaryRowsCacheGeneration;
        ObservableCollection<PlaylistSummaryRow> originalRows = workspace.PlaylistSummaryView;

        Assert.IsFalse(workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
        {
            Rows = new ObservableCollection<PlaylistSummaryRow> { new() },
            SummaryText = "stale presentation",
            PresentationGeneration = presentationGeneration - 1,
            DataRebuildGeneration = dataGeneration,
            CacheGeneration = cacheGeneration
        }));

        long currentDataGeneration = workspace.BeginPlaylistSummaryDataRebuildGeneration();
        Assert.IsFalse(workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
        {
            Rows = new ObservableCollection<PlaylistSummaryRow> { new() },
            SummaryText = "stale data",
            PresentationGeneration = presentationGeneration,
            DataRebuildGeneration = dataGeneration,
            CacheGeneration = cacheGeneration
        }));

        workspace.InvalidatePlaylistSummaryCache();
        long currentCacheGeneration = workspace.CurrentPlaylistSummaryRowsCacheGeneration;
        Assert.IsFalse(workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
        {
            Rows = new ObservableCollection<PlaylistSummaryRow> { new() },
            SummaryText = "stale cache",
            PresentationGeneration = presentationGeneration,
            DataRebuildGeneration = currentDataGeneration,
            CacheGeneration = currentCacheGeneration - 1
        }));

        workspace.IsPlaylistSummaryMode = false;
        Assert.IsFalse(workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
        {
            Rows = new ObservableCollection<PlaylistSummaryRow> { new() },
            SummaryText = "inactive",
            PresentationGeneration = presentationGeneration,
            DataRebuildGeneration = currentDataGeneration,
            CacheGeneration = currentCacheGeneration
        }));
        Assert.AreSame(originalRows, workspace.PlaylistSummaryView);
        Assert.AreEqual(string.Empty, workspace.PlaylistSummaryText);
    }

    [TestMethod]
    public void PlaylistWorkspaceSummaryPublishFailureAggregatesPropertyPublishFailure()
    {
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
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck)
        {
            IsPlaylistSummaryMode = true
        };
        long dataGeneration = workspace.BeginPlaylistSummaryDataRebuildGeneration();
        long presentationGeneration = workspace.BeginPlaylistSummaryPresentationGeneration();
        var rows = new ObservableCollection<PlaylistSummaryRow> { new() };
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaylistWorkspaceViewModel.PlaylistSummaryView))
            {
                throw new InvalidOperationException("rows binding failed");
            }
        };
        Assert.ThrowsException<PlaylistSummaryPublishException>(() => workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
        {
            Rows = rows,
            SummaryText = "committed",
            PresentationGeneration = presentationGeneration,
            DataRebuildGeneration = dataGeneration
        }));

        CollectionAssert.AreEqual(rows, workspace.PlaylistSummaryView);
        Assert.AreEqual("committed", workspace.PlaylistSummaryText);
    }

    [TestMethod]
    public void PlaylistWorkspaceSummaryApplyKeepsStableSourceAndSkipsSamePresentationIdentity()
    {
        PlaylistWorkspaceViewModel workspace = MainWindowViewModelTestFactory.Create().PlaylistWorkspace;
        workspace.IsPlaylistSummaryMode = true;
        ObservableCollection<PlaylistSummaryRow> stableSource = workspace.PlaylistSummaryView;
        int resetCount = 0;
        int sourceNotificationCount = 0;
        stableSource.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                resetCount++;
            }
        };
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaylistWorkspaceViewModel.PlaylistSummaryView))
            {
                sourceNotificationCount++;
            }
        };
        var identity = new PlaylistSummaryPresentationIdentity(
            sourceVersion: 11L,
            keywordFilter: string.Empty,
            PlaylistOwnedFilter.All,
            nameof(PlaylistSummaryRow.Name),
            ListSortDirection.Ascending);

        Assert.IsTrue(workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
        {
            Rows = [new PlaylistSummaryRow { PlaylistId = 1 }],
            SummaryText = "first",
            Identity = identity,
            PresentationGeneration = workspace.BeginPlaylistSummaryPresentationGeneration()
        }));
        Assert.IsTrue(workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
        {
            Rows = [new PlaylistSummaryRow { PlaylistId = 2 }],
            SummaryText = "first",
            Identity = identity,
            PresentationGeneration = workspace.BeginPlaylistSummaryPresentationGeneration()
        }));

        Assert.AreSame(stableSource, workspace.PlaylistSummaryView);
        Assert.AreEqual(1, workspace.PlaylistSummaryView[0].PlaylistId);
        Assert.AreEqual(1, resetCount);
        Assert.AreEqual(1, sourceNotificationCount);
    }

    [TestMethod]
    public void PlaylistWorkspaceSummaryTerminalCommitsRowsBeforeVisibleMode()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        PlaylistWorkspaceViewModel workspace = viewModel.PlaylistWorkspace;
        var publicationOrder = new List<string>();
        workspace.PlaylistSummaryView.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                publicationOrder.Add("rows");
            }
        };
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaylistWorkspaceViewModel.IsPlaylistSummaryMode))
            {
                publicationOrder.Add("mode");
            }
        };

        Assert.IsTrue(workspace.RequestPlaylistSummaryMode(enabled: true));
        Assert.IsFalse(workspace.IsPlaylistSummaryMode);
        Assert.IsTrue(workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
        {
            Rows = [new PlaylistSummaryRow { PlaylistId = 3 }],
            SummaryText = "ready",
            Identity = new PlaylistSummaryPresentationIdentity(
                sourceVersion: 17L,
                keywordFilter: string.Empty,
                PlaylistOwnedFilter.All,
                nameof(PlaylistSummaryRow.Name),
                ListSortDirection.Ascending),
            PresentationGeneration = workspace.BeginPlaylistSummaryPresentationGeneration()
        }));

        CollectionAssert.AreEqual(new[] { "rows", "mode" }, publicationOrder);
        Assert.IsTrue(workspace.IsPlaylistSummaryMode);
        Assert.IsFalse(workspace.IsPlaylistDetailViewActive);
        Assert.AreEqual(
            MainViewOperationSection.Playlist,
            viewModel.MainChartList.CurrentOperationContext.OperationSection);
    }

    [TestMethod]
    public void PlaylistWorkspaceSummaryCacheSurvivesModeExitUntilCatalogInvalidation()
    {
        PlaylistWorkspaceViewModel workspace = MainWindowViewModelTestFactory.Create().PlaylistWorkspace;
        workspace.IsPlaylistSummaryMode = true;
        long dataGeneration = workspace.BeginPlaylistSummaryDataRebuildGeneration();
        Assert.IsTrue(workspace.TrySetPlaylistSummaryRowsCache(
            [new PlaylistSummaryRow { PlaylistId = 7 }],
            dataGeneration));

        workspace.IsPlaylistSummaryMode = false;
        List<PlaylistSummaryRow> cachedRows =
            workspace.GetPlaylistSummaryRowsCacheSnapshot(out long cacheGeneration, out long cachedDataGeneration);

        Assert.IsNotNull(cachedRows);
        Assert.AreEqual(7, cachedRows[0].PlaylistId);
        Assert.AreEqual(dataGeneration, cachedDataGeneration);
        Assert.AreEqual(workspace.CurrentPlaylistSummaryRowsCacheGeneration, cacheGeneration);

        workspace.InvalidatePlaylistSummaryCache();
        Assert.IsNull(workspace.GetPlaylistSummaryRowsCacheSnapshot(out _, out _));
    }

    [TestMethod]
    public async Task PlaylistWorkspaceBmtPersistenceFailureFaultsTaskAndSuppressesSummaryRefresh()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistWorkspacePresentationStateTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var first = new BMSTable { playlist_id = 1, name = "First", symbol = "F", bmt_sort = 1 };
            var second = new BMSTable { playlist_id = 2, name = "Second", symbol = "S", bmt_sort = 2 };
            var playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>([first, second])
            };
            playlist.CommitBMSTableHeadersToDB([first, second]);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.Execute(
                    "CREATE TRIGGER fail_bmt_sort_insert BEFORE INSERT ON playlist "
                    + "WHEN EXISTS (SELECT 1 FROM playlist WHERE playlist_id = NEW.playlist_id) "
                    + "BEGIN SELECT RAISE(ABORT, 'bmt sort persistence failure'); END;");
                db.Execute(
                    "CREATE TRIGGER fail_bmt_sort_update BEFORE UPDATE OF bmt_sort ON playlist "
                    + "BEGIN SELECT RAISE(ABORT, 'bmt sort persistence failure'); END;");
            }

            PlaylistSummaryBmtSortCoordinator bmtSort =
                new(() => playlist, () => playlist.BMSTables);
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                playlistSummaryBmtSort: bmtSort);
            int refreshRequestCount = 0;
            workspace.PlaylistPresentationRefreshRequested += (_, _) => refreshRequestCount++;

            Task operation = workspace.ApplyCurrentVisibleBmtOrderAsync(
            [
                new PlaylistSummaryRow { PlaylistId = second.playlist_id, TableRef = second },
                new PlaylistSummaryRow { PlaylistId = first.playlist_id, TableRef = first }
            ]);
            Exception? failure = null;
            try
            {
                await operation;
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            Assert.IsNotNull(failure);
            Assert.IsTrue(
                failure!.ToString().Contains("bmt sort persistence failure", StringComparison.Ordinal));
            Assert.AreEqual(0, refreshRequestCount);
            using (var verify = new LR2SongDBExtended(songDbPath))
            {
                Assert.AreEqual(
                    1,
                    verify.ExecuteScalar<int>(
                        "SELECT bmt_sort FROM playlist WHERE playlist_id = ?;",
                        first.playlist_id));
                Assert.AreEqual(
                    2,
                    verify.ExecuteScalar<int>(
                        "SELECT bmt_sort FROM playlist WHERE playlist_id = ?;",
                        second.playlist_id));
            }
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task PlaylistWorkspaceSummarySelectionRestoreEventFailureAggregatesWithPropertyFailure()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceViewModelTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var first = new BMSTable { playlist_id = 1, name = "First", symbol = "F", bmt_sort = 1 };
            var second = new BMSTable { playlist_id = 2, name = "Second", symbol = "S", bmt_sort = 2 };
            var playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>([first, second])
            };
            var restoreRequests = new List<PlaylistSummarySelectionRestoreRequest>();
            PlaylistSummaryBmtSortCoordinator bmtSort = new(() => playlist, () => playlist.BMSTables);
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                playlistSummaryBmtSort: bmtSort,
                presentationRefreshDeferredProvider: _ => true,
                selectionRestoreSink: request =>
                {
                    restoreRequests.Add(request);
                    throw new InvalidOperationException("selection restore failed");
                });
            workspace.IsPlaylistSummaryMode = true;

            await workspace.DropSummaryRowsInBmtOrderAsync(
                [
                    new PlaylistSummaryRow { PlaylistId = first.playlist_id, TableRef = first },
                    new PlaylistSummaryRow { PlaylistId = second.playlist_id, TableRef = second }
                ],
                [new PlaylistSummaryRow { PlaylistId = second.playlist_id, TableRef = second }],
                visibleInsertIndex: 0,
                currentPlaylistId: second.playlist_id);
            long dataRebuildGeneration = workspace.CurrentPlaylistSummaryDataRebuildGeneration;
            Assert.IsTrue(dataRebuildGeneration > 0L);
            Assert.IsTrue(workspace.TryBeginPlaylistSummaryDataBuild(out PlaylistSummaryDataBuildRequest buildRequest));
            try
            {
                workspace.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(PlaylistWorkspaceViewModel.PlaylistSummaryView))
                    {
                        throw new InvalidOperationException("rows binding failed");
                    }
                };
                PlaylistSummaryPublishException exception = Assert.ThrowsException<PlaylistSummaryPublishException>(() =>
                    workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
                    {
                        Rows = new ObservableCollection<PlaylistSummaryRow>(),
                        PresentationGeneration = workspace.CurrentPlaylistSummaryPresentationGeneration,
                        DataRebuildGeneration = buildRequest.Generation,
                        CacheGeneration = buildRequest.CacheGeneration
                    }));

                var aggregate = exception.InnerException as AggregateException;
                Assert.IsNotNull(aggregate);
                Assert.AreEqual(2, aggregate!.InnerExceptions.Count);
                Assert.AreEqual(1, restoreRequests.Count);
                CollectionAssert.AreEquivalent(new[] { second.playlist_id }, restoreRequests[0].PlaylistIds.ToArray());
                Assert.AreEqual(second.playlist_id, restoreRequests[0].CurrentPlaylistId);
            }
            finally
            {
                workspace.CompletePlaylistSummaryDataBuild(buildRequest);
            }
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task PlaylistWorkspaceSummarySelectionRestoreMissingSubscriberAggregatesWithPropertyFailure()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceViewModelTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var first = new BMSTable { playlist_id = 1, name = "First", symbol = "F", bmt_sort = 1 };
            var second = new BMSTable { playlist_id = 2, name = "Second", symbol = "S", bmt_sort = 2 };
            var playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>([first, second])
            };
            Action<PlaylistSummarySelectionRestoreRequest> restoreHandler = _ => { };
            PlaylistSummaryBmtSortCoordinator bmtSort = new(() => playlist, () => playlist.BMSTables);
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                playlistSummaryBmtSort: bmtSort,
                presentationRefreshDeferredProvider: _ => true,
                selectionRestoreSink: restoreHandler);
            workspace.PlaylistSummarySelectionRestoreRequested -= restoreHandler;
            workspace.IsPlaylistSummaryMode = true;

            await workspace.DropSummaryRowsInBmtOrderAsync(
                [
                    new PlaylistSummaryRow { PlaylistId = first.playlist_id, TableRef = first },
                    new PlaylistSummaryRow { PlaylistId = second.playlist_id, TableRef = second }
                ],
                [new PlaylistSummaryRow { PlaylistId = second.playlist_id, TableRef = second }],
                visibleInsertIndex: 0,
                currentPlaylistId: second.playlist_id);
            Assert.IsTrue(workspace.TryBeginPlaylistSummaryDataBuild(out PlaylistSummaryDataBuildRequest buildRequest));
            try
            {
                workspace.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(PlaylistWorkspaceViewModel.PlaylistSummaryView))
                    {
                        throw new InvalidOperationException("rows binding failed");
                    }
                };
                PlaylistSummaryPublishException exception = Assert.ThrowsException<PlaylistSummaryPublishException>(() =>
                    workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
                    {
                        Rows = new ObservableCollection<PlaylistSummaryRow>(),
                        PresentationGeneration = workspace.CurrentPlaylistSummaryPresentationGeneration,
                        DataRebuildGeneration = buildRequest.Generation,
                        CacheGeneration = buildRequest.CacheGeneration
                    }));

                var aggregate = exception.InnerException as AggregateException;
                Assert.IsNotNull(aggregate);
                Assert.AreEqual(2, aggregate!.InnerExceptions.Count);
            }
            finally
            {
                workspace.CompletePlaylistSummaryDataBuild(buildRequest);
            }
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task PlaylistWorkspaceSummarySelectionRestoreDoesNotReachUnsubscribedView()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceViewModelTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var first = new BMSTable { playlist_id = 1, name = "First", symbol = "F", bmt_sort = 1 };
            var second = new BMSTable { playlist_id = 2, name = "Second", symbol = "S", bmt_sort = 2 };
            var playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>([first, second])
            };
            Queue<Action> pendingActions = new();
            int invocationCount = 0;
            Action<PlaylistSummarySelectionRestoreRequest> restoreHandler = _ => invocationCount++;
            PlaylistSummaryBmtSortCoordinator bmtSort = new(() => playlist, () => playlist.BMSTables);
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                dispatchPresentation: action => pendingActions.Enqueue(action),
                playlistStoreProvider: () => playlist,
                playlistSummaryBmtSort: bmtSort,
                presentationRefreshDeferredProvider: _ => true,
                selectionRestoreSink: restoreHandler);
            workspace.IsPlaylistSummaryMode = true;

            await workspace.DropSummaryRowsInBmtOrderAsync(
                [
                    new PlaylistSummaryRow { PlaylistId = first.playlist_id, TableRef = first },
                    new PlaylistSummaryRow { PlaylistId = second.playlist_id, TableRef = second }
                ],
                [new PlaylistSummaryRow { PlaylistId = second.playlist_id, TableRef = second }],
                visibleInsertIndex: 0,
                currentPlaylistId: second.playlist_id);
            Assert.IsTrue(workspace.TryBeginPlaylistSummaryDataBuild(out PlaylistSummaryDataBuildRequest buildRequest));
            try
            {
                Assert.IsTrue(workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
                {
                    Rows = new ObservableCollection<PlaylistSummaryRow>(),
                    PresentationGeneration = workspace.CurrentPlaylistSummaryPresentationGeneration,
                    DataRebuildGeneration = buildRequest.Generation,
                    CacheGeneration = buildRequest.CacheGeneration
                }));
            }
            finally
            {
                workspace.CompletePlaylistSummaryDataBuild(buildRequest);
            }

            Assert.IsTrue(pendingActions.Count > 0);
            workspace.PlaylistSummarySelectionRestoreRequested -= restoreHandler;
            while (pendingActions.Count > 0)
            {
                pendingActions.Dequeue()();
            }
            Assert.AreEqual(0, invocationCount);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

}
