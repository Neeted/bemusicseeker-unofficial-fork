using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using BeMusicSeeker.Diagnostics;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    internal PlaylistDetailTerminalApplyResult ApplyDetailFromCurrentSource(
        PlaylistDetailPresentationRequest request)
    {
        if (request?.BuildRequest == null || request.Stopwatch == null)
        {
            throw new ArgumentException("A complete playlist detail presentation request is required.", nameof(request));
        }
        List<PlaylistDetailSourceRow> sourceRows;
        long sourceGeneration;
        int currentViewCount;
        lock (DetailViewState.SyncRoot)
        {
            sourceRows = DetailViewState.Source.Rows ?? [];
            sourceGeneration = DetailViewState.Source.GenerationId;
            currentViewCount = CountDetailRows(DetailViewState.View.Rows);
        }
        detailViewLog("started mode=" + request.Mode + " sourceGenerationId=" + sourceGeneration + " sourceCount=" + sourceRows.Count + " playlistSourceRowCount=" + sourceRows.Count + " playlistViewRowCount=" + currentViewCount);
        PlaylistViewApplyResult viewApply = BuildDetailView(sourceRows, request);
        detailViewLog("completed mode=" + request.Mode + " sourceGenerationId=" + sourceGeneration + " sourceCount=" + viewApply.SourceCount + " keywordCount=" + viewApply.KeywordCount + " modeCount=" + viewApply.ModeCount + " viewCount=" + viewApply.ViewCount + " sortProfile=" + viewApply.SortProfile + " playlistSourceRowCount=" + sourceRows.Count + " playlistViewRowCount=" + CountDetailRows(viewApply.FinalRows));
        if (request.CancellationToken.IsCancellationRequested)
        {
            MainChartListViewModel.DisposeRows(viewApply.FinalRows);
            return new PlaylistDetailTerminalApplyResult(false, viewApply, null, 0);
        }
        return CommitDetailPresentation(request, viewApply, replaceSource: false, null, null, null, default);
    }

    internal PlaylistDetailTerminalApplyResult ApplyDetailFromRebuiltSource(
        PlaylistDetailRebuiltPresentationRequest request)
    {
        if (request?.SourceRows == null)
        {
            throw new ArgumentException("Rebuilt playlist source rows are required.", nameof(request));
        }
        int currentViewCount;
        lock (DetailViewState.SyncRoot)
        {
            currentViewCount = CountDetailRows(DetailViewState.View.Rows);
        }
        detailViewLog("started mode=" + request.Mode + " sourceCount=" + request.SourceRows.Count + " playlistSourceRowCount=" + request.SourceRows.Count + " playlistViewRowCount=" + currentViewCount);
        PlaylistViewApplyResult viewApply = BuildDetailView(request.SourceRows, request);
        detailViewLog("completed mode=" + request.Mode + " sourceCount=" + viewApply.SourceCount + " keywordCount=" + viewApply.KeywordCount + " modeCount=" + viewApply.ModeCount + " viewCount=" + viewApply.ViewCount + " sortProfile=" + viewApply.SortProfile + " playlistSourceRowCount=" + request.SourceRows.Count + " playlistViewRowCount=" + CountDetailRows(viewApply.FinalRows));
        if (request.CancellationToken.IsCancellationRequested)
        {
            MainChartListViewModel.DisposeRows(viewApply.FinalRows);
            return new PlaylistDetailTerminalApplyResult(false, viewApply, null, 0);
        }
        return CommitDetailPresentation(
            request,
            viewApply,
            replaceSource: true,
            request.SourceRows,
            request.CurrentTable,
            request.CurrentFolderName,
            request.CurrentFilterType);
    }

    private static PlaylistViewApplyResult BuildDetailView(
        IReadOnlyList<PlaylistDetailSourceRow> sourceRows,
        PlaylistDetailPresentationRequest request)
    {
        IList rows = PlaylistDetailPresentationService.ApplyVirtualViewFromSource(
            sourceRows,
            request.KeywordFilter,
            request.ModeFilter,
            request.SortParameters,
            out string sortProfile,
            out int keywordCount,
            out int modeCount,
            out long keywordStageMs,
            out long modeStageMs,
            out long sortStageMs,
            out long viewMaterializeMs);
        return new PlaylistViewApplyResult(
            rows,
            sourceRows.Count,
            sortProfile,
            keywordCount,
            modeCount,
            keywordStageMs,
            modeStageMs,
            sortStageMs,
            viewMaterializeMs);
    }

    private PlaylistDetailTerminalApplyResult CommitDetailPresentation(
        PlaylistDetailPresentationRequest request,
        PlaylistViewApplyResult viewApply,
        bool replaceSource,
        List<PlaylistDetailSourceRow> sourceRows,
        BMSTable currentTable,
        string currentFolderName,
        PlaylistDetailFilter currentFilterType)
    {
        long stageStartMs = request.Stopwatch.ElapsedMilliseconds;
        if (Net10PerformanceLog.IsEnabled)
        {
            Net10PerformanceLog.Write(
                PerformanceInteraction.Existing(
                    "playlist_detail",
                    request.BuildRequest.RequestVersion,
                    request.BuildRequest.RequestVersion),
                "snapshot_query_projection",
                "sourceRows=" + viewApply.SourceCount
                + " viewRows=" + viewApply.ViewCount
                + " keywordMs=" + viewApply.KeywordStageMs
                + " modeMs=" + viewApply.ModeStageMs
                + " sortMs=" + viewApply.SortStageMs
                + " materializeMs=" + viewApply.ViewMaterializeMs);
        }
        MainChartListColumnSelection columnSelection = detailMainChartList.LoadColumnSetting(
            request.ColumnSettingMode,
            request.CurrentTreeMode);
        PlaylistDetailTerminalCommitResult commit;
        PerformanceInteraction performanceInteraction = PerformanceInteraction.Existing(
            "playlist_detail",
            request.BuildRequest.RequestVersion,
            request.BuildRequest.RequestVersion);
        try
        {
            if (Net10PerformanceLog.IsEnabled)
            {
                Net10PerformanceLog.Write(
                    performanceInteraction,
                    "terminal_apply_started",
                    "rows=" + viewApply.ViewCount);
            }
            commit = ApplyDetailTerminal(new PlaylistDetailTerminalRequest
            {
                BuildRequest = request.BuildRequest,
                ReplaceSource = replaceSource,
                SourceRows = sourceRows,
                CurrentTable = currentTable,
                CurrentSelectionScope = request.BuildRequest.Identity.SelectionScope,
                CurrentFolderName = currentFolderName,
                CurrentFilterType = currentFilterType,
                ViewRows = viewApply.FinalRows,
                ColumnSelection = columnSelection,
                MainRowsRequest = new MainChartListRowsApplyRequest
                {
                    Rows = viewApply.FinalRows,
                    ColumnsSettings = columnSelection.ColumnsSettings,
                    SelectionPolicy = MainChartListSelectionPolicy.Reset,
                    Summary = IsPlaylistSummaryModeRequested
                    ? MainChartListSummaryUpdate.Preserve()
                    : MainChartListSummaryUpdate.NormalRows(viewApply.FinalRows),
                    ColumnSettingReuse = columnSelection.Reused,
                    ColumnPreparationMs = columnSelection.ElapsedMs,
                    TerminalStageStartMs = stageStartMs,
                    Stopwatch = request.Stopwatch,
                    OperationContextMode = columnSelection.AppliedMode
                },
                CancellationToken = request.CancellationToken
            });
        }
        catch (PlaylistDetailTerminalPublishException ex) when (!ex.OwnershipTransferred)
        {
            MainChartListViewModel.DisposeRows(viewApply.FinalRows);
            throw;
        }
        catch (PlaylistDetailTerminalPublishException)
        {
            throw;
        }
        catch
        {
            MainChartListViewModel.DisposeRows(viewApply.FinalRows);
            throw;
        }
        if (!commit.Applied)
        {
            MainChartListViewModel.DisposeRows(viewApply.FinalRows);
            return new PlaylistDetailTerminalApplyResult(false, viewApply, null, 0);
        }
        if (Net10PerformanceLog.IsEnabled)
        {
            Net10PerformanceLog.Write(
                performanceInteraction,
                "terminal_applied",
                "rows=" + viewApply.ViewCount
                + " sourceGeneration=" + commit.SourceGenerationId);
        }
        try
        {
            var publishExceptions = new List<Exception>();
            if (replaceSource)
            {
                TryPublish(
                    () => LogDetailWeakReferenceStatus("before_source_replace"),
                    publishExceptions);
                TryPublish(
                    () => detailRetentionLog("playlist_source_replace action=replace generationId="
                        + commit.SourceGenerationId
                        + " previousGenerationId=" + commit.PreviousSourceGenerationId
                        + " sourceCount=" + (sourceRows?.Count ?? 0)
                        + " disposedCount=" + (commit.PreviousSourceRows?.Count ?? 0)
                        + " playlistSourceRowCount=" + (sourceRows?.Count ?? 0)
                        + " playlistViewRowCount=" + CountDetailRows(viewApply.FinalRows)),
                    publishExceptions);
            }
            TryPublish(
                () => LogDetailWeakReferenceStatus("before_view_replace"),
                publishExceptions);
            TryPublish(
                () => detailRetentionLog((viewApply.FinalRows.Count == 0 ? "playlist_view_clear " : "playlist_view_replace ")
                    + "generationId=" + commit.ViewGenerationId
                    + " previousGenerationId=" + commit.PreviousViewGenerationId
                    + " sourceCount=" + commit.SourceRowsAlive
                    + " viewCount=" + viewApply.FinalRows.Count
                    + " playlistSourceRowCount=" + commit.SourceRowsAlive
                    + " playlistViewRowCount=" + CountDetailRows(viewApply.FinalRows)
                    + " previousViewRowsReferenced=" + CountDetailRows(commit.PreviousViewRows)
                    + " disposedCount=" + CountDetailRows(commit.PreviousViewRows)
                    + " selectedIndex=" + detailMainChartList.SelectedIndex),
                publishExceptions);
            if (publishExceptions.Count > 0)
            {
                throw new AggregateException(publishExceptions);
            }
            return new PlaylistDetailTerminalApplyResult(
                true,
                viewApply,
                new PlaylistMainViewApplyResult(commit.MainRowsApply.ColumnSettingMs, 0L),
                commit.PreviousSourceRows?.Count ?? 0);
        }
        catch (Exception ex)
        {
            throw new PlaylistDetailTerminalPublishException(ex, ownershipTransferred: true, commit);
        }
    }

    internal bool TryCommitPlayHistoryRowsAndSource(
        PlaylistSourceRetirementRequest request,
        Action commitRows,
        out PlaylistSourceClearCommitResult sourceClear)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        if (commitRows == null)
        {
            throw new ArgumentNullException(nameof(commitRows));
        }
        lock (DetailBuildState.SyncRoot)
        {
            if (request.RequestVersion != DetailBuildState.RequestVersion)
            {
                sourceClear = null;
                return false;
            }
            commitRows();
            sourceClear = DetailBuildState.CommitPreparedSourceRetirement(
                DetailViewState,
                request);
            return sourceClear != null;
        }
    }

    internal PlaylistSourceRetirementRequest PrepareDetailSourceRetirementForRegularView()
    {
        PlaylistSourceRetirementRequest request =
            PrepareDetailSourceRetirementWithoutPublishing();
        PublishDetailSourceRetirement(request);
        return request;
    }

    internal PlaylistSourceRetirementRequest PrepareDetailSourceRetirementWithoutPublishing()
    {
        return DetailBuildState.PrepareSourceRetirement();
    }

    internal void PublishDetailSourceRetirement(PlaylistSourceRetirementRequest request)
    {
        DetailBuildState.PublishSourceRetirement(request);
    }

    internal PlaylistSourceClearCommitResult CommitDetailSourceClearForRegularView(
        PlaylistSourceRetirementRequest request)
    {
        return DetailBuildState.CommitPreparedSourceRetirement(DetailViewState, request);
    }

    internal void PublishDetailSourceClearForRegularView(PlaylistSourceClearCommitResult commit)
    {
        if (commit != null)
        {
            PublishSourceClear(commit);
        }
    }

    internal void PublishPlayHistorySourceClear(PlaylistSourceClearCommitResult commit)
    {
        DetailBuildState.PublishSourceClear(commit);
    }

    internal void LogPlayHistorySourceClear(PlaylistSourceClearCommitResult commit)
    {
        if (commit == null)
        {
            throw new ArgumentNullException(nameof(commit));
        }
        LogDetailWeakReferenceStatus("before_source_clear");
        detailRetentionLog("playlist_source_replace action=clear generationId="
            + commit.PreviousGenerationId
            + " sourceCount=0 disposedCount="
            + (commit.SourceRows?.Count ?? 0)
            + " playlistSourceRowCount=0 playlistViewRowCount="
            + CountDetailRows(commit.ViewRows));
    }

    private void PublishSourceClear(PlaylistSourceClearCommitResult commit)
    {
        if (commit == null)
        {
            throw new ArgumentNullException(nameof(commit));
        }
        var publishExceptions = new List<Exception>();
        TryPublish(() => DetailBuildState.PublishSourceClear(commit), publishExceptions);
        TryPublish(() => LogDetailWeakReferenceStatus("before_source_clear"), publishExceptions);
        TryPublish(
            () => detailRetentionLog("playlist_source_replace action=clear generationId="
                + commit.PreviousGenerationId
                + " sourceCount=0 disposedCount="
                + (commit.SourceRows?.Count ?? 0)
                + " playlistSourceRowCount=0 playlistViewRowCount="
                + CountDetailRows(commit.ViewRows)),
            publishExceptions);
        if (publishExceptions.Count > 0)
        {
            throw new AggregateException(publishExceptions);
        }
    }

    private void LogDetailWeakReferenceStatus(string reason)
    {
        WeakReference<List<PlaylistDetailSourceRow>> sourceReference;
        WeakReference<System.Collections.IList> viewReference;
        long sourceGeneration;
        long viewGeneration;
        lock (DetailViewState.SyncRoot)
        {
            sourceReference = DetailViewState.Source.PreviousRowsWeakReference;
            viewReference = DetailViewState.View.PreviousRowsWeakReference;
            sourceGeneration = DetailViewState.Source.PreviousGenerationId;
            viewGeneration = DetailViewState.View.PreviousGenerationId;
        }
        List<PlaylistDetailSourceRow> sourceRows = null;
        System.Collections.IList viewRows = null;
        bool sourceAlive = sourceReference != null && sourceReference.TryGetTarget(out sourceRows);
        bool viewAlive = viewReference != null && viewReference.TryGetTarget(out viewRows);
        detailRetentionLog("playlist_weak_reference_check reason="
            + reason
            + " sourceGenerationId=" + sourceGeneration
            + " sourceAlive=" + sourceAlive
            + " playlistSourceRowCount=" + (sourceAlive ? sourceRows.Count : 0)
            + " viewGenerationId=" + viewGeneration
            + " viewAlive=" + viewAlive
            + " playlistViewRowCount=" + (viewAlive ? CountDetailRows(viewRows) : 0));
    }

    internal PlaylistDetailTerminalCommitResult ApplyDetailTerminal(
        PlaylistDetailTerminalRequest request)
    {
        if (request?.BuildRequest == null || request.ViewRows == null || request.MainRowsRequest == null)
        {
            throw new ArgumentException("A complete playlist detail terminal request is required.", nameof(request));
        }
        if (request.ReplaceSource && request.SourceRows == null)
        {
            throw new ArgumentException("Source rows are required when replacing the playlist source.", nameof(request));
        }
        var result = new PlaylistDetailTerminalCommitResult();
        bool CommitDetailPresentation(Action commitRows)
        {
            bool committed;
            lock (DetailBuildState.SyncRoot)
            {
                if (request.CancellationToken.IsCancellationRequested
                    || request.BuildRequest.RequestVersion != DetailBuildState.RequestVersion)
                {
                    return false;
                }
                DetailViewState.CommitTerminal(
                    request,
                    result,
                    commitRows,
                    () =>
                    {
                        result.MainTablePresentationCommit = CommitMainTablePresentationWithoutNotification(
                            request.ColumnSelection,
                            playlistDetailActive: true,
                            playlistSummaryActive: false);
                        result.AppliedColumnMode = request.ColumnSelection.AppliedMode;
                    });
                committed = true;
            }
            if (committed)
            {
                detailMainChartList.CommitAppliedColumnMode(request.ColumnSelection.AppliedMode);
                detailMainChartList.CommitCompletion(new MainChartListCompletion(
                    request.BuildRequest.MainViewBuildRequestId,
                    Stopwatch.GetTimestamp(),
                    Thread.CurrentThread.ManagedThreadId,
                    request.BuildRequest.Mode,
                    request.MainRowsRequest.Stopwatch.ElapsedMilliseconds));
            }
            return committed;
        }

        void PublishDetailPresentation()
        {
            if (result.MainTablePresentationCommit != null)
            {
                PublishMainTablePresentation(result.MainTablePresentationCommit);
            }
        }

        MainChartListPresentationApplyResult applied;
        try
        {
            applied = detailMainChartList.ApplyPresentation(
                request.MainRowsRequest,
                CommitDetailPresentation,
                PublishDetailPresentation);
        }
        catch (MainChartListPresentationPublishException ex)
        {
            result.MainRowsApply = ex.RowsApply;
            throw new PlaylistDetailTerminalPublishException(
                ex.InnerException ?? ex,
                ownershipTransferred: true,
                result);
        }
        if (!applied.WasApplied)
        {
            return result;
        }
        result.MainRowsApply = applied.RowsApply;
        return result;
    }

    private static int CountDetailRows(System.Collections.IEnumerable rows)
    {
        if (rows is IChartListViewMetadata metadata)
        {
            return metadata.RowCount;
        }
        int count = 0;
        if (rows != null)
        {
            foreach (object row in rows)
            {
                if (row is PlaylistDetailRow) count++;
            }
        }
        return count;
    }
}
