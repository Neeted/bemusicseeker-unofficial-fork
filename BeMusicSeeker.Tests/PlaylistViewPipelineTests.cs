using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Codeplex.Data;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PlaylistViewPipelineTests
{
    [ClassInitialize]
    public static void ClassInitialize(TestContext _)
    {
        TestResourceInitializer.EnsureJapaneseResources();
    }

    [TestMethod]
    public void ApplyPlaylistViewFromSource_SortKeepsAllRowsVisible()
    {
        PlaylistDetailSourceRow zetaRow = CreateSourceRow("11111111111111111111111111111111", "Zeta", 7);
        PlaylistDetailSourceRow alphaRow = CreateSourceRow("22222222222222222222222222222222", "Alpha", 7);
        var sourceRows = new PlaylistDetailSourceRow[] { zetaRow, alphaRow };
        var sortParameters = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = nameof(BMSFile.Title),
            Direction = ListSortDirection.Ascending
        };

        List<PlaylistDetailRow> result = MainWindowViewModel.ApplyPlaylistViewFromSource(
            sourceRows,
            keywordFilter: null,
            modeFilter: MainWindowViewModel.ModeFilterType.All,
            sortParameters: sortParameters,
            out string sortProfile,
            out int keywordCount,
            out int modeCount,
            out long _,
            out long _,
            out long _,
            out long _);

        Assert.AreEqual(2, result.Count);
        CollectionAssert.AreEqual(new[] { "Alpha", "Zeta" }, result.Select(row => row.Title).ToArray());
        CollectionAssert.AreEqual(new[] { "22222222222222222222222222222222", "11111111111111111111111111111111" }, result.Select(row => row.hash).ToArray());
        Assert.IsFalse(ReferenceEquals(sourceRows[0], result[0]));
        Assert.IsFalse(ReferenceEquals(sourceRows[1], result[1]));
        Assert.AreEqual(2, keywordCount);
        Assert.AreEqual(2, modeCount);
        Assert.IsFalse(string.IsNullOrWhiteSpace(sortProfile));
        Assert.IsTrue(result.All(row => !typeof(BMSFile).IsAssignableFrom(row.GetType())));
    }

    [TestMethod]
    public void PlaylistDetailPresentationService_AppliesKeywordModeAndSortWithoutRoot()
    {
        PlaylistDetailSourceRow zetaRow = CreateSourceRow("11111111111111111111111111111111", "Zeta", 7);
        PlaylistDetailSourceRow alphaRow = CreateSourceRow("22222222222222222222222222222222", "Alpha", 5);
        PlaylistDetailSourceRow bravoRow = CreateSourceRow("33333333333333333333333333333333", "Bravo", 7, memo: "target");
        var sourceRows = new PlaylistDetailSourceRow[] { zetaRow, alphaRow, bravoRow };
        var sortParameters = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = nameof(BMSFile.Title),
            Direction = ListSortDirection.Ascending
        };

        List<PlaylistDetailRow> result = PlaylistDetailPresentationService.ApplyViewFromSource(
            sourceRows,
            keywordFilter: "target",
            modeFilter: MainWindowViewModel.ModeFilterType._7KEYS,
            sortParameters: sortParameters,
            out string sortProfile,
            out int keywordCount,
            out int modeCount,
            out long keywordStageMs,
            out long modeStageMs,
            out long sortStageMs,
            out long viewMaterializeMs);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("Bravo", result[0].Title);
        Assert.AreEqual(1, keywordCount);
        Assert.AreEqual(1, modeCount);
        Assert.IsFalse(string.IsNullOrWhiteSpace(sortProfile));
        Assert.IsTrue(keywordStageMs >= 0);
        Assert.IsTrue(modeStageMs >= 0);
        Assert.IsTrue(sortStageMs >= 0);
        Assert.IsTrue(viewMaterializeMs >= 0);
    }

    [TestMethod]
    public void PlaylistBuildRequest_SourceText_IsRootExternalContract()
    {
        string rootSource = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs");
        string requestSource = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "ViewModels", "MainWindow", "PlaylistBuildRequest.cs");

        Assert.AreEqual(-1, rootSource.IndexOf("private sealed class PlaylistBuildRequest", StringComparison.Ordinal));
        StringAssert.Contains(requestSource, "internal sealed class PlaylistBuildRequest");
        StringAssert.Contains(requestSource, "internal MainViewUpdateMode Mode;");
        StringAssert.Contains(requestSource, "internal MainWindowViewModel.PlaylistRequestIdentity Identity;");
    }

    [TestMethod]
    public void PlaylistDetailBuildState_SourceText_OwnsWorkerQueueState()
    {
        string rootSource = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs");
        string playlistStateSource = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "ViewModels", "MainWindow", "MainWindowViewModel.PlaylistState.cs");
        string playlistDetailViewStateSource = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "ViewModels", "MainWindow", "PlaylistDetailViewState.cs");
        string buildStateSource = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "ViewModels", "MainWindow", "PlaylistDetailBuildState.cs");
        string queueCoordinatorSource = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "ViewModels", "MainWindow", "PlaylistDetailBuildQueueCoordinator.cs");
        string terminalOwnerSource = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "ViewModels", "MainWindow", "PlaylistDetailTerminalOwner.cs");
        string sourceBuildResultSource = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "ViewModels", "MainWindow", "PlaylistSourceBuildResult.cs");
        string sourceRowSource = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "ViewModels", "PlaylistDetailSourceRow.cs");
        string playlistViewStateSource = ExtractTypeBlock(playlistDetailViewStateSource, "internal sealed class PlaylistDetailViewState");
        string playlistSourceSnapshotStateSource = ExtractTypeBlock(playlistDetailViewStateSource, "internal sealed class PlaylistDetailSourceSnapshotState");
        string playlistViewSnapshotStateSource = ExtractTypeBlock(playlistDetailViewStateSource, "internal sealed class PlaylistDetailViewSnapshotState");

        foreach (string rootFieldDeclaration in new[]
        {
            "internal readonly SemaphoreSlim BuildGate = new(1, 1);",
            "internal int RequestVersion;",
            "internal CancellationTokenSource Cancellation = new();",
            "internal CancellationTokenSource CurrentBuildCancellation;",
            "internal PlaylistBuildRequest CurrentBuildRequest;",
            "internal PlaylistBuildRequest PendingRequest;",
            "internal bool WorkerRunning;",
            "internal bool ShutdownCancellationRequested;"
        })
        {
            Assert.AreEqual(-1, playlistViewStateSource.IndexOf(rootFieldDeclaration, StringComparison.Ordinal), rootFieldDeclaration);
        }

        StringAssert.Contains(buildStateSource, "internal sealed class PlaylistDetailBuildState");
        StringAssert.Contains(buildStateSource, "internal readonly object SyncRoot = new();");
        StringAssert.Contains(buildStateSource, "internal readonly SemaphoreSlim BuildGate = new(1, 1);");
        StringAssert.Contains(buildStateSource, "internal PlaylistBuildRequest PendingRequest;");
        StringAssert.Contains(buildStateSource, "internal bool ShutdownCancellationRequested;");
        StringAssert.Contains(playlistSourceSnapshotStateSource, "internal List<PlaylistDetailSourceRow> Rows = [];");
        StringAssert.Contains(playlistSourceSnapshotStateSource, "internal MainWindowViewModel.PlaylistSourceIdentity? CurrentIdentity;");
        StringAssert.Contains(playlistViewSnapshotStateSource, "internal IList Rows = new List<object>();");
        StringAssert.Contains(playlistViewSnapshotStateSource, "internal MainWindowViewModel.PlaylistRequestIdentity? CurrentIdentity;");
        StringAssert.Contains(playlistViewStateSource, "internal readonly PlaylistDetailSourceSnapshotState Source = new();");
        StringAssert.Contains(playlistViewStateSource, "internal readonly PlaylistDetailViewSnapshotState View = new();");
        Assert.AreEqual(-1, playlistViewStateSource.IndexOf("internal List<PlaylistDetailSourceRow> SourceRows", StringComparison.Ordinal));
        Assert.AreEqual(-1, playlistViewStateSource.IndexOf("internal IList CurrentViewRows", StringComparison.Ordinal));
        StringAssert.Contains(rootSource, "private readonly PlaylistDetailBuildState playlistDetailBuildState = new();");
        StringAssert.Contains(queueCoordinatorSource, "internal static class PlaylistDetailBuildQueueCoordinator");
        StringAssert.Contains(queueCoordinatorSource, "state.ShutdownCancellationRequested || isShutdownRequested");
        StringAssert.Contains(queueCoordinatorSource, "state.ShutdownCancellationRequested = true;");
        StringAssert.Contains(queueCoordinatorSource, "lock (state.SyncRoot)");
        StringAssert.Contains(rootSource, "CreatePlaylistBuildRequestViewSnapshotUnsafe");
        StringAssert.Contains(rootSource, "PlaylistDetailBuildQueueCoordinator.RegisterRequest");
        StringAssert.Contains(rootSource, "PlaylistDetailBuildQueueCoordinator.CancelForShutdown");
        StringAssert.Contains(terminalOwnerSource, "buildState.RequestVersion++;");
        StringAssert.Contains(terminalOwnerSource, "buildState.PendingRequest = null;");
        StringAssert.Contains(terminalOwnerSource, "commit.BuildCancellation?.Cancel();");
        Assert.AreEqual(-1, rootSource.IndexOf("CommitPlaylistSourceClearWithoutCallbacks", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("PublishPlaylistSourceClear", StringComparison.Ordinal));
        StringAssert.Contains(rootSource, "PlaylistDetailBuildDecisionService.Decide(request, stateSnapshot)");
        StringAssert.Contains(rootSource, "TryCommitPlaylistDetailTerminal(");
        StringAssert.Contains(sourceBuildResultSource, "internal sealed class PlaylistSourceBuildResult");
        StringAssert.Contains(sourceBuildResultSource, "internal sealed class PlaylistSourceBuildStageResult");
        StringAssert.Contains(sourceBuildResultSource, "internal sealed class PlaylistViewApplyResult");
        StringAssert.Contains(sourceBuildResultSource, "internal sealed class PlaylistMainViewApplyResult");
        StringAssert.Contains(sourceBuildResultSource, "internal sealed class PlaylistDetailBuildCompletionResult");
        StringAssert.Contains(sourceBuildResultSource, "internal sealed class PlaylistRebuildExecutionResult");
        StringAssert.Contains(sourceBuildResultSource, "internal sealed class PlaylistScoreProbeMetrics");
        StringAssert.Contains(rootSource, "private PlaylistSourceBuildStageResult BuildPlaylistSourceForRequest(");
        StringAssert.Contains(rootSource, "private PlaylistViewApplyResult ApplyPlaylistViewFromCurrentSource(");
        StringAssert.Contains(rootSource, "private PlaylistViewApplyResult ApplyPlaylistViewFromRebuiltSource(MainViewUpdateMode mode, List<PlaylistDetailSourceRow> sourceRows, int sourceCount, ref IList finalRows)");
        StringAssert.Contains(rootSource, "internal PlaylistDetailTerminalApplyResult TryCommitPlaylistDetailTerminal(");
        StringAssert.Contains(terminalOwnerSource, "internal sealed class PlaylistDetailTerminalOwner");
        StringAssert.Contains(terminalOwnerSource, "lock (buildState.SyncRoot)");
        StringAssert.Contains(terminalOwnerSource, "lock (viewState.SyncRoot)");
        StringAssert.Contains(terminalOwnerSource, "mainChartList.CommitPreparedRowsWithoutDisposal(prepared)");
        StringAssert.Contains(terminalOwnerSource, "mainChartList.DisposeCommittedRows(mainRowsCommit)");
        StringAssert.Contains(terminalOwnerSource, "mainChartList.PublishRowsCommit(mainRowsCommit)");
        StringAssert.Contains(terminalOwnerSource, "playlistWorkspace.CommitColumnPresentationWithoutNotification(");
        StringAssert.Contains(terminalOwnerSource, "playlistWorkspace.PublishColumnPresentation(result.ColumnPresentationCommit)");
        StringAssert.Contains(terminalOwnerSource, "new AggregateException(terminalExceptions)");
        Assert.IsFalse(rootSource.Contains("PlaylistDetailTerminalTransition"));
        StringAssert.Contains(rootSource, "request.RequestVersion != playlistDetailBuildState.RequestVersion");
        StringAssert.Contains(rootSource, "ReferenceEquals(playlistViewState.Source.Rows, sourceRows)");
        StringAssert.Contains(sourceRowSource, "internal PlaylistDetailSourceRow WithEntryChartInfo(");
        StringAssert.Contains(rootSource, "PlaylistSourceBuildResult sourceBuildResult = BuildPlaylistSourceRows");
        StringAssert.Contains(rootSource, "private bool RebuildPlaylistSource(");
        StringAssert.Contains(rootSource, "playlistDetailBuildState.BuildGate.Wait(cancellationToken);");
        StringAssert.Contains(rootSource, "playlistDetailBuildState.BuildGate.Release();");
        StringAssert.Contains(rootSource, "PlaylistSourceBuildStageResult sourceBuildStageResult = BuildPlaylistSourceForRequest(");
        StringAssert.Contains(rootSource, "PlaylistViewApplyResult viewApplyResult = ApplyPlaylistViewFromRebuiltSource(");
        StringAssert.Contains(rootSource, "terminalApplyResult = TryCommitPlaylistDetailTerminal(");
        StringAssert.Contains(rootSource, "private bool ApplyPlaylistViewWithoutSourceRebuild(");
        StringAssert.Contains(rootSource, "PlaylistViewApplyResult viewApplyResult = ApplyPlaylistViewFromCurrentSource(mode);");
        StringAssert.Contains(rootSource, "private void FinalizeRebuiltPlaylistDetailBuild(");
        StringAssert.Contains(rootSource, "private void FinalizeViewOnlyPlaylistDetailBuild(");
        StringAssert.Contains(rootSource, "private void FinalizePlaylistDetailBuild(");
        Assert.AreEqual(-1, rootSource.IndexOf("IPlaylistDetailBuildWorkflowHost", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("PlaylistDetailBuildWorkflowCoordinator", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("out int scoreUpdateTargetCount", StringComparison.Ordinal));
        Assert.AreEqual(-1, rootSource.IndexOf("out long columnStageMs", StringComparison.Ordinal));
        StringAssert.Contains(rootSource, "new PlaylistDetailBuildCompletionResult");
        Assert.AreEqual(-1, playlistStateSource.IndexOf("private sealed class PlaylistScoreProbeMetrics", StringComparison.Ordinal));
    }

    [TestMethod]
    public void PlaylistDetailTerminal_StaleRequestCancelsPreparationWithoutApplyingRows()
    {
        var viewModel = new MainWindowViewModel();
        var oldRows = new List<object>();
        var candidateRows = new List<object> { new object() };
        viewModel.MainChartList.Rows = oldRows;
        viewModel.MainChartList.ColumnsSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
        viewModel.PlaylistWorkspace.ColumnSettingsVisibilityForPlaylist = System.Windows.Visibility.Collapsed;
        PlaylistSummaryColumnSettings oldSummaryColumns = viewModel.PlaylistWorkspace.PlaylistSummaryColumnsSettings;
        var identity = CreatePlaylistIdentity("stale-terminal");
        var request = new PlaylistBuildRequest
        {
            RequestVersion = 1,
            Mode = MainViewUpdateMode.PlaylistFilterSelected,
            RequestedMode = MainViewUpdateMode.PlaylistFilterSelected,
            Identity = identity
        };
        int preparingCount = 0;
        int canceledCount = 0;
        viewModel.MainChartList.RowsReplacing += (_, _) => preparingCount++;
        viewModel.MainChartList.RowsReplacementCanceled += (_, _) => canceledCount++;

        PlaylistDetailTerminalApplyResult result = viewModel.TryCommitPlaylistDetailTerminal(
            request,
            replaceSource: false,
            sourceRows: null,
            currentTable: null,
            currentFolderName: null,
            identity.FilterType,
            candidateRows,
            candidateRows.Count,
            MainViewUpdateMode.PlaylistFilterSelected,
            Stopwatch.StartNew());

        Assert.IsFalse(result.Applied);
        Assert.AreSame(oldRows, viewModel.MainChartList.Rows);
        Assert.AreEqual(System.Windows.Visibility.Collapsed, viewModel.PlaylistWorkspace.ColumnSettingsVisibilityForPlaylist);
        Assert.AreSame(oldSummaryColumns, viewModel.PlaylistWorkspace.PlaylistSummaryColumnsSettings);
        Assert.AreEqual(1, preparingCount);
        Assert.AreEqual(1, canceledCount);
    }

    [TestMethod]
    public void PlaylistDetailTerminal_PostCommitFailureStillDisposesAndPublishesAllOwners()
    {
        var buildState = new PlaylistDetailBuildState { RequestVersion = 1 };
        var viewState = new PlaylistDetailViewState();
        var table = new MainChartListViewModel();
        var workspace = new PlaylistWorkspaceViewModel();
        var oldRow = new TrackingDisposableRow();
        var oldRows = new List<object> { oldRow };
        var candidateRows = new List<object> { new object() };
        table.Rows = oldRows;
        viewState.View.Rows = oldRows;
        int tableNotifications = 0;
        int workspaceNotifications = 0;
        table.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainChartListViewModel.Rows))
            {
                tableNotifications++;
                throw new InvalidOperationException("table publish failed");
            }
        };
        workspace.PropertyChanged += (_, _) => workspaceNotifications++;
        var owner = new PlaylistDetailTerminalOwner(
            buildState,
            viewState,
            table,
            workspace,
            _ => throw new InvalidOperationException("external column mode failed"));

        PlaylistDetailTerminalPublishException exception = Assert.ThrowsException<PlaylistDetailTerminalPublishException>(
            () => owner.TryApply(CreatePlaylistTerminalRequest(candidateRows, requestVersion: 1)));

        Assert.IsTrue(exception.OwnershipTransferred);
        Assert.AreSame(candidateRows, table.Rows);
        Assert.AreEqual(1, oldRow.DisposeCount);
        Assert.IsTrue(tableNotifications > 0);
        Assert.IsTrue(workspaceNotifications > 0);
    }

    [TestMethod]
    public void PlaylistDetailTerminal_DisposeFailureDoesNotSuppressRemainingPublishers()
    {
        var buildState = new PlaylistDetailBuildState { RequestVersion = 1 };
        var viewState = new PlaylistDetailViewState();
        var table = new MainChartListViewModel();
        var workspace = new PlaylistWorkspaceViewModel();
        var oldRow = new TrackingDisposableRow(throwOnDispose: true);
        var laterRow = new TrackingDisposableRow();
        var oldRows = new List<object> { oldRow, laterRow };
        var candidateRows = new List<object> { new object() };
        table.Rows = oldRows;
        viewState.View.Rows = oldRows;
        int tableNotifications = 0;
        int workspaceNotifications = 0;
        table.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainChartListViewModel.Rows))
            {
                tableNotifications++;
            }
        };
        workspace.PropertyChanged += (_, _) => workspaceNotifications++;
        var owner = new PlaylistDetailTerminalOwner(buildState, viewState, table, workspace, _ => { });

        PlaylistDetailTerminalPublishException exception = Assert.ThrowsException<PlaylistDetailTerminalPublishException>(
            () => owner.TryApply(CreatePlaylistTerminalRequest(candidateRows, requestVersion: 1)));

        Assert.IsTrue(exception.OwnershipTransferred);
        Assert.AreSame(candidateRows, table.Rows);
        Assert.AreEqual(1, oldRow.DisposeCount);
        Assert.AreEqual(1, laterRow.DisposeCount);
        Assert.IsTrue(tableNotifications > 0);
        Assert.IsTrue(workspaceNotifications > 0);
    }

    [TestMethod]
    public void PlaylistDetailTerminal_SourceClearCommitsStateBeforePublishingCancellation()
    {
        var pendingRequest = new PlaylistBuildRequest { RequestVersion = 7 };
        var buildCancellation = new CancellationTokenSource();
        var buildState = new PlaylistDetailBuildState
        {
            RequestVersion = 7,
            PendingRequest = pendingRequest,
            CurrentBuildRequest = pendingRequest,
            CurrentBuildCancellation = buildCancellation
        };
        var sourceRows = new List<PlaylistDetailSourceRow> { CreateSourceRow("11111111111111111111111111111111", "Source", 7) };
        var viewRow = new TrackingDisposableRow();
        var viewRows = new List<object> { viewRow };
        var viewState = new PlaylistDetailViewState
        {
            CurrentOpenInteraction = new PlaylistOpenInteractionState { RequestVersion = 7 }
        };
        viewState.Source.Rows = sourceRows;
        viewState.Source.GenerationId = 11;
        viewState.Source.CurrentFolderName = "folder";
        viewState.Source.LastBuiltLibraryIndexVersion = 3;
        viewState.Source.IsPlaylistCellEditing = true;
        viewState.View.Rows = viewRows;
        viewState.View.GenerationId = 13;
        viewState.View.LastAppliedCount = 1;
        var owner = new PlaylistDetailTerminalOwner(
            buildState,
            viewState,
            new MainChartListViewModel(),
            new PlaylistWorkspaceViewModel(),
            _ => { });

        PlaylistSourceClearCommitResult commit = owner.CommitSourceClearWithoutCallbacks();

        Assert.AreEqual(8, buildState.RequestVersion);
        Assert.IsNull(buildState.PendingRequest);
        Assert.IsNull(buildState.CurrentBuildRequest);
        Assert.IsFalse(buildCancellation.IsCancellationRequested);
        Assert.AreSame(sourceRows, commit.SourceRows);
        Assert.AreSame(viewRows, commit.ViewRows);
        Assert.AreEqual(11, commit.PreviousGenerationId);
        Assert.AreEqual(0, viewState.Source.Rows.Count);
        Assert.AreEqual(0, viewState.View.Rows.Count);
        Assert.AreEqual(0, viewState.Source.GenerationId);
        Assert.AreEqual(0, viewState.View.GenerationId);
        Assert.AreEqual(0, viewState.View.LastAppliedCount);
        Assert.IsNull(viewState.Source.CurrentFolderName);
        Assert.AreEqual(0, viewState.Source.LastBuiltLibraryIndexVersion);
        Assert.IsFalse(viewState.Source.IsPlaylistCellEditing);
        Assert.IsNull(viewState.CurrentOpenInteraction);
        Assert.AreEqual(0, viewRow.DisposeCount, "Source clear must not duplicate main-table row disposal.");

        owner.PublishSourceClear(commit);

        Assert.IsTrue(buildCancellation.IsCancellationRequested);
        Assert.AreEqual(0, viewRow.DisposeCount);
    }

    [TestMethod]
    public void PlaylistDetailTerminal_SourceClearPublishIgnoresDisposedCancellation()
    {
        var cancellation = new CancellationTokenSource();
        cancellation.Dispose();
        var owner = new PlaylistDetailTerminalOwner(
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            new MainChartListViewModel(),
            new PlaylistWorkspaceViewModel(),
            _ => { });
        var commit = new PlaylistSourceClearCommitResult(null, null, 0, cancellation);

        owner.PublishSourceClear(commit);
    }

    [TestMethod]
    public void ApplyPlaylistVirtualViewFromSource_DoesNotMaterializeRowsUntilIndexed()
    {
        PlaylistDetailSourceRow zetaRow = CreateSourceRow("11111111111111111111111111111111", "Zeta", 7);
        PlaylistDetailSourceRow alphaRow = CreateSourceRow("22222222222222222222222222222222", "Alpha", 7);
        var sourceRows = new PlaylistDetailSourceRow[] { zetaRow, alphaRow };
        var sortParameters = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = nameof(BMSFile.Title),
            Direction = ListSortDirection.Ascending
        };

        PlaylistDetailVirtualView view = MainWindowViewModel.ApplyPlaylistVirtualViewFromSource(
            sourceRows,
            keywordFilter: null,
            modeFilter: MainWindowViewModel.ModeFilterType.All,
            sortParameters: sortParameters,
            out string sortProfile,
            out int keywordCount,
            out int modeCount,
            out long _,
            out long _,
            out long _,
            out long viewMaterializeMs);

        Assert.AreEqual(2, view.Count);
        Assert.AreEqual(2, view.RowCount);
        Assert.AreEqual(0, view.RealizedRowCount);
        Assert.AreEqual(2, keywordCount);
        Assert.AreEqual(2, modeCount);
        Assert.IsFalse(string.IsNullOrWhiteSpace(sortProfile));
        Assert.IsTrue(viewMaterializeMs >= 0);

        var first = (PlaylistDetailRow)view[0];

        Assert.AreEqual("Alpha", first.Title);
        Assert.AreEqual(1, view.RealizedRowCount);
        Assert.IsTrue(ReferenceEquals(first, view[0]));
        Assert.AreEqual(1, view.RealizedRowCount);
    }

    [TestMethod]
    public void ApplyPlaylistViewFromSource_KeywordFilterMatchesPlaylistMemoAndComment()
    {
        PlaylistDetailSourceRow matchedRow = CreateSourceRow("33333333333333333333333333333333", "Matched", 7, memo: "special memo");
        PlaylistDetailSourceRow filteredRow = CreateSourceRow("44444444444444444444444444444444", "Filtered", 7, comment: "ordinary");
        var sourceRows = new PlaylistDetailSourceRow[] { matchedRow, filteredRow };
        var sortParameters = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = nameof(BMSFile.Title),
            Direction = ListSortDirection.Ascending
        };

        List<PlaylistDetailRow> result = MainWindowViewModel.ApplyPlaylistViewFromSource(
            sourceRows,
            keywordFilter: "SPECIAL",
            modeFilter: MainWindowViewModel.ModeFilterType.All,
            sortParameters: sortParameters,
            out string _,
            out int keywordCount,
            out int modeCount,
            out long _,
            out long _,
            out long _,
            out long _);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("Matched", result[0].Title);
        Assert.AreEqual(1, keywordCount);
        Assert.AreEqual(1, modeCount);
        Assert.IsFalse(ReferenceEquals(sourceRows[0], result[0]));
    }

    [TestMethod]
    public void ApplyPlaylistViewFromSource_KeywordFilterSupportsAndAndHashFields()
    {
        PlaylistDetailSourceRow matchedRow = CreateSourceRow(
            "33333333333333333333333333333333",
            "Matched",
            7,
            memo: "special memo",
            sha256: "abababababababababababababababababababababababababababababababab");
        PlaylistDetailSourceRow filteredRow = CreateSourceRow(
            "44444444444444444444444444444444",
            "Filtered",
            7,
            memo: "special memo",
            sha256: "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd");
        var sourceRows = new PlaylistDetailSourceRow[] { matchedRow, filteredRow };

        List<PlaylistDetailRow> result = MainWindowViewModel.ApplyPlaylistViewFromSource(
            sourceRows,
            keywordFilter: "title:Matched memo:special md5:333333 sha256:abab",
            modeFilter: MainWindowViewModel.ModeFilterType.All,
            sortParameters: new MainWindowViewModel.cSortParameters { ColumnsName = nameof(BMSFile.Title), Direction = ListSortDirection.Ascending },
            out string _,
            out int keywordCount,
            out int modeCount,
            out long _,
            out long _,
            out long _,
            out long _);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("Matched", result[0].Title);
        Assert.AreEqual(1, keywordCount);
        Assert.AreEqual(1, modeCount);
    }

    [TestMethod]
    public void ApplyPlaylistViewFromSource_UnknownFieldQueryDoesNotMatch()
    {
        PlaylistDetailSourceRow matchedRow = CreateSourceRow("33333333333333333333333333333333", "Matched", 7, memo: "special memo");

        List<PlaylistDetailRow> result = MainWindowViewModel.ApplyPlaylistViewFromSource(
            [matchedRow],
            keywordFilter: "unknown:Matched",
            modeFilter: MainWindowViewModel.ModeFilterType.All,
            sortParameters: new MainWindowViewModel.cSortParameters { ColumnsName = nameof(BMSFile.Title), Direction = ListSortDirection.Ascending },
            out string _,
            out int keywordCount,
            out int modeCount,
            out long _,
            out long _,
            out long _,
            out long _);

        Assert.AreEqual(0, result.Count);
        Assert.AreEqual(0, keywordCount);
        Assert.AreEqual(0, modeCount);
    }

    [TestMethod]
    public void ApplyPlaylistViewFromSource_KeywordFilterSupportsQuoteNegationOrAndRegex()
    {
        PlaylistDetailSourceRow matchedRow = CreateSourceRow(
            "33333333333333333333333333333333",
            "Matched Alpha",
            7,
            memo: "special memo",
            comment: "safe comment",
            sha256: "abababababababababababababababababababababababababababababababab");
        PlaylistDetailSourceRow filteredRow = CreateSourceRow(
            "44444444444444444444444444444444",
            "Filtered Alpha",
            7,
            memo: "special memo",
            comment: "ordinary comment",
            sha256: "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd");

        List<PlaylistDetailRow> result = MainWindowViewModel.ApplyPlaylistViewFromSource(
            [matchedRow, filteredRow],
            keywordFilter: "memo:\"special memo\" -comment:ordinary md5:333333|555555 sha256:abab|efef title:re:^matched",
            modeFilter: MainWindowViewModel.ModeFilterType.All,
            sortParameters: new MainWindowViewModel.cSortParameters { ColumnsName = nameof(BMSFile.Title), Direction = ListSortDirection.Ascending },
            out string _,
            out int keywordCount,
            out int modeCount,
            out long _,
            out long _,
            out long _,
            out long _);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("Matched Alpha", result[0].Title);
        Assert.AreEqual(1, keywordCount);
        Assert.AreEqual(1, modeCount);
    }

    [TestMethod]
    public void ApplyPlaylistViewFromSource_ModeFilterRecomputesFromSourceRows()
    {
        PlaylistDetailSourceRow sevenKeysRow = CreateSourceRow("55555555555555555555555555555555", "SevenKeys", 7);
        PlaylistDetailSourceRow fourteenKeysRow = CreateSourceRow("66666666666666666666666666666666", "FourteenKeys", 14);
        var sourceRows = new PlaylistDetailSourceRow[] { sevenKeysRow, fourteenKeysRow };
        var sortParameters = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = nameof(BMSFile.Title),
            Direction = ListSortDirection.Ascending
        };

        List<PlaylistDetailRow> result = MainWindowViewModel.ApplyPlaylistViewFromSource(
            sourceRows,
            keywordFilter: null,
            modeFilter: MainWindowViewModel.ModeFilterType._14KEYS,
            sortParameters: sortParameters,
            out string _,
            out int keywordCount,
            out int modeCount,
            out long _,
            out long _,
            out long _,
            out long _);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("FourteenKeys", result[0].Title);
        Assert.AreEqual(2, keywordCount);
        Assert.AreEqual(1, modeCount);
        Assert.IsFalse(ReferenceEquals(sourceRows[1], result[0]));
    }

    [TestMethod]
    public void ApplyPlaylistViewFromSource_RebuildsDetachedSnapshotsForEachApply()
    {
        PlaylistDetailSourceRow alphaRow = CreateSourceRow("77777777777777777777777777777777", "Alpha", 7);
        var sourceRows = new PlaylistDetailSourceRow[] { alphaRow };
        var sortParameters = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = nameof(BMSFile.Title),
            Direction = ListSortDirection.Ascending
        };

        List<PlaylistDetailRow> first = MainWindowViewModel.ApplyPlaylistViewFromSource(
            sourceRows,
            keywordFilter: null,
            modeFilter: MainWindowViewModel.ModeFilterType.All,
            sortParameters: sortParameters,
            out string _,
            out int _,
            out int _,
            out long _,
            out long _,
            out long _,
            out long _);
        List<PlaylistDetailRow> second = MainWindowViewModel.ApplyPlaylistViewFromSource(
            sourceRows,
            keywordFilter: null,
            modeFilter: MainWindowViewModel.ModeFilterType.All,
            sortParameters: sortParameters,
            out string _,
            out int _,
            out int _,
            out long _,
            out long _,
            out long _,
            out long _);

        Assert.AreEqual(1, first.Count);
        Assert.AreEqual(1, second.Count);
        Assert.IsFalse(ReferenceEquals(first[0], second[0]));
        Assert.IsFalse(ReferenceEquals(sourceRows[0], first[0]));
        Assert.IsFalse(ReferenceEquals(sourceRows[0], second[0]));
    }

    [TestMethod]
    public void ApplyPlaylistViewFromSource_LevelSortUsesPlaylistEntryDoubleValueNumerically()
    {
        PlaylistDetailSourceRow entryLevelTwelve = CreateSourceRow("88888888888888888888888888888888", "Twelve", 7, entryLevel: 12);
        PlaylistDetailSourceRow entryLevelTwoPointFive = CreateSourceRow("99999999999999999999999999999999", "TwoPointFive", 7, entryLevel: 2.5);
        PlaylistDetailSourceRow entryLevelThree = CreateSourceRow("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Three", 7, entryLevel: 3);
        var sortParameters = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = nameof(PlaylistDetailRow.Level),
            Direction = ListSortDirection.Ascending
        };

        List<PlaylistDetailRow> result = MainWindowViewModel.ApplyPlaylistViewFromSource(
            [entryLevelTwelve, entryLevelTwoPointFive, entryLevelThree],
            keywordFilter: null,
            modeFilter: MainWindowViewModel.ModeFilterType.All,
            sortParameters: sortParameters,
            out string sortProfile,
            out int _,
            out int _,
            out long _,
            out long _,
            out long _,
            out long _);

        CollectionAssert.AreEqual(new[] { "2.5", "3", "12" }, result.Select(row => row.Level).ToArray());
        CollectionAssert.AreEqual(new[] { "TwoPointFive", "Three", "Twelve" }, result.Select(row => row.Title).ToArray());
        Assert.AreEqual("level_mixed_double", sortProfile);
    }

    [TestMethod]
    public void CreatePlaylistRequestIdentity_NormalizesKeywordAndFolderForDedup()
    {
        var table = new BMSTable();
        var sortParameters = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = nameof(BMSFile.Title),
            Direction = ListSortDirection.Ascending
        };

        MainWindowViewModel.PlaylistRequestIdentity left = PlaylistRequestFactory.CreateIdentity(table, " FolderA ", MainWindowViewModel.PlaylistFilterType.PlaylistFilter, " keyword ", MainWindowViewModel.ModeFilterType._7KEYS, sortParameters, libraryIndexVersion: 10, playlistRevision: 20, scoreSnapshotVersion: 30, chartInfoIndexVersion: 40, hasResolvedSelection: true);
        MainWindowViewModel.PlaylistRequestIdentity right = PlaylistRequestFactory.CreateIdentity(table, "FolderA", MainWindowViewModel.PlaylistFilterType.PlaylistFilter, "KEYWORD", MainWindowViewModel.ModeFilterType._7KEYS, sortParameters, libraryIndexVersion: 10, playlistRevision: 20, scoreSnapshotVersion: 30, chartInfoIndexVersion: 40, hasResolvedSelection: true);

        Assert.AreEqual(left, right);
    }

    [TestMethod]
    public void CreatePlaylistRequestIdentity_DifferentPlaylistRevisionBreaksDedup()
    {
        var table = new BMSTable();
        MainWindowViewModel.PlaylistRequestIdentity before = PlaylistRequestFactory.CreateIdentity(table, null, MainWindowViewModel.PlaylistFilterType.PlaylistNotOwnedFilterSelected, null, MainWindowViewModel.ModeFilterType.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);
        MainWindowViewModel.PlaylistRequestIdentity after = PlaylistRequestFactory.CreateIdentity(table, null, MainWindowViewModel.PlaylistFilterType.PlaylistNotOwnedFilterSelected, null, MainWindowViewModel.ModeFilterType.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 5, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);

        Assert.AreNotEqual(before, after);
    }

    [TestMethod]
    public void CreatePlaylistRequestIdentity_DifferentScoreSnapshotVersionBreaksDedup()
    {
        var table = new BMSTable();
        MainWindowViewModel.PlaylistRequestIdentity before = PlaylistRequestFactory.CreateIdentity(table, null, MainWindowViewModel.PlaylistFilterType.PlaylistFilter, null, MainWindowViewModel.ModeFilterType.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);
        MainWindowViewModel.PlaylistRequestIdentity after = PlaylistRequestFactory.CreateIdentity(table, null, MainWindowViewModel.PlaylistFilterType.PlaylistFilter, null, MainWindowViewModel.ModeFilterType.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 6, chartInfoIndexVersion: 6, hasResolvedSelection: true);

        Assert.AreNotEqual(before, after);
    }

    [TestMethod]
    public void CreatePlaylistRequestIdentity_DifferentChartInfoIndexVersionBreaksDedup()
    {
        var table = new BMSTable();
        MainWindowViewModel.PlaylistRequestIdentity before = PlaylistRequestFactory.CreateIdentity(table, null, MainWindowViewModel.PlaylistFilterType.PlaylistFilter, null, MainWindowViewModel.ModeFilterType.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);
        MainWindowViewModel.PlaylistRequestIdentity after = PlaylistRequestFactory.CreateIdentity(table, null, MainWindowViewModel.PlaylistFilterType.PlaylistFilter, null, MainWindowViewModel.ModeFilterType.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 7, hasResolvedSelection: true);

        Assert.AreNotEqual(before, after);
        Assert.IsTrue(before.SourceIdentity.EqualsIgnoringChartInfoIndex(after.SourceIdentity));
    }

    [TestMethod]
    public void PlaylistIdentity_KeywordModeAndSortOnlyChangePresentationIdentity()
    {
        var table = new BMSTable();
        var titleAscending = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = nameof(BMSFile.Title),
            Direction = ListSortDirection.Ascending
        };
        var titleDescending = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = nameof(BMSFile.Title),
            Direction = ListSortDirection.Descending
        };

        MainWindowViewModel.PlaylistRequestIdentity before = PlaylistRequestFactory.CreateIdentity(table, "Folder", MainWindowViewModel.PlaylistFilterType.PlaylistFilter, "alpha", MainWindowViewModel.ModeFilterType.All, titleAscending, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);
        MainWindowViewModel.PlaylistRequestIdentity after = PlaylistRequestFactory.CreateIdentity(table, "Folder", MainWindowViewModel.PlaylistFilterType.PlaylistFilter, "beta", MainWindowViewModel.ModeFilterType._7KEYS, titleDescending, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);

        Assert.AreEqual(before.SourceIdentity, after.SourceIdentity);
        Assert.AreNotEqual(before.PresentationIdentity, after.PresentationIdentity);
        Assert.AreNotEqual(before, after);
    }

    [TestMethod]
    public void PlaylistIdentity_SourceVersionsOnlyChangeSourceIdentity()
    {
        var table = new BMSTable();
        MainWindowViewModel.PlaylistRequestIdentity before = PlaylistRequestFactory.CreateIdentity(table, "Folder", MainWindowViewModel.PlaylistFilterType.PlaylistFilter, "keyword", MainWindowViewModel.ModeFilterType.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);
        MainWindowViewModel.PlaylistRequestIdentity after = PlaylistRequestFactory.CreateIdentity(table, "Folder", MainWindowViewModel.PlaylistFilterType.PlaylistFilter, "keyword", MainWindowViewModel.ModeFilterType.All, sortParameters: null, libraryIndexVersion: 4, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);

        Assert.AreNotEqual(before.SourceIdentity, after.SourceIdentity);
        Assert.AreEqual(before.PresentationIdentity, after.PresentationIdentity);
        Assert.AreNotEqual(before, after);
    }

    [TestMethod]
    public void DeterminePlaylistSourceInvalidationReason_WhenOnlyScoreSnapshotChanges_ReturnsScoreSnapshotVersion()
    {
        string reason = PlaylistRequestFactory.DetermineSourceInvalidationReason(
            selectionChanged: false,
            libraryIndexInvalidated: false,
            playlistRevisionInvalidated: false,
            scoreSnapshotInvalidated: true,
            chartInfoIndexInvalidated: false,
            sourceMissing: false);

        Assert.AreEqual("score_snapshot_version", reason);
    }

    [TestMethod]
    public void DeterminePlaylistSourceInvalidationReason_WhenOnlyChartInfoIndexChanges_ReturnsChartInfoIndex()
    {
        string reason = PlaylistRequestFactory.DetermineSourceInvalidationReason(
            selectionChanged: false,
            libraryIndexInvalidated: false,
            playlistRevisionInvalidated: false,
            scoreSnapshotInvalidated: false,
            chartInfoIndexInvalidated: true,
            sourceMissing: false);

        Assert.AreEqual("chart_info_index", reason);
    }

    [TestMethod]
    public void DeterminePlaylistSourceInvalidationReason_WhenSelectionChanges_TakesPrecedence()
    {
        string reason = PlaylistRequestFactory.DetermineSourceInvalidationReason(
            selectionChanged: true,
            libraryIndexInvalidated: true,
            playlistRevisionInvalidated: true,
            scoreSnapshotInvalidated: true,
            chartInfoIndexInvalidated: true,
            sourceMissing: true);

        Assert.AreEqual("selection_changed", reason);
    }

    [TestMethod]
    public void PlaylistDetailBuildDecision_WhenOnlyPresentationChanges_AppliesViewOnly()
    {
        var table = new BMSTable();
        MainWindowViewModel.PlaylistRequestIdentity current = PlaylistRequestFactory.CreateIdentity(table, "Folder", MainWindowViewModel.PlaylistFilterType.PlaylistFilter, "alpha", MainWindowViewModel.ModeFilterType.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);
        MainWindowViewModel.PlaylistRequestIdentity request = PlaylistRequestFactory.CreateIdentity(table, "Folder", MainWindowViewModel.PlaylistFilterType.PlaylistFilter, "beta", MainWindowViewModel.ModeFilterType._7KEYS, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);

        PlaylistDetailBuildDecision decision = PlaylistDetailBuildDecisionService.Decide(
            CreatePlaylistBuildRequest(request),
            CreatePlaylistDetailBuildStateSnapshot(current));

        Assert.AreEqual(PlaylistDetailBuildAction.ApplyViewOnly, decision.Action);
        Assert.IsTrue(decision.PresentationIdentityChanged);
        Assert.IsNull(decision.SourceInvalidationReason);
    }

    [TestMethod]
    public void PlaylistDetailBuildDecision_WhenScoreSnapshotChanges_RebuildsSource()
    {
        var table = new BMSTable();
        MainWindowViewModel.PlaylistRequestIdentity current = PlaylistRequestFactory.CreateIdentity(table, "Folder", MainWindowViewModel.PlaylistFilterType.PlaylistFilter, null, MainWindowViewModel.ModeFilterType.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);
        MainWindowViewModel.PlaylistRequestIdentity request = PlaylistRequestFactory.CreateIdentity(table, "Folder", MainWindowViewModel.PlaylistFilterType.PlaylistFilter, null, MainWindowViewModel.ModeFilterType.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 6, chartInfoIndexVersion: 6, hasResolvedSelection: true);

        PlaylistDetailBuildDecision decision = PlaylistDetailBuildDecisionService.Decide(
            CreatePlaylistBuildRequest(request),
            CreatePlaylistDetailBuildStateSnapshot(current));

        Assert.AreEqual(PlaylistDetailBuildAction.RebuildSource, decision.Action);
        Assert.IsTrue(decision.ScoreSnapshotInvalidated);
        Assert.AreEqual("score_snapshot_version", decision.SourceInvalidationReason);
        Assert.AreEqual(5, decision.LastBuiltScoreSnapshotVersion);
    }

    [TestMethod]
    public void PlaylistDetailBuildDecision_WhenOnlyChartInfoIndexChanges_PatchesThenAppliesView()
    {
        var table = new BMSTable();
        MainWindowViewModel.PlaylistRequestIdentity current = PlaylistRequestFactory.CreateIdentity(table, "Folder", MainWindowViewModel.PlaylistFilterType.PlaylistFilter, null, MainWindowViewModel.ModeFilterType.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);
        MainWindowViewModel.PlaylistRequestIdentity request = PlaylistRequestFactory.CreateIdentity(table, "Folder", MainWindowViewModel.PlaylistFilterType.PlaylistFilter, null, MainWindowViewModel.ModeFilterType.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 7, hasResolvedSelection: true);

        PlaylistDetailBuildDecision decision = PlaylistDetailBuildDecisionService.Decide(
            CreatePlaylistBuildRequest(request),
            CreatePlaylistDetailBuildStateSnapshot(current));

        Assert.AreEqual(PlaylistDetailBuildAction.PatchChartInfoThenApplyView, decision.Action);
        Assert.IsTrue(decision.ChartInfoIndexInvalidated);
        Assert.IsNull(decision.SourceInvalidationReason);
    }

    [TestMethod]
    public void PlaylistDetailBuildDecision_WhenEmptyFolderChartInfoOnlyChanges_PatchesThenAppliesView()
    {
        var table = new BMSTable();
        MainWindowViewModel.PlaylistRequestIdentity current = PlaylistRequestFactory.CreateIdentity(table, string.Empty, MainWindowViewModel.PlaylistFilterType.PlaylistFilter, null, MainWindowViewModel.ModeFilterType.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);
        MainWindowViewModel.PlaylistRequestIdentity request = PlaylistRequestFactory.CreateIdentity(table, string.Empty, MainWindowViewModel.PlaylistFilterType.PlaylistFilter, null, MainWindowViewModel.ModeFilterType.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 7, hasResolvedSelection: true);

        PlaylistDetailBuildDecision decision = PlaylistDetailBuildDecisionService.Decide(
            CreatePlaylistBuildRequest(request),
            CreatePlaylistDetailBuildStateSnapshot(current));

        Assert.AreEqual(PlaylistDetailBuildAction.PatchChartInfoThenApplyView, decision.Action);
        Assert.IsFalse(decision.SelectionChanged);
        Assert.IsTrue(decision.ChartInfoIndexInvalidated);
    }

    [TestMethod]
    public void PlaylistDetailBuildDecision_WhenSelectionAndChartInfoChange_RebuildsSource()
    {
        var currentTable = new BMSTable();
        var requestTable = new BMSTable();
        MainWindowViewModel.PlaylistRequestIdentity current = PlaylistRequestFactory.CreateIdentity(currentTable, "Folder", MainWindowViewModel.PlaylistFilterType.PlaylistFilter, null, MainWindowViewModel.ModeFilterType.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);
        MainWindowViewModel.PlaylistRequestIdentity request = PlaylistRequestFactory.CreateIdentity(requestTable, "Folder", MainWindowViewModel.PlaylistFilterType.PlaylistFilter, null, MainWindowViewModel.ModeFilterType.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 7, hasResolvedSelection: true);

        PlaylistDetailBuildDecision decision = PlaylistDetailBuildDecisionService.Decide(
            CreatePlaylistBuildRequest(request),
            CreatePlaylistDetailBuildStateSnapshot(current));

        Assert.AreEqual(PlaylistDetailBuildAction.RebuildSource, decision.Action);
        Assert.IsTrue(decision.SelectionChanged);
        Assert.AreEqual("selection_changed", decision.SourceInvalidationReason);
    }

    [TestMethod]
    public void PlaylistDetailBuildDecision_WhenResolvedSourceHasNoRows_DoesNotTreatSourceAsMissing()
    {
        var table = new BMSTable();
        MainWindowViewModel.PlaylistRequestIdentity identity = PlaylistRequestFactory.CreateIdentity(table, "Folder", MainWindowViewModel.PlaylistFilterType.PlaylistFilter, null, MainWindowViewModel.ModeFilterType.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);

        PlaylistDetailBuildDecision decision = PlaylistDetailBuildDecisionService.Decide(
            CreatePlaylistBuildRequest(identity),
            CreatePlaylistDetailBuildStateSnapshot(identity, sourceRowCount: 0));

        Assert.AreEqual(PlaylistDetailBuildAction.ApplyViewOnly, decision.Action);
        Assert.IsFalse(decision.SourceMissing);
    }

    [TestMethod]
    public void PlaylistDetailBuildDecision_WhenSnapshotRowsAreNull_RebuildsAsSourceMissing()
    {
        var table = new BMSTable();
        MainWindowViewModel.PlaylistRequestIdentity identity = PlaylistRequestFactory.CreateIdentity(table, "Folder", MainWindowViewModel.PlaylistFilterType.PlaylistFilter, null, MainWindowViewModel.ModeFilterType.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);

        PlaylistDetailBuildDecision decision = PlaylistDetailBuildDecisionService.Decide(
            CreatePlaylistBuildRequest(identity),
            CreatePlaylistDetailBuildStateSnapshot(identity, hasSourceRows: false, sourceRowCount: 0));

        Assert.AreEqual(PlaylistDetailBuildAction.RebuildSource, decision.Action);
        Assert.IsTrue(decision.SourceMissing);
        Assert.AreEqual("source_missing", decision.SourceInvalidationReason);
    }

    [TestMethod]
    public void PlaylistDetailBuildQueueRegister_WhenNewRequest_EnqueuesAndStartsWorker()
    {
        var state = new PlaylistDetailBuildState();
        MainWindowViewModel.PlaylistRequestIdentity identity = CreatePlaylistIdentity("Folder");
        PlaylistBuildRequest request = CreatePlaylistBuildRequest(identity);

        PlaylistBuildQueueRegisterResult result = PlaylistDetailBuildQueueCoordinator.RegisterRequest(
            state,
            request,
            currentViewIdentity: null,
            lastBuiltScoreSnapshotVersion: 5,
            isShutdownRequested: false);

        Assert.IsTrue(result.Enqueued);
        Assert.IsTrue(result.StartWorker);
        Assert.AreEqual(1, request.RequestVersion);
        Assert.AreEqual(5, result.LastBuiltScoreSnapshotVersion);
        Assert.IsTrue(state.WorkerRunning);
        Assert.AreSame(request, PlaylistDetailBuildQueueCoordinator.DequeuePendingRequest(state));
        Assert.AreSame(request, state.CurrentBuildRequest);
    }

    [TestMethod]
    public void PlaylistDetailBuildQueueRegister_WhenCurrentViewMatches_DeduplicatesNoop()
    {
        var state = new PlaylistDetailBuildState();
        MainWindowViewModel.PlaylistRequestIdentity identity = CreatePlaylistIdentity("Folder");
        PlaylistBuildRequest request = CreatePlaylistBuildRequest(identity);

        PlaylistBuildQueueRegisterResult result = PlaylistDetailBuildQueueCoordinator.RegisterRequest(
            state,
            request,
            currentViewIdentity: identity,
            lastBuiltScoreSnapshotVersion: 5,
            isShutdownRequested: false);

        Assert.IsFalse(result.Enqueued);
        Assert.AreEqual("current_view", result.DeduplicatedTarget);
        Assert.AreEqual("noop_same_view", result.IgnoredReason);
        Assert.IsFalse(state.WorkerRunning);
        Assert.IsNull(state.PendingRequest);
    }

    [TestMethod]
    public void PlaylistDetailBuildQueueCancelForShutdown_ClearsPendingAndCancelsTokens()
    {
        var state = new PlaylistDetailBuildState();
        MainWindowViewModel.PlaylistRequestIdentity identity = CreatePlaylistIdentity("Folder");
        PlaylistBuildRequest request = CreatePlaylistBuildRequest(identity);
        PlaylistDetailBuildQueueCoordinator.RegisterRequest(
            state,
            request,
            currentViewIdentity: null,
            lastBuiltScoreSnapshotVersion: 5,
            isShutdownRequested: false);
        var activeCancellation = new CancellationTokenSource();
        Assert.IsTrue(PlaylistDetailBuildQueueCoordinator.TryBeginIteration(state, request, activeCancellation, isShutdownRequested: false));

        PlaylistDetailBuildQueueCoordinator.CancelForShutdown(state);

        Assert.IsNull(state.PendingRequest);
        Assert.IsTrue(state.ShutdownCancellationRequested);
        Assert.IsTrue(activeCancellation.IsCancellationRequested);
        Assert.IsTrue(state.Cancellation.IsCancellationRequested);
        activeCancellation.Dispose();
    }

    [TestMethod]
    public void CreatePlaylistRequestIdentity_DistinguishesRootPlaylistAndEmptyFolderNode()
    {
        var table = new BMSTable();
        MainWindowViewModel.PlaylistRequestIdentity rootPlaylist = PlaylistRequestFactory.CreateIdentity(table, null, MainWindowViewModel.PlaylistFilterType.PlaylistFilter, null, MainWindowViewModel.ModeFilterType.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);
        MainWindowViewModel.PlaylistRequestIdentity emptyFolder = PlaylistRequestFactory.CreateIdentity(table, string.Empty, MainWindowViewModel.PlaylistFilterType.PlaylistFilter, null, MainWindowViewModel.ModeFilterType.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);

        Assert.AreNotEqual(rootPlaylist, emptyFolder);
    }

    [TestMethod]
    public void PlaylistDetailSourceRow_SynchronizeEditableSnapshot_UpdatesEditableFieldsAndSearchText()
    {
        PlaylistDetailSourceRow sourceRow = CreateSourceRow("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Editable", 7, memo: "old memo", comment: "old comment", entryLevel: 3);
        PlaylistDetailRow editedRow = sourceRow.CreateViewRow();

        editedRow.comment = "new comment";
        editedRow.memo = "new memo";
        editedRow.Level = "12.5";
        editedRow.Url = new Uri("https://example.com/original");
        editedRow.Url_diff = new Uri("https://example.com/diff");

        sourceRow.SynchronizeEditableSnapshot(editedRow);

        Assert.AreEqual("new comment", sourceRow.comment);
        Assert.AreEqual("new memo", sourceRow.memo);
        Assert.AreEqual("12.5", sourceRow.Level);
        Assert.AreEqual(12.5, sourceRow.EntryLevelSortKey);
        Assert.AreEqual(new Uri("https://example.com/original"), sourceRow.Url);
        Assert.AreEqual(new Uri("https://example.com/diff"), sourceRow.Url_diff);
        StringAssert.Contains(sourceRow.SearchText, "NEW MEMO");
        StringAssert.Contains(sourceRow.SearchText, "NEW COMMENT");
    }

    [TestMethod]
    public void PlaylistDetailRow_PreservesSha256SnapshotAcrossViewMaterialization()
    {
        PlaylistDetailSourceRow sourceRow = CreateSourceRow("cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd", "ShaVisible", 7, sha256: "abababababababababababababababababababababababababababababababab");

        PlaylistDetailRow row = sourceRow.CreateViewRow();

        Assert.AreEqual("abababababababababababababababababababababababababababababababab", sourceRow.sha256);
        Assert.AreEqual("abababababababababababababababababababababababababababababababab", row.sha256);
        Assert.AreEqual("abababababababababababababababababababababababababababababababab", GridRowResolver.GetSha256(row));
        Assert.AreEqual("abababababababababababababababababababababababababababababababab", GridRowResolver.GetRepositorySha256(row));
    }

    [TestMethod]
    public void GridRowResolver_GetRepositorySha256_UsesBmsChartRowChartInfoFallback()
    {
        var file = new TestableBmsFile();
        file.ApplySnapshot("abababababababababababababababab", "ChartInfoSha", 7);
        LR2SongDBExtended.chart_info chartInfo = CreateChartInfo(new string('d', 64), file.hash);
        LibraryChartRow row = LibraryChartRow.FromBmsFile(file);
        row.SetChartInfoProjectionProvider(CreateChartInfoProvider(chartInfo));

        Assert.AreEqual(new string('d', 64), GridRowResolver.GetRepositorySha256(row));
    }

    [TestMethod]
    public void GridRowResolver_BmsPlayerDisplayHelpers_ReadRawBmsFile()
    {
        var file = new TestableBmsFile();
        file.ApplySnapshot("abababababababababababababababab", "PlayerTitle", 7);
        file.SetSubtitle("[PlayerSubtitle]");
        file.SetArtist("PlayerArtist");

        Assert.AreEqual("PlayerTitle", GridRowResolver.GetBmsPlayerDisplayTitle(file));
        Assert.AreEqual("[PlayerSubtitle]", GridRowResolver.GetBmsPlayerDisplaySubtitle(file));
        Assert.AreEqual("PlayerArtist", GridRowResolver.GetBmsPlayerDisplayArtist(file));
        Assert.AreEqual(string.Empty, GridRowResolver.GetDisplayTitle(file));
        Assert.AreEqual(string.Empty, GridRowResolver.GetDisplaySubtitle(file));
        Assert.AreEqual(string.Empty, GridRowResolver.GetDisplayArtist(file));
    }

    [TestMethod]
    public void SetBmsPlayerHeader_UsesSplitBmsMetadata()
    {
        var viewModel = new MainWindowViewModel();
        var file = new TestableBmsFile();
        file.ApplySnapshot("abababababababababababababababab", "ouroVoros", 7);
        file.SetSubtitle("[LAST BOSS]");
        file.SetArtist("Nepentropy Movie:Vogeln obj:sak");

        viewModel.SetBmsPlayerHeader(file);

        Assert.AreSame(file, viewModel.DisplayedBmsPlayerFile);
        Assert.AreEqual("ouroVoros", viewModel.BmsPlayerHeaderTitle);
        Assert.AreEqual("[LAST BOSS]", viewModel.BmsPlayerHeaderSubtitle);
        Assert.AreEqual("Nepentropy Movie:Vogeln obj:sak", viewModel.BmsPlayerHeaderArtist);
        Assert.AreEqual("ouroVoros", viewModel.PlayerHeaderTitle);
        Assert.AreEqual("[LAST BOSS]", viewModel.PlayerHeaderSubtitle);
        Assert.AreEqual("Nepentropy Movie:Vogeln obj:sak", viewModel.PlayerHeaderArtist);
    }

    [TestMethod]
    public void NowPlayingBMS_SetterSynchronizesPlayerHeader()
    {
        var viewModel = new MainWindowViewModel();
        var file = new TestableBmsFile();
        file.ApplySnapshot("abababababababababababababababab", "NextTitle", 7);
        file.SetSubtitle("[NextSubtitle]");
        file.SetArtist("NextArtist");

        viewModel.NowPlayingBMS = file;

        Assert.AreSame(file, viewModel.DisplayedBmsPlayerFile);
        Assert.AreEqual("NextTitle", viewModel.BmsPlayerHeaderTitle);
        Assert.AreEqual("[NextSubtitle]", viewModel.BmsPlayerHeaderSubtitle);
        Assert.AreEqual("NextArtist", viewModel.BmsPlayerHeaderArtist);
        Assert.AreEqual("NextTitle", viewModel.PlayerHeaderTitle);
        Assert.AreEqual("[NextSubtitle]", viewModel.PlayerHeaderSubtitle);
        Assert.AreEqual("NextArtist", viewModel.PlayerHeaderArtist);
    }

    [TestMethod]
    public void SetMoviePlayerHeader_UsesRowRawTitleAndKeepsBmsDisplayTarget()
    {
        var viewModel = new MainWindowViewModel();
        var bmsFile = new TestableBmsFile();
        bmsFile.ApplySnapshot("abababababababababababababababab", "BmsTitle", 7);
        bmsFile.SetSubtitle("[BmsSubtitle]");
        bmsFile.SetArtist("BmsArtist");
        viewModel.SetBmsPlayerHeader(bmsFile);

        var movieFile = new TestableBmsFile();
        movieFile.ApplySnapshot("cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd", "MovieTitle", 7);
        movieFile.SetSubtitle("[MovieSubtitle]");
        movieFile.SetArtist("MovieArtist");
        LibraryChartRow row = LibraryChartRow.FromBmsFile(movieFile);

        viewModel.SetMoviePlayerHeader(row);

        Assert.AreSame(bmsFile, viewModel.DisplayedBmsPlayerFile);
        Assert.AreEqual("BmsTitle", viewModel.BmsPlayerHeaderTitle);
        Assert.AreEqual("[BmsSubtitle]", viewModel.BmsPlayerHeaderSubtitle);
        Assert.AreEqual("BmsArtist", viewModel.BmsPlayerHeaderArtist);
        Assert.AreEqual("MovieTitle", viewModel.MoviePlayerHeaderTitle);
        Assert.AreEqual("[MovieSubtitle]", viewModel.MoviePlayerHeaderSubtitle);
        Assert.AreEqual("MovieArtist", viewModel.MoviePlayerHeaderArtist);
    }

    [TestMethod]
    public void PlaylistDetailSourceRow_ImmutableResolvedBmsKeepsPlayerOwner()
    {
        var file = new TestableBmsFile();
        file.ApplySnapshot("abababababababababababababababab", "Immutable BMS", 7);
        LibraryChartRef resolvedRef = LibraryChartRef.FromImmutableSnapshot(LibraryChartRef.FromBmsFile(file));
        ChartFile resolvedChart = resolvedRef.ToChartFileIdentity();
        var entry = new TestablePlaylistEntry(file);

        PlaylistDetailRow row = new PlaylistDetailSourceRow(
            entry,
            resolvedChart,
            resolvedChartRef: resolvedRef).CreateViewRow();

        Assert.IsTrue(row.IsOwned);
        Assert.IsNull(row.Chart.GetBmsStorageOwner());
        Assert.AreEqual(file.path, row.Chart.Path);
        Assert.IsTrue(GridRowResolver.TryGetBmsPlayerFile(row, out BMSFile playerFile));
        Assert.AreSame(file, playerFile);
        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, out ChartOperationTarget target));
        Assert.AreSame(file, target.Chart.GetBmsStorageOwner());
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RunBmsEncodingFix));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RenameInvalidExtension));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.ConvertToAudio));
    }

    [TestMethod]
    public void PlaylistDetailSourceRow_ImmutableResolvedBmsUsesChartInfoProjection()
    {
        var file = new TestableBmsFile();
        file.ApplySnapshot("abababababababababababababababab", "Immutable BMS", 7);
        file.SetSha256(new string('b', 64));
        LibraryChartRef resolvedRef = LibraryChartRef.FromImmutableSnapshot(LibraryChartRef.FromBmsFile(file));
        ChartFile resolvedChart = resolvedRef.ToChartFileIdentity();
        LR2SongDBExtended.chart_info chartInfo = CreateChartInfo(file.sha256, file.hash, level: 12, notes: 2000);
        var entry = new TestablePlaylistEntry(file);

        PlaylistDetailRow row = new PlaylistDetailSourceRow(
            entry,
            resolvedChart,
            chartInfoProjectionProvider: CreateChartInfoProvider(chartInfo),
            resolvedChartRef: resolvedRef).CreateViewRow();

        Assert.AreEqual("12", row.ChartLevelText);
        Assert.AreEqual(12d, row.ChartLevelSortKey);
        Assert.AreEqual(2000, row.ChartNotes);
    }

    [TestMethod]
    public void GridRowResolver_GetRepositorySha256_ReturnsNullWhenMissing()
    {
        PlaylistDetailSourceRow sourceRow = CreateSourceRow("cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd", "NoSha", 7);

        Assert.IsNull(GridRowResolver.GetRepositorySha256(sourceRow.CreateViewRow()));
    }

    [TestMethod]
    public void PlaylistDetailSourceRow_BmsonOwnedChart_UsesBmsonMetadata()
    {
        var entry = new TestablePlaylistEntry
        {
            comment = "comment",
            memo = "memo"
        };
        var bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\chart.bmson",
            folder = "C:\\Songs\\Bmson",
            title = "Bmson",
            subtitle = "[Another]",
            artist = "Artist",
            genre = "Genre",
            level = 11,
            mode_hint = "beat-7k",
            md5 = "dddddddddddddddddddddddddddddddd",
            sha256 = new string('e', 64)
        };
        entry.SetMd5(bmson.md5);
        entry.SetSha256(bmson.sha256);

        var sourceRow = new PlaylistDetailSourceRow(entry, ChartFileProjection.FromBmsonSong(bmson, includeWarningSnapshot: false));
        PlaylistDetailRow row = sourceRow.CreateViewRow();

        Assert.IsTrue(sourceRow.IsOwned);
        Assert.IsNull(row.Chart.GetBmsStorageOwner());
        Assert.AreSame(bmson, row.Chart.GetBmsonStorageOwner());
        Assert.AreSame(sourceRow.Chart, row.Chart);
        Assert.IsTrue(GridRowResolver.TryGetChartFile(sourceRow, out ChartFile sourceChart));
        Assert.AreSame(sourceRow.Chart, sourceChart);
        Assert.IsTrue(GridRowResolver.TryGetChartFile(row, out ChartFile rowChart));
        Assert.AreSame(row.Chart, rowChart);
        Assert.AreEqual(ChartFileKind.Bmson, rowChart.Kind);
        Assert.AreSame(bmson, rowChart.GetBmsonStorageOwner());
        Assert.AreEqual("Bmson [Another]", row.Title);
        Assert.AreEqual("Artist", row.Artist);
        Assert.AreEqual("[Another]", GridRowResolver.GetDisplaySubtitle(row));
        Assert.AreEqual("Genre", row.genre);
        Assert.AreEqual(7, row.mode);
        Assert.AreEqual(bmson.path, row.path);
        Assert.AreEqual(bmson.sha256, row.sha256);
        Assert.AreEqual(bmson.sha256, GridRowResolver.GetRepositorySha256(row));
        Assert.AreEqual(ClearType.NO_PLAY, row.clear);
        Assert.AreEqual(RankType.INVALID, row.rank);
        Assert.IsNull(row.score);
        Assert.AreEqual(ChartFileStatus.NONE, row.status);
        Assert.IsFalse(GridRowResolver.TryGetBmsPlayerFile(row, out _));
    }

    [TestMethod]
    public void PlaylistDetailSourceRow_BmsonOwnedWithResolvedScore_UsesScoreSnapshot()
    {
        var entry = new TestablePlaylistEntry();
        var bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\chart.bmson",
            title = "Bmson",
            md5 = "dddddddddddddddddddddddddddddddd",
            sha256 = new string('e', 64)
        };
        var score = new BMSScore
        {
            hash = bmson.md5,
            clear = ClearType.HARD,
            rank = RankType.AA,
            perfect = 800,
            great = 100,
            totalnotes = 1000,
            maxcombo = 900,
            minbp = 7
        };
        entry.SetMd5(bmson.md5);
        entry.SetSha256(bmson.sha256);

        var sourceRow = new PlaylistDetailSourceRow(
            entry,
            ChartFileProjection.FromBmsonSong(bmson, includeWarningSnapshot: false),
            scoreSnapshot: score);

        Assert.AreEqual(ClearType.HARD, sourceRow.clear);
        Assert.AreEqual(RankType.AA, sourceRow.rank);
        Assert.AreEqual(1700, sourceRow.score);
        Assert.AreEqual(1000, sourceRow.totalnotes);
        Assert.AreEqual(900, sourceRow.maxcombo);
        Assert.AreEqual(7, sourceRow.minbp);
    }

    [TestMethod]
    public void PlaylistDetailSourceRow_BmsonOwnedChart_UsesPlaylistReferenceProjection()
    {
        var entry = new TestablePlaylistEntry();
        var bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\chart.bmson",
            title = "Bmson",
            md5 = "dddddddddddddddddddddddddddddddd",
            sha256 = new string('e', 64)
        };
        entry.SetMd5(bmson.md5);
        entry.SetSha256(bmson.sha256);
        var table = new BMSTable
        {
            name = "Bmson Playlist",
            symbol = "BMSN",
            entries = [entry]
        };
        PlaylistReferenceIndex index = PlaylistReferenceIndex.Empty;
        index.ReplaceTable(table, table.entries);

        var sourceRow = new PlaylistDetailSourceRow(
            entry,
            ChartFileProjection.FromBmsonSong(bmson, includeWarningSnapshot: false),
            playlistReferenceDisplayProvider: chart => index.Find(chart));
        PlaylistDetailRow row = sourceRow.CreateViewRow();

        Assert.AreEqual("BMSN", sourceRow.RefTablesSymbols);
        Assert.AreEqual("Bmson Playlist", sourceRow.RefTablesNames);
        Assert.AreEqual("BMSN", row.RefTablesSymbols);
        Assert.AreEqual("Bmson Playlist", row.RefTablesNames);
        Assert.IsTrue(GridKeywordSearchQuery.Parse("BMSN").MatchesPlaylistDetail(sourceRow));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("playlist:\"Bmson Playlist\"").MatchesPlaylistDetail(sourceRow));
    }

    [TestMethod]
    public void ChartOperationTarget_PlaylistOwnedBmson_IsOwnedWithBmsonChartFile()
    {
        var bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\chart.bmson",
            folder = "C:\\Songs\\Bmson",
            title = "Bmson",
            artist = "Artist",
            level = 11,
            mode_hint = "beat-7k",
            md5 = "dddddddddddddddddddddddddddddddd",
            sha256 = new string('e', 64)
        };
        var entry = new TestablePlaylistEntry();
        entry.SetMd5(bmson.md5);
        entry.SetSha256(bmson.sha256);
        PlaylistDetailRow row = new PlaylistDetailSourceRow(entry, ChartFileProjection.FromBmsonSong(bmson, includeWarningSnapshot: false)).CreateViewRow();

        Assert.IsFalse(GridRowResolver.TryGetBmsPlayerFile(row, out _));
        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, out ChartOperationTarget target));
        Assert.AreSame(row.Chart, target.Chart);
        Assert.AreEqual(ChartFileKind.Bmson, target.Chart.Kind);
        Assert.IsTrue(target.IsOwned);
        Assert.IsFalse(target.IsPlaylistMissing);
        Assert.IsNull(target.Chart.GetBmsStorageOwner());
        Assert.AreSame(bmson, target.Chart.GetBmsonStorageOwner());
        LibraryChartRef libraryRef = target.ToLibraryChartRef();
        Assert.AreEqual(LibraryChartKind.Bmson, libraryRef.Kind);
        Assert.IsNull(libraryRef.GetBmsStorageOwner());
        Assert.AreSame(bmson, libraryRef.GetBmsonStorageOwner());
        Assert.AreEqual(bmson.path, libraryRef.Path);
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.OpenFile));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.OpenFolder));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.OpenRepositoryBySha256));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RunResourceHealthCheck));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.UseLr2Ir));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.UseScoreViewer));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.RunBmsEncodingFix));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.RunZeroNoteCheck));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RepairInstalledLocation));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.MoveInLibrary));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RemoveFromLibrary));
    }

    [TestMethod]
    public void PlaylistMissingContextMenuPolicy_UsesChartOperationTargetOwnership()
    {
        var bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\chart.bmson",
            folder = "C:\\Songs\\Bmson",
            title = "Owned Bmson",
            md5 = "dddddddddddddddddddddddddddddddd",
            sha256 = new string('e', 64)
        };
        var ownedBmsonEntry = new TestablePlaylistEntry();
        ownedBmsonEntry.SetMd5(bmson.md5);
        ownedBmsonEntry.SetSha256(bmson.sha256);
        PlaylistDetailRow ownedBmsonRow = new PlaylistDetailSourceRow(ownedBmsonEntry, ChartFileProjection.FromBmsonSong(bmson, includeWarningSnapshot: false)).CreateViewRow();

        var missingEntry = new TestablePlaylistEntry();
        missingEntry.SetTitle("Missing");
        missingEntry.SetMd5("abababababababababababababababab");
        PlaylistDetailRow missingRow = new PlaylistDetailSourceRow(missingEntry, resolvedChart: null).CreateViewRow();

        Assert.IsFalse(GridRowResolver.TryGetBmsPlayerFile(ownedBmsonRow, out _));
        Assert.IsFalse(MainWindow.ShouldUsePlaylistMissingContextMenu(ownedBmsonRow, ChartOperationSourceScope.PlaylistOwned));
        Assert.IsTrue(MainWindow.ShouldUsePlaylistMissingContextMenu(missingRow, ChartOperationSourceScope.PlaylistOwned));
        Assert.IsFalse(MainWindow.ShouldUsePlaylistMissingContextMenu(new object(), ChartOperationSourceScope.PlaylistOwned));
    }

    [TestMethod]
    public void PlaylistDetailRow_BmsonChartProjectionHasNoCompatibilityBmsFileSurface()
    {
        var bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\chart.bmson",
            folder = "C:\\Songs\\Bmson",
            title = "Bmson",
            md5 = "dddddddddddddddddddddddddddddddd",
            sha256 = new string('e', 64)
        };
        var entry = new TestablePlaylistEntry();
        entry.SetMd5(bmson.md5);
        entry.SetSha256(bmson.sha256);
        var sourceRow = new PlaylistDetailSourceRow(
            entry,
            ChartFileProjection.FromBmsonSong(bmson, includeWarningSnapshot: false));

        PlaylistDetailRow row = sourceRow.CreateViewRow();

        Assert.AreEqual(ChartFileKind.Bmson, row.Chart.Kind);
        Assert.AreSame(bmson, row.Chart.GetBmsonStorageOwner());
        Assert.IsNull(row.Chart.GetBmsStorageOwner());
        Assert.IsNull(typeof(PlaylistDetailRow).GetProperty("CompatibilityBmsFile", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public));
    }

    [TestMethod]
    public void PlaylistDetailSourceRow_UsesResolvedChartProjectionBeforeStorageOwnerMetadata()
    {
        var bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\chart.bmson",
            folder = "C:\\Songs\\Bmson",
            title = "Storage Title",
            artist = "Storage Artist",
            genre = "Storage Genre",
            md5 = "dddddddddddddddddddddddddddddddd",
            sha256 = new string('e', 64),
            level = 3,
            mode_hint = "beat-7k"
        };
        var entry = new TestablePlaylistEntry();
        entry.SetTitle("Entry Title");
        entry.SetArtist("Entry Artist");
        entry.SetMd5(bmson.md5);
        entry.SetSha256(bmson.sha256);
        ChartFile projectedChart = new(
            ChartFileKind.Bmson,
            bmson.path,
            bmson.md5,
            bmson.sha256,
            "Projected Title",
            "Projected Raw Title",
            "Projected Artist",
            "Projected Genre",
            "Projected Folder",
            "Projected Tag",
            "12",
            12d,
            14,
            chartInfo: null,
            bmsFile: null,
            bmsonSong: bmson,
            installDestination: "C:\\Installed\\Bmson",
            warnings: [ChartWarning.Create(ChartWarningKind.InstallEstimationAmbiguous, "projected warning")]);

        var sourceRow = new PlaylistDetailSourceRow(entry, projectedChart);
        PlaylistDetailRow row = sourceRow.CreateViewRow();

        Assert.AreEqual("Projected Title", row.Title);
        Assert.AreEqual("Projected Artist", row.Artist);
        Assert.AreEqual("Projected Genre", row.genre);
        Assert.AreEqual("Projected Tag", row.tag);
        Assert.AreEqual(14, row.mode);
        Assert.AreEqual("12", row.Level);
        Assert.AreEqual("C:\\Installed\\Bmson", row.instl_dst);
        Assert.AreEqual("projected warning", row.DisplayWarning);
        Assert.IsTrue(row.HasHighlightedWarning);
        Assert.AreSame(projectedChart, sourceRow.Chart);
        Assert.AreSame(projectedChart, row.Chart);
    }

    [TestMethod]
    public void LibraryChartRow_BmsonChartProjectionDoesNotCreateCompatibilityAdapter()
    {
        var bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\chart.bmson",
            folder = "C:\\Songs\\Bmson",
            title = "Bmson",
            md5 = "dddddddddddddddddddddddddddddddd",
            sha256 = new string('e', 64)
        };
        LibraryChartRow row = LibraryChartRow.FromBmsonSong(bmson);

        Assert.AreEqual(ChartFileKind.Bmson, row.Chart.Kind);
        Assert.AreEqual(string.Empty, row.instl_dst);
        Assert.IsNull(row.Chart.GetBmsStorageOwner());
        Assert.AreSame(bmson, row.Chart.GetBmsonStorageOwner());
        Assert.IsNull(typeof(LibraryChartRow).GetProperty("CompatibilityBmsFile", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public));
    }

    [TestMethod]
    public void LibraryChartRow_BmsonChartProjectionUsesUpdatedStorageSongAfterSourceSongReplacement()
    {
        var oldSong = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\chart.bmson",
            folder = "C:\\Songs\\Bmson",
            title = "Old Bmson",
            md5 = "dddddddddddddddddddddddddddddddd",
            sha256 = new string('e', 64)
        };
        var newSong = new LR2SongDBExtended.bmson_song
        {
            path = oldSong.path,
            folder = oldSong.folder,
            title = "New Bmson",
            md5 = oldSong.md5,
            sha256 = oldSong.sha256
        };
        LibraryChartRow row = LibraryChartRow.FromBmsonSong(oldSong);
        ChartFile statefulChart = ChartFileProjection.WithPackageState(
            ChartFileProjection.FromBmsonSong(newSong),
            "C:\\Installed\\Bmson",
            string.Empty,
            string.Empty,
            [ChartWarning.Create(ChartWarningKind.InstallEstimationAmbiguous, "ambiguous install destination")]);
        int transientStateLookupCount = 0;
        row.SetChartTransientStateProvider((chart, includeWarningSnapshot) =>
        {
            transientStateLookupCount++;
            Assert.AreSame(newSong, chart.GetBmsonStorageOwner());
            return ChartFileTransientState.FromChartFile(statefulChart, includeWarningSnapshot);
        });

        row.UpdateFromBmsonSong(newSong);
        ChartFile chart = row.Chart;

        Assert.AreSame(newSong, chart.GetBmsonStorageOwner());
        Assert.AreEqual("C:\\Installed\\Bmson", chart.InstallDestination);
        Assert.IsTrue(chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
        Assert.IsTrue(transientStateLookupCount > 0);
    }

    [TestMethod]
    public void ChartListSourceRow_BmsonChartProjectionHasNoCompatibilityBmsFileSurface()
    {
        var bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\chart.bmson",
            folder = "C:\\Songs\\Bmson",
            title = "Bmson",
            md5 = "dddddddddddddddddddddddddddddddd",
            sha256 = new string('e', 64)
        };
        ChartListSourceRow row = ChartListSourceRow.BuildStandardLibraryRows(
            [ChartFileProjection.FromBmsonSong(bmson, includeWarningSnapshot: false)],
            ChartListSourceProjectionMode.OwnerBacked).Single();

        Assert.AreEqual(ChartFileKind.Bmson, row.Chart.Kind);
        Assert.AreEqual(string.Empty, row.InstallDestination);
        Assert.IsNull(row.Chart.GetBmsStorageOwner());
        Assert.IsNull(typeof(ChartListSourceRow).GetProperty("CompatibilityBmsFile", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public));
        Assert.IsNull(typeof(ChartListSourceRow).GetProperty("BmsFile", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public));
        Assert.IsNull(typeof(ChartListSourceRow).GetProperty("BmsonSong", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public));
    }

    [TestMethod]
    public void ChartOperationTarget_PlaylistOwnedBms_HasBmsOnlyAndLocalCapabilities()
    {
        var file = new TestableBmsFile();
        file.ApplySnapshot("abababababababababababababababab", "Owned Bms", 7);
        file.SetSha256(new string('a', 64));
        file.SetSubtitle("Another");
        var entry = new TestablePlaylistEntry(file);

        PlaylistDetailRow row = new PlaylistDetailSourceRow(entry, ChartFileProjection.FromBmsFile(file)).CreateViewRow();

        Assert.AreEqual("Another", GridRowResolver.GetDisplaySubtitle(row));
        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, out ChartOperationTarget target));
        Assert.AreEqual(ChartFileKind.Bms, target.Chart.Kind);
        Assert.IsTrue(target.IsOwned);
        Assert.IsFalse(target.IsPlaylistMissing);
        Assert.AreSame(file, target.Chart.GetBmsStorageOwner());
        LibraryChartRef libraryRef = target.ToLibraryChartRef();
        Assert.AreEqual(LibraryChartKind.Bms, libraryRef.Kind);
        Assert.AreSame(file, libraryRef.GetBmsStorageOwner());
        Assert.AreEqual(file.path, libraryRef.Path);
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.OpenFile));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.OpenFolder));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.OpenRepositoryBySha256));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RunResourceHealthCheck));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.MoveInLibrary));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RemoveFromLibrary));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.UseLr2Ir));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.UseScoreViewer));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.UpdateRanking));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RunBmsEncodingFix));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RunZeroNoteCheck));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RenameInvalidExtension));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.ConvertToAudio));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RepairInstalledLocation));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.UpdateInstallDestination));
    }

    [TestMethod]
    public void ChartOperationTarget_RegularBmson_DisablesBmsOnlyCapabilities()
    {
        var bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\chart.bmson",
            folder = "C:\\Songs\\Bmson",
            title = "Bmson",
            artist = "Artist",
            md5 = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
            sha256 = new string('f', 64)
        };
        var row = LibraryChartRow.FromBmsonSong(bmson);

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, out ChartOperationTarget target));
        Assert.AreEqual(ChartFileKind.Bmson, target.Chart.Kind);
        Assert.IsNull(target.Chart.GetBmsStorageOwner());
        Assert.AreSame(bmson, target.Chart.GetBmsonStorageOwner());
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.OpenRepositoryBySha256));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RunResourceHealthCheck));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.MoveInLibrary));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RemoveFromLibrary));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.UseLr2Ir));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.UpdateRanking));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.RenameInvalidExtension));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.ConvertToAudio));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RepairInstalledLocation));
    }

    [TestMethod]
    public void ChartOperationTarget_LibraryChartRowBmson_DisablesBmsOnlyCapabilities()
    {
        var bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\chart.bmson",
            folder = "C:\\Songs\\Bmson",
            title = "Bmson",
            artist = "Artist",
            md5 = "12121212121212121212121212121212",
            sha256 = new string('1', 64)
        };
        var row = LibraryChartRow.FromBmsonSong(bmson);

        Assert.IsFalse(GridRowResolver.TryGetBmsPlayerFile(row, out _));
        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, out ChartOperationTarget target));
        Assert.AreEqual(ChartFileKind.Bmson, target.Chart.Kind);
        Assert.IsNull(target.Chart.GetBmsStorageOwner());
        Assert.AreSame(bmson, target.Chart.GetBmsonStorageOwner());
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.OpenRepositoryBySha256));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RunResourceHealthCheck));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.MoveInLibrary));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RemoveFromLibrary));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.UseLr2Ir));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.RunBmsEncodingFix));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RepairInstalledLocation));
    }

    [TestMethod]
    public void ChartOperationTarget_LibraryChartRowBmson_HasNoCompatibilityAdapterSurface()
    {
        var bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\lazy.bmson",
            folder = "C:\\Songs\\Bmson",
            title = "Lazy Bmson",
            artist = "Artist",
            md5 = "56565656565656565656565656565656",
            sha256 = new string('5', 64)
        };
        var row = LibraryChartRow.FromBmsonSong(bmson);

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, out ChartOperationTarget target));

        Assert.AreEqual(ChartFileKind.Bmson, target.Chart.Kind);
        Assert.IsNull(target.Chart.GetBmsStorageOwner());
        Assert.AreSame(bmson, target.Chart.GetBmsonStorageOwner());
        Assert.IsNull(typeof(ChartOperationTarget).GetProperty("CompatibilityBmsFile", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public));

        LibraryChartRef libraryRef = target.ToLibraryChartRef();

        Assert.AreEqual(LibraryChartKind.Bmson, libraryRef.Kind);
        Assert.IsNull(libraryRef.GetBmsStorageOwner());
        Assert.AreSame(bmson, libraryRef.GetBmsonStorageOwner());
    }

    [TestMethod]
    public void LibraryChartRef_StorageOwnersAreGetterOnlyAndKindGated()
    {
        var bms = new TestableBmsFile();
        bms.ApplySnapshot("abababababababababababababababab", "BMS", 7);
        bms.SetSha256(new string('a', 64));
        var bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\chart.bmson",
            folder = "C:\\Songs\\Bmson",
            title = "Bmson",
            md5 = "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd",
            sha256 = new string('c', 64)
        };

        LibraryChartRef bmsRef = LibraryChartRef.FromBmsFile(bms);
        LibraryChartRef bmsonRef = LibraryChartRef.FromBmsonSong(bmson);
        LibraryChartRef pathOnlyRef = LibraryChartRef.FromPath(
            LibraryChartKind.Bmson,
            "C:\\Songs\\Missing\\missing.bmson",
            "efefefefefefefefefefefefefefefef",
            new string('e', 64));

        Assert.IsNull(typeof(LibraryChartRef).GetProperty("BmsFile", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public));
        Assert.IsNull(typeof(LibraryChartRef).GetProperty("BmsonSong", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public));
        Assert.AreSame(bms, bmsRef.GetBmsStorageOwner());
        Assert.IsNull(bmsRef.GetBmsonStorageOwner());
        Assert.AreSame(bms, bmsRef.ToChartFile().GetBmsStorageOwner());
        Assert.IsNull(bmsonRef.GetBmsStorageOwner());
        Assert.AreSame(bmson, bmsonRef.GetBmsonStorageOwner());
        Assert.AreSame(bmson, bmsonRef.ToChartFile().GetBmsonStorageOwner());
        Assert.IsNull(pathOnlyRef.GetBmsStorageOwner());
        Assert.IsNull(pathOnlyRef.GetBmsonStorageOwner());
        Assert.IsNull(pathOnlyRef.ToChartFile());
    }

    [TestMethod]
    public void ChartOperationTarget_ToPackageChartEntry_DoesNotMaterializeBmsonCompatibilityAdapter()
    {
        var bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\loose-entry.bmson",
            folder = "C:\\Songs\\Bmson",
            title = "Loose Entry Bmson",
            artist = "Artist",
            md5 = "68686868686868686868686868686868"
        };
        var target = new ChartOperationTarget(
            ChartFileProjection.FromBmsonSong(bmson),
            null,
            ChartOperationSourceScope.PendingPackage,
            isOwned: false,
            isPending: true,
            isPlaylistMissing: false,
            ChartOperationCapabilities.UpdateInstallDestination);

        PackageChartEntry entry = target.ToPackageChartEntry();

        Assert.IsNotNull(entry);
        Assert.IsNull(entry.GetBmsOwnerForTest());
        Assert.AreEqual(ChartFileKind.Bmson, entry.Chart.Kind);
        Assert.AreSame(bmson, entry.Chart.GetBmsonStorageOwner());
    }

    [TestMethod]
    public void ChartOperationTarget_ToPackageChartEntry_UsesBmsStorageOwner()
    {
        BMSFile file = CreateBmsFile("C:\\Songs\\Bms\\chart.bms", "BMS", "Artist", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var target = new ChartOperationTarget(
            ChartFileProjection.FromBmsFile(file),
            null,
            ChartOperationSourceScope.Library,
            isOwned: true,
            isPending: false,
            isPlaylistMissing: false,
            ChartOperationCapabilities.UpdateInstallDestination);

        PackageChartEntry entry = target.ToPackageChartEntry();

        Assert.IsNotNull(entry);
        Assert.AreSame(file, entry.GetBmsOwnerForTest());
        Assert.AreSame(file, entry.Chart.GetBmsStorageOwner());
    }

    [TestMethod]
    public void ChartFolderAutoRenameRequest_UsesChartFileWithoutMaterializingBmsonCompatibilityAdapter()
    {
        var bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\auto-rename.bmson",
            folder = "C:\\Songs\\Bmson",
            title = "Auto Rename Bmson",
            artist = "Artist",
            md5 = "67676767676767676767676767676767",
            sha256 = new string('6', 64)
        };
        var row = LibraryChartRow.FromBmsonSong(bmson);

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, out ChartOperationTarget target));
        Assert.IsTrue(ChartFolderAutoRenameRequest.TryCreate([target], out ChartFolderAutoRenameRequest request));

        Assert.AreEqual(1, request.Charts.Count);
        Assert.IsTrue(request.HasTargets);
        Assert.AreSame(bmson, request.Charts[0].GetBmsonStorageOwner());
    }

    [TestMethod]
    public void ChartLibraryMoveRequest_UsesLibraryChartRefWithoutMaterializingBmsonCompatibilityAdapter()
    {
        var bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\move.bmson",
            folder = "C:\\Songs\\Bmson",
            title = "Move Bmson",
            artist = "Artist",
            md5 = "68686868686868686868686868686868",
            sha256 = new string('8', 64)
        };
        var row = LibraryChartRow.FromBmsonSong(bmson);

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, out ChartOperationTarget target));
        Assert.IsTrue(ChartLibraryMoveRequest.TryCreate([target], "D:\\Songs", out ChartLibraryMoveRequest request));

        Assert.AreEqual(1, request.Charts.Count);
        Assert.IsTrue(request.HasTargets);
        Assert.AreEqual("D:\\Songs", request.NewParentDirectory);
        Assert.AreSame(bmson, request.Charts[0].GetBmsonStorageOwner());
    }

    [TestMethod]
    public void RepairInstalledLocationTargetSnapshot_HasInstallDestinationDoesNotMaterializeLooseBmsonCompatibilityAdapter()
    {
        var bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\repair.bmson",
            folder = "C:\\Songs\\Bmson",
            title = "Repair Bmson",
            artist = "Artist",
            md5 = "68686868686868686868686868686868",
            sha256 = new string('8', 64)
        };
        var row = LibraryChartRow.FromBmsonSong(bmson);

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, out ChartOperationTarget target));
        var viewModel = new MainWindowViewModel();
        MainWindowViewModel.IRepairInstalledLocationTargetSnapshot snapshot =
            viewModel.CreateRepairInstalledLocationTargetSnapshot([target]);

        Assert.IsTrue(snapshot.HasTargets);
        Assert.IsFalse(snapshot.HasInstallDestination);
        Assert.AreEqual(string.Empty, snapshot.RepairCharts[0].InstallDestination);

        snapshot.MaterializeRepairEntries();

        Assert.IsFalse(snapshot.HasInstallDestination);
    }

    [TestMethod]
    public void RepairInstalledLocationTargetSnapshot_RepairChartsDoNotOverlayLooseCompatibilityInstallDestination()
    {
        var bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\repair-playlist.bmson",
            folder = "C:\\Songs\\Bmson",
            title = "Repair Playlist Bmson",
            artist = "Artist",
            md5 = "70707070707070707070707070707070",
            sha256 = new string('a', 64)
        };
        ChartFile staleChart = ChartFileProjection.FromBmsonSong(bmson);
        var target = new ChartOperationTarget(
            staleChart,
            playlistEntry: null,
            ChartOperationSourceScope.PlaylistOwned,
            isOwned: true,
            isPending: false,
            isPlaylistMissing: false,
            ChartOperationCapabilities.RepairInstalledLocation);

        var viewModel = new MainWindowViewModel();
        MainWindowViewModel.IRepairInstalledLocationTargetSnapshot snapshot =
            viewModel.CreateRepairInstalledLocationTargetSnapshot([target]);

        Assert.IsFalse(snapshot.HasInstallDestination);
        Assert.AreEqual(string.Empty, snapshot.RepairCharts[0].InstallDestination);
        Assert.AreSame(bmson, snapshot.RepairCharts[0].GetBmsonStorageOwner());
    }

    [TestMethod]
    public void RepairInstalledLocationTargetSnapshot_RepairChartsOverlayUsesPathBeforeHash()
    {
        string md5 = "71717171717171717171717171717171";
        var firstBmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\duplicate-a.bmson",
            folder = "C:\\Songs\\Bmson",
            title = "Duplicate A",
            md5 = md5
        };
        var secondBmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\duplicate-b.bmson",
            folder = "C:\\Songs\\Bmson",
            title = "Duplicate B",
            md5 = md5
        };
        ChartFile firstChart = ChartFileProjection.WithPackageState(
            ChartFileProjection.FromBmsonSong(firstBmson),
            "C:\\Installed\\A",
            string.Empty,
            string.Empty,
            []);
        ChartFile secondChart = ChartFileProjection.WithPackageState(
            ChartFileProjection.FromBmsonSong(secondBmson),
            "C:\\Installed\\B",
            string.Empty,
            string.Empty,
            []);
        var firstTarget = new ChartOperationTarget(
            firstChart,
            null,
            ChartOperationSourceScope.PlaylistOwned,
            isOwned: true,
            isPending: false,
            isPlaylistMissing: false,
            ChartOperationCapabilities.RepairInstalledLocation);
        var secondTarget = new ChartOperationTarget(
            secondChart,
            null,
            ChartOperationSourceScope.PlaylistOwned,
            isOwned: true,
            isPending: false,
            isPlaylistMissing: false,
            ChartOperationCapabilities.RepairInstalledLocation);

        var viewModel = new MainWindowViewModel();
        MainWindowViewModel.IRepairInstalledLocationTargetSnapshot snapshot =
            viewModel.CreateRepairInstalledLocationTargetSnapshot([firstTarget, secondTarget]);

        Assert.AreEqual("C:\\Installed\\A", snapshot.RepairCharts[0].InstallDestination);
        Assert.AreEqual("C:\\Installed\\B", snapshot.RepairCharts[1].InstallDestination);
    }

    [TestMethod]
    public void RepairInstalledLocationTargetSnapshot_HasInstallDestinationUsesExistingChartSnapshotWithoutCreatingAdapter()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\repair-existing.bmson",
            folder = "C:\\Songs\\Bmson",
            title = "Repair Existing Bmson",
            artist = "Artist",
            md5 = "69696969696969696969696969696969",
            sha256 = new string('9', 64)
        };
        var row = LibraryChartRow.FromChartFile(ChartFileProjection.WithPackageState(
            ChartFileProjection.FromBmsonSong(bmson),
            "C:\\Installed\\Bmson",
            string.Empty,
            string.Empty,
            []));

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, out ChartOperationTarget target));
        var viewModel = new MainWindowViewModel();
        MainWindowViewModel.IRepairInstalledLocationTargetSnapshot snapshot =
            viewModel.CreateRepairInstalledLocationTargetSnapshot([target]);

        Assert.IsTrue(snapshot.HasTargets);
        Assert.IsTrue(snapshot.HasInstallDestination);
        Assert.AreEqual("C:\\Installed\\Bmson", snapshot.RepairCharts[0].InstallDestination);
    }

    [TestMethod]
    public void PendingInstallDestinationTargetSnapshot_DoesNotMaterializeLooseBmsonEntryBeforeBackgroundWork()
    {
        var bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\pending-loose.bmson",
            folder = "C:\\Songs\\Bmson",
            title = "Pending Loose Bmson",
            artist = "Artist",
            md5 = "69696969696969696969696969696969",
            sha256 = new string('9', 64)
        };
        ChartFile chart = ChartFileProjection.FromBmsonSong(bmson);
        int adapterRequestCount = 0;
        var target = new ChartOperationTarget(
            chart,
            null,
            ChartOperationSourceScope.PendingPackage,
            isOwned: false,
            isPending: true,
            isPlaylistMissing: false,
            ChartOperationCapabilities.UpdateInstallDestination);

        var viewModel = new MainWindowViewModel();
        MainWindowViewModel.PendingInstallDestinationTargetSnapshot snapshot =
            viewModel.CreatePendingInstallDestinationTargetSnapshot([target]);

        Assert.AreEqual(0, adapterRequestCount);
        Assert.IsTrue(snapshot.HasTargets);
        Assert.AreEqual(0, adapterRequestCount);
        Assert.AreEqual(0, snapshot.PackageTargets.Count);
        Assert.AreEqual(1, snapshot.Charts.Count);

        snapshot.MaterializeLooseEntries();

        Assert.AreEqual(0, adapterRequestCount);
        Assert.AreEqual(1, snapshot.LooseEntries.Count);
        Assert.AreEqual(ChartFileKind.Bmson, snapshot.LooseEntries[0].Chart.Kind);
        Assert.IsNull(snapshot.LooseEntries[0].GetBmsOwnerForTest());
        Assert.AreEqual(0, adapterRequestCount);
    }

    [TestMethod]
    public void PendingInstallDestinationEditTargetSnapshot_DoesNotMaterializeLooseBmsonCompatibilityAdapter()
    {
        var bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\pending-edit.bmson",
            folder = "C:\\Songs\\Bmson",
            title = "Pending Edit Bmson",
            artist = "Artist",
            md5 = "70707070707070707070707070707070",
            sha256 = new string('a', 64)
        };
        ChartFile chart = ChartFileProjection.FromBmsonSong(bmson);
        int adapterRequestCount = 0;
        var target = new ChartOperationTarget(
            chart,
            null,
            ChartOperationSourceScope.PendingPackage,
            isOwned: false,
            isPending: true,
            isPlaylistMissing: false,
            ChartOperationCapabilities.UpdateInstallDestination);

        var viewModel = new MainWindowViewModel();
        MainWindowViewModel.PendingInstallDestinationEditTargetSnapshot snapshot =
            viewModel.CreatePendingInstallDestinationEditTargetSnapshot(target);

        Assert.AreEqual(0, adapterRequestCount);
        Assert.IsTrue(snapshot.HasTarget);
        Assert.IsNull(snapshot.PackageEntry);
        Assert.IsNotNull(snapshot.ChartFile);
        Assert.AreEqual(ChartFileKind.Bmson, snapshot.ChartFile.Kind);
        Assert.AreEqual(0, adapterRequestCount);

        PackageChartEntry editEntry = snapshot.GetOrCreateChartEntry();
        editEntry.ApplyInstallDestination("C:\\Installed\\Bmson", "Installed Bmson", "Installed Artist");

        Assert.AreEqual(0, adapterRequestCount);
        Assert.IsNull(editEntry.GetBmsOwnerForTest());
        Assert.AreEqual("C:\\Installed\\Bmson", editEntry.Chart.InstallDestination);
        Assert.AreEqual("Installed Bmson", editEntry.Chart.InstallDestinationTitle);
    }

    [TestMethod]
    public void RenameChartFolderRequest_DoesNotMaterializeBmsonCompatibilityAdapter()
    {
        var bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\rename.bmson",
            folder = "C:\\Songs\\Bmson",
            title = "Rename Bmson",
            artist = "Artist",
            md5 = "71717171717171717171717171717171",
            sha256 = new string('b', 64)
        };
        var row = LibraryChartRow.FromBmsonSong(bmson);

        Assert.IsTrue(GridRowResolver.TryGetFolderEditChartOperationTarget(row, ChartOperationSourceScope.Library, out ChartOperationTarget target));
        Assert.IsTrue(RenameChartFolderRequest.TryCreate(target, out RenameChartFolderRequest request));

        Assert.IsTrue(request.HasTarget);
        Assert.AreSame(bmson, request.Chart.GetBmsonStorageOwner());
    }

    [TestMethod]
    public void LibraryChartRow_BmsonChartCombinesFreshSongMaintenanceWithTransientState()
    {
        var bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\maintenance.bmson",
            folder = "C:\\Songs\\Bmson",
            title = "Maintenance Bmson",
            artist = "Artist",
            md5 = "67676767676767676767676767676767",
            sha256 = new string('6', 64),
            MaintenanceInfo = new BMSFileMaintenanceInfo
            {
                wav_files_defined = 10,
                wav_files_existing = 10,
                bga_files_defined = 8,
                bga_files_existing = 8,
                movie_files_defined = 0,
                encoding = "utf-8"
            }
        };
        bmson.MaintenanceInfo = new BMSFileMaintenanceInfo
        {
            wav_files_defined = 10,
            wav_files_existing = 10,
            bga_files_defined = 8,
            bga_files_existing = 8,
            movie_files_defined = 0,
            encoding = "utf-16"
        };
        ChartFile statefulChart = ChartFileProjection.WithPackageState(
            ChartFileProjection.FromBmsonSong(bmson),
            "C:\\Installed\\Bmson",
            string.Empty,
            string.Empty,
            [ChartWarning.Create(ChartWarningKind.InstallEstimationAmbiguous, "ambiguous install destination")]);
        var row = LibraryChartRow.FromChartFile(statefulChart);

        ChartFile chart = row.Chart;

        Assert.AreEqual(100, chart.WAVHealth);
        Assert.AreEqual(100, chart.BGAHealth);
        Assert.AreEqual(100, chart.MovieHealth);
        Assert.AreEqual("utf-16", chart.EncodingName);
        Assert.AreEqual("C:\\Installed\\Bmson", chart.InstallDestination);
        Assert.IsTrue(chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
    }

    [TestMethod]
    public void ChartOperationTarget_BmsonRowsUseChartTransientStateForInstallLocationRepair()
    {
        var bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\chart.bmson",
            folder = "C:\\Songs\\Bmson",
            title = "Bmson",
            artist = "Artist",
            md5 = "34343434343434343434343434343434",
            sha256 = new string('3', 64)
        };
        ChartFile libraryChart = ChartFileProjection.WithPackageState(
            ChartFileProjection.FromBmsonSong(bmson),
            "C:\\Installed\\Bmson",
            string.Empty,
            string.Empty,
            [ChartWarning.Create(ChartWarningKind.InstallEstimationAmbiguous, "ambiguous install destination")]);
        var libraryRow = LibraryChartRow.FromChartFile(libraryChart);
        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(libraryRow, out ChartOperationTarget firstLibraryTarget));

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(libraryRow, out ChartOperationTarget secondLibraryTarget));

        Assert.AreEqual(firstLibraryTarget.Chart.Kind, secondLibraryTarget.Chart.Kind);
        Assert.AreEqual(firstLibraryTarget.Chart.Path, secondLibraryTarget.Chart.Path);
        Assert.AreEqual(firstLibraryTarget.Chart.PrimaryLookupHash, secondLibraryTarget.Chart.PrimaryLookupHash);
        Assert.AreSame(firstLibraryTarget.Chart.GetBmsonStorageOwner(), secondLibraryTarget.Chart.GetBmsonStorageOwner());
        Assert.AreEqual("C:\\Installed\\Bmson", secondLibraryTarget.Chart.InstallDestination);
        Assert.IsTrue(secondLibraryTarget.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
        Assert.AreEqual("C:\\Installed\\Bmson", libraryRow.instl_dst);
        Assert.IsTrue(libraryRow.HasHighlightedWarning);
        StringAssert.Contains(libraryRow.WarningTooltipText, "ambiguous install destination");

        var rebuiltWarningRow = LibraryChartRow.FromChartFile(libraryChart);
        Assert.IsTrue(rebuiltWarningRow.HasHighlightedWarning);
        Assert.AreEqual("C:\\Installed\\Bmson", rebuiltWarningRow.Chart.InstallDestination);
        StringAssert.Contains(rebuiltWarningRow.WarningTooltipText, "ambiguous install destination");

        var entry = new TestablePlaylistEntry();
        entry.SetMd5(bmson.md5);
        entry.SetSha256(bmson.sha256);
        ChartFile playlistChart = ChartFileProjection.WithPackageState(
            ChartFileProjection.FromBmsonSong(bmson),
            "C:\\Installed\\PlaylistBmson",
            "Installed Playlist Bmson",
            "Installed Playlist Artist",
            [ChartWarning.Create(ChartWarningKind.InstallEstimationAmbiguous, "playlist ambiguous install destination")]);
        PlaylistDetailRow playlistRow = new PlaylistDetailSourceRow(
            entry,
            playlistChart).CreateViewRow();
        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(playlistRow, out ChartOperationTarget firstPlaylistTarget));

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(playlistRow, out ChartOperationTarget secondPlaylistTarget));

        Assert.AreSame(firstPlaylistTarget.Chart, secondPlaylistTarget.Chart);
        Assert.AreEqual("C:\\Installed\\PlaylistBmson", secondPlaylistTarget.Chart.InstallDestination);

        var rebuiltPlaylistSourceRow = new PlaylistDetailSourceRow(
            entry,
            playlistChart);
        Assert.AreEqual("C:\\Installed\\PlaylistBmson", rebuiltPlaylistSourceRow.instl_dst);
        Assert.AreEqual("C:\\Installed\\PlaylistBmson", rebuiltPlaylistSourceRow.Chart.InstallDestination);
        Assert.AreEqual("Installed Playlist Bmson", rebuiltPlaylistSourceRow.InstallDestinationTitle);
        Assert.AreEqual("Installed Playlist Artist", rebuiltPlaylistSourceRow.InstallDestinationArtist);
        Assert.IsTrue(rebuiltPlaylistSourceRow.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
        StringAssert.Contains(rebuiltPlaylistSourceRow.WarningTooltipText, "playlist ambiguous install destination");
    }

    [TestMethod]
    public void ChartListSourceRow_BmsonUsesTransientStateForInstallRepairState()
    {
        var bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\chart.bmson",
            folder = "C:\\Songs\\Bmson",
            title = "Bmson",
            artist = "Artist",
            md5 = "45454545454545454545454545454545",
            sha256 = new string('4', 64)
        };
        ChartFile statefulChart = ChartFileProjection.WithPackageState(
            ChartFileProjection.FromBmsonSong(bmson),
            "C:\\Installed\\Bmson",
            "Installed Bmson",
            "Installed Artist",
            [ChartWarning.Create(ChartWarningKind.InstallEstimationAmbiguous, "ambiguous install destination")]);

        ChartListSourceRow firstSourceRow = ChartListSourceRow.BuildStandardLibraryRows(
            [ChartFileProjection.FromBmsonSong(bmson, includeWarningSnapshot: false)],
            ChartListSourceProjectionMode.OwnerBacked,
            chartTransientStateProvider: (chart, includeWarningSnapshot) => ChartFileTransientState.FromChartFile(statefulChart, includeWarningSnapshot)).Single();

        ChartListSourceRow rebuiltSourceRow = ChartListSourceRow.BuildStandardLibraryRows(
            [ChartFileProjection.FromBmsonSong(bmson, includeWarningSnapshot: false)],
            ChartListSourceProjectionMode.OwnerBacked,
            chartTransientStateProvider: (chart, includeWarningSnapshot) => ChartFileTransientState.FromChartFile(statefulChart, includeWarningSnapshot)).Single();
        var rebuiltViewRow = LibraryChartRow.FromChartFile(ChartFileProjection.FromBmsonSong(
            rebuiltSourceRow.Chart.GetBmsonStorageOwner(),
            ChartFileTransientState.FromChartFile(statefulChart)));

        Assert.AreEqual("C:\\Installed\\Bmson", firstSourceRow.InstallDestination);
        Assert.AreEqual("C:\\Installed\\Bmson", rebuiltSourceRow.InstallDestination);
        Assert.AreEqual("C:\\Installed\\Bmson", rebuiltSourceRow.Chart.InstallDestination);
        Assert.AreEqual("Installed Bmson", rebuiltSourceRow.InstallDestinationTitle);
        Assert.AreEqual("Installed Artist", rebuiltSourceRow.InstallDestinationArtist);
        Assert.IsTrue(rebuiltSourceRow.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
        StringAssert.Contains(rebuiltSourceRow.WarningDigestText, BeMusicSeeker.Properties.Resources.WarningDigest_InstallEstimationAmbiguous);
        Assert.AreEqual("C:\\Installed\\Bmson", rebuiltViewRow.instl_dst);
        Assert.IsTrue(rebuiltViewRow.HasHighlightedWarning);
        StringAssert.Contains(rebuiltViewRow.WarningTooltipText, "ambiguous install destination");
    }

    [TestMethod]
    public void ResolvePlaylistDropChart_LibraryBmsonRowCreatesPlaylistEntryWithBothHashes()
    {
        var bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\chart.bmson",
            folder = "C:\\Songs\\Bmson",
            title = "Bmson",
            artist = "Artist",
            level = 12,
            md5 = "12121212121212121212121212121212",
            sha256 = new string('1', 64)
        };
        var row = LibraryChartRow.FromBmsonSong(bmson);

        ChartFile chart = MainWindowViewModel.ResolvePlaylistDropChart(row);
        var entry = BMSTableEntry.CreateForPlaylistDrop(chart);

        Assert.IsNotNull(chart);
        Assert.AreEqual(ChartFileKind.Bmson, chart.Kind);
        Assert.IsNull(chart.GetBmsStorageOwner());
        Assert.AreSame(bmson, chart.GetBmsonStorageOwner());
        Assert.AreEqual(bmson.path, chart.Path);
        Assert.AreEqual(bmson.title, chart.Title);
        Assert.AreEqual(bmson.md5, entry.md5);
        Assert.AreEqual(bmson.sha256, entry.sha256);
        Assert.AreEqual(0, entry.Org_md5.Count);
    }

    [TestMethod]
    public void CreateForPlaylistDrop_BmsChartUsesProvidedOrgMd5s()
    {
        var file = new TestableBmsFile();
        file.ApplySnapshot("abababababababababababababababab", "BMS", 8);
        file.SetSha256(new string('e', 64));
        ChartFile chart = ChartFileProjection.FromBmsFile(file);

        var entry = BMSTableEntry.CreateForPlaylistDrop(chart, ["cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd"]);

        Assert.AreEqual(file.hash, entry.md5);
        Assert.AreEqual(file.sha256, entry.sha256);
        CollectionAssert.AreEqual(new[] { "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd" }, entry.Org_md5);
    }

    [TestMethod]
    public void CreateForPlaylistDrop_OwnerlessBmsonChartPreservesBothHashesAndOrgMd5()
    {
        var chart = new ChartFile(
            ChartFileKind.Bmson,
            path: "C:\\Songs\\ownerless.bmson",
            md5: "abababababababababababababababab",
            sha256: new string('f', 64),
            title: "Ownerless bmson",
            rawTitle: "Ownerless bmson",
            artist: "Artist",
            genre: string.Empty,
            folder: "Folder",
            tag: string.Empty,
            levelText: "12",
            level: 12,
            mode: null,
            chartInfo: null,
            bmsFile: null,
            bmsonSong: null);

        var entry = BMSTableEntry.CreateForPlaylistDrop(chart, ["cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd"]);

        Assert.AreEqual(chart.Md5, entry.md5);
        Assert.AreEqual(chart.Sha256, entry.sha256);
        Assert.AreEqual("Ownerless bmson", entry.title);
        CollectionAssert.AreEqual(new[] { "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd" }, entry.Org_md5);
    }

    [TestMethod]
    public void CommitPlaylistRow_ExternalSyncEntryDoesNotBackfillHashesFromResolvedChart()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "PlaylistViewPipelineTests", Guid.NewGuid().ToString("N"));
        string songDbPath = Path.Combine(tempDirectory, "song.db");
        Directory.CreateDirectory(tempDirectory);
        try
        {
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            BMSPlaylist.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 9101,
                name = "External",
                is_external_sync = true
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
            }
            var entry = new TestablePlaylistEntry
            {
                parent = table,
                folder = string.Empty,
                memo = "memo"
            };
            entry.SetSha256(new string('9', 64));
            entry.SetTitle("External Bmson");
            var bmson = new LR2SongDBExtended.bmson_song
            {
                path = "C:\\Songs\\Bmson\\external.bmson",
                title = "External Bmson",
                md5 = "99999999999999999999999999999999",
                sha256 = entry.sha256
            };
            PlaylistDetailRow row = new PlaylistDetailSourceRow(entry, ChartFileProjection.FromBmsonSong(bmson, includeWarningSnapshot: false)).CreateViewRow();
            var viewModel = new MainWindowViewModel();
            typeof(MainWindowViewModel).GetField("tables", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewModel, new BMSPlaylist(songDbPath));

            viewModel.CommitPlaylistRow(row);

            using var verifyDb = new LR2SongDBExtended(songDbPath);
            BMSTableEntry stored = verifyDb.Table<BMSTableEntry>().Single(dbRow => dbRow.playlist_id == table.playlist_id);
            Assert.IsNull(entry.md5);
            Assert.IsNull(stored.md5);
            Assert.AreEqual(entry.sha256, stored.sha256);
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
    public void LibraryChartRow_FromPendingBmson_PreservesPendingInstallState()
    {
        var bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Pending\\Bmson\\chart.bmson",
            folder = "C:\\Pending\\Bmson",
            title = "Pending Bmson",
            artist = "Artist",
            level = 9,
            mode_hint = "beat-7k",
            md5 = "34343434343434343434343434343434",
            sha256 = new string('3', 64)
        };
        bmson.MaintenanceInfo = BMSFileMaintenanceInfo.CreateForBmson(bmson.path, bmson.md5);
        bmson.MaintenanceInfo.wav_files_defined = 4;
        bmson.MaintenanceInfo.wav_files_existing = 1;

        PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmson));
        entry.SetWarning(ChartWarningKind.AlreadyInstalled, "installed chart warning");
        entry.ApplyInstallDestination("C:\\Library\\Destination", string.Empty, string.Empty);
        var row = LibraryChartRow.FromPackageChartEntry(entry);

        Assert.IsNull(row.GetBmsStorageOwner());
        Assert.AreSame(bmson, row.GetBmsonStorageOwner());
        Assert.AreEqual(ChartFileKind.Bmson, row.Chart.Kind);
        Assert.AreSame(bmson, row.Chart.GetBmsonStorageOwner());
        Assert.IsFalse(GridRowResolver.TryGetBmsPlayerFile(row, out _));
        Assert.IsTrue(row.DisplayWarning.Contains("installed chart warning"));
        Assert.AreEqual("C:\\Library\\Destination", row.instl_dst);
        Assert.AreEqual(bmson.MaintenanceInfo.WAVHealth, row.WAVHealth);

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, isPendingSection: true, out ChartOperationTarget target));
        Assert.AreEqual(ChartFileKind.Bmson, target.Chart.Kind);
        Assert.AreSame(bmson, target.Chart.GetBmsonStorageOwner());
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.UpdateInstallDestination));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.OpenRepositoryBySha256));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.UseLr2Ir));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.RunBmsEncodingFix));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.RunZeroNoteCheck));

        var chartRef = LibraryChartRef.FromChartFile(row.Chart);
        Assert.IsNull(chartRef.GetBmsStorageOwner());
        Assert.AreSame(bmson, chartRef.GetBmsonStorageOwner());
    }

    [TestMethod]
    public void LibraryChartRow_FromPackageChartEntry_HidesResourceHealthDigestWhenInstallDestinationSetButKeepsTooltip()
    {
        var bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Pending\\Bmson\\resource-missing.bmson",
            folder = "C:\\Pending\\Bmson",
            title = "Resource Missing",
            artist = "Artist",
            md5 = "45454545454545454545454545454545",
            sha256 = new string('4', 64)
        };
        PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmson));
        entry.SetWarning(ChartWarningKind.ResourceWavMissing, "WAV missing detail");

        var unresolvedRow = LibraryChartRow.FromPackageChartEntry(entry);
        StringAssert.Contains(unresolvedRow.WarningDigestText, BeMusicSeeker.Properties.Resources.WarningDigest_ResourceMissing);

        entry.ApplyInstallDestination("C:\\Library\\Destination", string.Empty, string.Empty);
        var resolvedRow = LibraryChartRow.FromPackageChartEntry(entry);

        Assert.IsFalse(resolvedRow.WarningDigestText.Contains(BeMusicSeeker.Properties.Resources.WarningDigest_ResourceMissing));
        StringAssert.Contains(resolvedRow.WarningTooltipText, "WAV missing detail");
    }

    [TestMethod]
    public void ChartListSourceRow_FromPackageChartEntry_HidesResourceHealthDigestWhenInstallDestinationSetButKeepsTooltip()
    {
        var bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Pending\\Bmson\\source-resource-missing.bmson",
            folder = "C:\\Pending\\Bmson",
            title = "Source Resource Missing",
            artist = "Artist",
            md5 = "67676767676767676767676767676767",
            sha256 = new string('6', 64)
        };
        PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmson));
        entry.SetWarning(ChartWarningKind.ResourceWavMissing, "source WAV missing detail");

        ChartListSourceRow unresolvedRow = ChartListSourceRow.FromPackageChartEntry(entry);
        StringAssert.Contains(unresolvedRow.WarningDigestText, BeMusicSeeker.Properties.Resources.WarningDigest_ResourceMissing);

        entry.ApplyInstallDestination("C:\\Library\\Destination", string.Empty, string.Empty);
        ChartListSourceRow resolvedRow = ChartListSourceRow.FromPackageChartEntry(entry);

        Assert.IsFalse(resolvedRow.WarningDigestText.Contains(BeMusicSeeker.Properties.Resources.WarningDigest_ResourceMissing));
        StringAssert.Contains(ChartWarningCollection.BuildTooltipText(resolvedRow.Chart.Warnings), "source WAV missing detail");
    }

    [TestMethod]
    public void ChartOperationTarget_PendingBmson_UsesPendingPathAndPendingCapabilities()
    {
        var bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Original\\Bmson\\chart.bmson",
            folder = "C:\\Original\\Bmson",
            title = "Pending Bmson",
            artist = "Artist",
            md5 = "56565656565656565656565656565656",
            sha256 = new string('5', 64)
        };
        bmson.path = "C:\\Pending\\Package\\chart.bmson";
        bmson.folder = "C:\\Pending\\Package";
        PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmson));
        var row = LibraryChartRow.FromPackageChartEntry(entry);

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, ChartOperationSourceScope.PendingPackage, out ChartOperationTarget target));

        Assert.AreEqual(ChartOperationSourceScope.PendingPackage, target.SourceScope);
        Assert.AreEqual(ChartFileKind.Bmson, target.Chart.Kind);
        Assert.AreEqual(bmson.path, target.Chart.Path);
        Assert.IsFalse(target.IsOwned);
        Assert.IsTrue(target.IsPending);
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.OpenFile));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.OpenFolder));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.UpdateInstallDestination));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RunResourceHealthCheck));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.RepairInstalledLocation));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.RemoveFromLibrary));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.MoveInLibrary));
    }

    [TestMethod]
    public void MainViewOperationContext_MapsViewModeToRowOperationScope()
    {
        AssertMainViewOperationContext(
            MainViewUpdateMode.PendingInstallFolderSelected,
            MainWindowViewModel.MainViewOperationSection.InstallPending,
            ChartOperationSourceScope.PendingPackage);
        AssertMainViewOperationContext(
            MainViewUpdateMode.NewlyInstalledFolderSelected,
            MainWindowViewModel.MainViewOperationSection.InstallInstalled,
            ChartOperationSourceScope.NewlyInstalledPackage);
        AssertMainViewOperationContext(
            MainViewUpdateMode.PlaylistFilterSelected,
            MainWindowViewModel.MainViewOperationSection.Playlist,
            ChartOperationSourceScope.Library);
        AssertMainViewOperationContext(
            MainViewUpdateMode.PlaylistNotOwnedFilterSelected,
            MainWindowViewModel.MainViewOperationSection.Playlist,
            ChartOperationSourceScope.Library);
        AssertMainViewOperationContext(
            MainViewUpdateMode.FullScanAllChartsFilterSelected,
            MainWindowViewModel.MainViewOperationSection.FullScanCheck,
            ChartOperationSourceScope.Library);
        AssertMainViewOperationContext(
            MainViewUpdateMode.FileMissingFilterSelected,
            MainWindowViewModel.MainViewOperationSection.FullScanCheck,
            ChartOperationSourceScope.Library);
        AssertMainViewOperationContext(
            MainViewUpdateMode.FileMissingIgnoredFilterSelected,
            MainWindowViewModel.MainViewOperationSection.FullScanCheck,
            ChartOperationSourceScope.Library);
        AssertMainViewOperationContext(
            MainViewUpdateMode.ChartInfoParseErrorFilterSelected,
            MainWindowViewModel.MainViewOperationSection.ChartInfoParseError,
            ChartOperationSourceScope.Library);
        AssertMainViewOperationContext(
            MainViewUpdateMode.PlayHistorySelected,
            MainWindowViewModel.MainViewOperationSection.PlayHistory,
            ChartOperationSourceScope.Library);
        AssertMainViewOperationContext(
            MainViewUpdateMode.FolderFilterSelected,
            MainWindowViewModel.MainViewOperationSection.Library,
            ChartOperationSourceScope.Library);
    }

    [TestMethod]
    public void MainViewOperationContext_PendingModeMakesLibraryChartRowPendingScoped()
    {
        var file = new TestableBmsFile();
        file.ApplySnapshot("abababababababababababababababab", "Pending Bms", 7);
        var row = LibraryChartRow.FromBmsFile(file);
        ChartOperationSourceScope sourceScope = MainWindowViewModel.ResolveMainViewChartOperationSourceScope(
            MainWindowViewModel.ResolveMainViewOperationSection(MainViewUpdateMode.PendingInstallFolderSelected));

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, sourceScope, out ChartOperationTarget target));

        Assert.AreEqual(ChartOperationSourceScope.PendingPackage, target.SourceScope);
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.UpdateInstallDestination));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.MoveInLibrary));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.RemoveFromLibrary));
    }

    [TestMethod]
    public void DeleteTargetResolver_NewlyInstalledRoutesToLibrary()
    {
        ChartOperationTarget target = CreateDeleteTarget(ChartOperationSourceScope.NewlyInstalledPackage, ChartOperationCapabilities.RemoveFromLibrary);

        ChartDeleteTargetResolution resolution = ChartDeleteTargetResolver.Resolve(
            [target],
            target,
            MainWindowViewModel.MainViewOperationSection.InstallInstalled);

        Assert.AreEqual(ChartDeleteRoute.Library, resolution.Route);
        Assert.AreEqual(1, resolution.Targets.Count);
        Assert.AreSame(target, resolution.Targets[0]);
        Assert.IsFalse(resolution.UsedContextFallback);
    }

    [TestMethod]
    public void DeleteTargetResolver_PendingRoutesToPending()
    {
        ChartOperationTarget target = CreateDeleteTarget(ChartOperationSourceScope.PendingPackage, ChartOperationCapabilities.UpdateInstallDestination);

        ChartDeleteTargetResolution resolution = ChartDeleteTargetResolver.Resolve(
            [target],
            target,
            MainWindowViewModel.MainViewOperationSection.InstallPending);

        Assert.AreEqual(ChartDeleteRoute.Pending, resolution.Route);
        Assert.AreEqual(1, resolution.Targets.Count);
        Assert.AreSame(target, resolution.Targets[0]);
    }

    [TestMethod]
    public void DeleteTargetResolver_UsesContextFallbackWhenSelectionIsEmpty()
    {
        ChartOperationTarget target = CreateDeleteTarget(ChartOperationSourceScope.Library, ChartOperationCapabilities.RemoveFromLibrary);

        ChartDeleteTargetResolution resolution = ChartDeleteTargetResolver.Resolve(
            [],
            target,
            MainWindowViewModel.MainViewOperationSection.Library);

        Assert.AreEqual(ChartDeleteRoute.Library, resolution.Route);
        Assert.AreEqual(1, resolution.Targets.Count);
        Assert.AreSame(target, resolution.Targets[0]);
        Assert.IsTrue(resolution.UsedContextFallback);
    }

    [TestMethod]
    public void DeleteTargetResolver_PlaylistMissingIsNotFileDeleteTarget()
    {
        ChartOperationTarget target = CreateDeleteTarget(ChartOperationSourceScope.PlaylistMissing, ChartOperationCapabilities.None);

        ChartDeleteTargetResolution resolution = ChartDeleteTargetResolver.Resolve(
            [target],
            target,
            MainWindowViewModel.MainViewOperationSection.Playlist);

        Assert.AreEqual(ChartDeleteRoute.None, resolution.Route);
        Assert.AreEqual(0, resolution.Targets.Count);
    }

    [TestMethod]
    public void DeleteTargetResolver_ContextScopeFiltersMixedSelection()
    {
        ChartOperationTarget libraryTarget = CreateDeleteTarget(ChartOperationSourceScope.Library, ChartOperationCapabilities.RemoveFromLibrary);
        ChartOperationTarget pendingTarget = CreateDeleteTarget(ChartOperationSourceScope.PendingPackage, ChartOperationCapabilities.UpdateInstallDestination);

        ChartDeleteTargetResolution resolution = ChartDeleteTargetResolver.Resolve(
            [libraryTarget, pendingTarget],
            pendingTarget,
            MainWindowViewModel.MainViewOperationSection.InstallPending);

        Assert.AreEqual(ChartDeleteRoute.Pending, resolution.Route);
        Assert.AreEqual(1, resolution.Targets.Count);
        Assert.AreSame(pendingTarget, resolution.Targets[0]);
        Assert.AreEqual(1, resolution.MixedScopeDroppedCount);
    }

    [TestMethod]
    public void ChartOperationTarget_NewlyInstalledBmson_UsesInstalledPathAndLibraryCapabilities()
    {
        var original = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Pending\\Package\\chart.bmson",
            folder = "C:\\Pending\\Package",
            title = "Installed Bmson",
            artist = "Artist",
            md5 = "67676767676767676767676767676767",
            sha256 = new string('6', 64)
        };
        var installed = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Library\\Package\\chart.bmson",
            folder = "C:\\Library\\Package",
            title = original.title,
            artist = original.artist,
            md5 = original.md5,
            sha256 = original.sha256
        };
        var row = LibraryChartRow.FromBmsonSong(installed);

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, ChartOperationSourceScope.NewlyInstalledPackage, out ChartOperationTarget target));

        Assert.AreEqual(ChartOperationSourceScope.NewlyInstalledPackage, target.SourceScope);
        Assert.IsTrue(target.IsOwned);
        Assert.IsFalse(target.IsPending);
        Assert.AreEqual(installed.path, target.Chart.Path);
        Assert.AreSame(installed, target.Chart.GetBmsonStorageOwner());
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.OpenFile));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.OpenFolder));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RemoveFromLibrary));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.MoveInLibrary));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.UpdateInstallDestination));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RepairInstalledLocation));
        Assert.AreEqual(installed.path, LibraryChartRef.FromChartFile(target.Chart).Path);
    }

    [TestMethod]
    public void ChartOperationTarget_BmsRow_HasBmsOnlyCapabilities()
    {
        var file = new TestableBmsFile();
        file.ApplySnapshot("abababababababababababababababab", "Bms", 7);
        file.SetSha256(new string('a', 64));

        var row = LibraryChartRow.FromBmsFile(file);

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, out ChartOperationTarget target));
        Assert.AreEqual(ChartFileKind.Bms, target.Chart.Kind);
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.UseLr2Ir));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.UseScoreViewer));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.UpdateRanking));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RunResourceHealthCheck));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RunBmsEncodingFix));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RunZeroNoteCheck));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RenameInvalidExtension));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.ConvertToAudio));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RepairInstalledLocation));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.UpdateInstallDestination));
    }

    [TestMethod]
    public void ChartOperationTarget_CapabilityMatrix_SeparatesBmsOnlyAndBmsonCommonOperations()
    {
        var bms = new TestableBmsFile();
        bms.ApplySnapshot("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Bms", 7);
        bms.SetSha256(new string('a', 64));
        var bmson = LibraryChartRow.FromBmsonSong(new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\chart.bmson",
            folder = "C:\\Songs\\Bmson",
            title = "Bmson",
            artist = "Artist",
            md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            sha256 = new string('b', 64)
        });
        var pendingBmson = LibraryChartRow.FromPackageChartEntry(PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Pending\\Bmson\\chart.bmson",
            folder = "C:\\Pending\\Bmson",
            title = "Pending Bmson",
            artist = "Artist",
            md5 = "cccccccccccccccccccccccccccccccc",
            sha256 = new string('c', 64)
        })));

        var bmsRow = LibraryChartRow.FromBmsFile(bms);

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(bmsRow, out ChartOperationTarget bmsTarget));
        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(bmson, out ChartOperationTarget bmsonTarget));
        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(pendingBmson, ChartOperationSourceScope.PendingPackage, out ChartOperationTarget pendingBmsonTarget));

        foreach (ChartOperationCapabilities commonCapability in new[]
        {
            ChartOperationCapabilities.OpenFile,
            ChartOperationCapabilities.OpenFolder,
            ChartOperationCapabilities.OpenRepositoryBySha256,
            ChartOperationCapabilities.RunResourceHealthCheck
        })
        {
            Assert.IsTrue(bmsTarget.HasCapability(commonCapability), commonCapability + " should apply to BMS.");
            Assert.IsTrue(bmsonTarget.HasCapability(commonCapability), commonCapability + " should apply to owned bmson.");
            Assert.IsTrue(pendingBmsonTarget.HasCapability(commonCapability), commonCapability + " should apply to pending bmson.");
        }

        foreach (ChartOperationCapabilities bmsOnlyCapability in new[]
        {
            ChartOperationCapabilities.UseLr2Ir,
            ChartOperationCapabilities.UseScoreViewer,
            ChartOperationCapabilities.UpdateRanking,
            ChartOperationCapabilities.RunBmsEncodingFix,
            ChartOperationCapabilities.RunZeroNoteCheck,
            ChartOperationCapabilities.RenameInvalidExtension,
            ChartOperationCapabilities.ConvertToAudio
        })
        {
            Assert.IsTrue(bmsTarget.HasCapability(bmsOnlyCapability), bmsOnlyCapability + " should apply to BMS.");
            Assert.IsFalse(bmsonTarget.HasCapability(bmsOnlyCapability), bmsOnlyCapability + " must not apply to owned bmson.");
            Assert.IsFalse(pendingBmsonTarget.HasCapability(bmsOnlyCapability), bmsOnlyCapability + " must not apply to pending bmson.");
        }

        Assert.IsTrue(bmsonTarget.HasCapability(ChartOperationCapabilities.MoveInLibrary));
        Assert.IsTrue(bmsonTarget.HasCapability(ChartOperationCapabilities.RemoveFromLibrary));
        Assert.IsTrue(bmsonTarget.HasCapability(ChartOperationCapabilities.RepairInstalledLocation));
        Assert.IsFalse(bmsonTarget.HasCapability(ChartOperationCapabilities.UpdateInstallDestination));
        Assert.IsTrue(bmsTarget.HasCapability(ChartOperationCapabilities.MoveInLibrary));
        Assert.IsTrue(bmsTarget.HasCapability(ChartOperationCapabilities.RemoveFromLibrary));
        Assert.IsTrue(bmsTarget.HasCapability(ChartOperationCapabilities.RepairInstalledLocation));
        Assert.IsFalse(bmsTarget.HasCapability(ChartOperationCapabilities.UpdateInstallDestination));
        Assert.IsTrue(pendingBmsonTarget.HasCapability(ChartOperationCapabilities.UpdateInstallDestination));
        Assert.IsFalse(pendingBmsonTarget.HasCapability(ChartOperationCapabilities.RepairInstalledLocation));
        Assert.IsFalse(pendingBmsonTarget.HasCapability(ChartOperationCapabilities.MoveInLibrary));
        Assert.IsFalse(pendingBmsonTarget.HasCapability(ChartOperationCapabilities.RemoveFromLibrary));
    }

    [TestMethod]
    public void FolderEditChartOperationTarget_UsesBmsonChartForOwnedRowsAndRejectsPendingRows()
    {
        var ownedBmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\chart.bmson",
            folder = "C:\\Songs\\Bmson",
            title = "Owned Bmson",
            artist = "Artist",
            md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            sha256 = new string('b', 64)
        };
        var pendingBmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Pending\\Bmson\\chart.bmson",
            folder = "C:\\Pending\\Bmson",
            title = "Pending Bmson",
            artist = "Artist",
            md5 = "cccccccccccccccccccccccccccccccc",
            sha256 = new string('c', 64)
        };
        PackageChartEntry pendingEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(pendingBmson));

        var ownedRow = LibraryChartRow.FromBmsonSong(ownedBmson);
        var pendingRow = LibraryChartRow.FromPackageChartEntry(pendingEntry);

        Assert.IsFalse(GridRowResolver.TryGetBmsPlayerFile(ownedRow, out _));
        Assert.IsTrue(GridRowResolver.TryGetFolderEditChartOperationTarget(ownedRow, ChartOperationSourceScope.Library, out ChartOperationTarget ownedTarget));
        Assert.AreEqual(ChartFileKind.Bmson, ownedTarget.Chart.Kind);
        Assert.AreEqual(ownedBmson.path, ownedTarget.Chart.Path);
        Assert.AreSame(ownedBmson, ownedTarget.Chart.GetBmsonStorageOwner());
        Assert.IsFalse(GridRowResolver.TryGetFolderEditChartOperationTarget(pendingRow, ChartOperationSourceScope.PendingPackage, out ChartOperationTarget pendingTarget));
        Assert.IsNull(pendingTarget);
    }

    [TestMethod]
    public void ChartOperationTarget_PendingBms_SeparatesInstallDestinationFromInstalledRepair()
    {
        var file = new TestableBmsFile();
        file.ApplySnapshot("abababababababababababababababab", "Pending Bms", 7);

        var row = LibraryChartRow.FromPackageChartEntry(PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(file)));

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, ChartOperationSourceScope.PendingPackage, out ChartOperationTarget target));

        Assert.AreEqual(ChartFileKind.Bms, target.Chart.Kind);
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.UpdateInstallDestination));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.RepairInstalledLocation));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RunResourceHealthCheck));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RunBmsEncodingFix));
    }

    [TestMethod]
    public void ChartOperationTarget_ChartNamedApis_TreatRawBmsAsBmsPlayerOnlyBoundary()
    {
        var file = new TestableBmsFile();
        file.ApplySnapshot("abababababababababababababababab", "Bms", 7);

        Assert.IsFalse(GridRowResolver.TryGetChartFile(file, out _));
        Assert.IsFalse(GridRowResolver.TryGetChartOperationTarget(file, out _));
        Assert.IsTrue(GridRowResolver.TryGetBmsPlayerFile(file, out BMSFile playerFile));
        Assert.AreSame(file, playerFile);

        var row = LibraryChartRow.FromBmsFile(file);
        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, out ChartOperationTarget target));
        Assert.AreEqual(ChartFileKind.Bms, target.Chart.Kind);
        Assert.AreSame(file, target.Chart.GetBmsStorageOwner());
    }

    private static void AssertMainViewOperationContext(
        MainViewUpdateMode mode,
        MainWindowViewModel.MainViewOperationSection expectedSection,
        ChartOperationSourceScope expectedScope)
    {
        MainWindowViewModel.MainViewOperationSection section = MainWindowViewModel.ResolveMainViewOperationSection(mode);

        Assert.AreEqual(expectedSection, section);
        Assert.AreEqual(expectedScope, MainWindowViewModel.ResolveMainViewChartOperationSourceScope(section));
    }

    [TestMethod]
    public void ChartOperationTarget_PlaylistMissing_DoesNotAllowLocalFileOperations()
    {
        var entry = new TestablePlaylistEntry();
        entry.SetTitle("Missing");
        entry.SetMd5("abababababababababababababababab");
        entry.lr2_bmsid = "12345";
        PlaylistDetailRow row = new PlaylistDetailSourceRow(entry, resolvedChart: null).CreateViewRow();

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, out ChartOperationTarget target));
        Assert.AreEqual(ChartFileKind.Bms, target.Chart.Kind);
        Assert.IsFalse(target.IsOwned);
        Assert.IsTrue(target.IsPlaylistMissing);
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.OpenFile));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.OpenFolder));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.MoveInLibrary));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.RemoveFromLibrary));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.RepairInstalledLocation));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.UseLr2Ir));
        Assert.IsNull(target.ToLibraryChartRef());
    }

    [TestMethod]
    public void GridRowResolver_TreatsPlaylistSourceRowAsPlaylistEntryRow()
    {
        var bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\chart.bmson",
            folder = "C:\\Songs\\Bmson",
            title = "Owned Bmson",
            md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            sha256 = new string('b', 64)
        };
        var entry = new TestablePlaylistEntry();
        entry.SetMd5(bmson.md5);
        entry.SetSha256(bmson.sha256);
        var sourceRow = new PlaylistDetailSourceRow(entry, ChartFileProjection.FromBmsonSong(bmson, includeWarningSnapshot: false));

        Assert.IsTrue(GridRowResolver.IsPlaylistRow(sourceRow));
        Assert.AreSame(entry, GridRowResolver.GetPlaylistEntry(sourceRow));
        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(sourceRow, out ChartOperationTarget target));
        Assert.AreSame(entry, target.PlaylistEntry);
        Assert.AreEqual(ChartOperationSourceScope.PlaylistOwned, target.SourceScope);
        Assert.IsFalse(target.IsPlaylistMissing);
        Assert.AreEqual(ChartFileKind.Bmson, target.Chart.Kind);
    }

    [TestMethod]
    public void PlaylistDetailContextMenuPolicy_OwnedRowsAllowEntryAndFileDeleteButMissingRowsAllowEntryOnly()
    {
        var bms = new TestableBmsFile();
        bms.ApplySnapshot("abababababababababababababababab", "Owned Bms", 7);
        PlaylistDetailRow ownedBmsRow = new PlaylistDetailSourceRow(new TestablePlaylistEntry(bms), ChartFileProjection.FromBmsFile(bms)).CreateViewRow();

        var bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\chart.bmson",
            folder = "C:\\Songs\\Bmson",
            title = "Owned Bmson",
            md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            sha256 = new string('b', 64)
        };
        var bmsonEntry = new TestablePlaylistEntry();
        bmsonEntry.SetMd5(bmson.md5);
        bmsonEntry.SetSha256(bmson.sha256);
        PlaylistDetailRow ownedBmsonRow = new PlaylistDetailSourceRow(bmsonEntry, ChartFileProjection.FromBmsonSong(bmson, includeWarningSnapshot: false)).CreateViewRow();

        var missingEntry = new TestablePlaylistEntry();
        missingEntry.SetMd5("cccccccccccccccccccccccccccccccc");
        PlaylistDetailRow missingRow = new PlaylistDetailSourceRow(missingEntry, resolvedChart: null).CreateViewRow();

        AssertPlaylistRowFileDeletePolicy(ownedBmsRow, expectedRemoveFromLibrary: true);
        AssertPlaylistRowFileDeletePolicy(ownedBmsonRow, expectedRemoveFromLibrary: true);
        AssertPlaylistRowFileDeletePolicy(missingRow, expectedRemoveFromLibrary: false);
    }

    [TestMethod]
    public void RootFolderDropEntryPolicy_PreservesOnlyMissingPlaylistRows()
    {
        var bms = new TestableBmsFile();
        bms.ApplySnapshot("abababababababababababababababab", "Owned Bms", 7);
        PlaylistDetailRow ownedBmsRow = new PlaylistDetailSourceRow(new TestablePlaylistEntry(bms), ChartFileProjection.FromBmsFile(bms)).CreateViewRow();

        var bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\chart.bmson",
            folder = "C:\\Songs\\Bmson",
            title = "Owned Bmson",
            md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            sha256 = new string('b', 64)
        };
        var bmsonEntry = new TestablePlaylistEntry();
        bmsonEntry.SetMd5(bmson.md5);
        bmsonEntry.SetSha256(bmson.sha256);
        PlaylistDetailRow ownedBmsonRow = new PlaylistDetailSourceRow(bmsonEntry, ChartFileProjection.FromBmsonSong(bmson, includeWarningSnapshot: false)).CreateViewRow();

        var missingEntry = new TestablePlaylistEntry();
        missingEntry.SetMd5("cccccccccccccccccccccccccccccccc");
        PlaylistDetailRow missingRow = new PlaylistDetailSourceRow(missingEntry, resolvedChart: null).CreateViewRow();

        Assert.IsFalse(MainWindowViewModel.ShouldPreservePlaylistEntryForRootFolderDrop(ownedBmsRow));
        Assert.IsFalse(MainWindowViewModel.ShouldPreservePlaylistEntryForRootFolderDrop(ownedBmsonRow));
        Assert.IsTrue(MainWindowViewModel.ShouldPreservePlaylistEntryForRootFolderDrop(missingRow));
        Assert.AreEqual(ChartFileKind.Bmson, MainWindowViewModel.ResolvePlaylistDropChart(ownedBmsonRow).Kind);
        Assert.IsNull(MainWindowViewModel.ResolvePlaylistDropChart(missingRow));
    }

    [TestMethod]
    public void RootFolderDropEntryPolicy_SourceRowsPreservesOnlyMissingPlaylistRows()
    {
        var bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\chart.bmson",
            folder = "C:\\Songs\\Bmson",
            title = "Owned Bmson",
            md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            sha256 = new string('b', 64)
        };
        var bmsonEntry = new TestablePlaylistEntry();
        bmsonEntry.SetMd5(bmson.md5);
        bmsonEntry.SetSha256(bmson.sha256);
        var ownedBmsonSourceRow = new PlaylistDetailSourceRow(bmsonEntry, ChartFileProjection.FromBmsonSong(bmson, includeWarningSnapshot: false));

        var missingEntry = new TestablePlaylistEntry();
        missingEntry.SetMd5("cccccccccccccccccccccccccccccccc");
        var missingSourceRow = new PlaylistDetailSourceRow(missingEntry, resolvedChart: null);

        Assert.IsFalse(MainWindowViewModel.ShouldPreservePlaylistEntryForRootFolderDrop(ownedBmsonSourceRow));
        Assert.IsTrue(MainWindowViewModel.ShouldPreservePlaylistEntryForRootFolderDrop(missingSourceRow));
        Assert.AreEqual(ChartFileKind.Bmson, MainWindowViewModel.ResolvePlaylistDropChart(ownedBmsonSourceRow).Kind);
        Assert.IsNull(MainWindowViewModel.ResolvePlaylistDropChart(missingSourceRow));
    }

    [TestMethod]
    public void PlaylistDropCandidatePolicy_RejectsPlaylistSummaryRows()
    {
        var summaryRow = new PlaylistSummaryRow
        {
            Name = "Summary",
            TableRef = new BMSTable()
        };

        Assert.IsFalse(MainWindowViewModel.IsPlaylistDropCandidateRow(summaryRow));
        Assert.IsFalse(MainWindowViewModel.ArePlaylistDropCandidateRows([summaryRow]));
    }

    [TestMethod]
    public void PlaylistDropCandidatePolicy_AcceptsChartAndPlaylistDetailRows()
    {
        var bms = new TestableBmsFile();
        bms.ApplySnapshot("abababababababababababababababab", "Owned Bms", 7);
        LibraryChartRow libraryRow = LibraryChartRow.FromBmsFile(bms);
        PlaylistDetailRow playlistRow = new PlaylistDetailSourceRow(new TestablePlaylistEntry(bms), ChartFileProjection.FromBmsFile(bms)).CreateViewRow();

        Assert.IsTrue(MainWindowViewModel.IsPlaylistDropCandidateRow(libraryRow));
        Assert.IsTrue(MainWindowViewModel.IsPlaylistDropCandidateRow(playlistRow));
        Assert.IsTrue(MainWindowViewModel.ArePlaylistDropCandidateRows([libraryRow, playlistRow]));
        Assert.IsFalse(MainWindowViewModel.ArePlaylistDropCandidateRows([libraryRow, new PlaylistSummaryRow()]));
    }

    [TestMethod]
    public void PlayHistoryRowOperationPolicy_AllowsPlaylistDropOnlyWhenResolved()
    {
        PlayHistoryRow resolvedRow = CreateResolvedPlayHistoryRow();
        PlayHistoryRow unresolvedRow = CreateUnresolvedPlayHistoryRow();

        Assert.IsNotNull(resolvedRow.ResolvedChart);
        Assert.IsNull(unresolvedRow.ResolvedChart);
        Assert.IsTrue(MainWindowViewModel.IsPlaylistDropCandidateRow(resolvedRow));
        Assert.IsFalse(MainWindowViewModel.IsPlaylistDropCandidateRow(unresolvedRow));
        Assert.IsTrue(MainWindowViewModel.ArePlaylistDropCandidateRows([resolvedRow]));
        Assert.IsFalse(MainWindowViewModel.ArePlaylistDropCandidateRows([resolvedRow, unresolvedRow]));
        Assert.AreSame(resolvedRow.ResolvedChart, MainWindowViewModel.ResolvePlaylistDropChart(resolvedRow));
        Assert.IsNull(MainWindowViewModel.ResolvePlaylistDropChart(unresolvedRow));
        Assert.IsFalse(GridRowResolver.IsPlaylistRow(resolvedRow));
        Assert.IsNull(GridRowResolver.GetPlaylistEntry(resolvedRow));
        Assert.IsFalse(GridRowResolver.TryGetBmsPlayerFile(resolvedRow, out _));
        Assert.IsFalse(GridRowResolver.TryGetChartFile(resolvedRow, out _));
        Assert.IsFalse(GridRowResolver.TryGetChartOperationTarget(resolvedRow, out _));
        Assert.IsFalse(GridRowResolver.TryGetFolderEditChartOperationTarget(resolvedRow, ChartOperationSourceScope.Library, out _));
    }

    [TestMethod]
    public void PlaylistSummaryBmtSortDrop_FilteredRowsPreservesHiddenRows()
    {
        List<BMSTable> fullOrder = CreateBmtSortTables(1, 2, 3, 4, 5);
        PlaylistSummaryRow[] visibleRows =
        [
            CreatePlaylistSummaryRow(fullOrder[0]),
            CreatePlaylistSummaryRow(fullOrder[3]),
            CreatePlaylistSummaryRow(fullOrder[4])
        ];
        PlaylistSummaryRow[] draggedRows = [visibleRows[2]];

        List<BMSTable> result = MainWindowViewModel.BuildBmtSortOrderByVisibleDrop(fullOrder, visibleRows, draggedRows, visibleInsertIndex: 1);

        CollectionAssert.AreEqual(new[] { 1, 5, 2, 3, 4 }, result.Select(table => table.playlist_id.GetValueOrDefault()).ToArray());
    }

    [TestMethod]
    public void PlaylistSummaryBmtSortApplyCurrentOrder_ReplacesOnlyVisibleSlots()
    {
        List<BMSTable> fullOrder = CreateBmtSortTables(1, 2, 3, 4, 5);
        PlaylistSummaryRow[] visibleRows =
        [
            CreatePlaylistSummaryRow(fullOrder[4]),
            CreatePlaylistSummaryRow(fullOrder[0]),
            CreatePlaylistSummaryRow(fullOrder[3])
        ];

        List<BMSTable> result = MainWindowViewModel.BuildBmtSortOrderByReplacingVisibleSlots(fullOrder, visibleRows);

        CollectionAssert.AreEqual(new[] { 5, 2, 3, 1, 4 }, result.Select(table => table.playlist_id.GetValueOrDefault()).ToArray());
    }

    [TestMethod]
    public void PlaylistSummaryBmtSortMoveToBottom_KeepsSelectionOrder()
    {
        List<BMSTable> fullOrder = CreateBmtSortTables(1, 2, 3, 4, 5);
        PlaylistSummaryRow[] selectedRows =
        [
            CreatePlaylistSummaryRow(fullOrder[1]),
            CreatePlaylistSummaryRow(fullOrder[3])
        ];

        List<BMSTable> result = MainWindowViewModel.BuildBmtSortOrderByMovingRows(fullOrder, selectedRows, insertAtTop: false);

        CollectionAssert.AreEqual(new[] { 1, 3, 5, 2, 4 }, result.Select(table => table.playlist_id.GetValueOrDefault()).ToArray());
    }

    [TestMethod]
    public void AddBMSTableEntriesToFolder_EmptyInputDoesNotTouchPlaylist()
    {
        DateTime lastUpdate = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Local);
        var table = new BMSTable
        {
            last_update = lastUpdate,
            entries = [new BMSTableEntry(DynamicJson.Parse("{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Existing\",\"level\":\"A\"}"))]
        };
        int entriesRevision = table.PlaylistEntriesRevision;
        List<BMSTableEntry> entriesBefore = [.. table.entries];

        table.AddBMSTableEntriesToFolder([], "New Folder");

        Assert.AreEqual(lastUpdate, table.last_update);
        Assert.AreEqual(entriesRevision, table.PlaylistEntriesRevision);
        CollectionAssert.AreEqual(entriesBefore, table.entries.ToList());
    }

    [TestMethod]
    public void PlaylistDetailSourceRow_AfterBmsRemoval_RematerializesEntryAsMissingNoSong()
    {
        var file = new TestableBmsFile();
        file.ApplySnapshot("abababababababababababababababab", "Owned Bms", 7);
        var entry = new TestablePlaylistEntry(file);

        var ownedSource = new PlaylistDetailSourceRow(entry, ChartFileProjection.FromBmsFile(file));
        var missingSource = new PlaylistDetailSourceRow(entry, resolvedChart: null);
        PlaylistDetailRow missingRow = missingSource.CreateViewRow();

        Assert.IsTrue(ownedSource.IsOwned);
        Assert.IsFalse(missingSource.IsOwned);
        Assert.AreEqual(ClearType.NO_SONG, missingSource.clear);
        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(missingRow, out ChartOperationTarget target));
        Assert.IsTrue(target.IsPlaylistMissing);
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.RemoveFromLibrary));
    }

    [TestMethod]
    public void PlaylistDetailSourceRow_AfterBmsonRemoval_RematerializesEntryAsMissingNoSong()
    {
        var bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\chart.bmson",
            folder = "C:\\Songs\\Bmson",
            title = "Owned Bmson",
            md5 = "dddddddddddddddddddddddddddddddd",
            sha256 = new string('d', 64)
        };
        var entry = new TestablePlaylistEntry();
        entry.SetMd5(bmson.md5);
        entry.SetSha256(bmson.sha256);

        var ownedSource = new PlaylistDetailSourceRow(entry, ChartFileProjection.FromBmsonSong(bmson, includeWarningSnapshot: false));
        var missingSource = new PlaylistDetailSourceRow(entry, resolvedChart: null);
        PlaylistDetailRow missingRow = missingSource.CreateViewRow();

        Assert.IsTrue(ownedSource.IsOwned);
        Assert.IsFalse(missingSource.IsOwned);
        Assert.AreEqual(ClearType.NO_SONG, missingSource.clear);
        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(missingRow, out ChartOperationTarget target));
        Assert.IsTrue(target.IsPlaylistMissing);
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.RemoveFromLibrary));
    }

    [TestMethod]
    public void ChartOperationTarget_MissingSha256_DisablesRepositoryCapability()
    {
        var file = new TestableBmsFile();
        file.ApplySnapshot("abababababababababababababababab", "NoSha", 7);

        var row = LibraryChartRow.FromBmsFile(file);

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, out ChartOperationTarget target));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.OpenRepositoryBySha256));
    }

    [TestMethod]
    public void PlaylistDetailSourceRow_MissingWithoutResolvedFiles_PreservesPlaylistEntryFallbackValues()
    {
        var entry = new TestablePlaylistEntry
        {
            folder = "EntryFolder",
            level = 12
        };
        entry.SetTitle("EntryTitle");
        entry.SetArtist("EntryArtist");
        entry.SetMd5("abababababababababababababababab");
        entry.SetSha256(new string('c', 64));

        var sourceRow = new PlaylistDetailSourceRow(entry, resolvedChart: null);

        Assert.IsFalse(sourceRow.IsOwned);
        Assert.AreEqual("EntryTitle", sourceRow.Title);
        Assert.AreEqual("EntryArtist", sourceRow.Artist);
        Assert.AreEqual("EntryFolder", sourceRow.Folder);
        Assert.AreEqual("12", sourceRow.Level);
        Assert.AreEqual(entry.md5, sourceRow.hash);
        Assert.AreEqual(entry.sha256, sourceRow.sha256);
        Assert.AreEqual(ClearType.NO_SONG, sourceRow.clear);
    }

    [TestMethod]
    public void PlaylistDetailSourceRow_MissingWithEntryChartInfo_DisplaysMetadataWithoutChangingOwnership()
    {
        LR2SongDBExtended.chart_info chartInfo = CreateChartInfo(
            sha256: new string('d', 64),
            md5: "abababababababababababababababab",
            level: 12,
            notes: 2500,
            total: 777.5);
        var entry = new TestablePlaylistEntry();
        entry.SetTitle("Missing");
        entry.SetMd5(chartInfo.md5);
        entry.SetSha256(chartInfo.sha256);

        var sourceRow = new PlaylistDetailSourceRow(entry, resolvedChart: null, entryChartInfo: chartInfo);
        PlaylistDetailRow row = sourceRow.CreateViewRow();

        Assert.IsFalse(sourceRow.IsOwned);
        Assert.AreEqual(ClearType.NO_SONG, sourceRow.clear);
        Assert.AreSame(chartInfo, sourceRow.EntryChartInfo);
        Assert.AreSame(chartInfo, sourceRow.ChartInfo);
        Assert.AreSame(chartInfo, sourceRow.Chart.ChartInfo);
        Assert.AreSame(sourceRow.Chart, row.Chart);
        Assert.IsTrue(GridRowResolver.TryGetChartFile(row, out ChartFile rowChart));
        Assert.AreSame(row.Chart, rowChart);
        Assert.AreSame(chartInfo, rowChart.ChartInfo);
        Assert.AreEqual(chartInfo.sha256, sourceRow.sha256);
        Assert.AreEqual(chartInfo.sha256, GridRowResolver.GetRepositorySha256(row));
        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, out ChartOperationTarget target));
        Assert.IsTrue(target.IsPlaylistMissing);
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.OpenRepositoryBySha256));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.OpenFile));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.RunResourceHealthCheck));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.MoveInLibrary));
        Assert.AreEqual("12", row.ChartLevelText);
        Assert.AreEqual("ANOTHER", row.ChartDifficultyText);
        Assert.AreEqual(2500, row.ChartNotes);
        Assert.AreEqual("777.5", row.ChartTotalText);
    }

    [TestMethod]
    public void PlaylistDetailSourceRow_MissingWithChartInfoFallback_UsesChartInfoSha256()
    {
        LR2SongDBExtended.chart_info chartInfo = CreateChartInfo(new string('a', 64), "abababababababababababababababab");
        var entry = new TestablePlaylistEntry();
        entry.SetTitle("MissingShaFallback");
        entry.SetMd5(chartInfo.md5);

        PlaylistDetailRow row = new PlaylistDetailSourceRow(entry, resolvedChart: null, entryChartInfo: chartInfo).CreateViewRow();

        Assert.AreEqual(chartInfo.sha256, row.sha256);
        Assert.AreEqual(chartInfo.sha256, GridRowResolver.GetRepositorySha256(row));
    }

    [TestMethod]
    public void PlaylistDetailSourceRow_MissingEntryChartInfoPatchCreatesCopyForViewRematerialize()
    {
        LR2SongDBExtended.chart_info oldInfo = CreateChartInfo(new string('e', 64), "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", level: 3, notes: 500, total: 100);
        LR2SongDBExtended.chart_info newInfo = CreateChartInfo(new string('e', 64), "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", level: 12, notes: 2500, total: 500);
        PlaylistDetailSourceRow sourceRow = CreateMissingSourceRow("PatchChartInfo", oldInfo);

        Assert.IsTrue(sourceRow.HasEntryChartInfoDependency);
        Assert.AreEqual("3", sourceRow.ChartLevelText);
        Assert.AreEqual(500, sourceRow.ChartNotes);

        PlaylistDetailSourceRow patchedSourceRow = sourceRow.WithEntryChartInfo(newInfo);

        Assert.AreSame(oldInfo, sourceRow.EntryChartInfo);
        Assert.AreEqual("3", sourceRow.ChartLevelText);
        PlaylistDetailRow viewRow = patchedSourceRow.CreateViewRow();
        Assert.AreSame(newInfo, patchedSourceRow.EntryChartInfo);
        Assert.AreSame(newInfo, patchedSourceRow.ChartInfo);
        Assert.AreSame(newInfo, patchedSourceRow.Chart.ChartInfo);
        Assert.AreSame(patchedSourceRow.Chart, viewRow.Chart);
        Assert.IsTrue(GridRowResolver.TryGetChartFile(viewRow, out ChartFile viewChart));
        Assert.AreSame(viewRow.Chart, viewChart);
        Assert.AreSame(newInfo, viewChart.ChartInfo);
        Assert.AreEqual(newInfo.sha256, viewRow.sha256);
        Assert.AreEqual(newInfo.sha256, GridRowResolver.GetRepositorySha256(viewRow));
        Assert.AreEqual("12", viewRow.ChartLevelText);
        Assert.AreEqual(2500, viewRow.ChartNotes);
        Assert.AreEqual("500", viewRow.ChartTotalText);
    }

    [TestMethod]
    public void PlaylistDetailSourceRow_ChartInfoCopyDoesNotInvokeProjectionProvider()
    {
        LR2SongDBExtended.chart_info oldInfo = CreateChartInfo(new string('e', 64), "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", level: 3);
        LR2SongDBExtended.chart_info newInfo = CreateChartInfo(new string('e', 64), "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", level: 12);
        var entry = new TestablePlaylistEntry();
        entry.SetTitle("ProviderFreePatch");
        entry.SetMd5(oldInfo.md5);
        entry.SetSha256(oldInfo.sha256);
        var resolvedChart = new ChartFile(
            ChartFileKind.Bms,
            string.Empty,
            oldInfo.md5,
            oldInfo.sha256,
            "ProviderFreePatch",
            "ProviderFreePatch",
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            "3",
            3,
            7,
            oldInfo,
            null,
            null,
            null);
        int projectionCount = 0;
        var sourceRow = new PlaylistDetailSourceRow(
            entry,
            resolvedChart,
            entryChartInfo: oldInfo,
            chartInfoProjectionProvider: _ =>
            {
                projectionCount++;
                return oldInfo;
            });
        int constructionProjectionCount = projectionCount;

        PlaylistDetailSourceRow patchedSourceRow = sourceRow.WithEntryChartInfo(newInfo);

        Assert.AreEqual(constructionProjectionCount, projectionCount);
        Assert.AreSame(oldInfo, sourceRow.EntryChartInfo);
        Assert.AreSame(newInfo, patchedSourceRow.EntryChartInfo);
        Assert.AreSame(newInfo, patchedSourceRow.Chart.ChartInfo);
    }

    [TestMethod]
    public void PlaylistDetailSourceRow_OwnedBmsChartInfoFollowsProjectionProviderAfterHydration()
    {
        var file = new TestableBmsFile();
        file.ApplySnapshot("abababababababababababababababab", "Owned Bms", 7);
        LR2SongDBExtended.chart_info chartInfo = CreateChartInfo(new string('b', 64), file.hash, level: 13, notes: 3333, total: 700);
        var sourceRow = new PlaylistDetailSourceRow(
            new TestablePlaylistEntry(file),
            ChartFileProjection.FromBmsFile(file),
            chartInfoProjectionProvider: CreateChartInfoProvider(chartInfo));

        Assert.AreSame(chartInfo, sourceRow.ChartInfo);
        Assert.AreEqual(13, sourceRow.ChartLevelSortKey);
        Assert.AreEqual(3333, sourceRow.ChartNotes);
        PlaylistDetailRow viewRow = sourceRow.CreateViewRow();
        Assert.AreSame(chartInfo, viewRow.Chart.ChartInfo);
        Assert.AreEqual("13", viewRow.ChartLevelText);
        Assert.AreEqual(3333, viewRow.ChartNotes);
    }

    [TestMethod]
    public void PlaylistDetailSourceRow_OwnedBmsonChartInfoFollowsProjectionProviderAfterHydration()
    {
        var song = new LR2SongDBExtended.bmson_song
        {
            path = @"C:\Songs\OwnedBmson\chart.bmson",
            title = "Owned Bmson",
            artist = "Bmson Artist",
            mode_hint = "beat-7k",
            level = 7,
            md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            sha256 = new string('c', 64)
        };
        var entry = new TestablePlaylistEntry();
        entry.SetTitle(song.title);
        entry.SetMd5(song.md5);
        entry.SetSha256(song.sha256);
        LR2SongDBExtended.chart_info chartInfo = CreateChartInfo(new string('d', 64), song.md5, level: 14, notes: 4444, total: 800);
        var sourceRow = new PlaylistDetailSourceRow(
            entry,
            ChartFileProjection.FromBmsonSong(song),
            chartInfoProjectionProvider: CreateChartInfoProvider(chartInfo));

        Assert.AreSame(chartInfo, sourceRow.ChartInfo);
        Assert.AreEqual(14, sourceRow.ChartLevelSortKey);
        Assert.AreEqual(4444, sourceRow.ChartNotes);
        PlaylistDetailRow viewRow = sourceRow.CreateViewRow();
        Assert.AreSame(chartInfo, viewRow.Chart.ChartInfo);
        Assert.AreEqual("14", viewRow.ChartLevelText);
        Assert.AreEqual(4444, viewRow.ChartNotes);
    }

    [TestMethod]
    public void PlaylistDetailSourceRow_OwnedBmsonProjectionPreservesMetadataWithTransientProvider()
    {
        var song = new LR2SongDBExtended.bmson_song
        {
            path = @"C:\Songs\OwnedBmsonProjection\chart.bmson",
            title = "Storage Title",
            artist = "Storage Artist",
            mode_hint = "beat-7k",
            level = 7,
            md5 = "cccccccccccccccccccccccccccccccc",
            sha256 = new string('e', 64)
        };
        ChartFile projectedChart = new(
            ChartFileKind.Bmson,
            song.path,
            song.md5,
            song.sha256,
            "Projected Title",
            "Projected Raw Title",
            "Projected Artist",
            "Projected Genre",
            "Projected Folder",
            "Projected Tag",
            "12",
            12d,
            14,
            chartInfo: null,
            bmsFile: null,
            bmsonSong: song,
            installDestination: @"C:\Installed\Projected",
            warnings: [ChartWarning.Create(ChartWarningKind.InstallEstimationAmbiguous, "projected warning")]);
        var entry = new TestablePlaylistEntry();
        entry.SetMd5(song.md5);
        entry.SetSha256(song.sha256);
        LR2SongDBExtended.chart_info chartInfo = CreateChartInfo(new string('f', 64), song.md5, level: 15, notes: 5555, total: 900);

        var sourceRow = new PlaylistDetailSourceRow(
            entry,
            projectedChart,
            chartTransientStateProvider: (_, _) => ChartFileTransientState.Empty,
            chartInfoProjectionProvider: CreateChartInfoProvider(chartInfo));
        PlaylistDetailRow row = sourceRow.CreateViewRow();

        Assert.AreEqual("Projected Title", row.Title);
        Assert.AreEqual("Projected Artist", row.Artist);
        Assert.AreEqual("Projected Genre", row.genre);
        Assert.AreEqual("Projected Tag", row.tag);
        Assert.AreEqual(14, row.mode);
        Assert.AreEqual("12", row.Level);
        Assert.AreEqual(@"C:\Installed\Projected", row.instl_dst);
        Assert.AreEqual("projected warning", row.DisplayWarning);
        Assert.AreSame(chartInfo, row.Chart.ChartInfo);
        Assert.AreEqual("15", row.ChartLevelText);
        Assert.AreEqual(5555, row.ChartNotes);
    }

    [TestMethod]
    public void PlaylistDetailRow_MissingLevelEditRefreshesChartSnapshot()
    {
        var entry = new TestablePlaylistEntry();
        entry.SetTitle("MissingEditableLevel");
        entry.SetMd5("abababababababababababababababab");
        PlaylistDetailRow row = new PlaylistDetailSourceRow(entry, resolvedChart: null).CreateViewRow();

        row.Level = "13";

        Assert.IsTrue(GridRowResolver.TryGetChartFile(row, out ChartFile chart));
        Assert.AreSame(row.Chart, chart);
        Assert.AreEqual("13", chart.LevelText);
        Assert.AreEqual(13d, chart.Level);
        Assert.AreEqual(13d, row.Entry.level);
        Assert.AreEqual(13d, row.EntryLevelSortKey);
    }

    [TestMethod]
    public void ResolveChartInfoForPlaylistEntry_PrefersSha256ThenFallsBackToMd5()
    {
        var entry = new TestablePlaylistEntry();
        entry.SetMd5("abababababababababababababababab");
        entry.SetSha256(new string('c', 64));
        LR2SongDBExtended.chart_info shaMatch = CreateChartInfo(entry.sha256, "ffffffffffffffffffffffffffffffff", level: 12);
        LR2SongDBExtended.chart_info md5Match = CreateChartInfo(new string('d', 64), entry.md5, level: 3);
        var bySha256 = new Dictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase)
        {
            [shaMatch.sha256] = shaMatch
        };
        var byMd5 = new Dictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase)
        {
            [md5Match.md5] = md5Match
        };

        Assert.AreSame(shaMatch, MainWindowViewModel.ResolveChartInfoForPlaylistEntry(entry, byMd5, bySha256));

        var md5OnlyEntry = new TestablePlaylistEntry();
        md5OnlyEntry.SetMd5(md5Match.md5);
        Assert.AreSame(md5Match, MainWindowViewModel.ResolveChartInfoForPlaylistEntry(md5OnlyEntry, byMd5, bySha256));
    }

    [TestMethod]
    public void ApplyPlaylistViewFromSource_MissingChartInfoParticipatesInKeywordAndNumericSort()
    {
        LR2SongDBExtended.chart_info highNotesInfo = CreateChartInfo(new string('e', 64), "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", level: 12, notes: 2500, total: 500);
        LR2SongDBExtended.chart_info lowNotesInfo = CreateChartInfo(new string('f', 64), "ffffffffffffffffffffffffffffffff", level: 3, notes: 500, total: 100);
        PlaylistDetailSourceRow highNotesRow = CreateMissingSourceRow("HighNotes", highNotesInfo);
        PlaylistDetailSourceRow lowNotesRow = CreateMissingSourceRow("LowNotes", lowNotesInfo);

        List<PlaylistDetailRow> result = MainWindowViewModel.ApplyPlaylistViewFromSource(
            [highNotesRow, lowNotesRow],
            keywordFilter: "notes:>=2000 feature:random level:12",
            modeFilter: MainWindowViewModel.ModeFilterType.All,
            sortParameters: new MainWindowViewModel.cSortParameters { ColumnsName = nameof(PlaylistDetailRow.ChartNotes), Direction = ListSortDirection.Descending },
            out string _,
            out int keywordCount,
            out int modeCount,
            out long _,
            out long _,
            out long _,
            out long _);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("HighNotes", result[0].Title);
        Assert.AreEqual(2500, result[0].ChartNotes);
        Assert.AreEqual(1, keywordCount);
        Assert.AreEqual(1, modeCount);
        Assert.IsFalse(result[0].IsOwned);
    }

    [TestMethod]
    public void ResolveChartForPlaylistEntry_PrefersMd5BeforeSha256AndRepresentativePathOrder()
    {
        var entry = new TestablePlaylistEntry();
        entry.SetMd5("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        entry.SetSha256(new string('f', 64));
        var bmsMd5LaterPath = new TestableBmsFile();
        bmsMd5LaterPath.Apply("C:\\Songs\\Omega\\chart.bms", "BMS", "Artist", entry.md5);
        bmsMd5LaterPath.SetSha256(entry.sha256);

        var laterPath = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Zeta\\chart.bmson",
            md5 = entry.md5,
            sha256 = new string('1', 64)
        };
        var earlierPath = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Alpha\\chart.bmson",
            md5 = entry.md5,
            sha256 = new string('2', 64)
        };
        var shaOnly = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Sha\\chart.bmson",
            md5 = "99999999999999999999999999999999",
            sha256 = entry.sha256
        };

        PlaylistLibraryResolveIndexSnapshot md5Index = PlaylistLibraryResolveIndexSnapshot.FromLibraryChartRefs(
        [
            LibraryChartRef.FromBmsonSong(laterPath),
            LibraryChartRef.FromBmsonSong(earlierPath),
            LibraryChartRef.FromBmsFile(bmsMd5LaterPath)
        ]);
        LibraryChartRef preferred = md5Index.ChartsByMd5[entry.md5];
        LibraryChartRef resolvedMd5First = md5Index.ResolveChartForPlaylistEntry(entry);
        PlaylistLibraryResolveIndexSnapshot shaIndex = PlaylistLibraryResolveIndexSnapshot.FromLibraryChartRefs(
        [
            LibraryChartRef.FromBmsonSong(shaOnly)
        ]);
        LibraryChartRef resolvedBmson = shaIndex.ResolveChartForPlaylistEntry(entry);

        Assert.AreSame(earlierPath, preferred.GetBmsonStorageOwner());
        Assert.AreSame(earlierPath, resolvedMd5First.GetBmsonStorageOwner());
        Assert.AreEqual(earlierPath.path, preferred.Path);
        Assert.AreEqual(earlierPath.path, resolvedMd5First.Path);
        Assert.AreEqual(earlierPath.md5, preferred.Md5);
        Assert.AreEqual(earlierPath.sha256, preferred.Sha256);
        Assert.IsNull(preferred.ToChartFileIdentity().GetBmsonStorageOwner());
        Assert.IsNull(resolvedBmson);
    }

    [TestMethod]
    public void ResolveChartForPlaylistEntry_UsesSha256RepresentativeByPathOnlyWhenMd5IsMissing()
    {
        var entry = new TestablePlaylistEntry();
        entry.SetSha256(new string('f', 64));
        var laterBms = new TestableBmsFile();
        laterBms.Apply("C:\\Songs\\Omega\\chart.bms", "BMS", "Artist", new string('1', 32));
        laterBms.SetSha256(entry.sha256);
        var earlierBmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Alpha\\chart.bmson",
            md5 = new string('2', 32),
            sha256 = entry.sha256
        };

        PlaylistLibraryResolveIndexSnapshot index = PlaylistLibraryResolveIndexSnapshot.FromLibraryChartRefs(
        [
            LibraryChartRef.FromBmsFile(laterBms),
            LibraryChartRef.FromBmsonSong(earlierBmson)
        ]);
        LibraryChartRef preferred = index.ChartsBySha256[entry.sha256];
        LibraryChartRef resolved = index.ResolveChartForPlaylistEntry(entry);

        Assert.AreSame(earlierBmson, preferred.GetBmsonStorageOwner());
        Assert.AreSame(earlierBmson, resolved.GetBmsonStorageOwner());
        Assert.AreEqual(earlierBmson.path, preferred.Path);
        Assert.AreEqual(earlierBmson.path, resolved.Path);
        Assert.AreEqual(earlierBmson.md5, preferred.Md5);
        Assert.AreEqual(earlierBmson.sha256, preferred.Sha256);
        Assert.IsNull(preferred.ToChartFileIdentity().GetBmsonStorageOwner());
    }

    [TestMethod]
    public void ResolveChartForPlaylistEntry_DoesNotFallbackToSha256WhenMd5Exists()
    {
        var entry = new TestablePlaylistEntry();
        entry.SetMd5("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        entry.SetSha256(new string('f', 64));
        var shaMatch = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Sha\\chart.bmson",
            md5 = new string('1', 32),
            sha256 = entry.sha256
        };

        PlaylistLibraryResolveIndexSnapshot index = PlaylistLibraryResolveIndexSnapshot.FromLibraryChartRefs(
        [
            LibraryChartRef.FromBmsonSong(shaMatch)
        ]);
        LibraryChartRef resolved = index.ResolveChartForPlaylistEntry(entry);

        Assert.IsNull(resolved);
    }

    [TestMethod]
    public void ResolveChartForPlaylistEntry_ExcludesPathlessBmsAndBmsonRepresentatives()
    {
        var md5Entry = new TestablePlaylistEntry();
        md5Entry.SetMd5("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var shaEntry = new TestablePlaylistEntry();
        shaEntry.SetMd5("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        shaEntry.SetSha256(new string('c', 64));
        var pathlessBms = new TestableBmsFile();
        pathlessBms.Apply("C:\\Songs\\Temp\\chart.bms", "BMS", "Artist", md5Entry.md5);
        pathlessBms.path = null;
        var pathlessBmson = new LR2SongDBExtended.bmson_song
        {
            path = null,
            md5 = "dddddddddddddddddddddddddddddddd",
            sha256 = shaEntry.sha256
        };

        PlaylistLibraryResolveIndexSnapshot index = PlaylistLibraryResolveIndexSnapshot.FromLibraryChartRefs(
        [
            LibraryChartRef.FromBmsFile(pathlessBms),
            LibraryChartRef.FromBmsonSong(pathlessBmson)
        ]);

        Assert.IsFalse(index.ChartsByMd5.ContainsKey(md5Entry.md5));
        Assert.IsFalse(index.ChartsBySha256.ContainsKey(shaEntry.sha256));
        Assert.IsNull(index.ResolveChartForPlaylistEntry(md5Entry));
        Assert.IsNull(index.ResolveChartForPlaylistEntry(shaEntry));
    }

    [TestMethod]
    public void ResolveChartForPlaylistEntry_ExcludesMd5lessBmsAndBmsonRepresentatives()
    {
        var shaEntry = new TestablePlaylistEntry();
        shaEntry.SetSha256(new string('c', 64));
        var md5lessBms = new TestableBmsFile();
        md5lessBms.Apply("C:\\Songs\\Bms\\chart.bms", "BMS", "Artist", string.Empty);
        md5lessBms.SetSha256(shaEntry.sha256);
        var md5lessBmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\chart.bmson",
            md5 = null,
            sha256 = shaEntry.sha256
        };

        PlaylistLibraryResolveIndexSnapshot index = PlaylistLibraryResolveIndexSnapshot.FromLibraryChartRefs(
        [
            LibraryChartRef.FromBmsFile(md5lessBms),
            LibraryChartRef.FromBmsonSong(md5lessBmson)
        ]);

        Assert.IsFalse(index.ChartsBySha256.ContainsKey(shaEntry.sha256));
        Assert.IsNull(index.ResolveChartForPlaylistEntry(shaEntry));
    }

    [TestMethod]
    public void ResolvePlaylistEntryScoreSnapshot_BeatorajaUsesResolvedRepresentativeSha256AfterPathTieBreak()
    {
        var entry = new TestablePlaylistEntry();
        entry.SetMd5("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        var laterBms = new TestableBmsFile();
        laterBms.Apply("C:\\Songs\\Omega\\chart.bms", "BMS", "Artist", entry.md5);
        laterBms.SetSha256(new string('1', 64));
        var earlierBmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Alpha\\chart.bmson",
            md5 = entry.md5,
            sha256 = new string('2', 64)
        };
        PlaylistLibraryResolveIndexSnapshot index = PlaylistLibraryResolveIndexSnapshot.FromLibraryChartRefs(
        [
            LibraryChartRef.FromBmsFile(laterBms),
            LibraryChartRef.FromBmsonSong(earlierBmson)
        ]);
        LibraryChartRef representativeRef = index.ResolveChartForPlaylistEntry(entry);
        ChartFile representative = representativeRef.ToChartFileIdentity();
        var bmsonScore = new BMSScore
        {
            hash = earlierBmson.sha256,
            clear = ClearType.HARD,
            perfect = 900,
            great = 50,
            totalnotes = 1000
        };
        var bmsScore = new BMSScore
        {
            hash = laterBms.sha256,
            clear = ClearType.EASY,
            perfect = 100,
            great = 50,
            totalnotes = 1000
        };
        var snapshot = new BMSLibrary.ScoreSnapshot
        {
            ActiveScoreSource = ActiveScoreSource.Beatoraja,
            ScoresBySha256 = new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase)
            {
                [earlierBmson.sha256] = bmsonScore,
                [laterBms.sha256] = bmsScore
            }
        };

        BMSScore resolved = MainWindowViewModel.ResolvePlaylistEntryScoreSnapshotForTest(
            entry,
            representative,
            entryChartInfo: null,
            snapshot,
            scoresByHash: new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase),
            scoresBySha256: snapshot.ScoresBySha256);

        Assert.IsNull(representative.GetBmsonStorageOwner());
        Assert.AreEqual(ChartFileKind.Bmson, representative.Kind);
        Assert.AreEqual(earlierBmson.path, representative.Path);
        Assert.AreEqual(earlierBmson.md5, representative.Md5);
        Assert.AreEqual(earlierBmson.sha256, representative.Sha256);
        Assert.IsNotNull(resolved);
        Assert.AreEqual(earlierBmson.md5, resolved.hash);
        Assert.AreEqual(ClearType.HARD, resolved.clear);
    }

    [TestMethod]
    public void PlaylistDetailSourceRow_UsesScoreSnapshotForOwnedRowsBeforeGlobalHydration()
    {
        var file = new TestableBmsFile();
        file.ApplySnapshot("cccccccccccccccccccccccccccccccc", "Owned", 7);
        var entry = new BMSTableEntry(file);
        var score = new BMSScore
        {
            hash = file.hash,
            clear = ClearType.HARD,
            totalnotes = 1000,
            perfect = 800,
            great = 100,
            rank = RankType.AA,
            minbp = 3
        };
        var sourceRow = new PlaylistDetailSourceRow(entry, ChartFileProjection.FromBmsFile(file), scoreSnapshot: score);

        Assert.AreEqual(ClearType.HARD, sourceRow.clear);
        Assert.AreEqual(RankType.AA, sourceRow.rank);
        Assert.AreEqual(1700, sourceRow.score);
        Assert.AreEqual(1000, sourceRow.totalnotes);
        Assert.AreEqual(3, sourceRow.minbp);
    }

    [TestMethod]
    public void ResolvePlaylistEntryScoreSnapshot_BeatorajaUsesResolvedChartSha256ForMd5OnlyEntry()
    {
        var file = new TestableBmsFile();
        file.ApplySnapshot("cccccccccccccccccccccccccccccccc", "Owned", 7);
        file.SetSha256(new string('a', 64));
        var entry = new TestablePlaylistEntry();
        entry.SetMd5(file.hash);
        var score = new BMSScore
        {
            hash = file.sha256,
            clear = ClearType.HARD,
            perfect = 800,
            great = 100,
            totalnotes = 1000
        };
        var snapshot = new BMSLibrary.ScoreSnapshot
        {
            ActiveScoreSource = ActiveScoreSource.Beatoraja,
            ScoresBySha256 = new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase)
            {
                [file.sha256] = score
            }
        };

        BMSScore resolved = MainWindowViewModel.ResolvePlaylistEntryScoreSnapshotForTest(
            entry,
            ChartFileProjection.FromBmsFile(file),
            entryChartInfo: null,
            snapshot,
            scoresByHash: new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase),
            scoresBySha256: snapshot.ScoresBySha256);

        Assert.IsNotNull(resolved);
        Assert.AreEqual(file.hash, resolved.hash);
        Assert.AreEqual(ClearType.HARD, resolved.clear);
        Assert.AreEqual(1700, resolved.score);
    }

    [TestMethod]
    public void ResolvePlaylistEntryScoreSnapshot_Lr2PrefersResolvedChartHashWhenEntryHashIsStale()
    {
        var file = new TestableBmsFile();
        file.ApplySnapshot("cccccccccccccccccccccccccccccccc", "Owned", 7);
        var entry = new TestablePlaylistEntry();
        entry.SetMd5("dddddddddddddddddddddddddddddddd");
        var fileScore = new BMSScore
        {
            hash = file.hash,
            clear = ClearType.HARD,
            perfect = 800,
            great = 100,
            totalnotes = 1000
        };
        var staleEntryScore = new BMSScore
        {
            hash = entry.md5,
            clear = ClearType.EASY,
            perfect = 100,
            great = 50,
            totalnotes = 1000
        };
        var snapshot = new BMSLibrary.ScoreSnapshot
        {
            ActiveScoreSource = ActiveScoreSource.Lr2,
            ScoresByHash = new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase)
            {
                [file.hash] = fileScore,
                [entry.md5] = staleEntryScore
            }
        };

        BMSScore resolved = MainWindowViewModel.ResolvePlaylistEntryScoreSnapshotForTest(
            entry,
            ChartFileProjection.FromBmsFile(file),
            entryChartInfo: null,
            snapshot,
            scoresByHash: snapshot.ScoresByHash,
            scoresBySha256: new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase));

        Assert.AreSame(fileScore, resolved);
        Assert.AreEqual(ClearType.HARD, resolved.clear);
    }

    [TestMethod]
    public void ResolvePlaylistEntryScoreSnapshot_BeatorajaUsesEntryChartInfoSha256ForMissingEntry()
    {
        var entry = new TestablePlaylistEntry();
        entry.SetMd5("dddddddddddddddddddddddddddddddd");
        LR2SongDBExtended.chart_info chartInfo = CreateChartInfo(new string('b', 64), entry.md5);
        var score = new BMSScore
        {
            hash = chartInfo.sha256,
            clear = ClearType.EX_HARD,
            perfect = 600,
            great = 50,
            totalnotes = 800
        };
        var snapshot = new BMSLibrary.ScoreSnapshot
        {
            ActiveScoreSource = ActiveScoreSource.Beatoraja,
            ScoresBySha256 = new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase)
            {
                [chartInfo.sha256] = score
            }
        };

        BMSScore resolved = MainWindowViewModel.ResolvePlaylistEntryScoreSnapshotForTest(
            entry,
            resolvedChart: null,
            entryChartInfo: chartInfo,
            snapshot,
            scoresByHash: new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase),
            scoresBySha256: snapshot.ScoresBySha256);

        Assert.IsNotNull(resolved);
        Assert.AreEqual(entry.md5, resolved.hash);
        Assert.AreEqual(ClearType.EX_HARD, resolved.clear);
        Assert.AreEqual(1250, resolved.score);
    }

    [TestMethod]
    public void ResolvePlaylistEntryScoreSnapshot_BeatorajaUsesBmsonChartSha256ForOwnedBmsonEntry()
    {
        var entry = new TestablePlaylistEntry();
        entry.SetMd5("dddddddddddddddddddddddddddddddd");
        var bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\score.bmson",
            md5 = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
            sha256 = new string('c', 64),
            title = "Bmson score"
        };
        var score = new BMSScore
        {
            hash = bmson.sha256,
            clear = ClearType.HARD,
            perfect = 700,
            great = 100,
            totalnotes = 900
        };
        var snapshot = new BMSLibrary.ScoreSnapshot
        {
            ActiveScoreSource = ActiveScoreSource.Beatoraja,
            ScoresBySha256 = new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase)
            {
                [bmson.sha256] = score
            }
        };

        BMSScore resolved = MainWindowViewModel.ResolvePlaylistEntryScoreSnapshotForTest(
            entry,
            ChartFileProjection.FromBmsonSong(bmson, includeWarningSnapshot: false),
            entryChartInfo: null,
            snapshot,
            scoresByHash: new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase),
            scoresBySha256: snapshot.ScoresBySha256);

        Assert.IsNotNull(resolved);
        Assert.AreEqual(bmson.md5, resolved.hash);
        Assert.AreEqual(ClearType.HARD, resolved.clear);
        Assert.AreEqual(1500, resolved.score);
    }

    [TestMethod]
    public void ShouldRebuildRegularFolderStage_WhenIncrementalRegularUpdateHasMissingCaches_ReturnsTrue()
    {
        Assert.IsTrue(MainViewRefreshDecisionService.ShouldRebuildRegularFolderStage(
            (MainViewUpdateMode)81,
            hasFolderView: false,
            hasKeywordView: true,
            hasModeView: true,
            currentTreeMode: (MainViewUpdateMode)17));
        Assert.IsTrue(MainViewRefreshDecisionService.ShouldRebuildRegularFolderStage(
            (MainViewUpdateMode)65,
            hasFolderView: true,
            hasKeywordView: false,
            hasModeView: true,
            currentTreeMode: (MainViewUpdateMode)17));
        Assert.IsFalse(MainViewRefreshDecisionService.ShouldRebuildRegularFolderStage(
            (MainViewUpdateMode)81,
            hasFolderView: true,
            hasKeywordView: true,
            hasModeView: true,
            currentTreeMode: (MainViewUpdateMode)17));
    }

    [TestMethod]
    public void NormalLibraryRowCache_ReusesRowsAndPrunesRemovedFiles()
    {
        var fileA = new TestableBmsFile();
        fileA.ApplySnapshot("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "A", 7);
        var fileB = new TestableBmsFile();
        fileB.ApplySnapshot("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "B", 7);
        var cache = new NormalLibraryRowCache();
        var firstStats = new LibraryRowCacheBuildStats();
        LibraryChartRow firstA = cache.GetOrCreate(ChartFileProjection.FromBmsFile(fileA), firstStats);
        LibraryChartRow firstB = cache.GetOrCreate(ChartFileProjection.FromBmsFile(fileB), firstStats);
        var secondStats = new LibraryRowCacheBuildStats();
        LibraryChartRow secondA = cache.GetOrCreate(ChartFileProjection.FromBmsFile(fileA), secondStats);

        Assert.AreSame(firstA, secondA);
        Assert.AreEqual(0, firstStats.HitCount);
        Assert.AreEqual(2, firstStats.MissCount);
        Assert.AreEqual(1, secondStats.HitCount);
        Assert.AreEqual(0, secondStats.MissCount);
        Assert.AreEqual(1, cache.PruneBmsFiles([fileA]));
        Assert.AreEqual(1, cache.Count);
        fileB.SetTitle("B2");
        fileA.SetTitle("A2");
        Assert.AreSame(firstA, cache.GetOrCreate(ChartFileProjection.FromBmsFile(fileA), new LibraryRowCacheBuildStats()));
    }

    [TestMethod]
    public void NormalLibraryRowCache_RemoveBmsFilesPrunesOnlyRemovedReferences()
    {
        var fileA = new TestableBmsFile();
        fileA.ApplySnapshot("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "A", 7);
        var fileB = new TestableBmsFile();
        fileB.ApplySnapshot("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "B", 7);
        var cache = new NormalLibraryRowCache();
        LibraryChartRow rowA = cache.GetOrCreate(ChartFileProjection.FromBmsFile(fileA), new LibraryRowCacheBuildStats());
        LibraryChartRow rowB = cache.GetOrCreate(ChartFileProjection.FromBmsFile(fileB), new LibraryRowCacheBuildStats());

        Assert.AreEqual(1, cache.RemoveBmsFiles([fileA]));
        Assert.AreEqual(1, cache.Count);
        Assert.AreSame(rowB, cache.GetOrCreate(ChartFileProjection.FromBmsFile(fileB), new LibraryRowCacheBuildStats()));
        Assert.AreNotSame(rowA, cache.GetOrCreate(ChartFileProjection.FromBmsFile(fileA), new LibraryRowCacheBuildStats()));
    }

    [TestMethod]
    public void NormalLibraryRowCache_RemoveBmsFilesPrunesByStaleInputPath()
    {
        var current = new TestableBmsFile();
        current.path = @"folder\chart.bms";
        current.ApplySnapshot("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "A", 7);
        var staleSamePath = new TestableBmsFile();
        staleSamePath.path = @"folder\chart.bms";
        staleSamePath.ApplySnapshot("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "A", 7);
        var kept = new TestableBmsFile();
        kept.path = @"folder\kept.bms";
        kept.ApplySnapshot("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "B", 7);
        var cache = new NormalLibraryRowCache();
        LibraryChartRow removedRow = cache.GetOrCreate(ChartFileProjection.FromBmsFile(current), new LibraryRowCacheBuildStats());
        LibraryChartRow keptRow = cache.GetOrCreate(ChartFileProjection.FromBmsFile(kept), new LibraryRowCacheBuildStats());

        Assert.AreEqual(1, cache.RemoveBmsFiles([staleSamePath]));

        Assert.AreEqual(1, cache.Count);
        Assert.AreSame(keptRow, cache.GetOrCreate(ChartFileProjection.FromBmsFile(kept), new LibraryRowCacheBuildStats()));
        Assert.AreNotSame(removedRow, cache.GetOrCreate(ChartFileProjection.FromBmsFile(current), new LibraryRowCacheBuildStats()));
    }

    [TestMethod]
    public void NormalLibraryRowCache_ReusesSyncedBmsonRowsByReferenceAndPath()
    {
        var cache = new NormalLibraryRowCache();
        LR2SongDBExtended.bmson_song original = CreateBmsonCacheSong(@"folder\chart.bmson", "Original");

        BmsonLibraryRowCacheSyncResult firstResult = cache.SyncBmsonRows([original], null);
        LibraryChartRow firstRow = cache.GetOrCreate(ChartFileProjection.FromBmsonSong(original), new LibraryRowCacheBuildStats());

        Assert.IsTrue(firstResult.MembershipChanged);
        Assert.IsTrue(firstResult.SortKeyChanged);
        Assert.IsFalse(firstResult.SourceReferenceChanged);
        Assert.AreEqual(1, cache.Count);
        Assert.AreEqual(1, cache.SnapshotRows().Count);

        LR2SongDBExtended.bmson_song samePathNext = CreateBmsonCacheSong(@"folder\chart.bmson", "Original");
        BmsonLibraryRowCacheSyncResult secondResult = cache.SyncBmsonRows([samePathNext], null);
        LibraryChartRow secondRow = cache.GetOrCreate(ChartFileProjection.FromBmsonSong(samePathNext), new LibraryRowCacheBuildStats());

        Assert.AreSame(firstRow, secondRow);
        Assert.IsFalse(secondResult.MembershipChanged);
        Assert.IsFalse(secondResult.SortKeyChanged);
        Assert.IsFalse(secondResult.SourceIdentityChanged);
        Assert.IsTrue(secondResult.SourceReferenceChanged);
        Assert.AreSame(secondRow, cache.GetOrCreate(ChartFileProjection.FromBmsonSong(original), new LibraryRowCacheBuildStats()));
    }

    [TestMethod]
    public void NormalLibraryRowCache_DetectsBmsonSortKeyAndMembershipChanges()
    {
        var cache = new NormalLibraryRowCache();
        LR2SongDBExtended.bmson_song original = CreateBmsonCacheSong(@"folder\chart.bmson", "Original");
        cache.SyncBmsonRows([original], null);
        LibraryChartRow row = cache.GetOrCreate(ChartFileProjection.FromBmsonSong(original), new LibraryRowCacheBuildStats());

        LR2SongDBExtended.bmson_song changed = CreateBmsonCacheSong(@"folder\chart.bmson", "Changed");
        BmsonLibraryRowCacheSyncResult changedResult = cache.SyncBmsonRows([changed], null);

        Assert.AreSame(row, cache.GetOrCreate(ChartFileProjection.FromBmsonSong(changed), new LibraryRowCacheBuildStats()));
        Assert.IsFalse(changedResult.MembershipChanged);
        Assert.IsTrue(changedResult.SortKeyChanged);
        Assert.IsTrue(changedResult.SourceIdentityChanged);
        Assert.IsTrue(changedResult.SourceReferenceChanged);
        Assert.AreEqual("Changed", row.Title);

        BmsonLibraryRowCacheSyncResult removedResult = cache.SyncBmsonRows([], null);

        Assert.IsTrue(removedResult.MembershipChanged);
        Assert.IsTrue(removedResult.SortKeyChanged);
        Assert.IsTrue(removedResult.SourceIdentityChanged);
        Assert.IsFalse(removedResult.SourceReferenceChanged);
        Assert.AreEqual(0, cache.Count);
    }

    [TestMethod]
    public void NormalLibraryRowCache_DoesNotReportSourceChangeForSameReferenceProjectionOnlyBmsonChange()
    {
        var cache = new NormalLibraryRowCache();
        LR2SongDBExtended.bmson_song original = CreateBmsonCacheSong(@"folder\chart.bmson", "Original");
        LR2SongDBExtended.chart_info projectedChartInfo = CreateChartInfo(original.sha256, original.md5, level: 4, notes: 1000, total: 250);
        cache.SyncBmsonRows([original], row => row.SetChartInfoProjectionProvider(_ => projectedChartInfo));

        projectedChartInfo = CreateChartInfo(original.sha256, original.md5, level: 12, notes: 3000, total: 700);
        BmsonLibraryRowCacheSyncResult result = cache.SyncBmsonRows([original], row => row.SetChartInfoProjectionProvider(_ => projectedChartInfo));

        Assert.IsFalse(result.MembershipChanged);
        Assert.IsFalse(result.SortKeyChanged);
        Assert.IsFalse(result.SourceIdentityChanged);
        Assert.IsFalse(result.SourceReferenceChanged);
        Assert.IsFalse(result.SourceChanged);
    }

    [TestMethod]
    public void NormalLibraryRowCache_RemoveBmsonSongsPrunesByReferenceAndPath()
    {
        var cache = new NormalLibraryRowCache();
        LR2SongDBExtended.bmson_song original = CreateBmsonCacheSong(@"folder\chart.bmson", "Original");
        LR2SongDBExtended.bmson_song other = CreateBmsonCacheSong(@"folder\other.bmson", "Other");
        cache.SyncBmsonRows([original, other], null);
        LibraryChartRow originalRow = cache.GetOrCreate(ChartFileProjection.FromBmsonSong(original), new LibraryRowCacheBuildStats());
        LibraryChartRow otherRow = cache.GetOrCreate(ChartFileProjection.FromBmsonSong(other), new LibraryRowCacheBuildStats());

        LR2SongDBExtended.bmson_song staleSamePath = CreateBmsonCacheSong(@"folder\chart.bmson", "Stale");
        BmsonLibraryRowCacheSyncResult result = cache.RemoveBmsonSongs([staleSamePath]);

        Assert.IsTrue(result.MembershipChanged);
        Assert.IsTrue(result.SortKeyChanged);
        Assert.IsTrue(result.SourceIdentityChanged);
        Assert.AreEqual(1, cache.Count);
        Assert.AreSame(otherRow, cache.GetOrCreate(ChartFileProjection.FromBmsonSong(other), new LibraryRowCacheBuildStats()));
        Assert.AreNotSame(originalRow, cache.GetOrCreate(ChartFileProjection.FromBmsonSong(original), new LibraryRowCacheBuildStats()));
    }

    [TestMethod]
    public void NormalLibraryRowCache_DoesNotAttachUnsyncedBmsonRows()
    {
        var cache = new NormalLibraryRowCache();
        var stats = new LibraryRowCacheBuildStats();

        LibraryChartRow row = cache.GetOrCreate(
            ChartFileProjection.FromBmsonSong(CreateBmsonCacheSong(@"folder\detached.bmson", "Detached")),
            stats);

        Assert.IsNotNull(row);
        Assert.AreEqual(0, cache.Count);
        Assert.AreEqual(0, cache.SnapshotRows().Count);
        Assert.AreEqual(0, stats.HitCount);
        Assert.AreEqual(0, stats.MissCount);
    }

    [TestMethod]
    public void LibraryChartRowSourceNotificationMapper_MapsBmsStorageNotificationsToDisplayGroups()
    {
        LibraryChartRowSourceNotificationGroups allGroups = LibraryChartRowSourceNotificationMapper.MapBmsStorageProperty(string.Empty);
        LibraryChartRowSourceNotificationGroups nullGroups = LibraryChartRowSourceNotificationMapper.MapBmsStorageProperty(null);

        Assert.IsTrue(allGroups.HasFlag(LibraryChartRowSourceNotificationGroups.WarningPresentation));
        Assert.IsTrue(allGroups.HasFlag(LibraryChartRowSourceNotificationGroups.ScoreDisplay));
        Assert.IsTrue(allGroups.HasFlag(LibraryChartRowSourceNotificationGroups.MaintenanceDisplay));
        Assert.AreEqual(allGroups, nullGroups);
        Assert.AreEqual(
            LibraryChartRowSourceNotificationGroups.WarningPresentation,
            LibraryChartRowSourceNotificationMapper.MapBmsStorageProperty(nameof(BMSFile.Warnings)));
        Assert.AreEqual(
            LibraryChartRowSourceNotificationGroups.ScoreDisplay,
            LibraryChartRowSourceNotificationMapper.MapBmsStorageProperty(nameof(BMSFile.bmsScore)));
        Assert.AreEqual(
            LibraryChartRowSourceNotificationGroups.MaintenanceDisplay,
            LibraryChartRowSourceNotificationMapper.MapBmsStorageProperty(nameof(BMSFile.maintenanceInfo)));
        Assert.AreEqual(
            LibraryChartRowSourceNotificationGroups.None,
            LibraryChartRowSourceNotificationMapper.MapBmsStorageProperty(nameof(BMSFile.path)));
    }

    private static LR2SongDBExtended.bmson_song CreateBmsonCacheSong(string path, string title)
    {
        return new LR2SongDBExtended.bmson_song
        {
            path = path,
            folder = Path.GetDirectoryName(path) ?? string.Empty,
            title = title,
            artist = "Artist",
            genre = "Genre",
            mode_hint = "beat-7k",
            level = 7,
            md5 = "22222222222222222222222222222222",
            sha256 = "2222222222222222222222222222222222222222222222222222222222222222"
        };
    }

    [TestMethod]
    public void ResolvePlaylistColumnSettingMode_ReturnsPlaylistViewModesForBothPlaylistFilters()
    {
        Assert.AreEqual(
            1,
            MainWindowViewModel.ResolvePlaylistColumnSettingModeForTest((int)MainWindowViewModel.PlaylistFilterType.PlaylistFilter));
        Assert.AreEqual(
            2,
            MainWindowViewModel.ResolvePlaylistColumnSettingModeForTest((int)MainWindowViewModel.PlaylistFilterType.PlaylistNotOwnedFilterSelected));
    }

    private static PlaylistDetailSourceRow CreateSourceRow(string hash, string title, int? mode, string memo = "", string comment = "", double? entryLevel = null, string? sha256 = null)
    {
        var file = new TestableBmsFile();
        file.ApplySnapshot(hash, title, mode);
        if (sha256 != null)
        {
            file.SetSha256(sha256);
        }
        var entry = new TestablePlaylistEntry(file)
        {
            memo = memo,
            comment = comment,
            level = entryLevel
        };
        if (sha256 != null)
        {
            entry.SetSha256(sha256);
        }
        return new PlaylistDetailSourceRow(entry, ChartFileProjection.FromBmsFile(file));
    }

    private static void AssertPlaylistRowFileDeletePolicy(PlaylistDetailRow row, bool expectedRemoveFromLibrary)
    {
        Assert.IsTrue(GridRowResolver.IsPlaylistRow(row));
        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, out ChartOperationTarget target));
        Assert.AreEqual(expectedRemoveFromLibrary, target.HasCapability(ChartOperationCapabilities.RemoveFromLibrary));
        Assert.AreEqual(expectedRemoveFromLibrary, target.HasCapability(ChartOperationCapabilities.MoveInLibrary));
    }

    private static ChartOperationTarget CreateDeleteTarget(ChartOperationSourceScope sourceScope, ChartOperationCapabilities capabilities)
    {
        // Path-only target: used by delete-policy tests that do not need a backing BMSFile adapter.
        var chart = new ChartFile(
            ChartFileKind.Bms,
            "C:\\Library\\Song\\chart.bms",
            "abababababababababababababababab",
            null,
            "Title",
            "Title",
            "Artist",
            string.Empty,
            "Song",
            string.Empty,
            "7",
            7,
            7,
            null,
            null,
            null,
            null);
        bool isPending = sourceScope == ChartOperationSourceScope.PendingPackage;
        bool isPlaylistMissing = sourceScope == ChartOperationSourceScope.PlaylistMissing;
        return new ChartOperationTarget(
            chart,
            null,
            sourceScope,
            !isPending && !isPlaylistMissing,
            isPending,
            isPlaylistMissing,
            capabilities);
    }

    private static PlaylistDetailSourceRow CreateMissingSourceRow(string title, LR2SongDBExtended.chart_info chartInfo)
    {
        var entry = new TestablePlaylistEntry();
        entry.SetTitle(title);
        entry.SetMd5(chartInfo.md5);
        entry.SetSha256(chartInfo.sha256);
        return new PlaylistDetailSourceRow(entry, resolvedChart: null, entryChartInfo: chartInfo);
    }

    private static BMSFile CreateBmsFile(string path, string title, string artist, string hash)
    {
        var file = new TestableBmsFile();
        file.Apply(path, title, artist, hash);
        return file;
    }

    private static LR2SongDBExtended.chart_info CreateChartInfo(string sha256, string md5, int? level = 12, int notes = 2500, double total = 500)
    {
        return new LR2SongDBExtended.chart_info
        {
            sha256 = sha256,
            md5 = md5,
            charthash = new string('a', 64),
            level = level,
            difficulty = 4,
            difficulty_defined = true,
            mainbpm = 180,
            maxbpm = 180,
            minbpm = 120,
            length = 123000,
            mode = 7,
            judge = 100,
            feature = ChartInfoDisplayFormatter.FeatureRandom,
            notes = notes,
            n = notes,
            ln = 10,
            s = 20,
            ls = 5,
            total = total,
            total_defined = true,
            density = 10,
            peakdensity = 20,
            enddensity = 2,
            distribution = "0,1,2",
            speedchange = "0=180",
            speedchange_count = 1,
            lanenotes = "1,2,3,4,5,6,7",
            parser_version = 1,
            updated_at = DateTime.UtcNow
        };
    }

    private static Func<ChartFile, LR2SongDBExtended.chart_info> CreateChartInfoProvider(params LR2SongDBExtended.chart_info[] rows)
    {
        return chart =>
        {
            if (chart == null)
            {
                return null!;
            }
            return (rows ?? [])
                .Where(row => row != null)
                .FirstOrDefault(row => !string.IsNullOrWhiteSpace(chart.Sha256) && string.Equals(row.sha256, chart.Sha256, StringComparison.OrdinalIgnoreCase))
                ?? (rows ?? [])
                    .Where(row => row != null)
                    .FirstOrDefault(row => !string.IsNullOrWhiteSpace(chart.Md5) && string.Equals(row.md5, chart.Md5, StringComparison.OrdinalIgnoreCase))
                ?? null!;
        };
    }

    private static List<BMSTable> CreateBmtSortTables(params int[] playlistIds)
    {
        return [.. playlistIds.Select(id => new BMSTable
        {
            playlist_id = id,
            bmt_sort = id,
            name = "Playlist " + id.ToString(CultureInfo.InvariantCulture)
        })];
    }

    private static PlaylistSummaryRow CreatePlaylistSummaryRow(BMSTable table)
    {
        return new PlaylistSummaryRow
        {
            PlaylistId = table.playlist_id,
            Name = table.name,
            BmtSort = table.bmt_sort ?? int.MaxValue,
            IsBmtOutput = table.is_bmt_output != false,
            TableRef = table
        };
    }

    private static PlayHistoryRow CreateResolvedPlayHistoryRow()
    {
        const string hash = "dddddddddddddddddddddddddddddddd";
        const string sha256 = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
        var file = new TestableBmsFile();
        file.Apply(@"C:\BMS\play-history-resolved.bms", "Resolved Play History", "Artist", hash);
        file.SetSha256(sha256);
        PlaylistLibraryResolveIndexSnapshot resolveIndex = PlaylistLibraryResolveIndexSnapshot.FromLibraryChartRefs([LibraryChartRef.FromBmsFile(file)]);
        PlayHistoryProjectionIndex projectionIndex = PlayHistoryProjectionIndex.Create(
            resolveIndex,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [hash] = sha256 });
        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectLr2Rows(
            new Lr2PlayHistoryReadResult(
                PlayHistorySourceProfile.Lr2("score.db"),
                [
                    new Lr2PlayHistoryRecord
                    {
                        history_id = 1,
                        hash = hash,
                        played_at = 1000,
                        finalized = 1,
                        score_write_type = "update",
                        new_playcount = 1,
                        playcount_delta = 1,
                        new_exscore = 100,
                        new_totalnotes = 100
                    }
                ],
                [],
                Lr2PlayHistorySchemaStatus.Installed),
            projectionIndex);

        return projected.Rows.Single();
    }

    private static PlayHistoryRow CreateUnresolvedPlayHistoryRow()
    {
        const string hash = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectLr2Rows(
            new Lr2PlayHistoryReadResult(
                PlayHistorySourceProfile.Lr2("score.db"),
                [
                    new Lr2PlayHistoryRecord
                    {
                        history_id = 2,
                        hash = hash,
                        played_at = 1000,
                        finalized = 1,
                        score_write_type = "update",
                        new_playcount = 1,
                        playcount_delta = 1,
                        new_exscore = 100,
                        new_totalnotes = 100
                    }
                ],
                [],
                Lr2PlayHistorySchemaStatus.Installed),
            PlayHistoryProjectionIndex.Empty);

        return projected.Rows.Single();
    }

    private static PlaylistBuildRequest CreatePlaylistBuildRequest(MainWindowViewModel.PlaylistRequestIdentity identity)
    {
        return new PlaylistBuildRequest
        {
            Identity = identity
        };
    }

    private static MainWindowViewModel.PlaylistRequestIdentity CreatePlaylistIdentity(string folderName)
    {
        return PlaylistRequestFactory.CreateIdentity(
            new BMSTable(),
            folderName,
            MainWindowViewModel.PlaylistFilterType.PlaylistFilter,
            keywordFilter: null,
            MainWindowViewModel.ModeFilterType.All,
            sortParameters: null,
            libraryIndexVersion: 3,
            playlistRevision: 4,
            scoreSnapshotVersion: 5,
            chartInfoIndexVersion: 6,
            hasResolvedSelection: true);
    }

    private static PlaylistDetailTerminalRequest CreatePlaylistTerminalRequest(IList rows, int requestVersion)
    {
        MainWindowViewModel.PlaylistRequestIdentity identity = CreatePlaylistIdentity("terminal");
        var stopwatch = Stopwatch.StartNew();
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAYLIST);
        var columnSelection = new MainChartListColumnSelection(
            settings,
            reused: false,
            elapsedMs: 0L,
            MainViewUpdateMode.PlaylistFilterSelected,
            System.Windows.Visibility.Collapsed,
            new PlaylistSummaryColumnSettings());
        return new PlaylistDetailTerminalRequest
        {
            BuildRequest = new PlaylistBuildRequest
            {
                RequestVersion = requestVersion,
                Mode = MainViewUpdateMode.PlaylistFilterSelected,
                RequestedMode = MainViewUpdateMode.PlaylistFilterSelected,
                Identity = identity
            },
            ReplaceSource = false,
            CurrentFilterType = identity.FilterType,
            ViewRows = rows,
            ColumnSelection = columnSelection,
            MainRowsRequest = new MainChartListRowsApplyRequest
            {
                Rows = rows,
                ColumnsSettings = settings,
                SelectionPolicy = MainChartListSelectionPolicy.Reset,
                Summary = MainChartListSummaryUpdate.NormalRows(rows),
                Stopwatch = stopwatch
            }
        };
    }

    private static PlaylistDetailBuildStateSnapshot CreatePlaylistDetailBuildStateSnapshot(
        MainWindowViewModel.PlaylistRequestIdentity identity,
        bool hasSourceRows = true,
        int sourceRowCount = 1,
        MainWindowViewModel.PlaylistRequestIdentity? currentViewIdentity = null)
    {
        return new PlaylistDetailBuildStateSnapshot(
            hasSourceRows,
            sourceRowCount,
            identity.Table,
            identity.FolderName,
            identity.FilterType,
            identity.LibraryIndexVersion,
            identity.PlaylistRevision,
            identity.ScoreSnapshotVersion,
            identity.ChartInfoIndexVersion,
            identity.SourceIdentity,
            currentViewIdentity ?? identity);
    }

    private static string ExtractTypeBlock(string text, string typeDeclaration)
    {
        int declarationIndex = text.IndexOf(typeDeclaration, StringComparison.Ordinal);
        if (declarationIndex < 0)
        {
            throw new InvalidOperationException("Type declaration was not found.");
        }

        int braceIndex = text.IndexOf('{', declarationIndex);
        if (braceIndex < 0)
        {
            throw new InvalidOperationException("Type declaration brace was not found.");
        }

        int depth = 0;
        for (int index = braceIndex; index < text.Length; index++)
        {
            if (text[index] == '{')
            {
                depth++;
            }
            else if (text[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return text.Substring(declarationIndex, index - declarationIndex + 1);
                }
            }
        }

        throw new InvalidOperationException("Type declaration body was not closed.");
    }

    private sealed class TestablePlaylistEntry : BMSTableEntry
    {
        public TestablePlaylistEntry()
        {
        }

        public TestablePlaylistEntry(BMSFile bmsFile)
            : base(bmsFile)
        {
        }

        public void SetSha256(string value)
        {
            sha256 = value;
        }

        public void SetMd5(string value)
        {
            md5 = value;
        }

        public void SetTitle(string value)
        {
            title = value;
        }

        public void SetArtist(string value)
        {
            artist = value;
        }
    }

    private sealed class TrackingDisposableRow : IDisposable
    {
        private readonly bool throwOnDispose;

        internal TrackingDisposableRow(bool throwOnDispose = false)
        {
            this.throwOnDispose = throwOnDispose;
        }

        internal int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
            if (throwOnDispose)
            {
                throw new InvalidOperationException("dispose failed");
            }
        }
    }

    private sealed class TestableBmsFile : BMSFile
    {
        public void Apply(string filePath, string title, string artist, string md5)
        {
            path = filePath;
            hash = md5;
            Title = title;
            Artist = artist;
            folder = System.IO.Path.GetDirectoryName(filePath);
        }

        public void ApplySnapshot(string snapshotHash, string snapshotTitle, int? snapshotMode)
        {
            hash = snapshotHash;
            path = snapshotTitle + ".bms";
            Title = snapshotTitle;
            Artist = "TestArtist";
            genre = "TestGenre";
            mode = snapshotMode;
        }

        public void SetSha256(string value)
        {
            sha256 = value;
        }

        public void SetTitle(string value)
        {
            Title = value;
        }

        public void SetArtist(string value)
        {
            Artist = value;
        }

        public void SetSubtitle(string value)
        {
            typeof(BMSFile).GetField("_subtitle", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(this, value);
        }
    }
}
