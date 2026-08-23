using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlayHistoryRuntimeEventReporterTests
{
    [TestMethod]
    public void ReportReadWorkflowProgress_EmitsExpectedStageEventsExactlyOnce()
    {
        var events = new List<PlayHistoryRuntimeEvent>();
        var reporter = new PlayHistoryRuntimeEventReporter(events.Add);
        PlayHistoryPeriodRequest period = PlayHistoryPeriodRequest.Create(
            PlayHistoryPeriodKind.Today,
            new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);
        var read = new PlayHistoryReadStageMetrics(
            completed: true,
            provider: PlayHistoryProvider.Lr2,
            schemaStatus: Lr2PlayHistorySchemaStatus.Installed,
            cacheHit: true,
            rowCount: 4,
            diagnosticCount: 2,
            elapsedMs: 31);

        reporter.ReportReadWorkflowProgress(
            period,
            requestId: 7,
            PlayHistoryReadWorkflowProgress.ReadCompleted(read, schemaStatusSnapshot: null));
        Assert.AreEqual(1, events.Count);
        Assert.AreEqual("play_history_read_done", events[0].Name);
        Assert.AreEqual(
            "period=Today requestId=7 provider=Lr2 schemaStatus=Installed cacheHit=true rows=4 diagnosticsCount=2 elapsedMs=31",
            events[0].Fields);

        reporter.ReportReadWorkflowProgress(
            period,
            requestId: 7,
            PlayHistoryReadWorkflowProgress.PeriodIndexCompleted(
                read,
                new PlayHistoryPeriodIndexStageMetrics(
                    PlayHistoryPeriodIndexStageStatus.Completed,
                    cacheHit: false,
                    dayCount: 3,
                    diagnosticCount: 1,
                    elapsedMs: 12)));
        Assert.AreEqual(2, events.Count);
        Assert.AreEqual("play_history_read_period_index_done", events[1].Name);
        Assert.AreEqual(
            "period=Today requestId=7 provider=Lr2 schemaStatus=Installed cacheHit=false days=3 diagnosticsCount=1 elapsedMs=12",
            events[1].Fields);

        var projectionFailure = new InvalidOperationException("projection index failed");
        reporter.ReportReadWorkflowProgress(
            period,
            requestId: 7,
            PlayHistoryReadWorkflowProgress.ProjectionCompleted(
                read,
                new PlayHistoryPeriodIndexStageMetrics(
                    PlayHistoryPeriodIndexStageStatus.Completed,
                    cacheHit: false,
                    dayCount: 3,
                    diagnosticCount: 1,
                    elapsedMs: 12),
                new PlayHistoryProjectionStageMetrics(
                    PlayHistoryProjectionStageStatus.Fallback,
                    rawCount: 4,
                    projectedCount: 3,
                    diagnosticCount: 1,
                    indexMs: 8,
                    indexCacheHit: true,
                    indexStaleRetries: 2,
                    projectionMs: 19,
                    failure: projectionFailure)));

        Assert.AreEqual(4, events.Count);
        Assert.AreEqual("play_history_projection_fallback", events[2].Name);
        Assert.AreEqual(
            "period=Today requestId=7 provider=Lr2 schemaStatus=Installed rawCount=4 projectedCount=3 diagnosticsCount=1 fallback=true projectionIndexMs=8 projectionIndexCacheHit=true projectionIndexStaleRetries=2 projectionMs=19 reason=projection_index_failed",
            events[2].Fields);
        Assert.AreEqual("play_history_projection_index_failed", events[3].Name);
        Assert.AreEqual(string.Empty, events[3].Fields);
        Assert.IsTrue(events[3].IsWarning);
        Assert.AreSame(projectionFailure, events[3].Exception);

        CollectionAssert.AreEquivalent(
            new[]
            {
                "play_history_read_done",
                "play_history_read_period_index_done",
                "play_history_projection_fallback",
                "play_history_projection_index_failed"
            },
            events.Select(runtimeEvent => runtimeEvent.Name).ToArray());
    }

    [TestMethod]
    public void ReportReadWorkflowProgress_PreservesSkipReasonsAndFailsMalformedPayloadsWithoutEmitting()
    {
        var events = new List<PlayHistoryRuntimeEvent>();
        var reporter = new PlayHistoryRuntimeEventReporter(events.Add);
        PlayHistoryPeriodRequest period = PlayHistoryPeriodRequest.All();
        var read = new PlayHistoryReadStageMetrics(
            completed: true,
            provider: PlayHistoryProvider.Beatoraja,
            schemaStatus: Lr2PlayHistorySchemaStatus.Repairable,
            cacheHit: false,
            rowCount: 0,
            diagnosticCount: 1,
            elapsedMs: 4);
        var skippedPeriodIndex = new PlayHistoryPeriodIndexStageMetrics(
            PlayHistoryPeriodIndexStageStatus.SkippedSchemaUnavailable,
            cacheHit: false,
            dayCount: 0,
            diagnosticCount: 1,
            elapsedMs: 5);

        reporter.ReportReadWorkflowProgress(
            period,
            requestId: 9,
            PlayHistoryReadWorkflowProgress.PeriodIndexCompleted(read, skippedPeriodIndex));
        reporter.ReportReadWorkflowProgress(
            period,
            requestId: 9,
            PlayHistoryReadWorkflowProgress.ProjectionCompleted(
                read,
                skippedPeriodIndex,
                new PlayHistoryProjectionStageMetrics(
                    PlayHistoryProjectionStageStatus.SkippedNoRows,
                    rawCount: 0,
                    projectedCount: 0,
                    diagnosticCount: 0,
                    indexMs: 0,
                    indexCacheHit: false,
                    indexStaleRetries: 0,
                     projectionMs: 0,
                     failure: null)));
        var malformedEvents = new List<PlayHistoryRuntimeEvent>();
        var malformedReporter = new PlayHistoryRuntimeEventReporter(malformedEvents.Add);
        InvalidOperationException missingProjection = Assert.ThrowsException<InvalidOperationException>(() =>
            malformedReporter.ReportReadWorkflowProgress(
                period,
                requestId: 9,
                PlayHistoryReadWorkflowProgress.ProjectionCompleted(read, skippedPeriodIndex, projection: null)));
        StringAssert.Contains(missingProjection.Message, "progress.Projection");
        Assert.AreEqual(0, malformedEvents.Count);

        InvalidOperationException missingRead = Assert.ThrowsException<InvalidOperationException>(() =>
            malformedReporter.ReportReadWorkflowProgress(
                period,
                requestId: 9,
                PlayHistoryReadWorkflowProgress.ReadCompleted(read: null, schemaStatusSnapshot: null)));
        StringAssert.Contains(missingRead.Message, "progress.Read");
        Assert.AreEqual(0, malformedEvents.Count);

        InvalidOperationException missingPeriodIndex = Assert.ThrowsException<InvalidOperationException>(() =>
            malformedReporter.ReportReadWorkflowProgress(
                period,
                requestId: 9,
                PlayHistoryReadWorkflowProgress.PeriodIndexCompleted(read, periodIndex: null)));
        StringAssert.Contains(missingPeriodIndex.Message, "progress.PeriodIndex");
        Assert.AreEqual(0, malformedEvents.Count);

        reporter.ReportReadWorkflowProgress(period, requestId: 9, progress: null);

        Assert.AreEqual(2, events.Count);
        Assert.AreEqual("play_history_read_period_index_skipped", events[0].Name);
        StringAssert.Contains(events[0].Fields, "reason=schema_unavailable");
        Assert.AreEqual("play_history_projection_skipped", events[1].Name);
        StringAssert.Contains(events[1].Fields, "reason=schema_unavailable");
        Assert.IsTrue(events.All(runtimeEvent => events.Count(eventItem => eventItem.Name == runtimeEvent.Name) == 1));
    }

    [TestMethod]
    public void ReportViewExecution_EmitsStaleAndNoCurrentRoutesWithoutDuplicates()
    {
        var events = new List<PlayHistoryRuntimeEvent>();
        var reporter = new PlayHistoryRuntimeEventReporter(events.Add);
        PlayHistoryPeriodRequest period = PlayHistoryPeriodRequest.All();
        var request = CreateViewExecutionRequest(period, requestId: 42);

        reporter.ReportViewExecution(
            PlayHistoryViewExecutionResult.NoCurrentMatchingState(request, fromSortOnly: true),
            currentDisplayTarget: null,
            currentRequestId: 43);
        reporter.ReportViewExecution(
            PlayHistoryViewExecutionResult.Stale(request),
            currentDisplayTarget: PlayHistoryDisplayTargetItem.All,
            currentRequestId: 43);

        Assert.AreEqual(4, events.Count);
        Assert.AreEqual("play_history_view_presentation_skipped", events[0].Name);
        StringAssert.Contains(events[0].Fields, "reason=no_current_matching_state");
        Assert.AreEqual("main_view_build", events[1].Name);
        StringAssert.Contains(events[1].Fields, "reason=no_current_matching_state");
        Assert.AreEqual("play_history_view_stale_skipped", events[2].Name);
        StringAssert.Contains(events[2].Fields, "currentRequestId=43");
        Assert.AreEqual("main_view_build", events[3].Name);
        StringAssert.Contains(events[3].Fields, "reason=stale_play_history_request");
    }

    [TestMethod]
    public void ReportStaleViewRequest_EmitsTheLegacyMainViewBuildPairExactlyOnce()
    {
        var events = new List<PlayHistoryRuntimeEvent>();
        var reporter = new PlayHistoryRuntimeEventReporter(events.Add);
        PlayHistoryPeriodRequest period = PlayHistoryPeriodRequest.All();

        reporter.ReportStaleViewRequest(
            MainViewUpdateMode.PlayHistorySelected,
            MainViewUpdateMode.KeywordFilterUpdated,
            parameter: null,
            periodRequest: period,
            requestId: 42,
            currentRequestId: 43,
            elapsedMs: 6);

        CollectionAssert.AreEqual(
            new[] { "play_history_view_stale_skipped", "main_view_build" },
            events.Select(runtimeEvent => runtimeEvent.Name).ToArray());
        StringAssert.Contains(events[0].Fields, "period=All requestId=42 currentRequestId=43 elapsedMs=6");
        StringAssert.Contains(events[1].Fields, "mode=PlayHistorySelected requestedMode=KeywordFilterUpdated parameterType=(null)");
        StringAssert.Contains(events[1].Fields, "playHistoryPeriod=All skipped=true reason=stale_play_history_request requestId=42 currentRequestId=43 elapsedMs=6");
    }

    [TestMethod]
    public void ReportViewExecution_EmitsFiltersDiagnosticsAndApplyFields()
    {
        var events = new List<PlayHistoryRuntimeEvent>();
        var reporter = new PlayHistoryRuntimeEventReporter(events.Add);
        PlayHistoryPeriodRequest period = PlayHistoryPeriodRequest.Create(PlayHistoryPeriodKind.Today);
        PlayHistoryDisplayTargetItem displayTarget = PlayHistoryDisplayTargetItem.All;
        var request = CreateViewExecutionRequest(period, requestId: 12, keywordFilter: "title");
        var state = new PlayHistoryViewState(
            requestId: 12,
            periodRequest: period,
            allProjectedRows: [],
            filterSourceRows: [],
            projectedRows: [],
            diagnostics: [],
            provider: PlayHistoryProvider.Lr2,
            schemaStatus: Lr2PlayHistorySchemaStatus.Installed,
            sourceCount: 4,
            sortSnapshot: new SortSnapshot("title", ListSortDirection.Ascending, revision: 1),
            keywordFilter: "title",
            keywordFilterRevision: 1,
            displayTarget: displayTarget,
            displayTargetRevision: 1);
        var presentation = PlayHistoryPresentationOnlyBuildResult.Built(
            state,
            sortedRows: [],
            sortSucceeded: true,
            sortProfile: "title",
            sortMs: 2,
            keywordMs: 3,
            keywordCount: 1,
            displayTargetApplied: true,
            displayTargetSourceCount: 4,
            displayTargetResultCount: 3,
            keywordFilterApplied: true,
            keywordSourceCount: 3,
            keywordProjectedCount: 1);
        var terminalCommit = new PlayHistoryTerminalCommitResult
        {
            Applied = true,
            MainRowsApply = new MainChartListRowsApplyResult(1, 2, 3, 4, columnSettingReuse: true)
        };
        PlayHistorySortedRowsApplyResult applyResult = PlayHistorySortedRowsApplyResult.Applied(
            sortSucceeded: true,
            sortProfile: "title",
            additionalSortMs: 5,
            viewCount: 3,
            diagnostics:
            [
                new PlayHistoryDiagnostic
                {
                    Provider = PlayHistoryProvider.Lr2,
                    Stage = "apply",
                    Severity = PlayHistoryDiagnosticSeverity.Warning,
                    Code = "sort_warning",
                    Message = "sort fallback",
                    SourcePath = "score.db"
                }
            ],
            terminalCommit: terminalCommit);
        PlayHistoryViewExecutionResult execution = PlayHistoryViewExecutionResult.Applied(
            request,
            readResult: null,
            presentationOnlyResult: presentation,
            applyResult: applyResult,
            fromSortOnly: false,
            readMs: 10,
            projectionIndexMs: 3,
            projectionIndexCacheHit: true,
            projectionIndexStaleRetries: 1,
            projectionMs: 4,
            periodIndexMs: 2,
            keywordMs: 3,
            keywordCount: 1,
            sortMs: 2);

        reporter.ReportViewExecution(execution, displayTarget, currentRequestId: 12);

        CollectionAssert.AreEqual(
            new[]
            {
                "play_history_view_display_target_filter",
                "play_history_view_keyword_filter",
                "play_history_diagnostics",
                "play_history_view_apply",
                "main_view_build"
            },
            events.Select(runtimeEvent => runtimeEvent.Name).ToArray());
        StringAssert.Contains(events[0].Fields, "sourceCount=4 targetCount=3");
        StringAssert.Contains(events[1].Fields, "keywordActive=true sourceCount=3 projectedCount=1 keywordCount=1");
        Assert.IsTrue(events[2].IsWarning);
        StringAssert.Contains(events[2].Fields, "code=\"sort_warning\"");
        StringAssert.Contains(events[3].Fields, "sortProfile=title");
        StringAssert.Contains(events[3].Fields, "viewCount=3");
        StringAssert.Contains(events[4].Fields, "playHistoryPeriod=Today");
    }

    [TestMethod]
    public void ReportViewExecution_FailsIncompleteAppliedPayloadBeforeEmittingAnyEvents()
    {
        var events = new List<PlayHistoryRuntimeEvent>();
        var reporter = new PlayHistoryRuntimeEventReporter(events.Add);
        PlayHistoryPeriodRequest period = PlayHistoryPeriodRequest.All();
        var request = CreateViewExecutionRequest(period, requestId: 12, keywordFilter: "title");
        var state = new PlayHistoryViewState(
            requestId: 12,
            periodRequest: period,
            allProjectedRows: [],
            filterSourceRows: [],
            projectedRows: [],
            diagnostics: [],
            provider: PlayHistoryProvider.Lr2,
            schemaStatus: Lr2PlayHistorySchemaStatus.Installed,
            sourceCount: 1,
            sortSnapshot: new SortSnapshot("title", ListSortDirection.Ascending, revision: 1),
            keywordFilter: "title",
            keywordFilterRevision: 1,
            displayTarget: PlayHistoryDisplayTargetItem.All,
            displayTargetRevision: 1);
        var presentation = PlayHistoryPresentationOnlyBuildResult.Built(
            state,
            sortedRows: [],
            sortSucceeded: true,
            sortProfile: "title",
            sortMs: 1,
            keywordMs: 1,
            keywordCount: 1,
            displayTargetApplied: true,
            displayTargetSourceCount: 1,
            displayTargetResultCount: 1,
            keywordFilterApplied: true,
            keywordSourceCount: 1,
            keywordProjectedCount: 1);
        var validTerminalCommit = new PlayHistoryTerminalCommitResult
        {
            Applied = true,
            MainRowsApply = new MainChartListRowsApplyResult(1, 1, 1, 1, columnSettingReuse: false)
        };
        PlayHistorySortedRowsApplyResult validApplyResult = PlayHistorySortedRowsApplyResult.Applied(
            sortSucceeded: true,
            sortProfile: "title",
            additionalSortMs: 0,
            viewCount: 1,
            diagnostics: [],
            terminalCommit: validTerminalCommit);

        PlayHistoryViewExecutionResult missingApplyResult = PlayHistoryViewExecutionResult.Applied(
            request,
            readResult: null,
            presentationOnlyResult: presentation,
            applyResult: null,
            fromSortOnly: false,
            readMs: 0,
            projectionIndexMs: 0,
            projectionIndexCacheHit: false,
            projectionIndexStaleRetries: 0,
            projectionMs: 0,
            periodIndexMs: 0,
            keywordMs: 0,
            keywordCount: 0,
            sortMs: 0);
        InvalidOperationException applyException = Assert.ThrowsException<InvalidOperationException>(() =>
            reporter.ReportViewExecution(missingApplyResult, PlayHistoryDisplayTargetItem.All, currentRequestId: 12));
        StringAssert.Contains(applyException.Message, "execution.ApplyResult");
        Assert.AreEqual(0, events.Count);

        PlayHistorySortedRowsApplyResult missingTerminalResult = PlayHistorySortedRowsApplyResult.Applied(
            sortSucceeded: true,
            sortProfile: "title",
            additionalSortMs: 0,
            viewCount: 1,
            diagnostics: [],
            terminalCommit: null);
        PlayHistoryViewExecutionResult missingTerminalExecution = PlayHistoryViewExecutionResult.Applied(
            request,
            readResult: null,
            presentationOnlyResult: presentation,
            applyResult: missingTerminalResult,
            fromSortOnly: false,
            readMs: 0,
            projectionIndexMs: 0,
            projectionIndexCacheHit: false,
            projectionIndexStaleRetries: 0,
            projectionMs: 0,
            periodIndexMs: 0,
            keywordMs: 0,
            keywordCount: 0,
            sortMs: 0);
        InvalidOperationException terminalException = Assert.ThrowsException<InvalidOperationException>(() =>
            reporter.ReportViewExecution(missingTerminalExecution, PlayHistoryDisplayTargetItem.All, currentRequestId: 12));
        StringAssert.Contains(terminalException.Message, "execution.ApplyResult.TerminalCommit");
        Assert.AreEqual(0, events.Count);

        var missingStatePresentation = PlayHistoryPresentationOnlyBuildResult.Built(
            state: null,
            sortedRows: [],
            sortSucceeded: true,
            sortProfile: "title",
            sortMs: 1,
            keywordMs: 1,
            keywordCount: 1,
            displayTargetApplied: true,
            displayTargetSourceCount: 1,
            displayTargetResultCount: 1,
            keywordFilterApplied: true,
            keywordSourceCount: 1,
            keywordProjectedCount: 1);
        PlayHistoryViewExecutionResult missingStateExecution = PlayHistoryViewExecutionResult.Applied(
            request,
            readResult: null,
            presentationOnlyResult: missingStatePresentation,
            applyResult: validApplyResult,
            fromSortOnly: false,
            readMs: 0,
            projectionIndexMs: 0,
            projectionIndexCacheHit: false,
            projectionIndexStaleRetries: 0,
            projectionMs: 0,
            periodIndexMs: 0,
            keywordMs: 0,
            keywordCount: 0,
            sortMs: 0);
        InvalidOperationException stateException = Assert.ThrowsException<InvalidOperationException>(() =>
            reporter.ReportViewExecution(missingStateExecution, PlayHistoryDisplayTargetItem.All, currentRequestId: 12));
        StringAssert.Contains(stateException.Message, "execution.State");
        Assert.AreEqual(0, events.Count);
    }

    [TestMethod]
    public void Reporter_IsSinkOwnedAndNullSafeForAbsentInputs()
    {
        var events = new List<PlayHistoryRuntimeEvent>();
        var reporter = new PlayHistoryRuntimeEventReporter(events.Add);

        reporter.ReportReadWorkflowProgress(PlayHistoryPeriodRequest.All(), requestId: 1, progress: null);
        reporter.ReportViewExecution(execution: null, currentDisplayTarget: null, currentRequestId: 0);

        Assert.AreEqual(0, events.Count);
        InvalidOperationException missingRequest = Assert.ThrowsException<InvalidOperationException>(() =>
            reporter.ReportViewExecution(
                PlayHistoryViewExecutionResult.Stale(request: null),
                currentDisplayTarget: null,
                currentRequestId: 0));
        StringAssert.Contains(missingRequest.Message, "execution.Request");
        Assert.AreEqual(0, events.Count);
        Assert.ThrowsException<ArgumentNullException>(() => new PlayHistoryRuntimeEventReporter(null));
        Assert.AreEqual("event fields", new PlayHistoryRuntimeEvent("event", "fields").Message);
        Assert.AreEqual("event", new PlayHistoryRuntimeEvent("event", string.Empty).Message);
    }

    private static PlayHistoryViewExecutionRequest CreateViewExecutionRequest(
        PlayHistoryPeriodRequest period,
        long requestId,
        string keywordFilter = "")
    {
        return new PlayHistoryViewExecutionRequest(
            MainViewUpdateMode.PlayHistorySelected,
            MainViewUpdateMode.PlayHistorySelected,
            parameter: null,
            Stopwatch.StartNew(),
            new PlayHistoryViewRequest(period, requestId),
            keywordFilter,
            PlayHistoryDisplayTargetItem.All);
    }
}
