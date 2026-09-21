using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Immutable play-history runtime event delivered to the owning logging sink.
/// </summary>
/// <param name="Name">The stable event name.</param>
/// <param name="Fields">The formatted event fields.</param>
/// <param name="IsWarning">Whether the event is a warning rather than a normal performance event.</param>
/// <param name="Exception">The failure associated with the event, when one exists.</param>
internal readonly record struct PlayHistoryRuntimeEvent(
    string Name,
    string Fields,
    bool IsWarning = false,
    Exception Exception = null)
{
    /// <summary>
    /// Gets the logger-compatible message while avoiding a trailing separator for events without fields.
    /// </summary>
    internal string Message
            => string.IsNullOrEmpty(Fields)
                ? (Name ?? string.Empty)
                : (Name ?? string.Empty) + " " + Fields;
}

/// <summary>
/// Formats and reports play-history stage events without depending on the shell logger implementation.
/// </summary>
internal sealed class PlayHistoryRuntimeEventReporter
{
    private readonly Action<PlayHistoryRuntimeEvent> sink;

    /// <summary>
    /// Initializes a reporter with the sink owned by its caller.
    /// </summary>
    /// <param name="sink">The immutable event recording or logging sink.</param>
    internal PlayHistoryRuntimeEventReporter(Action<PlayHistoryRuntimeEvent> sink)
    {
        this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    /// <summary>
    /// Reports exactly the stage event represented by one read-progress input.
    /// </summary>
    /// <param name="periodRequest">The requested play-history period.</param>
    /// <param name="requestId">The request identity.</param>
    /// <param name="progress">The immutable stage progress snapshot.</param>
    internal void ReportReadWorkflowProgress(
        PlayHistoryPeriodRequest periodRequest,
        long requestId,
        PlayHistoryReadWorkflowProgress progress)
    {
        if (progress == null)
        {
            return;
        }

        if (progress.Stage == PlayHistoryReadWorkflowProgressStage.ReadCompleted)
        {
            PlayHistoryPeriodRequest requestedPeriod = RequirePayload(periodRequest, "periodRequest");
            PlayHistoryReadStageMetrics read = RequirePayload(progress.Read, "progress.Read");
            Report(
                "play_history_read_done",
                "period=" + requestedPeriod.Kind
                + " requestId=" + requestId
                + " provider=" + read.Provider
                + " schemaStatus=" + read.SchemaStatus
                + " cacheHit=" + read.CacheHit.ToString().ToLowerInvariant()
                + " rows=" + read.RowCount
                + " diagnosticsCount=" + read.DiagnosticCount
                + " elapsedMs=" + read.ElapsedMs);
            return;
        }

        if (progress.Stage == PlayHistoryReadWorkflowProgressStage.PeriodIndexCompleted)
        {
            PlayHistoryPeriodRequest requestedPeriod = RequirePayload(periodRequest, "periodRequest");
            PlayHistoryReadStageMetrics read = RequirePayload(progress.Read, "progress.Read");
            PlayHistoryPeriodIndexStageMetrics periodIndex = RequirePayload(progress.PeriodIndex, "progress.PeriodIndex");
            if (periodIndex.Status == PlayHistoryPeriodIndexStageStatus.Completed)
            {
                Report(
                    "play_history_read_period_index_done",
                    "period=" + requestedPeriod.Kind
                    + " requestId=" + requestId
                    + " provider=" + read.Provider
                    + " schemaStatus=" + read.SchemaStatus
                    + " cacheHit=" + periodIndex.CacheHit.ToString().ToLowerInvariant()
                    + " days=" + periodIndex.DayCount
                    + " diagnosticsCount=" + periodIndex.DiagnosticCount
                    + " elapsedMs=" + periodIndex.ElapsedMs);
            }
            else if (periodIndex.Status == PlayHistoryPeriodIndexStageStatus.SkippedSchemaUnavailable)
            {
                Report(
                    "play_history_read_period_index_skipped",
                    "period=" + requestedPeriod.Kind
                    + " requestId=" + requestId
                    + " provider=" + read.Provider
                    + " schemaStatus=" + read.SchemaStatus
                    + " reason=schema_unavailable");
            }
            return;
        }

        if (progress.Stage != PlayHistoryReadWorkflowProgressStage.ProjectionCompleted)
        {
            return;
        }

        PlayHistoryPeriodRequest requestedProjectionPeriod = RequirePayload(periodRequest, "periodRequest");
        PlayHistoryReadStageMetrics projectionRead = RequirePayload(progress.Read, "progress.Read");
        PlayHistoryPeriodIndexStageMetrics periodIndexForProjection = RequirePayload(progress.PeriodIndex, "progress.PeriodIndex");
        PlayHistoryProjectionStageMetrics projection = RequirePayload(progress.Projection, "progress.Projection");
        string projectionEventName = projection.Status switch
        {
            PlayHistoryProjectionStageStatus.SkippedNoRows => "play_history_projection_skipped",
            PlayHistoryProjectionStageStatus.Fallback => "play_history_projection_fallback",
            _ => "play_history_projection_done"
        };
        string projectionReason = projection.Status switch
        {
            PlayHistoryProjectionStageStatus.Fallback => "projection_index_failed",
            PlayHistoryProjectionStageStatus.SkippedNoRows
                when periodIndexForProjection.Status == PlayHistoryPeriodIndexStageStatus.SkippedSchemaUnavailable
                => "schema_unavailable",
            PlayHistoryProjectionStageStatus.SkippedNoRows => "no_rows",
            _ => string.Empty
        };
        Report(
            projectionEventName,
            "period=" + requestedProjectionPeriod.Kind
            + " requestId=" + requestId
            + " provider=" + projectionRead.Provider
            + " schemaStatus=" + projectionRead.SchemaStatus
            + " rawCount=" + projection.RawCount
            + " projectedCount=" + projection.ProjectedCount
            + " diagnosticsCount=" + projection.DiagnosticCount
            + " fallback=" + (projection.Status == PlayHistoryProjectionStageStatus.Fallback).ToString().ToLowerInvariant()
            + " projectionIndexMs=" + projection.IndexMs
            + " projectionIndexCacheHit=" + projection.IndexCacheHit.ToString().ToLowerInvariant()
            + " projectionIndexStaleRetries=" + projection.IndexStaleRetries
            + " projectionMs=" + projection.ProjectionMs
            + (string.IsNullOrEmpty(projectionReason) ? string.Empty : " reason=" + projectionReason));
        if (projection.Failure != null)
        {
            Report(
                "play_history_projection_index_failed",
                string.Empty,
                isWarning: true,
                projection.Failure);
        }
    }

    /// <summary>
    /// Reports the immutable execution result and preserves the existing view event fields.
    /// </summary>
    /// <param name="execution">The completed, stale, or canceled execution.</param>
    /// <param name="currentDisplayTarget">The owner-selected display target at reporting time.</param>
    /// <param name="currentRequestId">The owner-current request identity.</param>
    internal void ReportViewExecution(
        PlayHistoryViewExecutionResult execution,
        PlayHistoryDisplayTargetItem currentDisplayTarget,
        long currentRequestId)
    {
        if (execution == null)
        {
            return;
        }

        PlayHistoryViewExecutionRequest request = RequirePayload(execution.Request, "execution.Request");
        PlayHistoryViewRequest viewRequest = RequirePayload(request.ViewRequest, "execution.Request.ViewRequest");
        PlayHistoryPeriodRequest periodRequest = RequirePayload(viewRequest.PeriodRequest, "execution.Request.ViewRequest.PeriodRequest");
        RequirePayload(request.Stopwatch, "execution.Request.Stopwatch");
        ValidateExecutionPayloads(execution);
        if (execution.Status == PlayHistoryViewExecutionStatus.NoCurrentMatchingState)
        {
            Report(
                "play_history_view_presentation_skipped",
                "period=" + periodRequest.Kind
                + " requestId=" + viewRequest.RequestId
                + " reason=no_current_matching_state"
                + " totalMs=" + request.Stopwatch.ElapsedMilliseconds);
            Report(
                "main_view_build",
                "mode=" + request.Mode
                + " requestedMode=" + request.RequestedMode
                + " parameterType=" + (request.Parameter?.GetType().Name ?? "(null)")
                + " playHistorySortOnly=true skipped=true reason=no_current_matching_state"
                + " playHistoryPeriod=" + periodRequest.Kind
                + " totalMs=" + request.Stopwatch.ElapsedMilliseconds);
            return;
        }

        PlayHistoryReadWorkflowResult readResult = execution.ReadResult;
        PlayHistoryReadPresentationBuildResult readPresentation = readResult?.Presentation;
        PlayHistoryPresentationOnlyBuildResult presentationOnly = execution.PresentationOnlyResult;
        if (readPresentation != null && readResult?.Read != null)
        {
            ReportDisplayTargetFilter(
                periodRequest,
                viewRequest.RequestId,
                currentDisplayTarget,
                readPresentation.DisplayTargetSourceCount,
                readPresentation.DisplayTargetResultCount);
            ReportKeywordFilter(
                periodRequest,
                viewRequest.RequestId,
                request.KeywordFilter,
                readResult.Read.RowCount,
                readPresentation.DisplayTargetResultCount,
                readPresentation.KeywordCount,
                readPresentation.KeywordMs);
        }
        else if (presentationOnly != null)
        {
            if (presentationOnly.DisplayTargetApplied)
            {
                ReportDisplayTargetFilter(
                    periodRequest,
                    viewRequest.RequestId,
                    currentDisplayTarget,
                    presentationOnly.DisplayTargetSourceCount,
                    presentationOnly.DisplayTargetResultCount);
            }
            if (presentationOnly.KeywordFilterApplied)
            {
                ReportKeywordFilter(
                    periodRequest,
                    viewRequest.RequestId,
                    request.KeywordFilter,
                    presentationOnly.KeywordSourceCount,
                    presentationOnly.KeywordProjectedCount,
                    presentationOnly.KeywordCount,
                    presentationOnly.KeywordMs);
            }
        }

        if (execution.Status != PlayHistoryViewExecutionStatus.Applied)
        {
            long elapsedMs = execution.Status == PlayHistoryViewExecutionStatus.ReadCanceled
                ? readResult?.ElapsedThroughCancellationMs ?? request.Stopwatch.ElapsedMilliseconds
                : request.Stopwatch.ElapsedMilliseconds;
            ReportStaleViewRequest(
                request.Mode,
                request.RequestedMode,
                request.Parameter,
                periodRequest,
                viewRequest.RequestId,
                currentRequestId,
                elapsedMs);
            return;
        }

        PlayHistoryViewState state = readPresentation?.State ?? presentationOnly?.State;
        PlayHistorySortedRowsApplyResult applyResult = execution.ApplyResult;
        PlayHistoryTerminalCommitResult terminalCommit = applyResult.TerminalCommit;
        MainChartListRowsApplyResult rowsApply = terminalCommit.MainRowsApply;
        long prepareSwapMs = rowsApply.PrepareSwapMs;
        long columnSettingMs = rowsApply.ColumnSettingMs;
        long setViewMs = rowsApply.SetViewMs;
        IReadOnlyList<PlayHistoryDiagnostic> diagnostics = applyResult.Diagnostics ?? [];
        int diagnosticsCount = diagnostics.Count;
        if (diagnosticsCount > 0)
        {
            ReportDiagnostics(periodRequest, applyResult.SortProfile, diagnostics);
        }
        long sortMs = execution.SortMs + applyResult.AdditionalSortMs;
        Report(
            "play_history_view_apply",
            "period=" + periodRequest.Kind
            + " requestId=" + viewRequest.RequestId
            + " sortOnly=" + execution.FromSortOnly.ToString().ToLowerInvariant()
            + " sortSucceeded=" + applyResult.SortSucceeded.ToString().ToLowerInvariant()
            + " sortProfile=" + (applyResult.SortProfile ?? string.Empty)
            + " schemaStatus=" + state.SchemaStatus
            + " diagnosticsCount=" + diagnosticsCount
            + " sourceCount=" + state.SourceCount
            + " projectedCount=" + state.ProjectedRows.Count
            + " viewCount=" + applyResult.ViewCount
            + " readMs=" + execution.ReadMs
            + " periodIndexMs=" + execution.PeriodIndexMs
            + " projectionIndexMs=" + execution.ProjectionIndexMs
            + " projectionIndexCacheHit=" + execution.ProjectionIndexCacheHit.ToString().ToLowerInvariant()
            + " projectionIndexStaleRetries=" + execution.ProjectionIndexStaleRetries
            + " projectionMs=" + execution.ProjectionMs
            + " keywordMs=" + execution.KeywordMs
            + " keywordCount=" + execution.KeywordCount
            + " sortMs=" + sortMs
            + " columnSettingMs=" + columnSettingMs
            + " setViewMs=" + setViewMs
            + " totalMs=" + request.Stopwatch.ElapsedMilliseconds);
        Report(
            "main_view_build",
            "mode=" + request.Mode
            + " requestedMode=" + request.RequestedMode
            + " parameterType=" + (request.Parameter?.GetType().Name ?? "(null)")
            + " playHistoryPeriod=" + periodRequest.Kind
            + " playHistorySortOnly=" + execution.FromSortOnly.ToString().ToLowerInvariant()
            + " readMs=" + execution.ReadMs
            + " periodIndexMs=" + execution.PeriodIndexMs
            + " projectionIndexMs=" + execution.ProjectionIndexMs
            + " projectionIndexCacheHit=" + execution.ProjectionIndexCacheHit.ToString().ToLowerInvariant()
            + " projectionIndexStaleRetries=" + execution.ProjectionIndexStaleRetries
            + " projectionMs=" + execution.ProjectionMs
            + " keywordMs=" + execution.KeywordMs
            + " keywordCount=" + execution.KeywordCount
            + " sortMs=" + sortMs
            + " sortProfile=" + applyResult.SortProfile
            + " schemaStatus=" + state.SchemaStatus
            + " diagnosticsCount=" + diagnosticsCount
            + " columnSettingMs=" + columnSettingMs
            + " prepareSwapMs=" + prepareSwapMs
            + " setViewMs=" + setViewMs
            + " columnSettingReuse=" + rowsApply.ColumnSettingReuse
            + " totalMs=" + request.Stopwatch.ElapsedMilliseconds
            + " sourceCount=" + state.SourceCount
            + " projectedCount=" + state.ProjectedRows.Count
            + " viewCount=" + applyResult.ViewCount);
    }

    /// <summary>
    /// Reports one display-target filter event for a view execution.
    /// </summary>
    internal void ReportDisplayTargetFilter(
        PlayHistoryPeriodRequest periodRequest,
        long requestId,
        PlayHistoryDisplayTargetItem displayTarget,
        int sourceCount,
        int targetCount)
    {
        PlayHistoryDisplayTargetItem safeTarget = displayTarget ?? PlayHistoryDisplayTargetItem.All;
        Report(
            "play_history_view_display_target_filter",
            "period=" + (periodRequest?.Kind.ToString() ?? string.Empty)
            + " requestId=" + requestId
            + " targetKind=" + safeTarget.Kind
            + " targetMode=" + safeTarget.Mode
            + " targetIdentity=" + Quote(safeTarget.Identity)
            + " active=" + safeTarget.UsesProjection.ToString().ToLowerInvariant()
            + " rowFiltering=" + safeTarget.IsFiltering.ToString().ToLowerInvariant()
            + " sourceCount=" + sourceCount
            + " targetCount=" + targetCount);
    }

    /// <summary>
    /// Reports one keyword-filter event for a view execution.
    /// </summary>
    internal void ReportKeywordFilter(
        PlayHistoryPeriodRequest periodRequest,
        long requestId,
        string keywordFilter,
        int sourceCount,
        int projectedCount,
        int keywordCount,
        long keywordMs)
    {
        Report(
            "play_history_view_keyword_filter",
            "period=" + (periodRequest?.Kind.ToString() ?? string.Empty)
            + " requestId=" + requestId
            + " keywordActive=" + (!string.IsNullOrWhiteSpace(keywordFilter)).ToString().ToLowerInvariant()
            + " sourceCount=" + sourceCount
            + " projectedCount=" + projectedCount
            + " keywordCount=" + keywordCount
            + " elapsedMs=" + keywordMs);
    }

    /// <summary>
    /// Reports a stale request with the owner-current request identity.
    /// </summary>
    internal void ReportStaleViewRequest(
        MainViewUpdateMode mode,
        MainViewUpdateMode requestedMode,
        object parameter,
        PlayHistoryPeriodRequest periodRequest,
        long requestId,
        long currentRequestId,
        long elapsedMs)
    {
        Report(
            "play_history_view_stale_skipped",
            "mode=" + mode
            + " requestedMode=" + requestedMode
            + " parameterType=" + (parameter?.GetType().Name ?? "(null)")
            + " period=" + (periodRequest?.Kind.ToString() ?? string.Empty)
            + " requestId=" + requestId
            + " currentRequestId=" + currentRequestId
            + " elapsedMs=" + elapsedMs);
        Report(
            "main_view_build",
            "mode=" + mode
            + " requestedMode=" + requestedMode
            + " parameterType=" + (parameter?.GetType().Name ?? "(null)")
            + " playHistoryPeriod=" + (periodRequest?.Kind.ToString() ?? string.Empty)
            + " skipped=true reason=stale_play_history_request requestId=" + requestId
            + " currentRequestId=" + currentRequestId
            + " elapsedMs=" + elapsedMs);
    }

    /// <summary>
    /// Reports diagnostics using the existing warning event fields and escaping rules.
    /// </summary>
    internal void ReportDiagnostics(
        PlayHistoryPeriodRequest request,
        string sortProfile,
        IReadOnlyList<PlayHistoryDiagnostic> diagnostics)
    {
        string diagnosticText = string.Join(
            ",",
            (diagnostics ?? [])
                .Take(20)
                .Select(diagnostic => "severity=" + Quote(diagnostic?.Severity.ToString())
                    + " stage=" + Quote(diagnostic?.Stage)
                    + " code=" + Quote(diagnostic?.Code)
                    + " message=" + Quote(diagnostic?.Message)
                    + " source=" + Quote(diagnostic?.SourcePath)));
        Report(
            "play_history_diagnostics",
            "period=" + (request?.Kind.ToString() ?? string.Empty)
            + " sortProfile=" + (sortProfile ?? string.Empty)
            + " count=" + (diagnostics?.Count ?? 0)
            + " items=" + diagnosticText,
            isWarning: true);
    }

    private static void ValidateExecutionPayloads(PlayHistoryViewExecutionResult execution)
    {
        if (execution.Status != PlayHistoryViewExecutionStatus.Applied)
        {
            return;
        }

        PlayHistoryReadWorkflowResult readResult = execution.ReadResult;
        PlayHistoryReadPresentationBuildResult readPresentation = readResult?.Presentation;
        PlayHistoryPresentationOnlyBuildResult presentationOnly = execution.PresentationOnlyResult;
        if (readPresentation == null && presentationOnly == null)
        {
            throw new InvalidOperationException(
                "Applied play-history execution requires execution.ReadResult.Presentation or execution.PresentationOnlyResult.");
        }
        if (readPresentation != null)
        {
            RequirePayload(readResult, "execution.ReadResult");
            RequirePayload(readResult.Read, "execution.ReadResult.Read");
            if (!readPresentation.Built)
            {
                throw new InvalidOperationException(
                    "Applied play-history execution requires a built execution.ReadResult.Presentation.");
            }
        }
        if (presentationOnly != null
            && presentationOnly.Status != PlayHistoryPresentationOnlyBuildStatus.Built)
        {
            throw new InvalidOperationException(
                "Applied play-history execution requires a built execution.PresentationOnlyResult.");
        }

        RequirePayload(readPresentation?.State ?? presentationOnly?.State, "execution.State");
        PlayHistorySortedRowsApplyResult applyResult = RequirePayload(execution.ApplyResult, "execution.ApplyResult");
        if (applyResult.Status != PlayHistorySortedRowsApplyStatus.Applied)
        {
            throw new InvalidOperationException(
                "Applied play-history execution requires an applied execution.ApplyResult.");
        }
        PlayHistoryTerminalCommitResult terminalCommit = RequirePayload(
            applyResult.TerminalCommit,
            "execution.ApplyResult.TerminalCommit");
        if (!terminalCommit.Applied)
        {
            throw new InvalidOperationException(
                "Applied play-history execution requires an applied execution.ApplyResult.TerminalCommit.");
        }
    }

    private static T RequirePayload<T>(T payload, string name)
        where T : class
    {
        if (payload == null)
        {
            throw new InvalidOperationException("Required play-history runtime payload is missing: " + name + ".");
        }
        return payload;
    }

    private void Report(string name, string fields, bool isWarning = false, Exception exception = null)
    {
        sink(new PlayHistoryRuntimeEvent(name, fields, isWarning, exception));
    }

    private static string Quote(string value)
    {
        return "\""
            + (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
            + "\"";
    }
}
