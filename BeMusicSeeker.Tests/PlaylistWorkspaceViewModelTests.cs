using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaylistWorkspaceViewModelTests
{
    [TestInitialize]
    public void TestInitialize()
    {
        TestResourceInitializer.EnsureJapaneseResources();
    }

    [TestMethod]
    public void SourceText_OwnsPlaylistPresentationStateOutsideRoot()
    {
        string rootSource = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs");
        string workspaceSource = SourceTextTestHelper.ReadPlaylistWorkspaceViewModelSourceText();
        string logicalSource = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string mainWindowSource = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "Views", "MainWindow.cs");
        string mainChartListSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "ViewModels", "MainWindow", "MainChartListViewModel.cs");

        foreach (string rootField in new[]
        {
            "_PlaylistSummarySortParameters",
            "_PlaylistSummaryColumnsSettings",
            "_ColumnSettingsVisibilityForPlaylist",
            "_PlaylistSummaryView",
            "playlistSummaryPresentationGeneration",
            "playlistSummaryDataRebuildGeneration",
            "playlistSummaryRowsCacheGeneration",
            "lockPlaylistSummaryRowsCache",
            "playlistSummaryRowsCache",
            "playlistSummaryTableCountCache",
            "deferredPlaylistSummaryRefreshRequested",
            "deferredPlaylistSummaryPresentationRefreshRequested",
            "previousPlaylistSummaryViewWeakReference",
            "_IsPlaylistSummaryMode",
            "_IsPlaylistDetailViewActive",
            "_UseAsyncChartRowsViewBinding",
            "_GridHeaderText",
            "_PlaylistSummaryKeywordFilter",
            "_PlaylistSummaryKeywordSearchWarningText",
            "_IsPlaylistSummaryKeywordSearchHelpOpen",
            "_PlaylistSummaryKeywordSearchSuggestions",
            "_IsPlaylistSummaryKeywordSearchSuggestionPopupOpen",
            "_PlaylistSummaryKeywordSearchSuggestionHeaderText",
            "_PlaylistSummaryOwnedFilter"
        })
        {
            Assert.AreEqual(-1, rootSource.IndexOf(rootField, StringComparison.Ordinal), rootField);
        }

        StringAssert.Contains(workspaceSource, "public sealed partial class PlaylistWorkspaceViewModel : ViewModel");
        StringAssert.Contains(workspaceSource, "private ObservableCollection<PlaylistSummaryRow> playlistSummaryView");
        StringAssert.Contains(workspaceSource, "private WeakReference<ObservableCollection<PlaylistSummaryRow>> previousPlaylistSummaryViewWeakReference;");
        StringAssert.Contains(workspaceSource, "private string playlistSummaryText = string.Empty;");
        StringAssert.Contains(workspaceSource, "internal bool TryApplyPlaylistSummary(PlaylistSummaryApplyRequest request)");
        StringAssert.Contains(workspaceSource, "private CancellationTokenSource playlistSummaryDataBuildCancellation;");
        StringAssert.Contains(workspaceSource, "internal bool TryBeginPlaylistSummaryDataBuild(out PlaylistSummaryDataBuildRequest request)");
        StringAssert.Contains(workspaceSource, "internal bool TryGetPlaylistSummaryTableCount(");
        StringAssert.Contains(workspaceSource, "internal PlaylistSummaryDeferredRefreshKind TakeDeferredPlaylistSummaryRefresh(bool dataRefreshRequired)");
        StringAssert.Contains(workspaceSource, "internal PlaylistSummaryDataRefreshRequestResult RequestPlaylistSummaryDataRefresh(");
        StringAssert.Contains(workspaceSource, "internal long LastPlaylistSummaryBuildCompletedTimestamp");
        StringAssert.Contains(logicalSource, "public PlaylistWorkspaceViewModel PlaylistWorkspace { get; }");
        StringAssert.Contains(logicalSource, "PlaylistWorkspace = new PlaylistWorkspaceViewModel(");
        StringAssert.Contains(logicalSource, "playlistDetailBuildState,");
        StringAssert.Contains(logicalSource, "playlistViewState,");
        StringAssert.Contains(logicalSource, "LogPlaylistRetention);");
        Assert.AreEqual(-1, logicalSource.IndexOf("PlaylistWorkspace.PropertyChanged += PlaylistWorkspacePropertyChanged;", StringComparison.Ordinal));
        Assert.AreEqual(-1, logicalSource.IndexOf("private void PlaylistWorkspacePropertyChanged(", StringComparison.Ordinal));
        StringAssert.Contains(logicalSource, "PlaylistWorkspace.PlaylistSummaryFilterChanged += PlaylistWorkspacePlaylistSummaryFilterChanged;");
        StringAssert.Contains(logicalSource, "private void PlaylistWorkspacePlaylistSummaryFilterChanged(");
        Assert.AreEqual(-1, rootSource.IndexOf("public ObservableCollection<PlaylistSummaryRow> PlaylistSummaryView", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("internal event EventHandler<PlaylistSummaryViewAppliedEventArgs> PlaylistSummaryViewApplied", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("BuildPlaylistSummaryDataRefreshDecision", StringComparison.Ordinal));
        StringAssert.Contains(mainWindowSource, "viewModel.PlaylistWorkspace.PlaylistSummaryViewApplied += MainWindowViewModel_PlaylistSummaryViewApplied;");
        Assert.AreEqual(-1, rootSource.IndexOf("BuildPlaylistSummaryRows", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("BuildPlaylistSummaryPresentationRows", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("CanApplyPlaylistSummaryPresentation", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("private void ApplyPlaylistSummaryPresentation", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("lastPlaylistSummaryBuildElapsedMs", StringComparison.Ordinal));
        StringAssert.Contains(workspaceSource, "private long lastPlaylistSummaryBuildElapsedMs;");
        string buildOwnerSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "ViewModels", "MainWindow", "PlaylistWorkspaceViewModel.PlaylistSummaryBuild.cs");
        StringAssert.Contains(buildOwnerSource, "internal long RebuildPlaylistSummaryView(");
        StringAssert.Contains(buildOwnerSource, "private PlaylistSummaryRowsBuildResult BuildPlaylistSummaryRows(");
        StringAssert.Contains(buildOwnerSource, "internal static PlaylistSummaryPresentationResult BuildPlaylistSummaryPresentationRows(");
        Assert.AreEqual(-1, buildOwnerSource.IndexOf("DispatcherHelper.UIDispatcher", StringComparison.Ordinal));
        StringAssert.Contains(buildOwnerSource, "dispatchPresentation(Reflect);");
        StringAssert.Contains(logicalSource, "PlaylistWorkspace.PlaylistSummaryViewApplied += PlaylistWorkspacePlaylistSummaryViewApplied;");
        Assert.AreEqual(-1, rootSource.IndexOf("MainTableDisplayRefreshRequested", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("MainTableSortParameters", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("public void ExecSort", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("public void ExecPlaylistSummarySort", StringComparison.Ordinal));
        StringAssert.Contains(mainChartListSource, "internal event EventHandler DisplayRefreshRequested;");
        StringAssert.Contains(mainChartListSource, "internal event EventHandler<MainChartListSortRequestedEventArgs> SortRequested;");
        StringAssert.Contains(workspaceSource, "internal void RequestPlaylistSummarySort(");
        Assert.IsFalse(mainWindowSource.Contains("MainChartList.DisplayRefreshRequested"));
        string customTableSource = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "Views", "CustomTableView.cs");
        StringAssert.Contains(customTableSource, "subscribedMainChartList.DisplayRefreshRequested += MainChartListDisplayRefreshRequested;");
        StringAssert.Contains(mainWindowSource, "viewModel.MainChartList.RequestSort(e.SortMemberPath, e.Direction);");
        StringAssert.Contains(mainWindowSource, "private void customTableView_SortRequested(");
        Assert.AreEqual(-1, mainWindowSource.IndexOf("private async void customTableView_SortRequested(", StringComparison.Ordinal));
        Assert.AreEqual(-1, mainChartListSource.IndexOf("CaptureSortRequest", StringComparison.Ordinal));
        StringAssert.Contains(mainWindowSource, "viewModel.PlaylistWorkspace.RequestPlaylistSummarySort(e.SortMemberPath, e.Direction);");
    }

    [TestMethod]
    public void PlaylistSummaryConfiguration_IsOwnedByPlaylistWorkspace()
    {
        var viewModel = new MainWindowViewModel();
        var columns = new PlaylistSummaryColumnSettings();
        var propertyNames = new List<string>();
        var rootPropertyNames = new List<string>();
        viewModel.PlaylistWorkspace.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName);
        viewModel.PropertyChanged += (_, e) => rootPropertyNames.Add(e.PropertyName);

        viewModel.PlaylistWorkspace.PlaylistSummaryColumnsSettings = columns;
        viewModel.PlaylistWorkspace.ColumnSettingsVisibilityForPlaylist = Visibility.Visible;
        viewModel.PlaylistWorkspace.UseAsyncChartRowsViewBinding = false;
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
        CollectionAssert.Contains(propertyNames, nameof(PlaylistWorkspaceViewModel.UseAsyncChartRowsViewBinding));
        CollectionAssert.DoesNotContain(rootPropertyNames, nameof(PlaylistWorkspaceViewModel.PlaylistSummaryColumnsSettings));
        CollectionAssert.DoesNotContain(rootPropertyNames, nameof(PlaylistWorkspaceViewModel.ColumnSettingsVisibilityForPlaylist));
        CollectionAssert.DoesNotContain(rootPropertyNames, nameof(PlaylistWorkspaceViewModel.UseAsyncChartRowsViewBinding));
    }

    [TestMethod]
    public void PlaylistWorkspaceSummarySortRequestCommitsOwnerStateBeforeEvent()
    {
        var workspace = new PlaylistWorkspaceViewModel(action => action());
        int raisedCount = 0;
        workspace.PlaylistSummarySortRequested += (_, _) =>
        {
            raisedCount++;
            Assert.AreEqual(nameof(PlaylistSummaryRow.TotalCharts), workspace.PlaylistSummarySortParameters.ColumnsName);
            Assert.AreEqual(System.ComponentModel.ListSortDirection.Descending, workspace.PlaylistSummarySortParameters.Direction);
        };

        workspace.RequestPlaylistSummarySort(
            nameof(PlaylistSummaryRow.TotalCharts),
            System.ComponentModel.ListSortDirection.Descending);

        Assert.AreEqual(1, raisedCount);
    }

    [TestMethod]
    public void PlaylistWorkspaceKeywordWarning_IsOwnedByPlaylistWorkspace()
    {
        var workspace = new PlaylistWorkspaceViewModel(action => action());
        var propertyNames = new List<string>();
        workspace.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName);

        workspace.SetPlaylistSummaryKeywordSearchWarningText("warning");

        Assert.AreEqual("warning", workspace.PlaylistSummaryKeywordSearchWarningText);
        Assert.IsTrue(workspace.HasPlaylistSummaryKeywordSearchWarning);
        CollectionAssert.Contains(propertyNames, nameof(PlaylistWorkspaceViewModel.PlaylistSummaryKeywordSearchWarningText));
        CollectionAssert.Contains(propertyNames, nameof(PlaylistWorkspaceViewModel.HasPlaylistSummaryKeywordSearchWarning));
    }

    [TestMethod]
    public void PlaylistWorkspaceSummaryApplyCommitsRowsAndTextBeforeDirectEvent()
    {
        var viewModel = new MainWindowViewModel();
        PlaylistWorkspaceViewModel workspace = viewModel.PlaylistWorkspace;
        workspace.IsPlaylistSummaryMode = true;
        long dataGeneration = workspace.BeginPlaylistSummaryDataRebuildGeneration();
        long presentationGeneration = workspace.BeginPlaylistSummaryPresentationGeneration();
        workspace.InvalidatePlaylistSummaryCache(invalidateTableCountCache: false);
        long cacheGeneration = workspace.CurrentPlaylistSummaryRowsCacheGeneration;
        var rows = new ObservableCollection<PlaylistSummaryRow> { new() { TotalCharts = 3 } };
        var notifications = new List<string>();
        object? sender = null;
        long observedGeneration = -1;
        workspace.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        workspace.PlaylistSummaryViewApplied += (s, e) =>
        {
            notifications.Add("applied");
            sender = s;
            observedGeneration = e.DataRebuildGeneration;
            Assert.AreSame(rows, workspace.PlaylistSummaryView);
            Assert.AreEqual("3 charts / 1 playlist", workspace.PlaylistSummaryText);
        };

        bool applied = workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
        {
            Rows = rows,
            SummaryText = "3 charts / 1 playlist",
            PresentationGeneration = presentationGeneration,
            DataRebuildGeneration = dataGeneration,
            CacheGeneration = cacheGeneration
        });

        Assert.IsTrue(applied);
        Assert.AreSame(workspace, sender);
        Assert.AreEqual(dataGeneration, observedGeneration);
        CollectionAssert.AreEqual(
            new[] { nameof(PlaylistWorkspaceViewModel.PlaylistSummaryView), nameof(PlaylistWorkspaceViewModel.PlaylistSummaryText), "applied" },
            notifications);
    }

    [TestMethod]
    public void PlaylistWorkspaceSummaryApplyRejectsStaleGenerationAndInactiveMode()
    {
        var workspace = new PlaylistWorkspaceViewModel(action => action());
        workspace.IsPlaylistSummaryMode = true;
        long dataGeneration = workspace.BeginPlaylistSummaryDataRebuildGeneration();
        long presentationGeneration = workspace.BeginPlaylistSummaryPresentationGeneration();
        workspace.InvalidatePlaylistSummaryCache(invalidateTableCountCache: false);
        long cacheGeneration = workspace.CurrentPlaylistSummaryRowsCacheGeneration;
        var originalRows = workspace.PlaylistSummaryView;

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

        workspace.InvalidatePlaylistSummaryCache(invalidateTableCountCache: false);
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
    public void PlaylistWorkspaceSummaryPublishFailureStillRaisesAppliedEvent()
    {
        var workspace = new PlaylistWorkspaceViewModel(action => action()) { IsPlaylistSummaryMode = true };
        long dataGeneration = workspace.BeginPlaylistSummaryDataRebuildGeneration();
        long presentationGeneration = workspace.BeginPlaylistSummaryPresentationGeneration();
        var rows = new ObservableCollection<PlaylistSummaryRow> { new() };
        bool appliedEventRaised = false;
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaylistWorkspaceViewModel.PlaylistSummaryView))
            {
                throw new InvalidOperationException("rows binding failed");
            }
        };
        workspace.PlaylistSummaryViewApplied += (_, _) => appliedEventRaised = true;

        Assert.ThrowsException<PlaylistSummaryPublishException>(() => workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
        {
            Rows = rows,
            SummaryText = "committed",
            PresentationGeneration = presentationGeneration,
            DataRebuildGeneration = dataGeneration
        }));

        Assert.AreSame(rows, workspace.PlaylistSummaryView);
        Assert.AreEqual("committed", workspace.PlaylistSummaryText);
        Assert.IsTrue(appliedEventRaised);
    }

    [TestMethod]
    public void PlaylistWorkspaceSummaryAppliedInvokesLaterSubscriberAfterEarlierFailure()
    {
        var workspace = new PlaylistWorkspaceViewModel(action => action()) { IsPlaylistSummaryMode = true };
        long dataGeneration = workspace.BeginPlaylistSummaryDataRebuildGeneration();
        long presentationGeneration = workspace.BeginPlaylistSummaryPresentationGeneration();
        bool laterSubscriberCalled = false;
        workspace.PlaylistSummaryViewApplied += (_, _) => throw new InvalidOperationException("cleanup failed");
        workspace.PlaylistSummaryViewApplied += (_, _) => laterSubscriberCalled = true;

        Assert.ThrowsException<PlaylistSummaryPublishException>(() => workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
        {
            Rows = new ObservableCollection<PlaylistSummaryRow> { new() },
            PresentationGeneration = presentationGeneration,
            DataRebuildGeneration = dataGeneration
        }));

        Assert.IsTrue(laterSubscriberCalled);
    }

    [TestMethod]
    public void PlaylistWorkspaceSummaryDataBuildCancelsSupersededAndHiddenWork()
    {
        var workspace = new PlaylistWorkspaceViewModel(action => action()) { IsPlaylistSummaryMode = true };

        Assert.IsTrue(workspace.TryBeginPlaylistSummaryDataBuild(out PlaylistSummaryDataBuildRequest first));
        Assert.IsTrue(workspace.TryBeginPlaylistSummaryDataBuild(out PlaylistSummaryDataBuildRequest second));

        Assert.IsTrue(first.CancellationToken.IsCancellationRequested);
        Assert.IsFalse(second.CancellationToken.IsCancellationRequested);
        Assert.IsTrue(second.Generation > first.Generation);

        workspace.IsPlaylistSummaryMode = false;

        Assert.IsTrue(second.CancellationToken.IsCancellationRequested);
        Assert.IsFalse(workspace.TryBeginPlaylistSummaryDataBuild(out _));
        workspace.CompletePlaylistSummaryDataBuild(first);
        workspace.CompletePlaylistSummaryDataBuild(second);
        Assert.IsTrue(workspace.IsPlaylistSummaryDataBuildIdle);
    }

    [TestMethod]
    public void PlaylistWorkspaceTableCountCacheReusesContentKeyAcrossDataGenerations()
    {
        var workspace = new PlaylistWorkspaceViewModel(action => action());
        var expected = new PlaylistSummaryCountResult
        {
            ScannedEntries = 4,
            TotalCharts = 3,
            OwnedCharts = 2
        };
        long cacheGeneration = workspace.CurrentPlaylistSummaryRowsCacheGeneration;
        Assert.IsTrue(workspace.TrySetPlaylistSummaryTableCount("content-key", expected, cacheGeneration));

        workspace.BeginPlaylistSummaryDataRebuildGeneration();
        workspace.BeginPlaylistSummaryDataRebuildGeneration();

        Assert.IsTrue(workspace.TryGetPlaylistSummaryTableCount("content-key", out PlaylistSummaryCountResult actual));
        Assert.AreEqual(expected.ScannedEntries, actual.ScannedEntries);
        Assert.AreEqual(expected.TotalCharts, actual.TotalCharts);
        Assert.AreEqual(expected.OwnedCharts, actual.OwnedCharts);

        workspace.InvalidatePlaylistSummaryCache(invalidateTableCountCache: true);
        Assert.IsFalse(workspace.TryGetPlaylistSummaryTableCount("content-key", out _));
    }

    [TestMethod]
    public void PlaylistWorkspaceTableCountCacheRejectsResultFromBuildBeforeInvalidation()
    {
        var workspace = new PlaylistWorkspaceViewModel(action => action());
        workspace.IsPlaylistSummaryMode = true;
        Assert.IsTrue(workspace.TryBeginPlaylistSummaryDataBuild(out PlaylistSummaryDataBuildRequest staleBuild));
        var staleResult = new PlaylistSummaryCountResult
        {
            ScannedEntries = 4,
            TotalCharts = 3,
            OwnedCharts = 2
        };

        workspace.InvalidatePlaylistSummaryCache(invalidateTableCountCache: true);

        Assert.IsFalse(workspace.TrySetPlaylistSummaryTableCount(
            "content-key",
            staleResult,
            staleBuild.TableCountCacheGeneration));
        Assert.IsFalse(workspace.TryGetPlaylistSummaryTableCount("content-key", out _));
        workspace.CompletePlaylistSummaryDataBuild(staleBuild);
    }

    [TestMethod]
    public void PlaylistWorkspaceSummaryDataBuildCannotRestartAfterShutdownStop()
    {
        var workspace = new PlaylistWorkspaceViewModel(action => action()) { IsPlaylistSummaryMode = true };
        Assert.IsTrue(workspace.TryBeginPlaylistSummaryDataBuild(out PlaylistSummaryDataBuildRequest activeBuild));
        long presentationGeneration = workspace.BeginPlaylistSummaryPresentationGeneration();

        workspace.StopPlaylistSummaryDataBuild();

        Assert.IsTrue(activeBuild.CancellationToken.IsCancellationRequested);
        Assert.IsFalse(workspace.TryBeginPlaylistSummaryDataBuild(out _));
        Assert.IsFalse(workspace.TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
        {
            Rows = new ObservableCollection<PlaylistSummaryRow> { new() },
            PresentationGeneration = presentationGeneration,
            DataRebuildGeneration = activeBuild.Generation
        }));
        workspace.CompletePlaylistSummaryDataBuild(activeBuild);
        Assert.IsTrue(workspace.IsPlaylistSummaryDataBuildIdle);
    }

    [TestMethod]
    public void PlaylistWorkspaceDeferredDataRefreshCancelsBuildAndDominatesPresentation()
    {
        var workspace = new PlaylistWorkspaceViewModel(action => action()) { IsPlaylistSummaryMode = true };
        Assert.IsTrue(workspace.TryBeginPlaylistSummaryDataBuild(out PlaylistSummaryDataBuildRequest activeBuild));
        workspace.RequestDeferredPlaylistSummaryPresentationRefresh();

        long nextBuildGeneration = workspace.RequestPlaylistSummaryDataRefresh(invalidateTableCountCache: false).NextBuildGeneration;
        workspace.RequestDeferredPlaylistSummaryPresentationRefresh();

        Assert.IsTrue(activeBuild.CancellationToken.IsCancellationRequested);
        Assert.AreEqual(workspace.CurrentPlaylistSummaryDataRebuildGeneration + 1L, nextBuildGeneration);
        Assert.AreEqual(
            PlaylistSummaryDeferredRefreshKind.Data,
            workspace.TakeDeferredPlaylistSummaryRefresh(dataRefreshRequired: false));
        Assert.AreEqual(
            PlaylistSummaryDeferredRefreshKind.None,
            workspace.TakeDeferredPlaylistSummaryRefresh(dataRefreshRequired: false));
        workspace.CompletePlaylistSummaryDataBuild(activeBuild);
    }

    [TestMethod]
    public void PlaylistWorkspaceDeferredRefreshIsAtomicWithExternalDataPriorityAndModeExit()
    {
        var workspace = new PlaylistWorkspaceViewModel(action => action()) { IsPlaylistSummaryMode = true };
        workspace.RequestDeferredPlaylistSummaryPresentationRefresh();

        Assert.AreEqual(
            PlaylistSummaryDeferredRefreshKind.Data,
            workspace.TakeDeferredPlaylistSummaryRefresh(dataRefreshRequired: true));

        workspace.RequestDeferredPlaylistSummaryPresentationRefresh();
        workspace.IsPlaylistSummaryMode = false;
        workspace.IsPlaylistSummaryMode = true;

        Assert.AreEqual(
            PlaylistSummaryDeferredRefreshKind.None,
            workspace.TakeDeferredPlaylistSummaryRefresh(dataRefreshRequired: false));
    }

    [TestMethod]
    public void PlaylistWorkspaceDataRefreshRequestOwnsVisibilityAndDeferralDecision()
    {
        var workspace = new PlaylistWorkspaceViewModel(action => action());
        long hiddenDataGeneration = workspace.CurrentPlaylistSummaryDataRebuildGeneration;
        long hiddenCacheGeneration = workspace.CurrentPlaylistSummaryRowsCacheGeneration;

        PlaylistSummaryDataRefreshRequestResult hidden = workspace.RequestPlaylistSummaryDataRefresh(invalidateTableCountCache: false);

        Assert.IsFalse(hidden.Queued);
        Assert.AreEqual(0L, hidden.NextBuildGeneration);
        Assert.IsTrue(workspace.CurrentPlaylistSummaryDataRebuildGeneration > hiddenDataGeneration);
        Assert.IsTrue(workspace.CurrentPlaylistSummaryRowsCacheGeneration > hiddenCacheGeneration);

        workspace.IsPlaylistSummaryMode = true;
        PlaylistSummaryDataRefreshRequestResult visible = workspace.RequestPlaylistSummaryDataRefresh(invalidateTableCountCache: true);
        Assert.IsTrue(visible.Queued);
        Assert.AreEqual(workspace.CurrentPlaylistSummaryDataRebuildGeneration + 1L, visible.NextBuildGeneration);

        PlaylistSummaryDataRefreshRequestResult coalesced = workspace.RequestPlaylistSummaryDataRefresh(invalidateTableCountCache: true);
        Assert.IsTrue(coalesced.Queued);
        Assert.AreEqual(workspace.CurrentPlaylistSummaryDataRebuildGeneration + 1L, coalesced.NextBuildGeneration);
        Assert.AreEqual(
            PlaylistSummaryDeferredRefreshKind.Data,
            workspace.TakeDeferredPlaylistSummaryRefresh(dataRefreshRequired: false));
    }
}
