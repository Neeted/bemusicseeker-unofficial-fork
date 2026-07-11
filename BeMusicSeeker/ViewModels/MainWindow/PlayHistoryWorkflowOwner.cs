using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

internal sealed class PlayHistoryWorkflowOwner
{
    private long keywordQueuedRevision = -1L;
    private int keywordActiveCount;
    private long displayTargetQueuedRevision = -1L;
    private int displayTargetActiveCount;
    private ChartListSortParameters sortParameters;

    internal PlayHistoryWorkflowOwner()
    {
        PresentationState.CurrentSortSnapshot = new SortSnapshot(null, null, revision: 0L);
    }

    internal PlayHistoryPresentationState PresentationState { get; } = new();

    internal PlayHistoryReadCache ReadCache { get; } = new();

    internal PlayHistoryViewRequest ActiveRequest { get; private set; }

    internal PlayHistoryTerminalCommitResult ApplyTerminal(
        PlayHistoryTerminalRequest request,
        MainChartListViewModel mainChartList,
        PlaylistWorkspaceViewModel playlistWorkspace,
        RegularChartListOwner regularChartListOwner,
        PlaylistDetailBuildState playlistDetailBuildState,
        PlaylistDetailViewState playlistDetailViewState)
    {
        if (request?.ViewState == null || request.MainRowsRequest?.Rows == null)
        {
            throw new ArgumentException("A complete play-history terminal request is required.", nameof(request));
        }
        if (mainChartList == null) throw new ArgumentNullException(nameof(mainChartList));
        if (playlistWorkspace == null) throw new ArgumentNullException(nameof(playlistWorkspace));
        if (regularChartListOwner == null) throw new ArgumentNullException(nameof(regularChartListOwner));
        if (playlistDetailBuildState == null) throw new ArgumentNullException(nameof(playlistDetailBuildState));
        if (playlistDetailViewState == null) throw new ArgumentNullException(nameof(playlistDetailViewState));

        var result = new PlayHistoryTerminalCommitResult();
        try
        {
            MainChartListCoordinatedRowsApplyResult coordinated = mainChartList.ApplyCoordinatedRows(
                request.MainRowsRequest,
                commitRows => PresentationState.TryCommitTerminal(
                    request,
                    result,
                    commitRows,
                    () =>
                    {
                        result.PlaylistSourceClear = playlistDetailBuildState.CommitSourceClear(playlistDetailViewState);
                        result.BindingMode = playlistWorkspace.CommitBindingModeWithoutNotification(playlistDetailActive: false);
                        result.ColumnPresentation = playlistWorkspace.CommitColumnPresentationWithoutNotification(
                            request.ColumnSelection.PlaylistColumnSettingsVisibility,
                            request.ColumnSelection.PlaylistSummaryColumnsSettings);
                        if (request.ColumnSelection.AppliedMode.HasValue)
                        {
                            regularChartListOwner.CommitExternalColumnMode(request.ColumnSelection.AppliedMode);
                        }
                        regularChartListOwner.ResetDerivedCaches();
                    }),
                () => PublishRelatedPresentation(result, playlistWorkspace));
            result.MainRowsApply = coordinated.RowsApply;
            return result;
        }
        catch (MainChartListCoordinatedPublishException ex)
        {
            throw new PlayHistoryTerminalPublishException(ex, ownershipTransferred: true, result);
        }
    }

    internal PlayHistorySortedRowsApplyResult ApplySortedRows(
        PlayHistorySortedRowsApplyRequest request,
        Stopwatch stopwatch,
        MainChartListViewModel mainChartList,
        PlaylistWorkspaceViewModel playlistWorkspace,
        RegularChartListOwner regularChartListOwner,
        PlaylistDetailBuildState playlistDetailBuildState,
        PlaylistDetailViewState playlistDetailViewState)
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
            MainChartListColumnSelection columnSelection = regularChartListOwner.ResolveColumnSettingForViewUpdate(
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
                new HashSet<string>(request.SelectedSummaryFilterKeys, StringComparer.Ordinal));
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
                playlistWorkspace,
                regularChartListOwner,
                playlistDetailBuildState,
                playlistDetailViewState);
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
        if (result.ColumnPresentation != null)
        {
            TryPublish(() => playlistWorkspace.PublishColumnPresentation(result.ColumnPresentation), publishExceptions);
        }
        if (result.BindingMode != null)
        {
            TryPublish(() => playlistWorkspace.PublishBindingMode(result.BindingMode), publishExceptions);
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
        lock (PresentationState.SyncRoot)
        {
            if (AreSameSortParameters(sortParameters, value))
            {
                return false;
            }
            sortParameters = CloneSortParameters(value);
            PresentationState.SortRevision++;
            PresentationState.CurrentSortSnapshot = new SortSnapshot(
                sortParameters?.ColumnsName,
                sortParameters?.Direction,
                PresentationState.SortRevision);
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
        long displayTargetRevision,
        Action<PlayHistoryViewRequest> activateRequest)
    {
        CancellationTokenSource previousCancellation;
        CancellationTokenSource failedCancellation = null;
        PlayHistoryViewRequest request;
        ExceptionDispatchInfo activationException = null;
        lock (PresentationState.SyncRoot)
        {
            previousCancellation = InvalidateRequestUnsafe();
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
            try
            {
                activateRequest?.Invoke(request);
            }
            catch (Exception ex)
            {
                activationException = ExceptionDispatchInfo.Capture(ex);
                failedCancellation = InvalidateRequestUnsafe();
            }
        }
        Cancel(previousCancellation);
        Cancel(failedCancellation);
        if (activationException != null)
        {
            activationException.Throw();
        }
        return request;
    }

    internal long RegisterRequest(
        PlayHistoryPeriodRequest periodRequest,
        string keywordIdentity,
        string displayTargetIdentity,
        long displayTargetRevision,
        Action<PlayHistoryViewRequest> activateRequest)
    {
        return BeginRequest(periodRequest, keywordIdentity, displayTargetIdentity, displayTargetRevision, activateRequest).RequestId;
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

    internal PlayHistoryViewRequest ResolveViewRequest(object parameter, Func<PlayHistoryPeriodRequest> fallbackPeriodRequestFactory)
    {
        if (parameter is PlayHistoryViewRequest request)
        {
            return request;
        }
        if (parameter is PlayHistoryPeriodRequest periodRequest)
        {
            return new PlayHistoryViewRequest(periodRequest, SnapshotActiveRequest()?.RequestId ?? 0L);
        }
        lock (PresentationState.SyncRoot)
        {
            return ActiveRequest ?? new PlayHistoryViewRequest(
                fallbackPeriodRequestFactory?.Invoke() ?? PlayHistoryPeriodRequest.All(),
                requestId: 0L);
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

    internal PlayHistoryProjectionResult CreateProjectionResult(
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

    internal PlayHistoryProjectionResult CreateProjectionResult(
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

    internal void Deactivate(Action deactivateSelection = null)
    {
        CancellationTokenSource cancellation;
        ExceptionDispatchInfo deactivationException = null;
        lock (PresentationState.SyncRoot)
        {
            cancellation = InvalidateRequestUnsafe();
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
