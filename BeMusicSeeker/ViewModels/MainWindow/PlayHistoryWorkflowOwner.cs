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

    internal PlayHistoryPresentationState PresentationState { get; } = new();

    internal PlayHistoryReadCache ReadCache { get; } = new();

    internal PlayHistoryViewRequest ActiveRequest { get; private set; }

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
