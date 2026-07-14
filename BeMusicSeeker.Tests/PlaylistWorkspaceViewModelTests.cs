using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
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
        string regularOwnerSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "ViewModels", "MainWindow", "RegularChartListOwner.cs");
        string playHistoryOwnerSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "ViewModels", "MainWindow", "PlayHistoryWorkflowOwner.cs");
        string bulkEditSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "ViewModels", "MainWindow", "PlaylistWorkspaceViewModel.PlaylistSummaryBulkEdit.cs");

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
        StringAssert.Contains(workspaceSource, "CommitMainTablePresentationWithoutNotification(");
        StringAssert.Contains(workspaceSource, "PublishMainTablePresentation(");
        Assert.AreEqual(-1, regularOwnerSource.IndexOf("CommitColumnPresentationWithoutNotification(", StringComparison.Ordinal));
        Assert.AreEqual(-1, regularOwnerSource.IndexOf("CommitBindingModeWithoutNotification(", StringComparison.Ordinal));
        Assert.AreEqual(-1, regularOwnerSource.IndexOf("PublishColumnPresentation(", StringComparison.Ordinal));
        Assert.AreEqual(-1, regularOwnerSource.IndexOf("PublishBindingMode(", StringComparison.Ordinal));
        Assert.AreEqual(-1, playHistoryOwnerSource.IndexOf("CommitColumnPresentationWithoutNotification(", StringComparison.Ordinal));
        Assert.AreEqual(-1, playHistoryOwnerSource.IndexOf("CommitBindingModeWithoutNotification(", StringComparison.Ordinal));
        Assert.AreEqual(-1, playHistoryOwnerSource.IndexOf("PublishColumnPresentation(", StringComparison.Ordinal));
        Assert.AreEqual(-1, playHistoryOwnerSource.IndexOf("PublishBindingMode(", StringComparison.Ordinal));
        StringAssert.Contains(logicalSource, "public PlaylistWorkspaceViewModel PlaylistWorkspace { get; }");
        StringAssert.Contains(logicalSource, "PlaylistWorkspace = composition.CreatePlaylistWorkspaceViewModel(");
        StringAssert.Contains(logicalSource, "PlaylistWorkspace.TreeSelectionRequested += PlaylistWorkspaceTreeSelectionRequested;");
        StringAssert.Contains(logicalSource, "PlaylistWorkspace.EntriesChanged += PlaylistWorkspaceEntriesChanged;");
        StringAssert.Contains(logicalSource, "PlaylistWorkspace.PlaylistSummaryDataRefreshRequested += PlaylistWorkspacePlaylistSummaryDataRefreshRequested;");
        StringAssert.Contains(logicalSource, "PlaylistWorkspace.PlaylistReferenceTableReplaced += PlaylistWorkspacePlaylistReferenceTableReplaced;");
        StringAssert.Contains(logicalSource, "InvokeMainChartListPresentationAction(() =>");
        StringAssert.Contains(logicalSource, "PlaylistWorkspace.ReplaceCurrentPlaylistDetailSelectionTable(request.OldTable, request.NewTable);");
        StringAssert.Contains(logicalSource, "ApplyPlaylistEntriesChanged(request.Table, refreshSummaryIfVisible: true);");
        StringAssert.Contains(logicalSource, "PlaylistWorkspace.MarkCurrentPlaylistDetailEntriesChanged(table, \"playlist_updated\")");
        StringAssert.Contains(logicalSource, "PlaylistWorkspace.RemapCurrentPlaylistDetailFolderSelection(request.Table, request.RewrittenFolders);");
        Assert.AreEqual(-1, rootSource.IndexOf("ReplaceCurrentPlaylistSelectionTable(", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("RemapCurrentPlaylistFolderSelection(", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("treeViewFilterParameterSelected is PlaylistDetailSelection", StringComparison.Ordinal));
        StringAssert.Contains(workspaceSource, "private void PublishEntriesChanged(BMSTable table)");
        StringAssert.Contains(workspaceSource, "EntriesChanged?.Invoke(this, new PlaylistWorkspaceEntriesChangedEventArgs(table));");
        StringAssert.Contains(workspaceSource, "internal Task AddRowsToFolderAsync(");
        StringAssert.Contains(workspaceSource, "internal Task DeleteEntriesAsync(");
        StringAssert.Contains(workspaceSource, "internal Task RemoveTableAsync(");
        StringAssert.Contains(workspaceSource, "internal Task RemoveTablesAsync(");
        StringAssert.Contains(workspaceSource, "internal void RefreshPlaylistSummaryKeywordSearchSuggestions(");
        StringAssert.Contains(workspaceSource, "internal void CommitPlaylistSummaryKeywordSearchHistory(");
        StringAssert.Contains(workspaceSource, "internal void ConfigureKeywordSearchHistory(");
        Assert.AreEqual(-1, rootSource.IndexOf("playlistSummaryKeywordSearchHistory", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("RefreshPlaylistSummaryKeywordSearchSuggestions", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("CommitPlaylistSummaryKeywordSearchHistory", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("ClosePlaylistSummaryKeywordSearchSuggestions", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("UpdatePlaylistSummaryKeywordSearchPresentation", StringComparison.Ordinal));
        StringAssert.Contains(mainWindowSource, "viewModel.PlaylistWorkspace.RefreshPlaylistSummaryKeywordSearchSuggestions(");
        StringAssert.Contains(mainWindowSource, "viewModel.PlaylistWorkspace.CommitPlaylistSummaryKeywordSearchHistory(");
        StringAssert.Contains(mainWindowSource, "viewModel.PlaylistWorkspace.ClosePlaylistSummaryKeywordSearchSuggestions();");
        StringAssert.Contains(workspaceSource, "RequestPlaylistSummaryRefresh(\n            \"playlist_table_removed\"");
        StringAssert.Contains(workspaceSource, "rebuildAsync: false");
        StringAssert.Contains(logicalSource, "PlaylistWorkspace.PlaylistReferenceSortInvalidationRequested += PlaylistWorkspacePlaylistReferenceSortInvalidationRequested;");
        StringAssert.Contains(mainWindowSource, "viewModel.PlaylistWorkspace.RemoveTableAsync(");
        StringAssert.Contains(workspaceSource, "internal void RequestSummarySelection()");
        StringAssert.Contains(workspaceSource, "internal void RequestDetailSelection(BMSTable table, PlaylistFolderNode folderNode = null)");
        StringAssert.Contains(workspaceSource, "internal PlaylistDetailSelection CapturePlaylistDetailSelection()");
        StringAssert.Contains(workspaceSource, "internal PlaylistDetailSelection CapturePlaylistDetailSelection(out long selectionRevision)");
        StringAssert.Contains(workspaceSource, "internal bool IsCurrentPlaylistDetailSelection(");
        StringAssert.Contains(workspaceSource, "internal bool TryExecuteCurrentPlaylistDetailSelection(");
        StringAssert.Contains(workspaceSource, "internal bool TryExecuteCurrentPlaylistSummarySelection(");
        StringAssert.Contains(workspaceSource, "internal bool ReplaceCurrentPlaylistDetailSelectionTable(");
        StringAssert.Contains(workspaceSource, "internal bool RemapCurrentPlaylistDetailFolderSelection(");
        StringAssert.Contains(workspaceSource, "internal bool MarkCurrentPlaylistDetailEntriesChanged(");
        StringAssert.Contains(workspaceSource, "internal long ClearPlaylistDetailSelection()");
        StringAssert.Contains(logicalSource, "PlaylistWorkspace.TryExecuteCurrentPlaylistSummarySelection(");
        StringAssert.Contains(logicalSource, "PlaylistWorkspace.TryExecuteCurrentPlaylistDetailSelection(");
        StringAssert.Contains(logicalSource, "CapturePlaylistDetailSelection(out long selectionRevision)");
        StringAssert.Contains(mainWindowSource, "viewModel.PlaylistWorkspace.RequestSummarySelection();");
        StringAssert.Contains(mainWindowSource, "viewModel.PlaylistWorkspace.RequestDetailSelection(bmsTable, selectedFolderNode);");
        StringAssert.Contains(mainWindowSource, "viewModel.PlaylistWorkspace.ApplyPlaylistSummaryBmtOutput(");
        StringAssert.Contains(bulkEditSource, "ownerWorkspace.ApplyPlaylistSummaryBmtOutput(");
        StringAssert.Contains(workspaceSource, "internal void ApplyPlaylistSummaryBmtOutput(");
        Assert.AreEqual(-1, rootSource.IndexOf("ApplyPlaylistSummaryBmtOutput(", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("RemoveBMSTable(", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("ExecPlaylistFilter", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("SelectPlaylistSummary", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("playlistViewState", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("AddChartRowsToFolderBMSTable", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("DeleteBMSTableEntries", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("ArePlaylistDropCandidateRows", StringComparison.Ordinal));
        Assert.AreEqual(-1, mainWindowSource.IndexOf("GetPlaylistFilterType", StringComparison.Ordinal));
        Assert.AreEqual(-1, mainWindowSource.IndexOf("GetPlaylistFolderSelectionKey", StringComparison.Ordinal));
        StringAssert.Contains(logicalSource, "LogPlaylistViewApply,");
        StringAssert.Contains(workspaceSource, "internal PlaylistDetailBuildState DetailBuildState { get; }");
        StringAssert.Contains(workspaceSource, "internal PlaylistDetailViewState DetailViewState { get; }");
        StringAssert.Contains(logicalSource, "LogPlaylistRetention);");
        Assert.AreEqual(-1, logicalSource.IndexOf("PlaylistWorkspace.PropertyChanged += PlaylistWorkspacePropertyChanged;", StringComparison.Ordinal));
        Assert.AreEqual(-1, logicalSource.IndexOf("private void PlaylistWorkspacePropertyChanged(", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("lockPlaylistSyncStatuses", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("playlistSyncStatuses", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("UpdatePlaylistSyncRuntimeStatus", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("GetPlaylistSyncStatusSnapshot", StringComparison.Ordinal));
        StringAssert.Contains(workspaceSource, "internal void RecordPlaylistSyncResult(PlaylistSyncAttemptResult result)");
        StringAssert.Contains(workspaceSource, "internal IReadOnlyDictionary<string, PlaylistSyncRuntimeStatus> CapturePlaylistSyncStatusSnapshot()");
        Assert.AreEqual(-1, rootSource.IndexOf("playlistSyncProgressLock", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("playlistSyncProgressActiveOperationCount", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("activeBeatorajaBmtExportProgressOperations", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("private void BeginPlaylistSyncProgressOperation", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("private void EndPlaylistSyncProgressOperation", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("UpdateBeatorajaBmtExportProgressStatus", StringComparison.Ordinal));
        StringAssert.Contains(workspaceSource, "internal void BeginPlaylistSyncProgressOperation()");
        StringAssert.Contains(workspaceSource, "internal void EndPlaylistSyncProgressOperation()");
        StringAssert.Contains(workspaceSource, "internal void ReportPlaylistSyncProgress(PlaylistSyncProgressSnapshot snapshot)");
        Assert.AreEqual(-1, rootSource.IndexOf("PlaylistSummaryBulkOperationStarted", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("PlaylistSummaryBulkOperationFinished", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("PlaylistPropertySyncStarted", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("PlaylistPropertySyncFinished", StringComparison.Ordinal));
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
        Assert.AreEqual(-1, workspaceSource.IndexOf("CustomFolderOutputSettingsSnapshot.CreateCurrent", StringComparison.Ordinal));
        StringAssert.Contains(buildOwnerSource, "dispatchPresentation(Reflect);");
        StringAssert.Contains(logicalSource, "PlaylistWorkspace.PlaylistSummaryViewApplied += PlaylistWorkspacePlaylistSummaryViewApplied;");
        Assert.AreEqual(-1, rootSource.IndexOf("MainTableDisplayRefreshRequested", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("MainTableSortParameters", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("public void ExecSort", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("public void ExecPlaylistSummarySort", StringComparison.Ordinal));
        StringAssert.Contains(mainChartListSource, "internal event EventHandler DisplayRefreshRequested;");
        StringAssert.Contains(mainChartListSource, "internal event EventHandler<MainChartListSortRequestedEventArgs> SortRequested;");
        StringAssert.Contains(workspaceSource, "internal void RequestPlaylistSummarySort(");
        StringAssert.Contains(workspaceSource, "internal void RequestPlaylistDetailSort(");
        StringAssert.Contains(workspaceSource, "internal ChartListSortParameters CapturePlaylistDetailSortParameters()");
        StringAssert.Contains(workspaceSource, "internal void RequestPlaylistDetailFilter(");
        StringAssert.Contains(workspaceSource, "internal ChartListFilterSnapshot CapturePlaylistDetailFilterSnapshot()");
        StringAssert.Contains(logicalSource, "PlaylistWorkspace.PlaylistDetailSortChanged += PlaylistWorkspacePlaylistDetailSortChanged;");
        StringAssert.Contains(logicalSource, "PlaylistWorkspace.InitializePlaylistDetailSort(regularChartListOwner.CaptureSortParameters());");
        StringAssert.Contains(logicalSource, "PlaylistWorkspace.RequestPlaylistDetailSort(request.ColumnName, request.Direction);");
        StringAssert.Contains(logicalSource, "PlaylistWorkspace.CapturePlaylistDetailSortParameters()");
        StringAssert.Contains(logicalSource, "PlaylistWorkspace.PlaylistDetailFilterChanged += PlaylistWorkspacePlaylistDetailFilterChanged;");
        StringAssert.Contains(logicalSource, "PlaylistWorkspace.InitializePlaylistDetailFilter(ChartFilters.CaptureSnapshot());");
        StringAssert.Contains(logicalSource, "PlaylistWorkspace.RequestPlaylistDetailFilter(MainViewUpdateMode.KeywordFilterUpdated, filters);");
        StringAssert.Contains(logicalSource, "PlaylistWorkspace.RequestPlaylistDetailFilter(MainViewUpdateMode.ModeFilterUpdated, filters);");
        Assert.AreEqual(-1, rootSource.IndexOf("CreatePlaylistDetailRefreshInput(", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("PlaylistDetailRefreshInput", StringComparison.Ordinal));
        string bindingModeUpdate = SourceTextTestHelper.ExtractMethodBody(logicalSource, "private void UpdateBmsFilesViewBindingMode(");
        StringAssert.Contains(bindingModeUpdate, "if (playlistDetailActive)");
        StringAssert.Contains(bindingModeUpdate, "PlaylistWorkspace.InitializePlaylistDetailFilter(ChartFilters.CaptureSnapshot());");
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
    public void Constructor_RequiresExplicitCustomFolderOutputSettingsProvider()
    {
        Assert.ThrowsException<ArgumentNullException>(() => new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            null));
    }

    [TestMethod]
    public void BuildDetailSourceRows_FiltersFolderAndRemovedEntriesInsideWorkspace()
    {
        var workspace = CreateDetailWorkspace(out FakePlaylistDetailDataSource dataSource);
        var included = new TestablePlaylistEntry("11111111111111111111111111111111", "included") { folder = "target" };
        var otherFolder = new TestablePlaylistEntry("22222222222222222222222222222222", "other") { folder = "other" };
        var removed = new TestablePlaylistEntry("33333333333333333333333333333333", "removed") { folder = "target", is_removed = true };
        var table = new BMSTable
        {
            entries = [included, otherFolder, removed]
        };
        string cancellationStage = string.Empty;

        PlaylistSourceBuildResult result = workspace.BuildDetailSourceRows(
            table,
            "target",
            onlyNotOwned: false,
            new PlaylistLibraryIndexSnapshot { ResolveIndex = PlaylistLibraryResolveIndexSnapshot.Empty },
            CancellationToken.None,
            ref cancellationStage);

        Assert.AreEqual(1, result.SourceRows.Count);
        Assert.AreSame(included, result.SourceRows[0].Entry);
        Assert.AreEqual(1, dataSource.EnsureEntriesLoadedCallCount);
        Assert.AreEqual("source_row_materialize", cancellationStage);
    }

    [TestMethod]
    public void TryPatchDetailSourceChartInfo_ReplacesCurrentGenerationWithoutMutatingOldRow()
    {
        var workspace = CreateDetailWorkspace(out FakePlaylistDetailDataSource dataSource);
        var oldInfo = new LR2SongDBExtended.chart_info
        {
            sha256 = new string('a', 64),
            parser_version = 1,
            updated_at = new DateTime(2026, 1, 1)
        };
        var newInfo = new LR2SongDBExtended.chart_info
        {
            sha256 = oldInfo.sha256,
            parser_version = 2,
            updated_at = new DateTime(2026, 2, 1)
        };
        var entry = new TestablePlaylistEntry("44444444444444444444444444444444", "patch");
        entry.SetSha256(oldInfo.sha256);
        var oldRow = new PlaylistDetailSourceRow(entry, resolvedChart: null, entryChartInfo: oldInfo);
        workspace.DetailViewState.Source.Rows = [oldRow];
        workspace.DetailBuildState.RequestVersion = 7;
        dataSource.ChartInfo = newInfo;
        var request = new PlaylistBuildRequest
        {
            RequestVersion = 7,
            Identity = PlaylistRequestFactory.CreateIdentity(
                new BMSTable(),
                null,
                PlaylistDetailFilter.PlaylistFilter,
                null,
                ChartModeFilter.All,
                null,
                libraryIndexVersion: 1,
                playlistRevision: 1,
                scoreSnapshotVersion: 1,
                chartInfoIndexVersion: 2,
                hasResolvedSelection: true)
        };

        bool patched = workspace.TryPatchDetailSourceChartInfo(
            request,
            CancellationToken.None,
            out int sourceCount,
            out int dependencyCount,
            out int patchedCount,
            out _);

        Assert.IsTrue(patched);
        Assert.AreEqual(1, sourceCount);
        Assert.AreEqual(1, dependencyCount);
        Assert.AreEqual(1, patchedCount);
        Assert.AreSame(oldInfo, oldRow.EntryChartInfo);
        Assert.AreNotSame(oldRow, workspace.DetailViewState.Source.Rows[0]);
        Assert.AreSame(newInfo, workspace.DetailViewState.Source.Rows[0].EntryChartInfo);
        Assert.AreEqual(2, workspace.DetailViewState.Source.LastBuiltChartInfoIndexVersion);
    }

    [TestMethod]
    public void TryPatchDetailSourceChartInfo_RejectsStaleRequestWithoutReplacingSource()
    {
        var workspace = CreateDetailWorkspace(out FakePlaylistDetailDataSource dataSource);
        var oldInfo = new LR2SongDBExtended.chart_info
        {
            sha256 = new string('b', 64),
            parser_version = 1,
            updated_at = new DateTime(2026, 1, 1)
        };
        var entry = new TestablePlaylistEntry("55555555555555555555555555555555", "stale");
        var oldRow = new PlaylistDetailSourceRow(entry, resolvedChart: null, entryChartInfo: oldInfo);
        List<PlaylistDetailSourceRow> oldRows = [oldRow];
        workspace.DetailViewState.Source.Rows = oldRows;
        workspace.DetailBuildState.RequestVersion = 8;
        dataSource.ChartInfo = new LR2SongDBExtended.chart_info
        {
            sha256 = oldInfo.sha256,
            parser_version = 2,
            updated_at = new DateTime(2026, 2, 1)
        };
        var staleRequest = new PlaylistBuildRequest
        {
            RequestVersion = 7,
            Identity = PlaylistRequestFactory.CreateIdentity(
                new BMSTable(), null, PlaylistDetailFilter.PlaylistFilter, null,
                ChartModeFilter.All, null, 1, 1, 1, 2, hasResolvedSelection: true)
        };

        bool patched = workspace.TryPatchDetailSourceChartInfo(
            staleRequest,
            CancellationToken.None,
            out _, out _, out _, out _);

        Assert.IsFalse(patched);
        Assert.AreSame(oldRows, workspace.DetailViewState.Source.Rows);
        Assert.AreSame(oldInfo, oldRow.EntryChartInfo);
    }

    [TestMethod]
    public void BuildDetailSourceRows_OnlyNotOwnedExcludesResolvedLibraryCharts()
    {
        var workspace = CreateDetailWorkspace(out _);
        var owned = new TestablePlaylistEntry("66666666666666666666666666666666", "owned");
        var missing = new TestablePlaylistEntry("77777777777777777777777777777777", "missing");
        var table = new BMSTable { entries = [owned, missing] };
        PlaylistLibraryResolveIndexSnapshot resolveIndex = PlaylistLibraryResolveIndexSnapshot.FromLibraryChartRefs(
            [LibraryChartRef.FromPath(LibraryChartKind.Bms, @"C:\songs\owned.bms", owned.md5, null)]);
        string cancellationStage = string.Empty;

        PlaylistSourceBuildResult result = workspace.BuildDetailSourceRows(
            table,
            null,
            onlyNotOwned: true,
            new PlaylistLibraryIndexSnapshot { ResolveIndex = resolveIndex },
            CancellationToken.None,
            ref cancellationStage);

        Assert.AreEqual(1, result.SourceRows.Count);
        Assert.AreSame(missing, result.SourceRows[0].Entry);
    }

    [TestMethod]
    public void BuildDetailSourceRows_CancellationIsNotHidden()
    {
        var workspace = CreateDetailWorkspace(out _);
        var table = new BMSTable
        {
            entries = [new TestablePlaylistEntry("88888888888888888888888888888888", "cancel")]
        };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        string cancellationStage = string.Empty;

        Assert.ThrowsException<OperationCanceledException>(() => workspace.BuildDetailSourceRows(
            table,
            null,
            onlyNotOwned: false,
            new PlaylistLibraryIndexSnapshot { ResolveIndex = PlaylistLibraryResolveIndexSnapshot.Empty },
            cancellation.Token,
            ref cancellationStage));
    }

    [TestMethod]
    public void RequestDetailRefresh_OwnsRequestThroughRebuildAndTerminalApply()
    {
        var logs = new List<string>();
        var mainChartList = new MainChartListViewModel(action => action());
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            mainChartList,
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            logs.Add,
            () => new CustomFolderOutputSettingsSnapshot());
        var dataSource = new FakePlaylistDetailDataSource();
        workspace.SetDetailDataSource(dataSource);
        var entry = new TestablePlaylistEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "request-entry");
        var table = new BMSTable { entries = [entry] };
        workspace.RequestDetailSelection(table);
        workspace.InitializePlaylistDetailFilter(new ChartListFilterSnapshot("request", ChartModeFilter.All));
        workspace.InitializePlaylistDetailSort(new ChartListSortParameters
        {
            ColumnsName = "TITLE",
            Direction = System.ComponentModel.ListSortDirection.Ascending
        });

        int requestVersion = workspace.RequestDetailRefresh(
            MainViewUpdateMode.PlaylistFilterSelected,
            MainViewUpdateMode.PlaylistFilterSelected,
            MainViewUpdateMode.PlaylistFilterSelected,
            useCoalescingWindow: false,
            openReadiness: default);

        Assert.IsTrue(SpinWait.SpinUntil(() => workspace.IsDetailBuildIdle, 5000));
        Assert.AreEqual(requestVersion, workspace.DetailBuildState.RequestVersion);
        Assert.AreSame(table, workspace.DetailViewState.Source.CurrentTable);
        Assert.AreEqual(1, workspace.DetailViewState.Source.Rows.Count);
        Assert.AreSame(entry, workspace.DetailViewState.Source.Rows[0].Entry);
        Assert.AreEqual(1, dataSource.EnsureEntriesLoadedCallCount);
        Assert.AreEqual("REQUEST", workspace.DetailViewState.View.CurrentIdentity?.KeywordFilter);
        Assert.IsTrue(mainChartList.LastCompletion.RequestId > 0L);
        Assert.AreEqual(MainViewUpdateMode.PlaylistFilterSelected, mainChartList.LastCompletion.Mode);
        Assert.IsTrue(logs.Exists(log => log.Contains("main_view_build mode=PlaylistFilterSelected")
            && log.Contains("isPlaylistDetailView=true")));
    }

    [TestMethod]
    public void RequestDetailRefresh_AfterShutdownIsIgnoredWithoutStartingWorker()
    {
        var workspace = CreateDetailWorkspace(out _);
        workspace.CancelDetailBuilds();
        workspace.RequestDetailSelection(new BMSTable());

        workspace.RequestDetailRefresh(
            MainViewUpdateMode.PlaylistFilterSelected,
            MainViewUpdateMode.PlaylistFilterSelected,
            MainViewUpdateMode.PlaylistFilterSelected,
            useCoalescingWindow: false,
            openReadiness: default);

        Assert.IsTrue(workspace.IsDetailBuildIdle);
        Assert.IsTrue(workspace.DetailBuildState.ShutdownCancellationRequested);
        Assert.IsFalse(workspace.DetailBuildState.WorkerRunning);
        Assert.IsNull(workspace.DetailBuildState.PendingRequest);
    }

    [TestMethod]
    public void RequestDetailRefresh_WithoutOwnerSelectionIsIgnored()
    {
        var workspace = CreateDetailWorkspace(out _);
        int initialRequestVersion = workspace.DetailBuildState.RequestVersion;

        int requestVersion = workspace.RequestDetailRefresh(
            MainViewUpdateMode.PlaylistFilterSelected,
            MainViewUpdateMode.PlaylistFilterSelected,
            MainViewUpdateMode.PlaylistFilterSelected,
            useCoalescingWindow: false,
            openReadiness: default);

        Assert.AreEqual(0, requestVersion);
        Assert.IsTrue(workspace.IsDetailBuildIdle);
        Assert.AreEqual(initialRequestVersion, workspace.DetailBuildState.RequestVersion);
        Assert.IsNull(workspace.DetailBuildState.PendingRequest);
    }

    [TestMethod]
    public void PlaylistSyncStatusOwner_ReplacesSourceKeyAndUsesSourceFallback()
    {
        var workspace = CreateDetailWorkspace(out _);
        var sourceTable = new BMSTable { playlist_id = 1 };
        var resultTable = new BMSTable { playlist_id = 2 };

        workspace.RecordPlaylistSyncResult(
            PlaylistSyncAttemptResult.CreateSuccess(
                sourceTable,
                resultTable,
                new Uri("https://example.test/table"),
                updated: true));

        IReadOnlyDictionary<string, PlaylistSyncRuntimeStatus> snapshot = workspace.CapturePlaylistSyncStatusSnapshot();
        Assert.IsFalse(snapshot.ContainsKey("id:1"));
        Assert.IsTrue(snapshot.ContainsKey("id:2"));
        Assert.AreEqual(PlaylistSyncStatusKind.Updated, snapshot["id:2"].Kind);

        var fallbackSourceTable = new BMSTable { name = "fallback" };
        workspace.RecordPlaylistSyncResult(
            PlaylistSyncAttemptResult.CreateSuccess(
                fallbackSourceTable,
                new BMSTable(),
                new Uri("https://example.test/fallback"),
                updated: false));

        snapshot = workspace.CapturePlaylistSyncStatusSnapshot();
        Assert.IsTrue(snapshot.ContainsKey("name:fallback"));
        Assert.AreEqual(PlaylistSyncStatusKind.Ok, snapshot["name:fallback"].Kind);
    }

    [TestMethod]
    public void PlaylistSyncStatusOwner_CapturesIsolatedSnapshots()
    {
        var workspace = CreateDetailWorkspace(out _);
        var table = new BMSTable { playlist_id = 3 };
        workspace.RecordPlaylistSyncResult(
            PlaylistSyncAttemptResult.CreateSuccess(
                table,
                table,
                new Uri("https://example.test/initial"),
                updated: false));

        IReadOnlyDictionary<string, PlaylistSyncRuntimeStatus> firstSnapshot = workspace.CapturePlaylistSyncStatusSnapshot();
        workspace.RecordPlaylistSyncResult(
            PlaylistSyncAttemptResult.CreateFailure(
                table,
                new Uri("https://example.test/failure"),
                new InvalidOperationException("failure")));
        IReadOnlyDictionary<string, PlaylistSyncRuntimeStatus> secondSnapshot = workspace.CapturePlaylistSyncStatusSnapshot();

        Assert.AreNotSame(firstSnapshot["id:3"], secondSnapshot["id:3"]);
        Assert.AreEqual(PlaylistSyncStatusKind.Ok, firstSnapshot["id:3"].Kind);
        Assert.AreEqual(PlaylistSyncStatusKind.UnknownError, secondSnapshot["id:3"].Kind);
        workspace.RecordPlaylistSyncResult(null);
        Assert.AreEqual(PlaylistSyncStatusKind.Ok, firstSnapshot["id:3"].Kind);
    }

    [TestMethod]
    public void PlaylistSyncProgressOwner_SuppressesInactiveUntilLastScopeEnds()
    {
        var workspace = CreateDetailWorkspace(out _);
        List<PlaylistSyncProgressSnapshot> snapshots = [];
        workspace.PlaylistSyncProgressChanged += (_, request) => snapshots.Add(request.Snapshot);

        workspace.BeginPlaylistSyncProgressOperation();
        workspace.BeginPlaylistSyncProgressOperation();
        workspace.ReportPlaylistSyncProgress(new PlaylistSyncProgressSnapshot
        {
            IsActive = true,
            TotalTableCount = 2,
            CompletedTableCount = 1,
            CurrentTableName = "active"
        });
        workspace.ReportPlaylistSyncProgress(new PlaylistSyncProgressSnapshot
        {
            IsActive = false
        });

        Assert.AreEqual(1, snapshots.Count);
        Assert.IsTrue(snapshots[0].IsActive);

        workspace.EndPlaylistSyncProgressOperation();
        Assert.AreEqual(1, snapshots.Count);

        workspace.EndPlaylistSyncProgressOperation();
        Assert.AreEqual(2, snapshots.Count);
        Assert.IsFalse(snapshots[1].IsActive);
    }

    [TestMethod]
    public void PlaylistSyncProgressOwner_TracksBeatorajaOperationIdsExactlyOnce()
    {
        var workspace = CreateDetailWorkspace(out _);
        List<PlaylistSyncProgressSnapshot> snapshots = [];
        workspace.PlaylistSyncProgressChanged += (_, request) => snapshots.Add(request.Snapshot);

        workspace.ReportPlaylistSyncProgress(new PlaylistSyncProgressSnapshot
        {
            IsActive = true,
            OperationId = 41,
            TotalTableCount = 1,
            CompletedTableCount = 0
        });
        workspace.ReportPlaylistSyncProgress(new PlaylistSyncProgressSnapshot
        {
            IsActive = true,
            OperationId = 41,
            TotalTableCount = 1,
            CompletedTableCount = 1
        });
        workspace.ReportPlaylistSyncProgress(new PlaylistSyncProgressSnapshot
        {
            IsActive = false,
            OperationId = 99
        });
        workspace.ReportPlaylistSyncProgress(new PlaylistSyncProgressSnapshot
        {
            IsActive = false,
            OperationId = 41
        });
        workspace.ReportPlaylistSyncProgress(new PlaylistSyncProgressSnapshot
        {
            IsActive = false,
            OperationId = 41
        });

        Assert.AreEqual(3, snapshots.Count);
        Assert.IsTrue(snapshots[0].IsActive);
        Assert.IsTrue(snapshots[1].IsActive);
        Assert.IsFalse(snapshots[2].IsActive);
    }

    [TestMethod]
    public void PlaylistSyncProgressOwner_BeatorajaCompletionDoesNotClearManualScope()
    {
        var workspace = CreateDetailWorkspace(out _);
        List<PlaylistSyncProgressSnapshot> snapshots = [];
        workspace.PlaylistSyncProgressChanged += (_, request) => snapshots.Add(request.Snapshot);

        workspace.BeginPlaylistSyncProgressOperation();
        workspace.ReportPlaylistSyncProgress(new PlaylistSyncProgressSnapshot
        {
            IsActive = true,
            OperationId = 7,
            TotalTableCount = 1,
            CompletedTableCount = 0
        });
        workspace.ReportPlaylistSyncProgress(new PlaylistSyncProgressSnapshot
        {
            IsActive = false,
            OperationId = 7
        });

        Assert.AreEqual(1, snapshots.Count);
        workspace.EndPlaylistSyncProgressOperation();
        Assert.AreEqual(2, snapshots.Count);
        Assert.IsFalse(snapshots[1].IsActive);
    }

    [TestMethod]
    public void TreeSelection_NormalFolderAndNotOwnedUseCanonicalDetailSelection()
    {
        var workspace = CreateDetailWorkspace(out _);
        var table = new BMSTable();
        PlaylistTreeSelectionRequestedEventArgs? request = null;
        workspace.TreeSelectionRequested += (_, e) => request = e;

        workspace.RequestDetailSelection(table, PlaylistFolderNode.CreateFolder("Folder A"));

        PlaylistTreeSelectionRequestedEventArgs folderRequest = request
            ?? throw new AssertFailedException("Folder selection request was not raised.");
        Assert.IsFalse(folderRequest.IsSummary);
        Assert.AreSame(table, folderRequest.Detail.Table);
        Assert.AreEqual("Folder A", folderRequest.Detail.FolderName);
        Assert.AreEqual(PlaylistDetailFilter.PlaylistFilter, folderRequest.Detail.Filter);
        Assert.AreEqual(1L, folderRequest.SelectionRevision);
        Assert.IsTrue(workspace.IsCurrentPlaylistDetailSelection(folderRequest.Detail, folderRequest.SelectionRevision));

        workspace.RequestDetailSelection(
            table,
            PlaylistFolderNode.CreateSpecial(PlaylistFolderNodeSpecialKind.NotOwned));

        PlaylistTreeSelectionRequestedEventArgs notOwnedRequest = request
            ?? throw new AssertFailedException("Not-owned selection request was not raised.");
        Assert.AreSame(table, notOwnedRequest.Detail.Table);
        Assert.IsNull(notOwnedRequest.Detail.FolderName);
        Assert.AreEqual(PlaylistDetailFilter.PlaylistNotOwnedFilterSelected, notOwnedRequest.Detail.Filter);
        Assert.AreEqual(2L, notOwnedRequest.SelectionRevision);
        Assert.IsFalse(workspace.IsCurrentPlaylistDetailSelection(folderRequest.Detail, folderRequest.SelectionRevision));
        Assert.IsTrue(workspace.IsCurrentPlaylistDetailSelection(notOwnedRequest.Detail, notOwnedRequest.SelectionRevision));
    }

    [TestMethod]
    public void TreeSelection_SummaryCanBeRequestedRepeatedly()
    {
        var workspace = CreateDetailWorkspace(out _);
        int requestCount = 0;
        workspace.TreeSelectionRequested += (_, request) =>
        {
            Assert.IsTrue(request.IsSummary);
            requestCount++;
        };

        workspace.RequestSummarySelection();
        workspace.RequestSummarySelection();

        Assert.AreEqual(2, requestCount);
        Assert.IsNull(workspace.CapturePlaylistDetailSelection());
    }

    [TestMethod]
    public void TreeSelection_StaleSummaryCannotExecuteAfterDetailSelection()
    {
        var workspace = CreateDetailWorkspace(out _);
        PlaylistTreeSelectionRequestedEventArgs? summaryRequest = null;
        workspace.TreeSelectionRequested += (_, request) =>
        {
            if (request.IsSummary)
            {
                summaryRequest = request;
            }
        };

        workspace.RequestSummarySelection();
        workspace.RequestDetailSelection(new BMSTable());

        PlaylistTreeSelectionRequestedEventArgs staleSummary = summaryRequest
            ?? throw new AssertFailedException("Summary selection request was not raised.");
        int appliedCount = 0;
        Assert.IsFalse(workspace.TryExecuteCurrentPlaylistSummarySelection(
            staleSummary.SelectionRevision,
            () => appliedCount++));
        Assert.AreEqual(0, appliedCount);
    }

    [TestMethod]
    public void TreeSelection_CurrentExecutionIsRejectedAfterClear()
    {
        var workspace = CreateDetailWorkspace(out _);
        PlaylistTreeSelectionRequestedEventArgs? detailRequest = null;
        workspace.TreeSelectionRequested += (_, request) =>
        {
            if (!request.IsSummary)
            {
                detailRequest = request;
            }
        };

        workspace.RequestDetailSelection(new BMSTable());
        PlaylistTreeSelectionRequestedEventArgs selected = detailRequest
            ?? throw new AssertFailedException("Detail selection request was not raised.");
        workspace.ClearPlaylistDetailSelection();

        int appliedCount = 0;
        Assert.IsFalse(workspace.TryExecuteCurrentPlaylistDetailSelection(
            selected.Detail,
            selected.SelectionRevision,
            () => appliedCount++));
        Assert.AreEqual(0, appliedCount);
    }

    [TestMethod]
    public void TreeSelection_CurrentExecutionSerializesRouteApplication()
    {
        var workspace = CreateDetailWorkspace(out _);
        var table = new BMSTable();
        PlaylistTreeSelectionRequestedEventArgs? detailRequest = null;
        workspace.TreeSelectionRequested += (_, request) =>
        {
            if (!request.IsSummary)
            {
                detailRequest = request;
            }
        };
        workspace.RequestDetailSelection(table);
        PlaylistTreeSelectionRequestedEventArgs selected = detailRequest
            ?? throw new AssertFailedException("Detail selection request was not raised.");

        bool applied = false;
        Assert.IsTrue(workspace.TryExecuteCurrentPlaylistDetailSelection(
            selected.Detail,
            selected.SelectionRevision,
            () =>
            {
                applied = true;
                workspace.ClearPlaylistDetailSelection();
            }));
        Assert.IsTrue(applied);
        Assert.IsNull(workspace.CapturePlaylistDetailSelection());
    }

    [TestMethod]
    public void RequestDetailRefresh_StaleInputUsesCurrentOwnerSelection()
    {
        var workspace = CreateDetailWorkspace(out _);
        workspace.InitializePlaylistDetailFilter(new ChartListFilterSnapshot("old", ChartModeFilter.All));
        var oldTable = new BMSTable();
        var currentTable = new BMSTable();
        PlaylistTreeSelectionRequestedEventArgs? request = null;
        workspace.TreeSelectionRequested += (_, selectionRequest) =>
        {
            if (!selectionRequest.IsSummary)
            {
                request = selectionRequest;
            }
        };
        workspace.RequestDetailSelection(oldTable);
        _ = request
            ?? throw new AssertFailedException("Initial detail selection request was not raised.");

        workspace.RequestDetailSelection(currentTable);
        workspace.RequestPlaylistDetailFilter(
            MainViewUpdateMode.KeywordFilterUpdated,
            new ChartListFilterSnapshot("current", ChartModeFilter.All));
        int requestVersion = workspace.RequestDetailRefresh(
            MainViewUpdateMode.PlaylistFilterSelected,
            MainViewUpdateMode.PlaylistFilterSelected,
            MainViewUpdateMode.PlaylistFilterSelected,
            useCoalescingWindow: false,
            openReadiness: default);

        Assert.IsTrue(requestVersion > 0);
        Assert.IsTrue(SpinWait.SpinUntil(() => workspace.IsDetailBuildIdle, 5000));
        Assert.AreSame(currentTable, workspace.DetailViewState.Source.CurrentTable);
        Assert.AreEqual("CURRENT", workspace.DetailViewState.View.CurrentIdentity?.KeywordFilter);
    }

    [TestMethod]
    public void TreeSelection_NullTableSelectionRemainsResolvedAsSelectionState()
    {
        var workspace = CreateDetailWorkspace(out _);
        PlaylistTreeSelectionRequestedEventArgs? request = null;
        workspace.TreeSelectionRequested += (_, e) => request = e;

        workspace.RequestDetailSelection(null);

        PlaylistTreeSelectionRequestedEventArgs detailRequest = request
            ?? throw new AssertFailedException("Null-table detail selection request was not raised.");
        Assert.IsFalse(detailRequest.IsSummary);
        Assert.IsNotNull(detailRequest.Detail);
        Assert.IsNull(detailRequest.Detail.Table);
        Assert.IsTrue(workspace.IsCurrentPlaylistDetailSelection(
            detailRequest.Detail,
            detailRequest.SelectionRevision));
        Assert.IsNotNull(workspace.CapturePlaylistDetailSelection());
        Assert.IsNull(workspace.CapturePlaylistDetailSelection().Table);
    }

    [TestMethod]
    public void TreeSelection_OwnerPreservesReplacementRemapAndContentRevision()
    {
        var workspace = CreateDetailWorkspace(out _);
        var oldTable = new BMSTable();
        var newTable = new BMSTable();
        PlaylistTreeSelectionRequestedEventArgs? request = null;
        workspace.TreeSelectionRequested += (_, e) => request = e;

        workspace.RequestDetailSelection(oldTable, PlaylistFolderNode.CreateFolder("Folder A"));
        PlaylistTreeSelectionRequestedEventArgs selected = request
            ?? throw new AssertFailedException("Detail selection request was not raised.");

        Assert.IsTrue(workspace.ReplaceCurrentPlaylistDetailSelectionTable(oldTable, newTable));
        PlaylistDetailSelection replaced = workspace.CapturePlaylistDetailSelection()
            ?? throw new AssertFailedException("Replaced detail selection was not retained.");
        Assert.AreSame(newTable, replaced.Table);
        Assert.AreEqual("Folder A", replaced.FolderName);
        Assert.IsFalse(workspace.IsCurrentPlaylistDetailSelection(selected.Detail, selected.SelectionRevision));

        Assert.IsTrue(workspace.RemapCurrentPlaylistDetailFolderSelection(
            newTable,
            new Dictionary<string, string> { ["Folder A"] = "Folder B" }));
        Assert.AreEqual("Folder B", workspace.CapturePlaylistDetailSelection().FolderName);

        workspace.DetailViewState.Source.PlaylistContentRevision = 9;
        Assert.IsTrue(workspace.MarkCurrentPlaylistDetailEntriesChanged(newTable, "test_entries_changed"));
        Assert.AreEqual(10, workspace.DetailViewState.Source.PlaylistContentRevision);
        Assert.IsFalse(workspace.MarkCurrentPlaylistDetailEntriesChanged(oldTable, "test_non_current_entries_changed"));
    }

    [TestMethod]
    public void EvaluateScoreSnapshotRefresh_DefersDuringEditAndPreservesHighestVersion()
    {
        var workspace = CreateDetailWorkspace(out _);
        workspace.DetailViewState.Source.LastBuiltScoreSnapshotVersion = 3;
        workspace.DetailViewState.Source.IsPlaylistCellEditing = true;

        PlaylistScoreRefreshDecision first = workspace.EvaluateScoreSnapshotRefresh(5);
        PlaylistScoreRefreshDecision second = workspace.EvaluateScoreSnapshotRefresh(4);

        Assert.IsTrue(first.Deferred);
        Assert.IsTrue(second.Deferred);
        Assert.AreEqual(5, workspace.DetailViewState.Source.PendingScoreSnapshotRefreshVersion);
        workspace.DetailViewState.Source.IsPlaylistCellEditing = false;
        PlaylistScoreRefreshDecision ready = workspace.EvaluateScoreSnapshotRefresh(5);
        Assert.IsTrue(ready.RefreshRequired);
        Assert.AreEqual(3, ready.LastBuiltVersion);
    }

    [TestMethod]
    public void SetDetailDataSource_ReinitializeUsesReplacementSource()
    {
        var workspace = CreateDetailWorkspace(out FakePlaylistDetailDataSource firstSource);
        var replacementSource = new FakePlaylistDetailDataSource();
        using var activeBuildCancellation = new CancellationTokenSource();
        PlaylistRequestIdentity identity = PlaylistRequestFactory.CreateIdentity(
            new BMSTable(), null, PlaylistDetailFilter.PlaylistFilter, null,
            ChartModeFilter.All, null, 1, 1, 1, 1, hasResolvedSelection: true);
        workspace.DetailBuildState.RequestVersion = 10;
        workspace.DetailBuildState.CurrentBuildCancellation = activeBuildCancellation;
        workspace.DetailBuildState.CurrentBuildRequest = new PlaylistBuildRequest { Identity = identity };
        workspace.DetailBuildState.WorkerRunning = true;
        workspace.DetailViewState.Source.CurrentIdentity = identity.SourceIdentity;
        workspace.SetDetailDataSource(replacementSource);
        Assert.AreEqual(11, workspace.DetailBuildState.RequestVersion);
        var replacementRequest = new PlaylistBuildRequest { Identity = identity };
        PlaylistBuildQueueRegisterResult registerResult = PlaylistDetailBuildQueueCoordinator.RegisterRequest(
            workspace.DetailBuildState,
            replacementRequest,
            currentViewIdentity: null,
            lastBuiltScoreSnapshotVersion: 0,
            isShutdownRequested: false);
        var table = new BMSTable
        {
            entries = [new TestablePlaylistEntry("99999999999999999999999999999999", "replacement")]
        };
        string cancellationStage = string.Empty;

        workspace.BuildDetailSourceRows(
            table,
            null,
            onlyNotOwned: false,
            new PlaylistLibraryIndexSnapshot { ResolveIndex = PlaylistLibraryResolveIndexSnapshot.Empty },
            CancellationToken.None,
            ref cancellationStage);

        Assert.AreEqual(0, firstSource.EnsureEntriesLoadedCallCount);
        Assert.AreEqual(1, replacementSource.EnsureEntriesLoadedCallCount);
        Assert.AreEqual(12, workspace.DetailBuildState.RequestVersion);
        Assert.IsTrue(activeBuildCancellation.IsCancellationRequested);
        Assert.IsNull(workspace.DetailViewState.Source.CurrentIdentity);
        Assert.IsTrue(registerResult.Enqueued);
        Assert.AreSame(replacementRequest, workspace.DetailBuildState.PendingRequest);
    }

    private static PlaylistWorkspaceViewModel CreateDetailWorkspace(out FakePlaylistDetailDataSource dataSource)
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot());
        dataSource = new FakePlaylistDetailDataSource();
        workspace.SetDetailDataSource(dataSource);
        return workspace;
    }

    private sealed class FakePlaylistDetailDataSource : IPlaylistDetailDataSource
    {
        internal int EnsureEntriesLoadedCallCount { get; private set; }

        internal LR2SongDBExtended.chart_info ChartInfo { get; set; } = null!;

        public int ChartInfoIndexVersion => 1;

        public int ScoreSnapshotVersion => 1;

        public long OwnedChartCollectionVersion => 1;

        public void EnsureEntriesLoaded(BMSTable table, string reason)
        {
            EnsureEntriesLoadedCallCount++;
        }

        public BMSLibrary.ScoreSnapshot GetScoreSnapshot()
        {
            return null!;
        }

        public PlaylistLibraryResolveIndexSnapshot GetResolveIndexSnapshot(
            CancellationToken cancellationToken,
            out bool cacheHit,
            out int staleRetryCount)
        {
            cacheHit = true;
            staleRetryCount = 0;
            return PlaylistLibraryResolveIndexSnapshot.Empty;
        }

        public LR2SongDBExtended.chart_info ResolveChartInfo(string sha256, string md5)
        {
            return ChartInfo;
        }

        public PlaylistDetailSourceRow CreateSourceRow(
            BMSTableEntry entry,
            ChartFile resolvedChart,
            BMSScore score,
            LR2SongDBExtended.chart_info chartInfo,
            LibraryChartRef resolvedChartRef)
        {
            return new PlaylistDetailSourceRow(
                entry,
                resolvedChart,
                scoreSnapshot: score,
                entryChartInfo: chartInfo,
                resolvedChartRef: resolvedChartRef);
        }
    }

    private sealed class TestablePlaylistEntry : BMSTableEntry
    {
        internal TestablePlaylistEntry(string md5Value, string titleValue)
        {
            md5 = md5Value;
            title = titleValue;
        }

        internal void SetSha256(string value)
        {
            sha256 = value;
        }
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
    public void MainTablePresentationCommit_CombinesWorkspaceStateBeforePublishingNotifications()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot());
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
        workspace.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName);

        PlaylistMainTablePresentationCommit commit = workspace.CommitMainTablePresentationWithoutNotification(
            selection,
            playlistDetailActive: true);

        Assert.AreEqual(Visibility.Visible, workspace.ColumnSettingsVisibilityForPlaylist);
        Assert.AreSame(summaryColumns, workspace.PlaylistSummaryColumnsSettings);
        Assert.IsTrue(workspace.IsPlaylistDetailViewActive);
        Assert.IsFalse(workspace.UseAsyncChartRowsViewBinding);
        Assert.AreEqual(0, propertyNames.Count);

        workspace.PublishMainTablePresentation(commit);

        CollectionAssert.Contains(propertyNames, nameof(PlaylistWorkspaceViewModel.ColumnSettingsVisibilityForPlaylist));
        CollectionAssert.Contains(propertyNames, nameof(PlaylistWorkspaceViewModel.PlaylistSummaryColumnsSettings));
        CollectionAssert.Contains(propertyNames, nameof(PlaylistWorkspaceViewModel.IsPlaylistDetailViewActive));
        CollectionAssert.Contains(propertyNames, nameof(PlaylistWorkspaceViewModel.UseAsyncChartRowsViewBinding));
    }

    [TestMethod]
    public void PlaylistWorkspaceSummarySortRequestCommitsOwnerStateBeforeEvent()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot());
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
    public void PlaylistWorkspaceDetailSortRequestCommitsOwnerStateBeforeEvent()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot());
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
    public void PlaylistWorkspaceDetailSortInitializationUsesFallbackOnlyOnce()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot());
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
            () => new CustomFolderOutputSettingsSnapshot());
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
    public void PlaylistWorkspaceDetailFilterInitializationResynchronizesAcrossDetailEntries()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot());

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
            () => new CustomFolderOutputSettingsSnapshot());
        var propertyNames = new List<string>();
        workspace.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName);

        workspace.PlaylistSummaryKeywordFilter = "memo:warning";

        StringAssert.Contains(workspace.PlaylistSummaryKeywordSearchWarningText, "memo");
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
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot());
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
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot())
        {
            IsPlaylistSummaryMode = true
        };
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
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot())
        {
            IsPlaylistSummaryMode = true
        };
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
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot())
        {
            IsPlaylistSummaryMode = true
        };

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
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot());
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
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot());
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
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot())
        {
            IsPlaylistSummaryMode = true
        };
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
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot())
        {
            IsPlaylistSummaryMode = true
        };
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
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot())
        {
            IsPlaylistSummaryMode = true
        };
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
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot());
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

    [TestMethod]
    public void PlaylistWorkspaceDropPolicyRejectsMixedExternalAndSpecialTargets()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot());
        var table = new BMSTable();
        var entry = new TestablePlaylistEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder");
        var playlistRow = new PlaylistDetailSourceRow(entry, resolvedChart: null).CreateViewRow();
        var specialFolder = PlaylistFolderNode.CreateSpecial(PlaylistFolderNodeSpecialKind.NotOwned);

        Assert.IsTrue(workspace.CanAcceptDrop([playlistRow], table, PlaylistFolderNode.CreateFolder("Folder")));
        Assert.IsFalse(workspace.CanAcceptDrop([playlistRow, new PlaylistSummaryRow()], table));
        Assert.IsFalse(workspace.CanAcceptDrop([playlistRow], table, specialFolder));

        table.is_external_sync = true;
        Assert.IsFalse(workspace.CanAcceptDrop([playlistRow], table));
    }

    [TestMethod]
    public async Task PlaylistWorkspaceExternalMutationRejectsBeforePersistenceAccess()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot());
        var table = new BMSTable { is_external_sync = true };
        var rejectedKinds = new List<PlaylistWorkspaceMutationKind>();
        workspace.MutationRejected += (_, request) => rejectedKinds.Add(request.Kind);

        await workspace.CreateFolderAsync(table);
        await workspace.RemoveFolderAsync(table, PlaylistFolderNode.CreateFolder("Folder"));
        await workspace.RenameFolderAsync(table, PlaylistFolderNode.CreateFolder("Folder"), "Renamed");
        await workspace.AddRowsToFolderAsync([], table);
        await workspace.DeleteEntriesAsync([], table);

        CollectionAssert.AreEqual(
            new[]
            {
                PlaylistWorkspaceMutationKind.CreateFolder,
                PlaylistWorkspaceMutationKind.RemoveFolder,
                PlaylistWorkspaceMutationKind.RenameFolder,
                PlaylistWorkspaceMutationKind.AddEntries,
                PlaylistWorkspaceMutationKind.RemoveEntries
            },
            rejectedKinds);
    }

    [TestMethod]
    public async Task PlaylistWorkspaceSpecialFolderMutationIsIgnoredWithoutPersistenceAccess()
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot());
        var table = new BMSTable();
        PlaylistFolderNode specialFolder = PlaylistFolderNode.CreateSpecial(PlaylistFolderNodeSpecialKind.NotOwned);

        await workspace.RenameFolderAsync(table, specialFolder, "Renamed");
        await workspace.RemoveFolderAsync(table, specialFolder);
        await workspace.AddRowsToFolderAsync([], table, specialFolder);
    }
}
