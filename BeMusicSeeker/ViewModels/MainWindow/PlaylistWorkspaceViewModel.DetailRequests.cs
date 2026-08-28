using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Diagnostics;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    private const int DetailBuildCoalescingWindowMs = 50;

    private const long DetailOpenSlowLogThresholdMs = 1000L;

    private long lastDetailBuildCompletedTimestamp;

    private long lastDetailBuildElapsedMs;

    internal long LastDetailBuildCompletedTimestamp => Interlocked.Read(ref lastDetailBuildCompletedTimestamp);

    internal long LastDetailBuildElapsedMs => Interlocked.Read(ref lastDetailBuildElapsedMs);

    internal int RequestDetailRefresh(
        MainViewUpdateMode mode,
        MainViewUpdateMode requestedMode,
        MainViewUpdateMode currentTreeMode,
        bool useCoalescingWindow,
        PlaylistOpenReadinessSnapshot openReadiness)
    {
        IPlaylistDetailDataSource dataSource = Volatile.Read(ref detailDataSource)
            ?? throw new InvalidOperationException("Playlist detail data source is not attached.");
        PlaylistBuildRequest request;
        PlaylistBuildQueueRegisterResult registerResult;
        lock (playlistDetailSelectionSyncRoot)
        {
            PlaylistDetailSelection selection = playlistDetailSelection;
            if (selection == null)
            {
                return 0;
            }
            BMSTable table = selection.Table;
            PlaylistDetailSelectionScope selectionScope = selection.Scope;
            string folderName = selection.FolderName;
            PlaylistDetailFilter filterType = selection.Filter;
            ChartListFilterSnapshot currentFilters = CapturePlaylistDetailFilterSnapshot();
            ChartListSortParameters currentSort = CapturePlaylistDetailSortParameters();
            string keywordFilter = currentFilters.KeywordFilter ?? string.Empty;
            ChartModeFilter modeFilter = currentFilters.ModeFilter;
            string sortColumnName = currentSort?.ColumnsName ?? string.Empty;
            ListSortDirection sortDirection = currentSort?.Direction ?? ListSortDirection.Ascending;

            lock (DetailBuildState.SyncRoot)
            {
                long playlistRevision;
                int lastBuiltScoreSnapshotVersion;
                PlaylistRequestIdentity? currentViewIdentity;
                lock (DetailViewState.SyncRoot)
                {
                    playlistRevision = DetailViewState.Source.PlaylistContentRevision;
                    lastBuiltScoreSnapshotVersion = DetailViewState.Source.LastBuiltScoreSnapshotVersion;
                    currentViewIdentity = DetailViewState.View.CurrentIdentity;
                }
                var sortParameters = new ChartListSortParameters
                {
                    ColumnsName = sortColumnName,
                    Direction = sortDirection
                };
                request = new PlaylistBuildRequest
                {
                    MainViewBuildRequestId = MainViewBuildRequestSequence.Next(),
                    Mode = mode,
                    RequestedMode = requestedMode,
                    Filters = new ChartListFilterSnapshot(keywordFilter, modeFilter),
                    SortParameters = sortParameters,
                    CurrentTreeMode = currentTreeMode,
                    OpenReadiness = openReadiness,
                    Identity = PlaylistRequestFactory.CreateIdentity(
                        table,
                        selectionScope,
                        folderName,
                        filterType,
                        keywordFilter,
                        modeFilter,
                        sortParameters,
                        dataSource.OwnedChartCollectionVersion,
                        playlistRevision,
                        dataSource.ScoreSnapshotVersion,
                        dataSource.ChartInfoIndexVersion,
                        hasResolvedSelection: true),
                    UseCoalescingWindow = useCoalescingWindow
                };
                registerResult = PlaylistDetailBuildQueueCoordinator.RegisterRequest(
                    DetailBuildState,
                    request,
                    currentViewIdentity,
                    lastBuiltScoreSnapshotVersion,
                    isShutdownRequested: false);
            }
        }

        detailRetentionLog("playlist_source_build requested version=" + request.RequestVersion
            + " mode=" + request.Mode
            + " scoreSnapshotVersion=" + request.Identity.ScoreSnapshotVersion
            + " lastBuiltScoreSnapshotVersion=" + registerResult.LastBuiltScoreSnapshotVersion);
        if (registerResult.DeduplicatedTarget != null)
        {
            detailRetentionLog("playlist_request_deduplicated target=" + registerResult.DeduplicatedTarget
                + " version=" + request.RequestVersion
                + " mode=" + request.Mode
                + " requestedMode=" + request.RequestedMode);
            return request.RequestVersion;
        }

        TrackDetailOpenRequest(request);
        try
        {
            registerResult.PreviousCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        if (registerResult.StartWorker)
        {
            Task.Run(ProcessPendingDetailRequests).Logging("ProcessPendingPlaylistDetailRequests");
        }
        return request.RequestVersion;
    }

    internal long IncrementDetailContentRevision(string reason)
    {
        long nextRevision;
        lock (DetailViewState.SyncRoot)
        {
            DetailViewState.Source.PlaylistContentRevision++;
            nextRevision = DetailViewState.Source.PlaylistContentRevision;
        }
        detailRetentionLog("playlist_revision incremented revision=" + nextRevision + " reason=" + reason);
        return nextRevision;
    }

    internal void CancelDetailBuilds()
    {
        PlaylistDetailBuildQueueCoordinator.CancelForShutdown(DetailBuildState);
    }

    internal bool IsDetailBuildIdle => PlaylistDetailBuildQueueCoordinator.IsIdle(DetailBuildState);

    /// <summary>
    /// Returns a task that completes when the identified detail request reaches any terminal state.
    /// </summary>
    internal Task WaitForDetailRequestCompletionAsync(int requestVersion)
        => PlaylistDetailBuildQueueCoordinator.WaitForRequestCompletionAsync(DetailBuildState, requestVersion);

    /// <summary>
    /// Returns a task that completes after the captured detail-build worker lifecycle has stopped.
    /// </summary>
    internal Task WaitForDetailBuildIdleAsync()
        => PlaylistDetailBuildQueueCoordinator.WaitForIdleAsync(DetailBuildState);

    internal string DescribeDetailBuildState(Func<bool, string> formatBool)
    {
        return PlaylistDetailBuildQueueCoordinator.FormatDiagnostics(DetailBuildState, formatBool);
    }

    private PlaylistBuildRequest CoalesceDetailRequest(PlaylistBuildRequest request)
    {
        if (request == null || !request.UseCoalescingWindow || DetailBuildCoalescingWindowMs <= 0)
        {
            return request;
        }
        PlaylistBuildQueueCoalesceResult result = PlaylistDetailBuildQueueCoordinator.CoalescePendingRequest(
            DetailBuildState,
            request,
            DetailBuildCoalescingWindowMs);
        return result.CancelledForShutdown ? null : result.Request;
    }

    private void ProcessPendingDetailRequests()
    {
        while (true)
        {
            if (!PlaylistDetailBuildQueueCoordinator.TryTakeNextRequestOrStopWorker(
                DetailBuildState,
                out PlaylistBuildRequest request))
            {
                return;
            }
            request = CoalesceDetailRequest(request);
            if (request == null)
            {
                PlaylistDetailBuildQueueCoordinator.FinishWorkerAfterFailure(DetailBuildState);
                return;
            }
            var buildCancellation = new CancellationTokenSource();
            if (!PlaylistDetailBuildQueueCoordinator.TryBeginIteration(
                DetailBuildState,
                request,
                buildCancellation,
                isShutdownRequested: false))
            {
                buildCancellation.Dispose();
                PlaylistDetailBuildQueueCoordinator.FinishWorkerAfterFailure(DetailBuildState);
                return;
            }
            ExceptionDispatchInfo failure = null;
            try
            {
                MarkDetailOpenBuildStarted(request);
                BuildDetailViewAndApply(request, buildCancellation.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                PlaylistDetailBuildQueueCoordinator.CompleteIteration(DetailBuildState, buildCancellation, request);
                buildCancellation.Dispose();
            }
            if (failure != null)
            {
                if (PlaylistDetailBuildQueueCoordinator.FinishWorkerAfterFailure(DetailBuildState))
                {
                    Task.Run(ProcessPendingDetailRequests).Logging("ProcessPendingPlaylistDetailRequestsAfterFailure");
                }
                failure.Throw();
            }
        }
    }

    private bool BuildDetailViewAndApply(PlaylistBuildRequest request, CancellationToken cancellationToken)
    {
        PlaylistDetailBuildStateSnapshot stateSnapshot;
        lock (DetailViewState.SyncRoot)
        {
            List<PlaylistDetailSourceRow> sourceRows = DetailViewState.Source.Rows;
            stateSnapshot = new PlaylistDetailBuildStateSnapshot(
                hasSourceRows: sourceRows != null,
                sourceRowCount: sourceRows?.Count ?? 0,
                DetailViewState.Source.CurrentTable,
                DetailViewState.Source.CurrentSelectionScope,
                DetailViewState.Source.CurrentFolderName,
                DetailViewState.Source.CurrentFilterType,
                DetailViewState.Source.LastBuiltLibraryIndexVersion,
                DetailViewState.Source.LastBuiltPlaylistRevision,
                DetailViewState.Source.LastBuiltScoreSnapshotVersion,
                DetailViewState.Source.LastBuiltChartInfoIndexVersion,
                DetailViewState.Source.CurrentIdentity,
                DetailViewState.View.CurrentIdentity);
        }
        PlaylistDetailBuildDecision decision = PlaylistDetailBuildDecisionService.Decide(request, stateSnapshot);
        if (decision.Action == PlaylistDetailBuildAction.PatchChartInfoThenApplyView
            && TryPatchDetailSourceChartInfo(request, cancellationToken, out _, out _, out _, out _))
        {
            return ApplyDetailViewWithoutSourceRebuild(request, cancellationToken);
        }
        if (decision.Action == PlaylistDetailBuildAction.RebuildSource)
        {
            request.LastBuiltScoreSnapshotVersion = decision.LastBuiltScoreSnapshotVersion;
            request.SourceInvalidationReason = decision.SourceInvalidationReason;
            return RebuildDetailSource(request, cancellationToken);
        }
        return ApplyDetailViewWithoutSourceRebuild(request, cancellationToken);
    }

    private bool ApplyDetailViewWithoutSourceRebuild(
        PlaylistBuildRequest request,
        CancellationToken cancellationToken)
    {
        ChartListFilterSnapshot filters = request.Filters ?? ChartListFilterSnapshot.Default;
        var stopwatch = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        PlaylistDetailTerminalApplyResult result = ApplyDetailFromCurrentSource(
            new PlaylistDetailPresentationRequest
            {
                BuildRequest = request,
                Mode = request.Mode,
                KeywordFilter = filters.KeywordFilter,
                ModeFilter = filters.ModeFilter,
                SortParameters = CloneDetailSortParameters(request.SortParameters),
                ColumnSettingMode = ResolveDetailColumnSettingMode(request.Identity.FilterType),
                CurrentTreeMode = request.CurrentTreeMode,
                Stopwatch = stopwatch,
                CancellationToken = cancellationToken
            });
        if (result.Applied)
        {
            MarkDetailOpenBuildCompleted(request, result.ViewApply.ViewCount);
            CompleteDetailBuild(stopwatch, request, result.ViewApply, result.MainViewApply, folderStageMs: 0L);
        }
        return true;
    }

    private bool RebuildDetailSource(PlaylistBuildRequest request, CancellationToken cancellationToken)
    {
        int requestVersion = request.RequestVersion;
        var stopwatch = Stopwatch.StartNew();
        List<PlaylistDetailSourceRow> sourceRows = null;
        bool gateEntered = false;
        string cancellationStage = "before_start";
        try
        {
            DetailBuildState.BuildGate.Wait(cancellationToken);
            gateEntered = true;
            if (!PlaylistDetailBuildQueueCoordinator.IsLatestRequest(DetailBuildState, requestVersion))
            {
                return true;
            }
            if (!request.Identity.HasResolvedSelection)
            {
                return false;
            }

            IPlaylistDetailDataSource dataSource = Volatile.Read(ref detailDataSource)
                ?? throw new InvalidOperationException("Playlist detail data source is not attached.");
            cancellationStage = "hash_index";
            long indexStartMs = stopwatch.ElapsedMilliseconds;
            PlaylistLibraryResolveIndexSnapshot resolveIndex = dataSource.GetResolveIndexSnapshot(
                cancellationToken,
                out bool cacheHit,
                out int staleRetryCount);
            long indexMs = stopwatch.ElapsedMilliseconds - indexStartMs;
            var indexSnapshot = new PlaylistLibraryIndexSnapshot
            {
                Version = request.Identity.LibraryIndexVersion,
                BuildElapsedMs = resolveIndex.BuildElapsedMs,
                ResolveIndex = resolveIndex
            };
            long sourceStartMs = stopwatch.ElapsedMilliseconds;
            PlaylistSourceBuildResult sourceBuild = BuildDetailSourceRows(
                request.Identity.Table,
                request.Identity.SelectionScope,
                request.Identity.FolderName,
                request.Identity.FilterType == PlaylistDetailFilter.PlaylistNotOwnedFilterSelected,
                indexSnapshot,
                cancellationToken,
                ref cancellationStage);
            sourceRows = sourceBuild.SourceRows;
            if (cancellationToken.IsCancellationRequested
                || !PlaylistDetailBuildQueueCoordinator.IsLatestRequest(DetailBuildState, requestVersion))
            {
                return true;
            }

            cancellationStage = "ui_apply";
            ChartListFilterSnapshot filters = request.Filters ?? ChartListFilterSnapshot.Default;
            PlaylistDetailTerminalApplyResult terminalResult;
            try
            {
                terminalResult = ApplyDetailFromRebuiltSource(
                    new PlaylistDetailRebuiltPresentationRequest
                    {
                        BuildRequest = request,
                        Mode = request.Mode,
                        KeywordFilter = filters.KeywordFilter,
                        ModeFilter = filters.ModeFilter,
                        SortParameters = CloneDetailSortParameters(request.SortParameters),
                        ColumnSettingMode = ResolveDetailColumnSettingMode(request.Identity.FilterType),
                        CurrentTreeMode = request.CurrentTreeMode,
                        Stopwatch = stopwatch,
                        CancellationToken = cancellationToken,
                        SourceRows = sourceRows,
                        CurrentTable = request.Identity.Table,
                        CurrentFolderName = request.Identity.FolderName,
                        CurrentFilterType = request.Identity.FilterType
                    });
            }
            catch (PlaylistDetailTerminalPublishException)
            {
                sourceRows = null;
                throw;
            }
            if (!terminalResult.Applied)
            {
                return true;
            }
            sourceRows = null;
            MarkDetailOpenBuildCompleted(request, terminalResult.ViewApply.ViewCount);
            CompleteDetailBuild(
                stopwatch,
                request,
                terminalResult.ViewApply,
                terminalResult.MainViewApply,
                sourceBuild.SourceMaterializeMs);
            detailRetentionLog("playlist_source_build completed version=" + requestVersion
                + " sourceCount=" + sourceBuild.SourceCount
                + " viewCount=" + terminalResult.ViewApply.ViewCount
                + " libraryIndexMs=" + indexMs
                + " libraryIndexAccess=" + (cacheHit ? "cached" : "inline")
                + " libraryIndexBuildMs=" + resolveIndex.BuildElapsedMs
                + " staleRetries=" + staleRetryCount
                + " sourceMaterializeMs=" + (stopwatch.ElapsedMilliseconds - sourceStartMs));
            return true;
        }
        catch (OperationCanceledException)
        {
            detailRetentionLog("playlist_source_build cancelled version=" + requestVersion
                + " stage=" + cancellationStage);
            return true;
        }
        finally
        {
            if (gateEntered)
            {
                DetailBuildState.BuildGate.Release();
            }
        }
    }

    private void CompleteDetailBuild(
        Stopwatch stopwatch,
        PlaylistBuildRequest request,
        PlaylistViewApplyResult viewApply,
        PlaylistMainViewApplyResult mainViewApply,
        long folderStageMs)
    {
        long completedTimestamp = Stopwatch.GetTimestamp();
        Interlocked.Exchange(ref lastDetailBuildCompletedTimestamp, completedTimestamp);
        Interlocked.Exchange(ref lastDetailBuildElapsedMs, stopwatch.ElapsedMilliseconds);
        detailRetentionLog("main_view_build mode=" + request.Mode
            + " requestedMode=" + request.RequestedMode
            + " parameterType=" + ResolveDetailParameterTypeForLog(request)
            + " folderMs=" + folderStageMs
            + " keywordMs=" + viewApply.KeywordStageMs
            + " modeMs=" + viewApply.ModeStageMs
            + " sortMs=" + viewApply.SortStageMs
            + " sortReuse=false"
            + " sortProfile=" + viewApply.SortProfile
            + " sortEngine=fast fastSortEnabled=true isPlaylistDetailView=true"
            + " columnMs=" + (mainViewApply?.ColumnStageMs ?? 0L)
            + " callbackMs=" + (mainViewApply?.CallbackStageMs ?? 0L)
            + " totalMs=" + stopwatch.ElapsedMilliseconds
            + " folderCount=" + viewApply.SourceCount
            + " keywordCount=" + viewApply.KeywordCount
            + " modeCount=" + viewApply.ModeCount
            + " viewCount=" + viewApply.ViewCount
            + " sortColumn=" + (request.SortParameters?.ColumnsName ?? "(default_title)")
            + " sortDirection=" + (request.SortParameters?.Direction.ToString() ?? "Ascending"));
    }

    private static string ResolveDetailParameterTypeForLog(PlaylistBuildRequest request)
    {
        if (request.Identity.Table == null)
        {
            return "(null)";
        }
        return request.Identity.FilterType == PlaylistDetailFilter.PlaylistNotOwnedFilterSelected
            ? nameof(BMSTable)
            : "Tuple`2";
    }

    private void TrackDetailOpenRequest(PlaylistBuildRequest request)
    {
        if (!IsDetailOpenRequest(request.RequestedMode))
        {
            return;
        }
        lock (DetailViewState.SyncRoot)
        {
            DetailViewState.CurrentOpenInteraction = new PlaylistOpenInteractionState
            {
                RequestVersion = request.RequestVersion,
                Identity = request.Identity,
                RequestedAtUtc = DateTime.UtcNow,
                Readiness = request.OpenReadiness
            };
        }
        detailRetentionLog("playlist_open_request requestVersion=" + request.RequestVersion
            + " requestedMode=" + request.RequestedMode
            + " mode=" + request.Mode
            + " table=" + FormatDetailTableNameForLog(request.Identity.Table)
            + " folder=" + FormatDetailFolderNameForLog(request.Identity.FolderName)
            + " filterType=" + request.Identity.FilterType);
        if (Net10PerformanceLog.IsEnabled)
        {
            PerformanceInteraction performanceInteraction = PerformanceInteraction.Existing(
                "playlist_detail",
                request.RequestVersion,
                request.RequestVersion);
            Net10PerformanceLog.Write(
                performanceInteraction,
                "input_accepted",
                "mode=" + request.Mode
                + " requestedMode=" + request.RequestedMode);
            Net10PerformanceLog.Write(performanceInteraction, "owner_queued");
        }
    }

    private void MarkDetailOpenBuildStarted(PlaylistBuildRequest request)
    {
        if (!IsDetailOpenRequest(request.RequestedMode))
        {
            return;
        }
        lock (DetailViewState.SyncRoot)
        {
            if (DetailViewState.CurrentOpenInteraction?.RequestVersion == request.RequestVersion
                && !DetailViewState.CurrentOpenInteraction.BuildStartedAtUtc.HasValue)
            {
                DetailViewState.CurrentOpenInteraction.BuildStartedAtUtc = DateTime.UtcNow;
            }
        }
        if (Net10PerformanceLog.IsEnabled)
        {
            Net10PerformanceLog.Write(
                PerformanceInteraction.Existing(
                    "playlist_detail",
                    request.RequestVersion,
                    request.RequestVersion),
                "owner_started");
        }
    }

    private void MarkDetailOpenBuildCompleted(PlaylistBuildRequest request, int viewCount)
    {
        if (!IsDetailOpenRequest(request.RequestedMode))
        {
            return;
        }
        DateTime completedAtUtc = DateTime.UtcNow;
        long sourceGeneration;
        long viewGeneration;
        lock (DetailViewState.SyncRoot)
        {
            if (DetailViewState.CurrentOpenInteraction?.RequestVersion != request.RequestVersion)
            {
                return;
            }
            DetailViewState.CurrentOpenInteraction.BuildStartedAtUtc ??= completedAtUtc;
            DetailViewState.CurrentOpenInteraction.BuildCompletedAtUtc = completedAtUtc;
            DetailViewState.CurrentOpenInteraction.ExpectedSourceGenerationId = DetailViewState.Source.GenerationId;
            DetailViewState.CurrentOpenInteraction.ExpectedViewGenerationId = DetailViewState.View.GenerationId;
            DetailViewState.CurrentOpenInteraction.ViewCount = viewCount;
            DetailViewState.CurrentOpenInteraction.VisibleCompletedLogged = false;
            sourceGeneration = DetailViewState.Source.GenerationId;
            viewGeneration = DetailViewState.View.GenerationId;
        }
        if (Net10PerformanceLog.IsEnabled)
        {
            PerformanceInteraction performanceInteraction = PerformanceInteraction.Existing(
                "playlist_detail",
                request.RequestVersion,
                request.RequestVersion);
            Net10PerformanceLog.Write(
                performanceInteraction,
                "view_applied",
                "rows=" + viewCount
                + " sourceGeneration=" + sourceGeneration
                + " viewGeneration=" + viewGeneration);
        }
    }

    public void TryLogDetailOpenVisibleCompleted(
        string checkpoint,
        long expectedSourceGenerationId,
        long expectedViewGenerationId)
    {
        PlaylistOpenInteractionState interaction;
        lock (DetailViewState.SyncRoot)
        {
            interaction = DetailViewState.CurrentOpenInteraction;
            if (interaction == null
                || interaction.VisibleCompletedLogged
                || !interaction.BuildCompletedAtUtc.HasValue
                || interaction.ExpectedSourceGenerationId != expectedSourceGenerationId
                || interaction.ExpectedViewGenerationId != expectedViewGenerationId)
            {
                return;
            }
            interaction.VisibleCompletedLogged = true;
        }
        DateTime visibleAtUtc = DateTime.UtcNow;
        long requestToBuildStartMs = interaction.BuildStartedAtUtc.HasValue
            ? (long)(interaction.BuildStartedAtUtc.Value - interaction.RequestedAtUtc).TotalMilliseconds
            : -1L;
        long requestToBuildCompleteMs = (long)(interaction.BuildCompletedAtUtc.Value - interaction.RequestedAtUtc).TotalMilliseconds;
        long requestToVisibleRenderMs = (long)(visibleAtUtc - interaction.RequestedAtUtc).TotalMilliseconds;
        long buildToVisibleRenderMs = (long)(visibleAtUtc - interaction.BuildCompletedAtUtc.Value).TotalMilliseconds;
        detailRetentionLog("playlist_open_visible completed requestVersion=" + interaction.RequestVersion
            + " checkpoint=" + checkpoint
            + " requestToBuildStartMs=" + requestToBuildStartMs
            + " requestToBuildCompleteMs=" + requestToBuildCompleteMs
            + " requestToVisibleRenderMs=" + requestToVisibleRenderMs
            + " buildToVisibleRenderMs=" + buildToVisibleRenderMs
            + " viewCount=" + interaction.ViewCount);
        if (Net10PerformanceLog.IsEnabled)
        {
            PerformanceInteraction performanceInteraction = PerformanceInteraction.Existing(
                "playlist_detail",
                interaction.RequestVersion,
                interaction.RequestVersion);
            Net10PerformanceLog.Write(
                performanceInteraction,
                "first_useful_visible",
                "checkpoint=" + checkpoint
                + " requestToVisibleMs=" + requestToVisibleRenderMs
                + " rows=" + interaction.ViewCount);
        }
        if (requestToVisibleRenderMs >= DetailOpenSlowLogThresholdMs)
        {
            detailRetentionLog("playlist_open_stage_detail requestVersion=" + interaction.RequestVersion
                + " checkpoint=" + checkpoint
                + " requestToVisibleRenderMs=" + requestToVisibleRenderMs
                + " thresholdMs=" + DetailOpenSlowLogThresholdMs);
        }
    }

    internal bool TryCreateDetailOpenVisibleTiming(
        long expectedSourceGenerationId,
        long expectedViewGenerationId,
        out TableFirstVisibleTiming timing)
    {
        timing = default;
        PlaylistOpenInteractionState interaction;
        lock (DetailViewState.SyncRoot)
        {
            interaction = DetailViewState.CurrentOpenInteraction;
            if (interaction == null
                || !interaction.BuildCompletedAtUtc.HasValue
                || interaction.ExpectedSourceGenerationId != expectedSourceGenerationId
                || interaction.ExpectedViewGenerationId != expectedViewGenerationId)
            {
                return false;
            }
        }
        long requestToBuildStartMs = interaction.BuildStartedAtUtc.HasValue
            ? (long)(interaction.BuildStartedAtUtc.Value - interaction.RequestedAtUtc).TotalMilliseconds
            : -1L;
        long requestToBuildCompleteMs = (long)(interaction.BuildCompletedAtUtc.Value - interaction.RequestedAtUtc).TotalMilliseconds;
        DateTime visibleAtUtc = DateTime.UtcNow;
        timing = new TableFirstVisibleTiming(
            interaction.RequestVersion,
            requestToBuildStartMs,
            requestToBuildCompleteMs,
            (long)(visibleAtUtc - interaction.RequestedAtUtc).TotalMilliseconds,
            (long)(visibleAtUtc - interaction.BuildCompletedAtUtc.Value).TotalMilliseconds,
            interaction.ViewCount);
        return true;
    }

    private static string FormatDetailFolderNameForLog(string folderName)
    {
        return folderName == null ? "(root)" : folderName.Length == 0 ? "(empty)" : folderName;
    }

    private static string FormatDetailTableNameForLog(BMSTable table)
    {
        return string.IsNullOrWhiteSpace(table?.name) ? "(null)" : table.name;
    }

    private static bool IsDetailOpenRequest(MainViewUpdateMode mode)
    {
        return mode == MainViewUpdateMode.PlaylistFilterSelected
            || mode == MainViewUpdateMode.PlaylistNotOwnedFilterSelected;
    }

    private static MainViewUpdateMode ResolveDetailColumnSettingMode(PlaylistDetailFilter filterType)
    {
        return filterType == PlaylistDetailFilter.PlaylistNotOwnedFilterSelected
            ? MainViewUpdateMode.PlaylistNotOwnedFilterSelected
            : MainViewUpdateMode.PlaylistFilterSelected;
    }

    private static ChartListSortParameters CloneDetailSortParameters(ChartListSortParameters value)
    {
        return value == null
            ? null
            : new ChartListSortParameters
            {
                ColumnsName = value.ColumnsName,
                Direction = value.Direction
            };
    }
}
