using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using Livet;
using Livet.Commands;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlayHistoryWorkflowOwner : ViewModel, ISettingsDialogPlayHistoryPort
{
    private long keywordQueuedRevision = -1L;
    private int keywordActiveCount;
    private long displayTargetQueuedRevision = -1L;
    private int displayTargetActiveCount;
    private ChartListSortParameters sortParameters;
    private ListenerCommand<PlayHistorySummaryCard> toggleSummaryFilterCommand;
    private Func<bool> isViewRefreshShutdownRequested;
    private Action<PlayHistoryViewRequest> refreshView;
    private bool isViewActive;
    internal PlayHistoryWorkflowOwner()
    {
        PresentationState.CurrentSortSnapshot = new SortSnapshot(null, null, revision: 0L);
    }

    internal PlayHistoryPresentationState PresentationState { get; } = new();

    internal event EventHandler SummaryFilterRefreshRequested;

    internal event EventHandler<MainChartListSortRequestedEventArgs> SortChanged;

    internal event EventHandler<MainChartListSortRequestedEventArgs> SortRefreshRequested;

    internal event EventHandler<PlayHistoryViewRequestActivatedEventArgs> PeriodRequestActivated;

    internal void ConfigureViewRefreshScheduler(
        Func<bool> isShutdownRequested,
        Action<PlayHistoryViewRequest> refresh)
    {
        isViewRefreshShutdownRequested = isShutdownRequested
            ?? throw new ArgumentNullException(nameof(isShutdownRequested));
        refreshView = refresh
            ?? throw new ArgumentNullException(nameof(refresh));
    }

    internal void QueueKeywordFilterRefresh(string identity, bool advanceRevision = true)
    {
        EnsureViewRefreshSchedulerConfigured();
        if (isViewRefreshShutdownRequested())
        {
            return;
        }
        if (!TryBeginKeywordRefresh(identity, advanceRevision, out PlayHistoryViewRequest request))
        {
            return;
        }
        QueueViewRefresh(
            request,
            () => CompleteKeywordRefresh(request.KeywordFilterRevision),
            "playHistoryKeywordFilterUpdated");
    }

    internal void QueueDisplayTargetRefresh(string identity, bool advanceRevision = true)
    {
        EnsureViewRefreshSchedulerConfigured();
        if (isViewRefreshShutdownRequested())
        {
            return;
        }
        if (!TryBeginDisplayTargetRefresh(identity, advanceRevision, out PlayHistoryViewRequest request))
        {
            return;
        }
        QueueViewRefresh(
            request,
            () => CompleteDisplayTargetRefresh(request.DisplayTargetRevision),
            "playHistoryDisplayTargetUpdated");
    }

    private void QueueViewRefresh(
        PlayHistoryViewRequest request,
        Action complete,
        string operationName)
    {
        Task refreshTask;
        try
        {
            refreshTask = Task.Run(() =>
            {
                try
                {
                    refreshView(request);
                }
                finally
                {
                    complete();
                }
            });
        }
        catch
        {
            complete();
            throw;
        }
        refreshTask.Logging(operationName);
    }

    private void EnsureViewRefreshSchedulerConfigured()
    {
        if (isViewRefreshShutdownRequested == null || refreshView == null)
        {
            throw new InvalidOperationException("Play-history view refresh must be configured before it is queued.");
        }
    }

    private MainChartListSortRequestedEventArgs pendingSortRefresh;

    private bool sortRefreshWorkerActive;

    public IReadOnlyList<PlayHistoryPeriodTreeItem> ArchivePeriodTree => PresentationState.ArchivePeriodTree;

    public IReadOnlyList<PlayHistorySummaryCard> SummaryCards => PresentationState.SummaryCards;

    public string SummaryDiagnosticText => PresentationState.DiagnosticText;

    /// <summary>
    /// Gets whether the play-history view is selected in the shell.
    /// </summary>
    public bool IsViewActive
    {
        get
        {
            lock (PresentationState.SyncRoot)
            {
                return isViewActive;
            }
        }
    }

    public ListenerCommand<PlayHistorySummaryCard> ToggleSummaryFilterCommand =>
        toggleSummaryFilterCommand ??= new ListenerCommand<PlayHistorySummaryCard>(ToggleSummaryFilterCard);

    internal void QueueSort(MainChartListSortRequestedEventArgs request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (request.Target != MainChartListSortTarget.PlayHistory)
        {
            throw new ArgumentException("A play-history sort request is required.", nameof(request));
        }
        MainChartListSortRequestedEventArgs ownedRequest;
        bool startWorker;
        lock (PresentationState.SyncRoot)
        {
            var next = new ChartListSortParameters
            {
                ColumnsName = request.ColumnName,
                Direction = request.Direction
            };
            if (AreSameSortParameters(sortParameters, next))
            {
                return;
            }
            sortParameters = CloneSortParameters(next);
            PresentationState.SortRevision++;
            PresentationState.CurrentSortSnapshot = new SortSnapshot(
                sortParameters.ColumnsName,
                sortParameters.Direction,
                PresentationState.SortRevision);
            ownedRequest = new MainChartListSortRequestedEventArgs(
                request.ColumnName,
                request.Direction,
                request.Target,
                PresentationState.SortRevision);
            pendingSortRefresh = ownedRequest;
            startWorker = !sortRefreshWorkerActive;
            sortRefreshWorkerActive = true;
        }
        try
        {
            SortChanged?.Invoke(this, ownedRequest);
        }
        finally
        {
            if (startWorker)
            {
                Task.Run(ProcessSortRefreshQueue).Logging("playHistorySortRequested");
            }
        }
    }

    private void ProcessSortRefreshQueue()
    {
        bool restartWorker = false;
        try
        {
            while (true)
            {
                MainChartListSortRequestedEventArgs request;
                lock (PresentationState.SyncRoot)
                {
                    request = pendingSortRefresh;
                    pendingSortRefresh = null;
                    if (request == null)
                    {
                        return;
                    }
                }
                if (IsCurrentSortRequest(request))
                {
                    SortRefreshRequested?.Invoke(this, request);
                }
            }
        }
        finally
        {
            lock (PresentationState.SyncRoot)
            {
                sortRefreshWorkerActive = false;
                if (pendingSortRefresh != null)
                {
                    sortRefreshWorkerActive = true;
                    restartWorker = true;
                }
            }
            if (restartWorker)
            {
                Task.Run(ProcessSortRefreshQueue).Logging("playHistorySortRequested");
            }
        }
    }

    internal bool IsCurrentSortRequest(MainChartListSortRequestedEventArgs request)
    {
        lock (PresentationState.SyncRoot)
        {
            return request.OwnerRevision == PresentationState.SortRevision
                && string.Equals(sortParameters?.ColumnsName, request.ColumnName, StringComparison.Ordinal)
                && sortParameters?.Direction == request.Direction;
        }
    }

    private readonly PlayHistoryReadCache readCache = new();

    private readonly HashSet<string> selectedSummaryFilterKeys = new(StringComparer.Ordinal);

    internal PlayHistoryViewRequest ActiveRequest { get; private set; }

    internal void InvalidateReadCache()
    {
        readCache.Invalidate();
    }

    private void ToggleSummaryFilterCard(PlayHistorySummaryCard card)
    {
        if (card?.IsFilterable != true)
        {
            return;
        }
        bool cardsChanged;
        lock (PresentationState.SyncRoot)
        {
            ToggleSummaryFilterUnsafe(card.FilterKey);
            cardsChanged = PresentationState.SetSummaryCards(
                ApplySummaryFilterSelection(PresentationState.SummaryCards, selectedSummaryFilterKeys));
        }
        if (cardsChanged)
        {
            RaisePropertyChanged(nameof(SummaryCards));
        }
        SummaryFilterRefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ToggleSummaryFilterUnsafe(string filterKey)
    {
        if (!selectedSummaryFilterKeys.Add(filterKey))
        {
            selectedSummaryFilterKeys.Remove(filterKey);
        }
    }

    private static IReadOnlyList<PlayHistorySummaryCard> ApplySummaryFilterSelection(
        IReadOnlyList<PlayHistorySummaryCard> cards,
        ISet<string> selectedKeys)
    {
        if ((cards?.Count ?? 0) == 0)
        {
            return [];
        }
        return [.. cards.Select(card => card == null
            ? null
            : new PlayHistorySummaryCard(
                card.Label,
                card.Value,
                card.Compact,
                card.FilterKey,
                card.FilterText,
                card.IsFilterable && selectedKeys.Contains(card.FilterKey)))];
    }

    internal HashSet<string> SnapshotSummaryFilterKeys(PlayHistoryProvider? provider = null)
    {
        lock (PresentationState.SyncRoot)
        {
            var snapshot = new HashSet<string>(selectedSummaryFilterKeys, StringComparer.Ordinal);
            if (provider.HasValue && provider.Value != PlayHistoryProvider.Beatoraja)
            {
                snapshot.Remove("exhard");
            }
            return snapshot;
        }
    }

    internal IReadOnlyList<string> SnapshotSummaryFilterTexts(PlayHistoryProvider provider)
    {
        HashSet<string> selectedKeys = SnapshotSummaryFilterKeys(provider);
        return selectedKeys.Count == 0
            ? []
            : PlayHistoryPresentationState.GetSummaryFilterTexts(selectedKeys);
    }

    internal void ClearSummaryFilters()
    {
        lock (PresentationState.SyncRoot)
        {
            selectedSummaryFilterKeys.Clear();
        }
    }

    internal void ClearSummaryPresentation()
    {
        bool cardsChanged;
        bool diagnosticChanged;
        lock (PresentationState.SyncRoot)
        {
            selectedSummaryFilterKeys.Clear();
            cardsChanged = PresentationState.SetSummaryCards([]);
            diagnosticChanged = PresentationState.SetDiagnosticText(string.Empty);
        }
        if (cardsChanged) RaisePropertyChanged(nameof(SummaryCards));
        if (diagnosticChanged) RaisePropertyChanged(nameof(SummaryDiagnosticText));
    }

    private void PruneSummaryFilters(PlayHistoryProvider provider)
    {
        if (provider == PlayHistoryProvider.Beatoraja)
        {
            return;
        }
        lock (PresentationState.SyncRoot)
        {
            selectedSummaryFilterKeys.Remove("exhard");
        }
    }

    internal PlayHistoryReadWorkflowResult BuildReadView(
        PlayHistoryReadWorkflowRequest request,
        BMSLibrary library,
        BMSPlaylist playlist)
    {
        if (request?.ViewRequest == null)
        {
            throw new ArgumentException("A complete play-history read request is required.", nameof(request));
        }

        long requestId = request.ViewRequest.RequestId;
        PlayHistoryPeriodRequest periodRequest = request.ViewRequest.PeriodRequest;
        CancellationToken cancellationToken = GetCancellationToken(requestId);
        var readStopwatch = Stopwatch.StartNew();
        var readStage = new PlayHistoryReadStageMetrics(
            completed: false,
            request.Source.Provider,
            schemaStatus: default,
            cacheHit: false,
            rowCount: 0,
            diagnosticCount: 0,
            elapsedMs: 0L);
        var periodStage = new PlayHistoryPeriodIndexStageMetrics(
            PlayHistoryPeriodIndexStageStatus.NotStarted,
            cacheHit: false,
            dayCount: 0,
            diagnosticCount: 0,
            elapsedMs: 0L);
        var projectionStage = new PlayHistoryProjectionStageMetrics(
            PlayHistoryProjectionStageStatus.NotStarted,
            rawCount: 0,
            projectedCount: 0,
            diagnosticCount: 0,
            indexMs: 0L,
            indexCacheHit: false,
            indexStaleRetries: 0,
            projectionMs: 0L,
            failure: null);
        Lr2PlayHistoryReadResult lr2ReadResult = null;
        BeatorajaPlayHistoryReadResult beatorajaReadResult = null;
        Lr2PlayHistorySchemaCheckResult lr2SchemaCheckResult = null;
        Lr2PlayHistorySchemaStatus schemaStatus;
        int rawReadCount;
        IReadOnlyList<PlayHistoryDiagnostic> readDiagnostics;
        bool readCacheHit;
        try
        {
            if (request.Source.Provider == PlayHistoryProvider.Beatoraja)
            {
                var readRequest = periodRequest.ToBeatorajaReadRequest(request.Source.ScoreDbPath);
                readRequest.ScoresBySha256 = request.Source.BeatorajaScoreContext.ScoresBySha256;
                readRequest.ScoreSnapshotVersion = request.Source.BeatorajaScoreContext.ScoreSnapshotVersion;
                beatorajaReadResult = readCache.ReadBeatoraja(readRequest, cancellationToken, out readCacheHit);
                schemaStatus = beatorajaReadResult.SchemaStatus;
                rawReadCount = beatorajaReadResult.Rows.Count;
                readDiagnostics = beatorajaReadResult.Diagnostics;
            }
            else
            {
                lr2ReadResult = readCache.ReadLr2(
                    periodRequest.ToLr2ReadRequest(request.Source.ScoreDbPath, request.Source.IsLr2LinkedProfile),
                    cancellationToken,
                    out readCacheHit);
                lr2SchemaCheckResult = lr2ReadResult?.SchemaCheckResult;
                schemaStatus = lr2ReadResult.SchemaStatus;
                rawReadCount = lr2ReadResult.Rows.Count;
                readDiagnostics = lr2ReadResult.Diagnostics;
            }
        }
        catch (OperationCanceledException)
        {
            return new PlayHistoryReadWorkflowResult(
                built: false,
                canceledStage: "read",
                readStopwatch.ElapsedMilliseconds,
                readStage,
                periodStage,
                projectionStage,
                lr2SchemaCheckResult,
                presentation: null);
        }

        long readMs = readStopwatch.ElapsedMilliseconds;
        readStage = new PlayHistoryReadStageMetrics(
            completed: true,
            request.Source.Provider,
            schemaStatus,
            readCacheHit,
            rawReadCount,
            readDiagnostics?.Count ?? 0,
            readMs);
        request.ReportProgress?.Invoke(PlayHistoryReadWorkflowProgress.ReadCompleted(readStage, lr2SchemaCheckResult));
        if (!IsCurrentRequest(requestId))
        {
            return new PlayHistoryReadWorkflowResult(
                built: false,
                canceledStage: "after_read",
                readMs,
                readStage,
                periodStage,
                projectionStage,
                lr2SchemaCheckResult,
                presentation: null);
        }

        IReadOnlyList<long> periodIndexPlayedAt = [];
        IReadOnlyList<PlayHistoryDiagnostic> periodIndexDiagnostics = [];
        bool canReadPeriodIndex = schemaStatus is Lr2PlayHistorySchemaStatus.Installed or Lr2PlayHistorySchemaStatus.Repairable;
        if (canReadPeriodIndex)
        {
            var periodStopwatch = Stopwatch.StartNew();
            try
            {
                bool periodCacheHit;
                if (request.Source.Provider == PlayHistoryProvider.Beatoraja)
                {
                    BeatorajaPlayHistoryPeriodIndexResult periodResult = readCache.ReadBeatorajaPeriodIndex(
                        new BeatorajaPlayHistoryPeriodIndexRequest
                        {
                            ScoreDbPath = request.Source.ScoreDbPath,
                            ScoresBySha256 = request.Source.BeatorajaScoreContext.ScoresBySha256,
                            ScoreSnapshotVersion = request.Source.BeatorajaScoreContext.ScoreSnapshotVersion
                        },
                        cancellationToken,
                        out periodCacheHit);
                    periodIndexPlayedAt = periodResult.PlayedAtUnixSeconds;
                    periodIndexDiagnostics = periodResult.Diagnostics;
                }
                else
                {
                    Lr2PlayHistoryPeriodIndexResult periodResult = readCache.ReadLr2PeriodIndex(
                        new Lr2PlayHistoryPeriodIndexRequest
                        {
                            ScoreDbPath = request.Source.ScoreDbPath,
                            IsLr2LinkedProfile = request.Source.IsLr2LinkedProfile
                        },
                        cancellationToken,
                        out periodCacheHit);
                    periodIndexPlayedAt = periodResult.PlayedAtUnixSeconds;
                    periodIndexDiagnostics = periodResult.Diagnostics;
                }
                periodStage = new PlayHistoryPeriodIndexStageMetrics(
                    PlayHistoryPeriodIndexStageStatus.Completed,
                    periodCacheHit,
                    periodIndexPlayedAt?.Count ?? 0,
                    periodIndexDiagnostics?.Count ?? 0,
                    periodStopwatch.ElapsedMilliseconds);
            }
            catch (OperationCanceledException)
            {
                periodStage = new PlayHistoryPeriodIndexStageMetrics(
                    PlayHistoryPeriodIndexStageStatus.Canceled,
                    cacheHit: false,
                    dayCount: 0,
                    diagnosticCount: 0,
                    periodStopwatch.ElapsedMilliseconds);
                return new PlayHistoryReadWorkflowResult(
                    built: false,
                    canceledStage: "period_index",
                    readMs + periodStopwatch.ElapsedMilliseconds,
                    readStage,
                    periodStage,
                    projectionStage,
                    lr2SchemaCheckResult,
                    presentation: null);
            }
            request.ReportProgress?.Invoke(PlayHistoryReadWorkflowProgress.PeriodIndexCompleted(readStage, periodStage));
            if (!IsCurrentRequest(requestId))
            {
                return new PlayHistoryReadWorkflowResult(
                    built: false,
                    canceledStage: "after_period_index",
                    readMs + periodStage.ElapsedMs,
                    readStage,
                    periodStage,
                    projectionStage,
                    lr2SchemaCheckResult,
                    presentation: null);
            }
        }
        else
        {
            periodStage = new PlayHistoryPeriodIndexStageMetrics(
                PlayHistoryPeriodIndexStageStatus.SkippedSchemaUnavailable,
                cacheHit: false,
                dayCount: 0,
                diagnosticCount: 0,
                elapsedMs: 0L);
            request.ReportProgress?.Invoke(PlayHistoryReadWorkflowProgress.PeriodIndexCompleted(readStage, periodStage));
        }

        PlayHistoryProjectionResult projectionResult;
        long projectionIndexMs = 0L;
        bool projectionIndexCacheHit = false;
        int projectionIndexStaleRetries = 0;
        long projectionMs = 0L;
        Exception projectionFailure = null;
        if (rawReadCount > 0)
        {
            try
            {
                projectionResult = request.Source.Provider == PlayHistoryProvider.Beatoraja
                    ? CreateProjectionResult(
                        library,
                        beatorajaReadResult,
                        cancellationToken,
                        ex => projectionFailure = ex,
                        out projectionIndexMs,
                        out projectionIndexCacheHit,
                        out projectionIndexStaleRetries,
                        out projectionMs)
                    : CreateProjectionResult(
                        library,
                        lr2ReadResult,
                        cancellationToken,
                        ex => projectionFailure = ex,
                        out projectionIndexMs,
                        out projectionIndexCacheHit,
                        out projectionIndexStaleRetries,
                        out projectionMs);
            }
            catch (OperationCanceledException)
            {
                projectionStage = new PlayHistoryProjectionStageMetrics(
                    PlayHistoryProjectionStageStatus.Canceled,
                    rawReadCount,
                    projectedCount: 0,
                    diagnosticCount: 0,
                    projectionIndexMs,
                    projectionIndexCacheHit,
                    projectionIndexStaleRetries,
                    projectionMs,
                    failure: null);
                return new PlayHistoryReadWorkflowResult(
                    built: false,
                    canceledStage: "projection",
                    readMs + periodStage.ElapsedMs + projectionIndexMs + projectionMs,
                    readStage,
                    periodStage,
                    projectionStage,
                    lr2SchemaCheckResult,
                    presentation: null);
            }
        }
        else
        {
            projectionResult = request.Source.Provider == PlayHistoryProvider.Beatoraja
                ? PlayHistoryRow.ProjectBeatorajaRows(beatorajaReadResult, PlayHistoryProjectionIndex.Empty)
                : PlayHistoryRow.ProjectLr2Rows(lr2ReadResult, PlayHistoryProjectionIndex.Empty);
        }

        bool projectionFallback = (projectionResult.Diagnostics ?? [])
            .Any(diagnostic => string.Equals(diagnostic?.Code, "play_history_projection_index_failed", StringComparison.Ordinal));
        PlayHistoryProjectionStageStatus projectionStatus = rawReadCount == 0
            ? PlayHistoryProjectionStageStatus.SkippedNoRows
            : (projectionFallback ? PlayHistoryProjectionStageStatus.Fallback : PlayHistoryProjectionStageStatus.Completed);
        projectionStage = new PlayHistoryProjectionStageMetrics(
            projectionStatus,
            rawReadCount,
            projectionResult.Rows.Count,
            projectionResult.Diagnostics?.Count ?? 0,
            projectionIndexMs,
            projectionIndexCacheHit,
            projectionIndexStaleRetries,
            projectionMs,
            projectionFailure);
        request.ReportProgress?.Invoke(PlayHistoryReadWorkflowProgress.ProjectionCompleted(readStage, periodStage, projectionStage));
        PlayHistoryPeriodSummaryOverride summaryOverride = request.Source.Provider == PlayHistoryProvider.Beatoraja
            ? ResolveBeatorajaPeriodSummaryOverride(periodRequest, beatorajaReadResult)
            : null;
        PlayHistoryReadPresentationBuildResult presentation = BuildReadPresentation(
            new PlayHistoryReadPresentationBuildRequest(
                requestId,
                periodRequest,
                projectionResult.Rows,
                projectionResult.Diagnostics,
                periodIndexPlayedAt,
                periodIndexDiagnostics,
                request.Source.Provider,
                schemaStatus,
                rawReadCount,
                request.KeywordFilter,
                request.ViewRequest.KeywordFilterRevision > 0 ? request.ViewRequest.KeywordFilterRevision : KeywordRevision,
                request.DisplayTarget,
                request.ViewRequest.DisplayTargetRevision > 0 ? request.ViewRequest.DisplayTargetRevision : DisplayTargetRevision,
                request.SummaryFilterTexts,
                summaryOverride),
            playlist);
        return new PlayHistoryReadWorkflowResult(
            presentation.Built,
            presentation.Built ? string.Empty : "presentation",
            readMs + periodStage.ElapsedMs + projectionIndexMs + projectionMs,
            readStage,
            periodStage,
            projectionStage,
            lr2SchemaCheckResult,
            presentation);
    }

    internal PlayHistoryTerminalCommitResult ApplyTerminal(
        PlayHistoryTerminalRequest request,
        MainChartListViewModel mainChartList,
        PlaylistWorkspaceViewModel playlistWorkspace)
    {
        if (request?.ViewState == null || request.MainRowsRequest?.Rows == null)
        {
            throw new ArgumentException("A complete play-history terminal request is required.", nameof(request));
        }
        if (mainChartList == null) throw new ArgumentNullException(nameof(mainChartList));
        if (playlistWorkspace == null) throw new ArgumentNullException(nameof(playlistWorkspace));

        var result = new PlayHistoryTerminalCommitResult();
        bool CommitPlayHistoryPresentation(Action commitRows)
        {
            return PresentationState.TryCommitTerminal(
                request,
                result,
                commitRows,
                () =>
                {
                    result.PlaylistSourceClear = playlistWorkspace.CommitPlayHistorySourceClear();
                    result.MainTablePresentation = playlistWorkspace.CommitMainTablePresentationWithoutNotification(
                        request.ColumnSelection,
                        playlistDetailActive: false,
                        commitBindingModeFirst: true);
                    mainChartList.CommitAppliedColumnMode(request.ColumnSelection.AppliedMode);
                    PruneSummaryFilters(request.ViewState.Provider);
                });
        }

        void PublishPlayHistoryPresentation()
        {
            var publishExceptions = new List<Exception>();
            TryPublish(() => PublishRelatedPresentation(result, playlistWorkspace), publishExceptions);
            TryPublish(() => PublishOwnPresentation(result), publishExceptions);
            if (publishExceptions.Count > 0)
            {
                throw new AggregateException(publishExceptions);
            }
        }

        MainChartListPresentationApplyResult applied;
        try
        {
            applied = mainChartList.ApplyPresentation(
                request.MainRowsRequest,
                CommitPlayHistoryPresentation,
                PublishPlayHistoryPresentation);
        }
        catch (MainChartListPresentationPublishException ex)
        {
            result.MainRowsApply = ex.RowsApply;
            var tablePublishException = new PlayHistoryTerminalPublishException(
                ex.InnerException ?? ex,
                ownershipTransferred: true,
                result);
            PublishTerminalShellStateAfterTablePublishFailure(tablePublishException, playlistWorkspace);
            throw tablePublishException;
        }
        if (!applied.WasApplied)
        {
            return result;
        }
        result.MainRowsApply = applied.RowsApply;
        PublishTerminalShellState(result, playlistWorkspace);
        return result;
    }

    internal PlayHistorySortedRowsApplyResult ApplySortedRows(
        PlayHistorySortedRowsApplyRequest request,
        Stopwatch stopwatch,
        MainChartListViewModel mainChartList,
        PlaylistWorkspaceViewModel playlistWorkspace)
    {
        if (request?.State == null || request.SortedRows == null)
        {
            throw new ArgumentException("A complete play-history presentation request is required.", nameof(request));
        }
        if (stopwatch == null) throw new ArgumentNullException(nameof(stopwatch));
        if (mainChartList == null) throw new ArgumentNullException(nameof(mainChartList));

        PlayHistoryViewState state = request.State;
        IReadOnlyList<PlayHistoryRow> sortedRows = request.SortedRows;
        bool sortSucceeded = request.SortSucceeded;
        string sortProfile = request.SortProfile;
        long additionalSortMs = 0L;
        while (true)
        {
            PlayHistoryPresentationFreshnessResult freshness = EvaluateTerminalPresentationFreshness(
                state,
                request.CurrentKeywordFilter,
                request.CurrentDisplayTarget);
            if (freshness.Status == PlayHistoryPresentationFreshnessStatus.Fresh)
            {
                break;
            }
            if (freshness.Status == PlayHistoryPresentationFreshnessStatus.SortStale)
            {
                var resortStopwatch = Stopwatch.StartNew();
                ChartListSortParameters currentSortParameters = CaptureSortParameters(out SortSnapshot currentSortSnapshot);
                sortSucceeded = PlayHistorySortEngine.TrySort(
                    state.ProjectedRows,
                    currentSortParameters,
                    out List<PlayHistoryRow> resortedRows,
                    out sortProfile);
                if (!sortSucceeded)
                {
                    resortedRows = [.. state.ProjectedRows];
                }
                sortedRows = resortedRows;
                additionalSortMs += resortStopwatch.ElapsedMilliseconds;
                state = new PlayHistoryViewState(
                    state.RequestId,
                    state.PeriodRequest,
                    state.AllProjectedRows,
                    state.FilterSourceRows,
                    state.ProjectedRows,
                    state.Diagnostics,
                    state.Provider,
                    state.SchemaStatus,
                    state.SourceCount,
                    currentSortSnapshot,
                    state.KeywordFilter,
                    state.KeywordFilterRevision,
                    state.DisplayTarget,
                    state.DisplayTargetRevision,
                    state.SummaryOverride);
                continue;
            }

            PlayHistorySortedRowsApplyStatus status = freshness.Status switch
            {
                PlayHistoryPresentationFreshnessStatus.DisplayTargetStale => PlayHistorySortedRowsApplyStatus.DisplayTargetStale,
                PlayHistoryPresentationFreshnessStatus.KeywordStale => PlayHistorySortedRowsApplyStatus.KeywordStale,
                _ => PlayHistorySortedRowsApplyStatus.StaleRequest
            };
            return PlayHistorySortedRowsApplyResult.Stale(
                status,
                freshness.QueueRefresh,
                sortSucceeded,
                sortProfile,
                additionalSortMs);
        }

        IReadOnlyList<PlayHistoryDiagnostic> diagnostics = CreateViewDiagnostics(state.Diagnostics, sortSucceeded, sortProfile);
        PlayHistoryPeriodSummary summary = PlayHistoryPeriodSummary.FromRows(
            state.PeriodRequest.Label,
            sortedRows,
            state.SummaryOverride);
        System.Collections.IList nextRowsView = sortedRows.Count == 0
            && mainChartList.Rows is PlayHistoryVirtualView currentPlayHistoryView
            && currentPlayHistoryView.Count == 0
                ? mainChartList.Rows
                : new PlayHistoryVirtualView(sortedRows, CountDistinctFolderLabels(sortedRows));
        long columnSettingStartMs = stopwatch.ElapsedMilliseconds;
        bool ownsCandidateRows = !ReferenceEquals(mainChartList.Rows, nextRowsView);
        try
        {
            MainChartListColumnSelection columnSelection = mainChartList.ResolveColumnSettingForViewUpdate(
                request.Mode,
                request.ColumnFilterMode);
            string diagnosticSummaryText = PlayHistoryPresentationState.FormatDiagnosticSummary(diagnostics);
            string gridSummaryText = PlayHistoryPresentationState.FormatGridSummaryText(
                state.PeriodRequest,
                summary,
                diagnostics,
                diagnosticSummaryText);
            IReadOnlyList<PlayHistorySummaryCard> summaryCards = PlayHistoryPresentationState.CreateSummaryCards(
                summary,
                state.Provider,
                SnapshotSummaryFilterKeys(state.Provider));
            PlayHistoryTerminalCommitResult terminalCommit = ApplyTerminal(
                new PlayHistoryTerminalRequest
                {
                    ViewState = state,
                    ColumnSelection = columnSelection,
                    ArchivePeriodTree = request.ArchivePeriodTree,
                    SummaryCards = summaryCards,
                    DiagnosticText = diagnosticSummaryText,
                    MainRowsRequest = new MainChartListRowsApplyRequest
                    {
                        Rows = nextRowsView,
                        ColumnsSettings = columnSelection.ColumnsSettings,
                        SelectionPolicy = MainChartListSelectionPolicy.Reset,
                        Summary = MainChartListSummaryUpdate.Explicit(gridSummaryText),
                        ColumnSettingReuse = columnSelection.Reused,
                        ColumnPreparationMs = columnSelection.ElapsedMs,
                        TerminalStageStartMs = columnSettingStartMs,
                        Stopwatch = stopwatch
                    }
                },
                mainChartList,
                playlistWorkspace);
            if (!terminalCommit.Applied)
            {
                if (ownsCandidateRows)
                {
                    MainChartListViewModel.DisposeRows(nextRowsView);
                }
                return PlayHistorySortedRowsApplyResult.Stale(
                    PlayHistorySortedRowsApplyStatus.StaleRequest,
                    queueRefresh: false,
                    sortSucceeded,
                    sortProfile,
                    additionalSortMs);
            }

            return PlayHistorySortedRowsApplyResult.Applied(
                sortSucceeded,
                sortProfile,
                additionalSortMs,
                sortedRows.Count,
                diagnostics,
                terminalCommit);
        }
        catch (PlayHistoryTerminalPublishException)
        {
            throw;
        }
        catch
        {
            if (ownsCandidateRows)
            {
                MainChartListViewModel.DisposeRows(nextRowsView);
            }
            throw;
        }
    }

    internal PlayHistoryPresentationOnlyBuildResult BuildPresentationOnly(
        PlayHistoryPresentationOnlyBuildRequest request,
        BMSPlaylist playlist)
    {
        if (request?.State == null)
        {
            throw new ArgumentException("A complete play-history presentation build request is required.", nameof(request));
        }

        PlayHistoryViewState state = request.State;
        bool displayTargetStale = request.DisplayTargetRevision != state.DisplayTargetRevision
            || !string.Equals(request.DisplayTarget.Identity, state.DisplayTargetIdentity, StringComparison.Ordinal);
        bool keywordStale = request.KeywordRevision != state.KeywordFilterRevision
            || !string.Equals(
                PlaylistRequestFactory.NormalizeKeywordFilter(request.KeywordFilter),
                state.KeywordFilterIdentity,
                StringComparison.Ordinal);
        if (request.RequestedMode == MainViewUpdateMode.SortUpdated && displayTargetStale)
        {
            return PlayHistoryPresentationOnlyBuildResult.Stale(
                PlayHistoryPresentationOnlyBuildStatus.DisplayTargetStale,
                queueRefresh: true,
                state);
        }
        if (request.RequestedMode == MainViewUpdateMode.SortUpdated && keywordStale)
        {
            return PlayHistoryPresentationOnlyBuildResult.Stale(
                PlayHistoryPresentationOnlyBuildStatus.KeywordStale,
                queueRefresh: true,
                state);
        }

        IReadOnlyList<PlayHistoryRow> targetRows = state.FilterSourceRows;
        IReadOnlyList<PlayHistoryRow> filteredRows = state.ProjectedRows;
        long keywordMs = 0L;
        if (displayTargetStale)
        {
            CancellationToken cancellationToken = GetCancellationToken(state.RequestId);
            try
            {
                targetRows = ApplyDisplayTarget(
                    state.AllProjectedRows,
                    request.DisplayTarget,
                    state.RequestId,
                    request.DisplayTargetRevision,
                    cancellationToken,
                    () => SnapshotDisplayTargetTables(playlist),
                    table => playlist?.EnsurePlaylistEntriesLoaded(table, "PlayHistoryDisplayTarget"));
            }
            catch (OperationCanceledException)
            {
                return PlayHistoryPresentationOnlyBuildResult.Stale(
                    PlayHistoryPresentationOnlyBuildStatus.StaleRequest,
                    queueRefresh: false,
                    state);
            }
        }
        if (displayTargetStale || keywordStale)
        {
            CancellationToken cancellationToken = GetCancellationToken(state.RequestId);
            try
            {
                filteredRows = ApplyKeywordFilters(
                    targetRows,
                    request.KeywordFilter,
                    request.SummaryFilterTexts,
                    state.RequestId,
                    request.KeywordRevision,
                    cancellationToken,
                    out keywordMs);
            }
            catch (OperationCanceledException)
            {
                return PlayHistoryPresentationOnlyBuildResult.Stale(
                    PlayHistoryPresentationOnlyBuildStatus.StaleRequest,
                    queueRefresh: false,
                    state,
                    displayTargetApplied: displayTargetStale,
                    displayTargetSourceCount: displayTargetStale ? state.AllProjectedRows.Count : 0,
                    displayTargetResultCount: displayTargetStale ? targetRows.Count : 0);
            }
        }

        var sortStopwatch = Stopwatch.StartNew();
        ChartListSortParameters sortParameters = CaptureSortParameters(out SortSnapshot sortSnapshot);
        bool sortSucceeded = PlayHistorySortEngine.TrySort(
            filteredRows,
            sortParameters,
            out List<PlayHistoryRow> sortedRows,
            out string sortProfile);
        if (!sortSucceeded)
        {
            sortedRows = [.. filteredRows];
        }
        long sortMs = sortStopwatch.ElapsedMilliseconds;
        var sortedState = new PlayHistoryViewState(
            state.RequestId,
            state.PeriodRequest,
            state.AllProjectedRows,
            targetRows,
            filteredRows,
            state.Diagnostics,
            state.Provider,
            state.SchemaStatus,
            state.SourceCount,
            sortSnapshot,
            request.KeywordFilter,
            request.KeywordRevision,
            request.DisplayTarget,
            request.DisplayTargetRevision,
            state.SummaryOverride);
        return PlayHistoryPresentationOnlyBuildResult.Built(
            sortedState,
            sortedRows,
            sortSucceeded,
            sortProfile,
            sortMs,
            keywordMs,
            filteredRows.Count,
            displayTargetStale,
            state.AllProjectedRows.Count,
            targetRows.Count,
            displayTargetStale || keywordStale,
            state.SourceCount,
            targetRows.Count);
    }

    internal PlayHistoryReadPresentationBuildResult BuildReadPresentation(
        PlayHistoryReadPresentationBuildRequest request,
        BMSPlaylist playlist)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        IReadOnlyList<PlayHistoryDiagnostic> diagnostics = MergeDiagnostics(
            request.ProjectionDiagnostics,
            request.PeriodIndexDiagnostics);
        CancellationToken cancellationToken = GetCancellationToken(request.RequestId);
        IReadOnlyList<PlayHistoryRow> targetRows;
        try
        {
            targetRows = ApplyDisplayTarget(
                request.ProjectedRows,
                request.DisplayTarget,
                request.RequestId,
                request.DisplayTargetRevision,
                cancellationToken,
                () => SnapshotDisplayTargetTables(playlist),
                table => playlist?.EnsurePlaylistEntriesLoaded(table, "PlayHistoryDisplayTarget"));
        }
        catch (OperationCanceledException)
        {
            return PlayHistoryReadPresentationBuildResult.Stale();
        }

        IReadOnlyList<PlayHistoryRow> filteredRows;
        long keywordMs;
        try
        {
            filteredRows = ApplyKeywordFilters(
                targetRows,
                request.KeywordFilter,
                request.SummaryFilterTexts,
                request.RequestId,
                request.KeywordRevision,
                cancellationToken,
                out keywordMs);
        }
        catch (OperationCanceledException)
        {
            return PlayHistoryReadPresentationBuildResult.Stale();
        }

        IReadOnlyList<PlayHistoryPeriodTreeItem> archivePeriodTree = PlayHistoryPeriodTreeItem.BuildArchiveTree(
            request.PeriodIndexPlayedAt,
            TimeZoneInfo.Local);
        if (!IsCurrentRequest(request.RequestId))
        {
            return PlayHistoryReadPresentationBuildResult.Stale();
        }

        var sortStopwatch = Stopwatch.StartNew();
        ChartListSortParameters sortParameters = CaptureSortParameters(out SortSnapshot sortSnapshot);
        bool sortSucceeded = PlayHistorySortEngine.TrySort(
            filteredRows,
            sortParameters,
            out List<PlayHistoryRow> sortedRows,
            out string sortProfile);
        if (!sortSucceeded)
        {
            sortedRows = [.. filteredRows];
        }
        long sortMs = sortStopwatch.ElapsedMilliseconds;
        var state = new PlayHistoryViewState(
            request.RequestId,
            request.PeriodRequest,
            request.ProjectedRows,
            targetRows,
            filteredRows,
            diagnostics,
            request.Provider,
            request.SchemaStatus,
            request.SourceCount,
            sortSnapshot,
            request.KeywordFilter,
            request.KeywordRevision,
            request.DisplayTarget,
            request.DisplayTargetRevision,
            request.SummaryOverride);
        return PlayHistoryReadPresentationBuildResult.Success(
            state,
            sortedRows,
            sortSucceeded,
            sortProfile,
            sortMs,
            keywordMs,
            filteredRows.Count,
            request.ProjectedRows.Count,
            targetRows.Count,
            archivePeriodTree);
    }

    private static IReadOnlyList<PlayHistoryDiagnostic> MergeDiagnostics(
        IReadOnlyList<PlayHistoryDiagnostic> first,
        IReadOnlyList<PlayHistoryDiagnostic> second)
    {
        if (second == null || second.Count == 0)
        {
            return first ?? [];
        }
        if (first == null || first.Count == 0)
        {
            return second;
        }
        return [.. first, .. second];
    }

    internal static PlayHistoryPeriodSummaryOverride ResolveBeatorajaPeriodSummaryOverride(
        PlayHistoryPeriodRequest request,
        BeatorajaPlayHistoryReadResult readResult)
    {
        if (request?.Kind == PlayHistoryPeriodKind.Diagnostics || readResult?.PlayerSnapshotsAvailable != true)
        {
            return new PlayHistoryPeriodSummaryOverride(null, null, null);
        }

        IReadOnlyList<BeatorajaPlayerAggregateSnapshot> snapshots = readResult.PlayerSnapshots ?? [];
        if (snapshots.Count == 0)
        {
            return new PlayHistoryPeriodSummaryOverride(0, 0, 0);
        }
        if (HasInvalidBeatorajaAggregateRange(snapshots, request?.PlayedAtFromInclusive, request?.PlayedAtToExclusive))
        {
            return new PlayHistoryPeriodSummaryOverride(null, null, null);
        }

        BeatorajaPlayerAggregateSnapshot endSnapshot = ResolveLatestBeatorajaPlayerAggregateBefore(
            snapshots,
            request?.PlayedAtToExclusive);
        if (HasNegativeBeatorajaAggregateValue(endSnapshot))
        {
            return new PlayHistoryPeriodSummaryOverride(null, null, null);
        }
        if (request?.PlayedAtFromInclusive.HasValue != true)
        {
            return new PlayHistoryPeriodSummaryOverride(
                endSnapshot.PlayCount,
                endSnapshot.JudgeCount,
                endSnapshot.PlaytimeSeconds);
        }

        BeatorajaPlayerAggregateSnapshot startSnapshot = ResolveLatestBeatorajaPlayerAggregateBefore(
            snapshots,
            request.PlayedAtFromInclusive);
        if (HasNegativeBeatorajaAggregateValue(startSnapshot))
        {
            return new PlayHistoryPeriodSummaryOverride(null, null, null);
        }
        long playCount = endSnapshot.PlayCount - startSnapshot.PlayCount;
        long judgeCount = endSnapshot.JudgeCount - startSnapshot.JudgeCount;
        long playtimeSeconds = endSnapshot.PlaytimeSeconds - startSnapshot.PlaytimeSeconds;
        if (playCount < 0 || judgeCount < 0 || playtimeSeconds < 0)
        {
            return new PlayHistoryPeriodSummaryOverride(null, null, null);
        }
        return new PlayHistoryPeriodSummaryOverride(playCount, judgeCount, playtimeSeconds);
    }

    private static BeatorajaPlayerAggregateSnapshot ResolveLatestBeatorajaPlayerAggregateBefore(
        IReadOnlyList<BeatorajaPlayerAggregateSnapshot> snapshots,
        long? exclusiveBoundary)
    {
        BeatorajaPlayerAggregateSnapshot latest = new();
        long latestDate = long.MinValue;
        foreach (BeatorajaPlayerAggregateSnapshot snapshot in snapshots ?? [])
        {
            if (snapshot == null || snapshot.DateUnixSeconds <= 0)
            {
                continue;
            }
            if (exclusiveBoundary.HasValue && snapshot.DateUnixSeconds >= exclusiveBoundary.Value)
            {
                continue;
            }
            if (snapshot.DateUnixSeconds >= latestDate)
            {
                latestDate = snapshot.DateUnixSeconds;
                latest = snapshot;
            }
        }
        return latest;
    }

    private static bool HasInvalidBeatorajaAggregateRange(
        IReadOnlyList<BeatorajaPlayerAggregateSnapshot> snapshots,
        long? fromInclusive,
        long? toExclusive)
    {
        BeatorajaPlayerAggregateSnapshot previous = null;
        foreach (BeatorajaPlayerAggregateSnapshot snapshot in snapshots ?? [])
        {
            if (snapshot == null || snapshot.DateUnixSeconds <= 0)
            {
                continue;
            }
            if (fromInclusive.HasValue && snapshot.DateUnixSeconds < fromInclusive.Value)
            {
                previous = snapshot;
                continue;
            }
            if (toExclusive.HasValue && snapshot.DateUnixSeconds >= toExclusive.Value)
            {
                break;
            }
            if (HasNegativeBeatorajaAggregateValue(snapshot))
            {
                return true;
            }
            if (previous != null
                && (snapshot.PlayCount < previous.PlayCount
                    || snapshot.JudgeCount < previous.JudgeCount
                    || snapshot.PlaytimeSeconds < previous.PlaytimeSeconds))
            {
                return true;
            }
            previous = snapshot;
        }
        return false;
    }

    private static bool HasNegativeBeatorajaAggregateValue(BeatorajaPlayerAggregateSnapshot snapshot)
    {
        return snapshot?.HasInvalidRawValue == true
            || snapshot?.PlayCount < 0
            || snapshot?.JudgeCount < 0
            || snapshot?.PlaytimeSeconds < 0;
    }

    private static IReadOnlyList<BMSTable> SnapshotDisplayTargetTables(BMSPlaylist playlist)
    {
        try
        {
            playlist?.AcquireReaderLockBMSTables();
            return [.. (playlist?.BMSTables ?? Enumerable.Empty<BMSTable>()).Where(table => table != null)];
        }
        finally
        {
            playlist?.FreeReaderLockBMSTables();
        }
    }

    private static IReadOnlyList<PlayHistoryDiagnostic> CreateViewDiagnostics(
        IReadOnlyList<PlayHistoryDiagnostic> diagnostics,
        bool sortSucceeded,
        string sortProfile)
    {
        if (sortSucceeded)
        {
            return diagnostics ?? [];
        }
        return
        [
            new PlayHistoryDiagnostic
            {
                Provider = PlayHistoryProvider.Lr2,
                Stage = "sort",
                Severity = PlayHistoryDiagnosticSeverity.Warning,
                Code = "play_history_sort_failed",
                Message = string.IsNullOrWhiteSpace(sortProfile) ? "Play history sort failed." : sortProfile,
                SourcePath = string.Empty
            },
            .. (diagnostics ?? [])
        ];
    }

    private static int CountDistinctFolderLabels(IEnumerable<PlayHistoryRow> rows)
    {
        return (rows ?? [])
            .Select(row => row?.FolderLabels)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    private static void PublishRelatedPresentation(
        PlayHistoryTerminalCommitResult result,
        PlaylistWorkspaceViewModel playlistWorkspace)
    {
        List<Exception> publishExceptions = [];
        if (result.MainTablePresentation != null)
        {
            TryPublish(() => playlistWorkspace.PublishMainTablePresentation(result.MainTablePresentation), publishExceptions);
        }
        if (publishExceptions.Count > 0)
        {
            throw new AggregateException(publishExceptions);
        }
    }

    private void PublishOwnPresentation(PlayHistoryTerminalCommitResult result)
    {
        var publishExceptions = new List<Exception>();
        if (result.ArchivePeriodTreeChanged)
        {
            TryPublish(() => RaisePropertyChanged(nameof(ArchivePeriodTree)), publishExceptions);
        }
        if (result.SummaryCardsChanged)
        {
            TryPublish(() => RaisePropertyChanged(nameof(SummaryCards)), publishExceptions);
        }
        if (result.DiagnosticTextChanged)
        {
            TryPublish(() => RaisePropertyChanged(nameof(SummaryDiagnosticText)), publishExceptions);
        }
        if (publishExceptions.Count > 0)
        {
            throw new AggregateException(publishExceptions);
        }
    }

    private static void TryPublish(Action action, ICollection<Exception> exceptions)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            exceptions.Add(ex);
        }
    }

    internal bool UpdateSortParameters(ChartListSortParameters value)
    {
        return TryUpdateSortParameters(value, out _);
    }

    private bool TryUpdateSortParameters(ChartListSortParameters value, out long revision)
    {
        lock (PresentationState.SyncRoot)
        {
            if (AreSameSortParameters(sortParameters, value))
            {
                revision = PresentationState.SortRevision;
                return false;
            }
            sortParameters = CloneSortParameters(value);
            PresentationState.SortRevision++;
            PresentationState.CurrentSortSnapshot = new SortSnapshot(
                sortParameters?.ColumnsName,
                sortParameters?.Direction,
                PresentationState.SortRevision);
            revision = PresentationState.SortRevision;
            return true;
        }
    }

    internal ChartListSortParameters CaptureSortParameters(out SortSnapshot snapshot)
    {
        lock (PresentationState.SyncRoot)
        {
            ChartListSortParameters captured = CloneSortParameters(sortParameters);
            snapshot = new SortSnapshot(captured?.ColumnsName, captured?.Direction, PresentationState.SortRevision);
            return captured;
        }
    }

    internal bool IsCurrentSortSnapshot(SortSnapshot snapshot)
    {
        lock (PresentationState.SyncRoot)
        {
            return SortSnapshot.Equals(snapshot, PresentationState.CurrentSortSnapshot);
        }
    }

    internal bool TrySnapshotCurrentView(PlayHistoryViewRequest request, out PlayHistoryViewState state)
    {
        lock (PresentationState.SyncRoot)
        {
            state = PresentationState.CurrentView;
            return state != null
                && IsCurrentRequestUnsafe(state.RequestId)
                && PlayHistoryWorkflowOwner.IsSamePeriod(state.PeriodRequest, request?.PeriodRequest);
        }
    }

    internal PlayHistoryPresentationFreshnessResult EvaluateTerminalPresentationFreshness(
        PlayHistoryViewState state,
        string currentKeywordFilter,
        PlayHistoryDisplayTargetItem currentDisplayTarget)
    {
        lock (PresentationState.SyncRoot)
        {
            if (state == null || !IsCurrentRequestUnsafe(state.RequestId))
            {
                return PlayHistoryPresentationFreshnessResult.StaleRequest();
            }
            if (!SortSnapshot.Equals(state.SortSnapshot, PresentationState.CurrentSortSnapshot))
            {
                return PlayHistoryPresentationFreshnessResult.SortStale();
            }

            string currentKeywordIdentity = PlaylistRequestFactory.NormalizeKeywordFilter(currentKeywordFilter);
            long currentKeywordRevision = Interlocked.Read(ref PresentationState.KeywordRevision);
            PlayHistoryDisplayTargetItem safeDisplayTarget = currentDisplayTarget ?? PlayHistoryDisplayTargetItem.All;
            string currentDisplayTargetIdentity = safeDisplayTarget.Identity;
            long currentDisplayTargetRevision = Interlocked.Read(ref PresentationState.DisplayTargetRevision);

            if (currentDisplayTargetRevision != state.DisplayTargetRevision
                || !string.Equals(currentDisplayTargetIdentity, state.DisplayTargetIdentity, StringComparison.Ordinal))
            {
                bool shouldQueueRefresh = !IsLatestDisplayTargetStateAvailable(
                    state.RequestId,
                    currentDisplayTargetRevision,
                    currentDisplayTargetIdentity);
                if (shouldQueueRefresh)
                {
                    SaveCurrentViewIfDisplayTargetRefreshCandidate(state);
                }
                return PlayHistoryPresentationFreshnessResult.DisplayTargetStale(shouldQueueRefresh);
            }
            if (currentKeywordRevision != state.KeywordFilterRevision
                || !string.Equals(currentKeywordIdentity, state.KeywordFilterIdentity, StringComparison.Ordinal))
            {
                bool shouldQueueRefresh = !IsLatestKeywordStateAvailable(
                    state.RequestId,
                    currentKeywordRevision,
                    currentKeywordIdentity);
                if (shouldQueueRefresh)
                {
                    SaveCurrentViewIfKeywordRefreshCandidate(state);
                }
                return PlayHistoryPresentationFreshnessResult.KeywordStale(shouldQueueRefresh);
            }
            return PlayHistoryPresentationFreshnessResult.Fresh();
        }
    }

    internal PlayHistoryViewRequest BeginRequest(
        PlayHistoryPeriodRequest periodRequest,
        string keywordIdentity,
        string displayTargetIdentity,
        long displayTargetRevision)
    {
        CancellationTokenSource previousCancellation;
        PlayHistoryViewRequest request;
        bool wasViewActive;
        lock (PresentationState.SyncRoot)
        {
            wasViewActive = isViewActive;
            previousCancellation = InvalidateRequestUnsafe();
            isViewActive = true;
            PresentationState.RequestCancellation = new CancellationTokenSource();
            PresentationState.CurrentKeywordIdentity = keywordIdentity ?? string.Empty;
            PresentationState.CurrentDisplayTargetIdentity = displayTargetIdentity ?? string.Empty;
            PresentationState.DisplayTargetRevision = displayTargetRevision;
            request = new PlayHistoryViewRequest(
                periodRequest ?? PlayHistoryPeriodRequest.All(),
                PresentationState.RequestGeneration,
                PresentationState.KeywordRevision,
                displayTargetRevision);
            ActiveRequest = request;
        }
        Cancel(previousCancellation);
        if (!wasViewActive)
        {
            RaisePropertyChanged(nameof(IsViewActive));
        }
        return request;
    }

    internal PlayHistoryViewRequest ActivatePeriod(
        PlayHistoryPeriodRequest periodRequest,
        string keywordFilter)
    {
        EnsureDisplayTargetCatalogRefreshConfigured();
        ReplaceDisplayTargetCatalog(
            snapshotDisplayTargetCatalogTables(),
            queueRefreshWhenSelectionChanges: false);
        PlayHistoryDisplayTargetItem safeDisplayTarget = SelectedDisplayTarget;
        PlayHistoryViewRequest request = BeginRequest(
            periodRequest,
            PlaylistRequestFactory.NormalizeKeywordFilter(keywordFilter),
            safeDisplayTarget.Identity,
            DisplayTargetRevision);
        try
        {
            PeriodRequestActivated?.Invoke(
                this,
                new PlayHistoryViewRequestActivatedEventArgs(request));
        }
        catch
        {
            Deactivate();
            throw;
        }
        return request;
    }

    internal bool IsCurrentRequest(long requestId)
    {
        lock (PresentationState.SyncRoot)
        {
            return IsCurrentRequestUnsafe(requestId);
        }
    }

    internal PlayHistoryViewRequest SnapshotActiveRequest()
    {
        lock (PresentationState.SyncRoot)
        {
            return ActiveRequest;
        }
    }

    internal static bool IsSamePeriod(PlayHistoryPeriodRequest left, PlayHistoryPeriodRequest right)
    {
        if (left == null || right == null)
        {
            return left == right;
        }
        return left.Kind == right.Kind
            && left.PlayedAtFromInclusive == right.PlayedAtFromInclusive
            && left.PlayedAtToExclusive == right.PlayedAtToExclusive
            && left.FinalizationFilter == right.FinalizationFilter;
    }

    internal long CurrentRequestId
    {
        get
        {
            lock (PresentationState.SyncRoot)
            {
                return PresentationState.RequestGeneration;
            }
        }
    }

    internal long KeywordRevision => Interlocked.Read(ref PresentationState.KeywordRevision);

    internal long DisplayTargetRevision => Interlocked.Read(ref PresentationState.DisplayTargetRevision);

    internal string CurrentKeywordIdentity
    {
        get
        {
            lock (PresentationState.SyncRoot)
            {
                return PresentationState.CurrentKeywordIdentity;
            }
        }
    }

    internal string CurrentDisplayTargetIdentity
    {
        get
        {
            lock (PresentationState.SyncRoot)
            {
                return PresentationState.CurrentDisplayTargetIdentity;
            }
        }
    }

    internal long UpdateKeywordIdentity(string identity, bool advanceRevision)
    {
        lock (PresentationState.SyncRoot)
        {
            PresentationState.CurrentKeywordIdentity = identity ?? string.Empty;
            return advanceRevision
                ? Interlocked.Increment(ref PresentationState.KeywordRevision)
                : Interlocked.Read(ref PresentationState.KeywordRevision);
        }
    }

    internal long AdvanceDisplayTargetRevision(string identity)
    {
        lock (PresentationState.SyncRoot)
        {
            long revision = Interlocked.Increment(ref PresentationState.DisplayTargetRevision);
            PresentationState.CurrentDisplayTargetIdentity = identity ?? string.Empty;
            return revision;
        }
    }

    internal void SetDisplayTargetIdentity(string identity)
    {
        lock (PresentationState.SyncRoot)
        {
            PresentationState.CurrentDisplayTargetIdentity = identity ?? string.Empty;
        }
    }

    internal bool TryBeginKeywordRefresh(string identity, bool advanceRevision, out PlayHistoryViewRequest request)
    {
        lock (PresentationState.SyncRoot)
        {
            request = null;
            if (!IsCurrentRequestUnsafe(ActiveRequest?.RequestId ?? 0L))
            {
                return false;
            }
            string normalizedIdentity = identity ?? string.Empty;
            long revision;
            if (advanceRevision)
            {
                revision = UpdateKeywordIdentity(normalizedIdentity, advanceRevision: true);
            }
            else
            {
                if (!string.Equals(PresentationState.CurrentKeywordIdentity, normalizedIdentity, StringComparison.Ordinal))
                {
                    return false;
                }
                revision = KeywordRevision;
            }
            if (!TryReserveRevision(ref keywordQueuedRevision, revision))
            {
                return false;
            }
            request = new PlayHistoryViewRequest(
                ActiveRequest.PeriodRequest,
                ActiveRequest.RequestId,
                revision,
                DisplayTargetRevision);
            Interlocked.Increment(ref keywordActiveCount);
            return true;
        }
    }

    internal bool TryBeginDisplayTargetRefresh(string identity, bool advanceRevision, out PlayHistoryViewRequest request)
    {
        lock (PresentationState.SyncRoot)
        {
            request = null;
            if (!IsCurrentRequestUnsafe(ActiveRequest?.RequestId ?? 0L))
            {
                if (advanceRevision)
                {
                    AdvanceDisplayTargetRevision(identity);
                }
                return false;
            }
            string normalizedIdentity = identity ?? string.Empty;
            long revision;
            if (advanceRevision)
            {
                revision = AdvanceDisplayTargetRevision(normalizedIdentity);
            }
            else
            {
                if (!string.Equals(PresentationState.CurrentDisplayTargetIdentity, normalizedIdentity, StringComparison.Ordinal))
                {
                    return false;
                }
                revision = DisplayTargetRevision;
            }
            if (!TryReserveRevision(ref displayTargetQueuedRevision, revision))
            {
                return false;
            }
            request = new PlayHistoryViewRequest(
                ActiveRequest.PeriodRequest,
                ActiveRequest.RequestId,
                KeywordRevision,
                revision);
            Interlocked.Increment(ref displayTargetActiveCount);
            return true;
        }
    }

    internal void CompleteKeywordRefresh(long revision)
    {
        DecrementActiveCount(ref keywordActiveCount, "keyword");
        Interlocked.CompareExchange(ref keywordQueuedRevision, -1L, revision);
    }

    internal void CompleteDisplayTargetRefresh(long revision)
    {
        DecrementActiveCount(ref displayTargetActiveCount, "display-target");
        Interlocked.CompareExchange(ref displayTargetQueuedRevision, -1L, revision);
    }

    internal void ClearQueuedRefreshes()
    {
        Interlocked.Exchange(ref keywordQueuedRevision, -1L);
        Interlocked.Exchange(ref displayTargetQueuedRevision, -1L);
    }

    internal bool AreRefreshQueuesIdle =>
        Interlocked.Read(ref keywordQueuedRevision) < 0L
        && Volatile.Read(ref keywordActiveCount) == 0
        && Interlocked.Read(ref displayTargetQueuedRevision) < 0L
        && Volatile.Read(ref displayTargetActiveCount) == 0;

    internal string DescribeRefreshQueues()
    {
        return "keywordQueuedRevision=" + Interlocked.Read(ref keywordQueuedRevision)
            + " keywordActiveCount=" + Volatile.Read(ref keywordActiveCount)
            + " displayTargetQueuedRevision=" + Interlocked.Read(ref displayTargetQueuedRevision)
            + " displayTargetActiveCount=" + Volatile.Read(ref displayTargetActiveCount);
    }

    internal IReadOnlyList<PlayHistoryRow> ApplyKeywordFilters(
        IReadOnlyList<PlayHistoryRow> rows,
        string keywordFilter,
        IReadOnlyList<string> summaryFilterTexts,
        long requestId,
        long keywordRevision,
        CancellationToken cancellationToken,
        out long elapsedMs)
    {
        var stopwatch = Stopwatch.StartNew();
        IReadOnlyList<PlayHistoryRow> safeRows = rows ?? [];
        bool hasKeywordFilter = !string.IsNullOrWhiteSpace(keywordFilter);
        GridKeywordSearchQuery[] summaryQueries = [.. (summaryFilterTexts ?? [])
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(GridKeywordSearchQuery.Parse)
            .Where(query => query.HasTokens)];
        if (!hasKeywordFilter && summaryQueries.Length == 0)
        {
            elapsedMs = stopwatch.ElapsedMilliseconds;
            return safeRows;
        }

        var keywordQuery = hasKeywordFilter ? GridKeywordSearchQuery.Parse(keywordFilter) : null;
        var filteredRows = new List<PlayHistoryRow>(safeRows.Count);
        for (int index = 0; index < safeRows.Count; index++)
        {
            if ((index & 0x7f) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsCurrentRequest(requestId) || KeywordRevision != keywordRevision)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
            }
            PlayHistoryRow row = safeRows[index];
            if (MatchesKeywordAndSummaryFilters(row, keywordQuery, summaryQueries))
            {
                filteredRows.Add(row);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        elapsedMs = stopwatch.ElapsedMilliseconds;
        return filteredRows;
    }

    internal static bool MatchesKeywordAndSummaryFilters(
        PlayHistoryRow row,
        string keywordFilter,
        params string[] summaryFilterTexts)
    {
        GridKeywordSearchQuery keywordQuery = string.IsNullOrWhiteSpace(keywordFilter)
            ? null
            : GridKeywordSearchQuery.Parse(keywordFilter);
        GridKeywordSearchQuery[] summaryQueries = [.. (summaryFilterTexts ?? [])
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(GridKeywordSearchQuery.Parse)
            .Where(query => query.HasTokens)];
        return MatchesKeywordAndSummaryFilters(row, keywordQuery, summaryQueries);
    }

    internal IReadOnlyList<PlayHistoryRow> ApplyDisplayTarget(
        IReadOnlyList<PlayHistoryRow> rows,
        PlayHistoryDisplayTargetItem displayTarget,
        long requestId,
        long displayTargetRevision,
        CancellationToken cancellationToken,
        Func<IReadOnlyList<BMSTable>> tableSnapshotFactory,
        Action<BMSTable> ensureEntriesLoaded)
    {
        IReadOnlyList<PlayHistoryRow> safeRows = rows ?? [];
        PlayHistoryDisplayTargetItem safeTarget = displayTarget ?? PlayHistoryDisplayTargetItem.All;
        if (!safeTarget.UsesProjection)
        {
            return safeRows;
        }
        ThrowIfStaleDisplayTargetRequest(requestId, displayTargetRevision, cancellationToken);
        IReadOnlyList<BMSTable> tableSnapshot = tableSnapshotFactory?.Invoke() ?? [];
        PlayHistoryDisplayTargetIndex index = PlayHistoryDisplayTargetIndex.Create(
            safeTarget,
            tableSnapshot,
            table => ensureEntriesLoaded?.Invoke(table),
            cancellationToken,
            () => IsCurrentRequest(requestId) && displayTargetRevision == DisplayTargetRevision);
        var filteredRows = new List<PlayHistoryRow>(safeRows.Count);
        for (int indexInRows = 0; indexInRows < safeRows.Count; indexInRows++)
        {
            if ((indexInRows & 0x7f) == 0)
            {
                ThrowIfStaleDisplayTargetRequest(requestId, displayTargetRevision, cancellationToken);
            }
            PlayHistoryRow row = safeRows[indexInRows];
            if (index.TryApply(row, out PlayHistoryRow displayRow))
            {
                filteredRows.Add(displayRow);
            }
        }
        ThrowIfStaleDisplayTargetRequest(requestId, displayTargetRevision, cancellationToken);
        return filteredRows;
    }

    private PlayHistoryProjectionResult CreateProjectionResult(
        BMSLibrary library,
        Lr2PlayHistoryReadResult readResult,
        CancellationToken cancellationToken,
        Action<Exception> logProjectionFailure,
        out long projectionIndexMs,
        out bool projectionIndexCacheHit,
        out int projectionIndexStaleRetries,
        out long projectionMs)
    {
        projectionIndexMs = 0L;
        projectionIndexCacheHit = false;
        projectionIndexStaleRetries = 0;
        projectionMs = 0L;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projectionIndexStopwatch = Stopwatch.StartNew();
            PlayHistoryProjectionIndex projectionIndex = library.CreatePlayHistoryProjectionIndex(
                readResult.Rows,
                cancellationToken,
                out projectionIndexCacheHit,
                out projectionIndexStaleRetries);
            projectionIndexMs = projectionIndexStopwatch.ElapsedMilliseconds;
            cancellationToken.ThrowIfCancellationRequested();
            var projectionStopwatch = Stopwatch.StartNew();
            PlayHistoryProjectionResult projectionResult = PlayHistoryRow.ProjectLr2Rows(readResult, projectionIndex);
            projectionMs = projectionStopwatch.ElapsedMilliseconds;
            return projectionResult;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            projectionMs = 0L;
            PlayHistoryProjectionResult fallback = PlayHistoryRow.ProjectLr2Rows(readResult, PlayHistoryProjectionIndex.Empty);
            List<PlayHistoryDiagnostic> diagnostics = [.. fallback.Diagnostics];
            diagnostics.Add(CreateProjectionFailureDiagnostic(PlayHistoryProvider.Lr2, ex.Message, readResult?.SourceProfile?.SourcePath));
            logProjectionFailure?.Invoke(ex);
            return new PlayHistoryProjectionResult(fallback.Rows, diagnostics);
        }
    }

    private PlayHistoryProjectionResult CreateProjectionResult(
        BMSLibrary library,
        BeatorajaPlayHistoryReadResult readResult,
        CancellationToken cancellationToken,
        Action<Exception> logProjectionFailure,
        out long projectionIndexMs,
        out bool projectionIndexCacheHit,
        out int projectionIndexStaleRetries,
        out long projectionMs)
    {
        projectionIndexMs = 0L;
        projectionIndexCacheHit = false;
        projectionIndexStaleRetries = 0;
        projectionMs = 0L;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projectionIndexStopwatch = Stopwatch.StartNew();
            PlayHistoryProjectionIndex projectionIndex = library.CreateBeatorajaPlayHistoryProjectionIndex(
                readResult.Rows,
                cancellationToken,
                out projectionIndexCacheHit,
                out projectionIndexStaleRetries);
            projectionIndexMs = projectionIndexStopwatch.ElapsedMilliseconds;
            cancellationToken.ThrowIfCancellationRequested();
            var projectionStopwatch = Stopwatch.StartNew();
            PlayHistoryProjectionResult projectionResult = PlayHistoryRow.ProjectBeatorajaRows(readResult, projectionIndex);
            projectionMs = projectionStopwatch.ElapsedMilliseconds;
            return projectionResult;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            projectionMs = 0L;
            PlayHistoryProjectionResult fallback = PlayHistoryRow.ProjectBeatorajaRows(readResult, PlayHistoryProjectionIndex.Empty);
            List<PlayHistoryDiagnostic> diagnostics = [.. fallback.Diagnostics];
            diagnostics.Add(CreateProjectionFailureDiagnostic(PlayHistoryProvider.Beatoraja, ex.Message, readResult?.SourceProfile?.SourcePath));
            logProjectionFailure?.Invoke(ex);
            return new PlayHistoryProjectionResult(fallback.Rows, diagnostics);
        }
    }

    private static PlayHistoryDiagnostic CreateProjectionFailureDiagnostic(
        PlayHistoryProvider provider,
        string message,
        string sourcePath)
    {
        return new PlayHistoryDiagnostic
        {
            Provider = provider,
            Stage = "projection",
            Severity = PlayHistoryDiagnosticSeverity.Error,
            Code = "play_history_projection_index_failed",
            Message = message ?? string.Empty,
            SourcePath = sourcePath ?? string.Empty
        };
    }

    private static ChartListSortParameters CloneSortParameters(ChartListSortParameters value)
    {
        return value == null
            ? null
            : new ChartListSortParameters
            {
                ColumnsName = value.ColumnsName,
                Direction = value.Direction
            };
    }

    private static bool AreSameSortParameters(ChartListSortParameters left, ChartListSortParameters right)
    {
        if (left == null || right == null)
        {
            return left == right;
        }
        return string.Equals(left?.ColumnsName, right?.ColumnsName, StringComparison.Ordinal)
            && left?.Direction == right?.Direction;
    }

    private bool IsLatestDisplayTargetStateAvailable(
        long requestId,
        long displayTargetRevision,
        string displayTargetIdentity)
    {
        PlayHistoryViewState state = PresentationState.CurrentView;
        return state != null
            && state.RequestId == requestId
            && state.DisplayTargetRevision == displayTargetRevision
            && string.Equals(displayTargetIdentity ?? string.Empty, state.DisplayTargetIdentity, StringComparison.Ordinal);
    }

    private bool IsLatestKeywordStateAvailable(
        long requestId,
        long keywordRevision,
        string keywordIdentity)
    {
        PlayHistoryViewState state = PresentationState.CurrentView;
        return state != null
            && state.RequestId == requestId
            && state.KeywordFilterRevision == keywordRevision
            && string.Equals(keywordIdentity ?? string.Empty, state.KeywordFilterIdentity, StringComparison.Ordinal);
    }

    private void SaveCurrentViewIfDisplayTargetRefreshCandidate(PlayHistoryViewState state)
    {
        PlayHistoryViewState cachedState = PresentationState.CurrentView;
        if (cachedState == null
            || cachedState.RequestId != state.RequestId
            || cachedState.DisplayTargetRevision <= state.DisplayTargetRevision)
        {
            PresentationState.CurrentView = state;
        }
    }

    private void SaveCurrentViewIfKeywordRefreshCandidate(PlayHistoryViewState state)
    {
        PlayHistoryViewState cachedState = PresentationState.CurrentView;
        if (cachedState == null
            || cachedState.RequestId != state.RequestId
            || cachedState.KeywordFilterRevision <= state.KeywordFilterRevision)
        {
            PresentationState.CurrentView = state;
        }
    }

    private static bool MatchesKeywordAndSummaryFilters(
        PlayHistoryRow row,
        GridKeywordSearchQuery keywordQuery,
        IReadOnlyList<GridKeywordSearchQuery> summaryQueries)
    {
        bool keywordMatched = keywordQuery == null || keywordQuery.MatchesPlayHistoryRow(row);
        bool summaryMatched = (summaryQueries?.Count ?? 0) == 0 || summaryQueries.Any(query => query.MatchesPlayHistoryRow(row));
        return keywordMatched && summaryMatched;
    }

    private void ThrowIfStaleDisplayTargetRequest(
        long requestId,
        long displayTargetRevision,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrentRequest(requestId) || displayTargetRevision != DisplayTargetRevision)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private static bool TryReserveRevision(ref long queuedRevision, long revision)
    {
        while (true)
        {
            long current = Interlocked.Read(ref queuedRevision);
            if (current >= revision)
            {
                return false;
            }
            if (Interlocked.CompareExchange(ref queuedRevision, revision, current) == current)
            {
                return true;
            }
        }
    }

    private static void DecrementActiveCount(ref int activeCount, string queueName)
    {
        while (true)
        {
            int current = Volatile.Read(ref activeCount);
            if (current <= 0)
            {
                throw new InvalidOperationException("The " + queueName + " refresh queue was completed without an active worker.");
            }
            if (Interlocked.CompareExchange(ref activeCount, current - 1, current) == current)
            {
                return;
            }
        }
    }

    internal CancellationToken GetCancellationToken(long requestId)
    {
        lock (PresentationState.SyncRoot)
        {
            return IsCurrentRequestUnsafe(requestId)
                ? PresentationState.RequestCancellation?.Token ?? new CancellationToken(canceled: true)
                : new CancellationToken(canceled: true);
        }
    }

    internal void Deactivate(Action deactivateSelection = null, bool clearViewActivity = true)
    {
        CancellationTokenSource cancellation;
        ExceptionDispatchInfo deactivationException = null;
        bool wasViewActive;
        lock (PresentationState.SyncRoot)
        {
            wasViewActive = isViewActive && clearViewActivity;
            cancellation = InvalidateRequestUnsafe();
            if (clearViewActivity)
            {
                isViewActive = false;
            }
            try
            {
                deactivateSelection?.Invoke();
            }
            catch (Exception ex)
            {
                deactivationException = ExceptionDispatchInfo.Capture(ex);
            }
        }
        Cancel(cancellation);
        if (wasViewActive)
        {
            RaisePropertyChanged(nameof(IsViewActive));
        }
        if (deactivationException != null)
        {
            deactivationException.Throw();
        }
    }

    private bool IsCurrentRequestUnsafe(long requestId)
    {
        return requestId > 0
            && PresentationState.RequestGeneration == requestId
            && ActiveRequest?.RequestId == requestId
            && PresentationState.RequestCancellation?.IsCancellationRequested != true;
    }

    private CancellationTokenSource InvalidateRequestUnsafe()
    {
        PresentationState.RequestGeneration++;
        CancellationTokenSource cancellation = PresentationState.RequestCancellation;
        PresentationState.RequestCancellation = null;
        ActiveRequest = null;
        return cancellation;
    }

    private static void Cancel(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

internal sealed class PlayHistoryViewRequestActivatedEventArgs : EventArgs
{
    internal PlayHistoryViewRequestActivatedEventArgs(PlayHistoryViewRequest request)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
    }

    internal PlayHistoryViewRequest Request { get; }
}

internal sealed class PlayHistoryViewRequest
{
    internal PlayHistoryViewRequest(
        PlayHistoryPeriodRequest periodRequest,
        long requestId,
        long keywordFilterRevision = 0L,
        long displayTargetRevision = 0L)
    {
        PeriodRequest = periodRequest ?? PlayHistoryPeriodRequest.All();
        RequestId = requestId;
        KeywordFilterRevision = keywordFilterRevision;
        DisplayTargetRevision = displayTargetRevision;
    }

    internal PlayHistoryPeriodRequest PeriodRequest { get; }

    internal long RequestId { get; }

    internal long KeywordFilterRevision { get; }

    internal long DisplayTargetRevision { get; }
}
