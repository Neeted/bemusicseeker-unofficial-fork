using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
        lock (DetailBuildState.SyncRoot)
            lock (DetailViewState.SyncRoot)
            {
                // 明示した sort/filter の入力だけを固定します。通常の確定差分ではこの経路を呼びません。
                sourceRows = [.. DetailViewState.Source.Rows ?? []];
                PlaylistDetailDependencies current = EnsureDetailDependencies();
                var dependencies = new PlaylistDetailDependencies(sourceRows, ReportDetailWork) { ReferenceInput = current.ReferenceInput };
                dependencies.ReferenceCommits.AddRange(current.ReferenceCommits);
                dependencies.PendingDetailMd5s.UnionWith(current.PendingDetailMd5s);
                dependencies.PendingDetailSha256s.UnionWith(current.PendingDetailSha256s);
                foreach (KeyValuePair<object, PlaylistPendingReferenceProjection> pair in current.PendingReferenceProjections)
                {
                    var projection = new PlaylistPendingReferenceProjection
                    {
                        Snapshot = pair.Value.Snapshot,
                        Keys = pair.Value.Keys,
                        ChartInfoVersion = pair.Value.ChartInfoVersion
                    };
                    projection.Changes.AddRange(pair.Value.Changes);
                    dependencies.PendingReferenceProjections.Add(ReferenceEquals(pair.Key, current.DetailProjectionKey)
                        ? dependencies.DetailProjectionKey : pair.Key, projection);
                }
                request.BuildRequest.InputDependencies = dependencies;
                request.BuildRequest.CatalogCommits.AddRange(deferredCatalogCommits);
                request.BuildRequest.EntryCommits.AddRange(deferredEntryCommits.Where(receipt => ReferenceEquals(receipt.Table, request.BuildRequest.Identity.Table)));
                request.BuildRequest.ScoreChanges.AddRange(current.PendingScores);
                request.BuildRequest.CatalogCommits.Sort((left, right) => left.Revision.CompareTo(right.Revision));
                request.BuildRequest.EntryCommits.Sort((left, right) => left.Revision.CompareTo(right.Revision));
                request.BuildRequest.ScoreChanges.Sort((left, right) => left.Version.CompareTo(right.Version));
                request.BuildRequest.Identity = WithDetailVersions(request.BuildRequest.Identity,
                    DetailViewState.Source.LastBuiltLibraryIndexVersion, DetailViewState.Source.LastBuiltPlaylistRevision,
                    DetailViewState.Source.LastBuiltScoreSnapshotVersion, DetailViewState.Source.LastBuiltChartInfoIndexVersion);
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

    private PlaylistViewApplyResult BuildDetailView(
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
            out long viewMaterializeMs,
            onlyNotOwned: request.BuildRequest.Identity.FilterType == PlaylistDetailFilter.PlaylistNotOwnedFilterSelected,
            observeWork: ReportDetailWork, identity: request.BuildRequest.Identity);
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
        var performanceInteraction = PerformanceInteraction.Existing(
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
                    SelectionPolicy = MainChartListSelectionPolicy.Preserve,
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

    /// <summary>source退役後の保持状況を任意診断し、診断失敗を確定済み行の結果へ加えません。</summary>
    internal void LogPlayHistorySourceClear(PlaylistSourceClearCommitResult commit)
    {
        if (commit == null)
        {
            throw new ArgumentNullException(nameof(commit));
        }
        DiagnoseSourceClearBestEffort(() => LogDetailWeakReferenceStatus("before_source_clear"));
        DiagnoseSourceClearBestEffort(() => detailRetentionLog("playlist_source_replace action=clear generationId="
            + commit.PreviousGenerationId
            + " sourceCount=0 disposedCount="
            + (commit.SourceRows?.Count ?? 0)
            + " playlistSourceRowCount=0 playlistViewRowCount="
            + CountDetailRows(commit.ViewRows)));
    }

    private void PublishSourceClear(PlaylistSourceClearCommitResult commit)
    {
        if (commit == null) { throw new ArgumentNullException(nameof(commit)); }
        try { DetailBuildState.PublishSourceClear(commit); }
        finally { LogPlayHistorySourceClear(commit); }
    }

    private static void DiagnoseSourceClearBestEffort(Action diagnostic)
    {
        try { diagnostic(); }
        catch (Exception failure)
        {
            try { Ribbit.Logging.NLogWrapper.FileLogger?.Warn(failure, "playlist_source_clear_diagnostic_failed"); }
            catch { /* 診断失敗は確定済み行の所有やsource公開結果を変更しない。 */ }
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
        System.Threading.Tasks.Task preparedDataChanged = null;
        bool followUpOnWorker = false;
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
                lock (DetailViewState.SyncRoot)
                {
                    long requiredReferences = detailDataSource?.ReferenceRevision ?? 0;
                    if (!ReferenceEquals(preparedDataChanged, request.BuildRequest.DataChanged.Task)
                        || detailDataSource != null && !HasRequiredDetailCandidateVersions(request.BuildRequest, requiredReferences))
                    {
                        request.BuildRequest.WaitingForData = true;
                        followUpOnWorker = true;
                        return false;
                    }
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
                deferredEntryCommits.RemoveAll(receipt => !ReferenceEquals(receipt.Table, DetailViewState.Source.CurrentTable)
                    || receipt.Revision <= DetailViewState.Source.LastBuiltPlaylistRevision);
                committed = true;
            }
            if (committed)
            {
                PublishDetailChanges();
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

        MainChartListPresentationApplyResult applied = default;
        int requestedScoreDelivery = -1;
        long requestedReferenceDelivery = -1;
        while (true)
        {
            bool prepared;
            lock (DetailBuildState.SyncRoot)
            {
                if (request.CancellationToken.IsCancellationRequested
                    || request.BuildRequest.RequestVersion != DetailBuildState.RequestVersion) { break; }
                lock (DetailViewState.SyncRoot)
                {
                    prepared = detailDataSource == null || TryPrepareDetailCandidate(request.BuildRequest,
                        request.SourceRows, request.ViewRows as PlaylistDetailVirtualView, request.CancellationToken);
                    // 準備と同じlock内で旧signalを捕捉し、配送がUI採用に先行しても待機の起床を失いません。
                    preparedDataChanged = request.BuildRequest.DataChanged.Task;
                }
            }
            applied = default;
            followUpOnWorker = false;
            if (prepared)
            {
                try
                {
                    // UIは既存版/signalの純粋再検査と、一回の採用・公開・選択復元だけを担当します。
                    playlistRestoreUiApplyScheduler(() => applied = detailMainChartList.ApplyPresentation(
                        request.MainRowsRequest,
                        CommitDetailPresentation,
                        PublishDetailPresentation)).GetAwaiter().GetResult();
                }
                catch (MainChartListPresentationPublishException ex)
                {
                    result.MainRowsApply = ex.RowsApply;
                    throw new PlaylistDetailTerminalPublishException(
                        ex.InnerException ?? ex, ownershipTransferred: true, result);
                }
            }
            if (followUpOnWorker) { continue; }
            if (applied.WasApplied || !request.BuildRequest.WaitingForData
                || request.BuildRequest.RequestVersion != DetailBuildState.RequestVersion) { break; }
            int scorePosition = request.BuildRequest.Identity.ScoreSnapshotVersion;
            if (requestedScoreDelivery > scorePosition)
            {
                throw new InvalidOperationException("Playlist score delivery completed without covering the required position.");
            }
            int requiredScorePosition = detailDataSource.ScoreSnapshotVersion;
            long referencePosition = request.BuildRequest.InputDependencies?.ReferenceInput?.Revision ?? 0;
            long requiredReferencePosition = request.BuildRequest.InputDependencies?.PendingReferenceProjections.Values
                .Select(value => value.Snapshot?.Revision ?? 0).DefaultIfEmpty().Max() ?? 0;
            requiredReferencePosition = Math.Max(requiredReferencePosition, request.BuildRequest.RequiredReferenceRevision);
            if (requestedReferenceDelivery > referencePosition)
            { throw new InvalidOperationException("Playlist reference delivery completed without covering the required position."); }
            if (requiredScorePosition > scorePosition || requiredReferencePosition > referencePosition)
            {
                if (detailDataSource is not PlaylistDetailDataSource attached || libraryChangeDelivery == null)
                { throw new InvalidOperationException("Playlist library change delivery is not connected."); }
                requestedScoreDelivery = requiredScorePosition;
                requestedReferenceDelivery = requiredReferencePosition;
                // build workerだけが待機します。UI/modelの排他を保持せず、共有drain自体は取消しません。
                libraryChangeDelivery(attached.Library, request.CancellationToken)
                    .WaitAsync(request.CancellationToken).GetAwaiter().GetResult();
                continue;
            }
            // 本番では build worker 上です。UI とモデルの排他を解放して、確定結果の配送だけを待ちます。
            preparedDataChanged.Wait(request.CancellationToken);
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
                if (row is PlaylistDetailRow)
                {
                    count++;
                }
            }
        }
        return count;
    }
}
