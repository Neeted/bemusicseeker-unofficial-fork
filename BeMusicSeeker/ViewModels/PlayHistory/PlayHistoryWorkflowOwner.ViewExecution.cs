using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlayHistoryWorkflowOwner
{
    private PlayHistoryViewExecutionDependencies viewExecutionDependencies;

    internal void ConfigureViewExecution(PlayHistoryViewExecutionDependencies dependencies)
    {
        viewExecutionDependencies = dependencies
            ?? throw new ArgumentNullException(nameof(dependencies));
    }

    internal PlayHistoryViewExecutionResult ExecuteView(PlayHistoryViewExecutionRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        PlayHistoryViewExecutionDependencies dependencies = viewExecutionDependencies
            ?? throw new InvalidOperationException("Play-history view execution must be configured before it is used.");
        PlayHistoryViewRequest viewRequest = request.ViewRequest;
        if (viewRequest == null)
        {
            throw new ArgumentException("A play-history view request is required.", nameof(request));
        }

        if (request.RequestedMode == MainViewUpdateMode.KeywordFilterUpdated
            && viewRequest.KeywordFilterRevision > 0
            && viewRequest.KeywordFilterRevision != KeywordRevision)
        {
            return PlayHistoryViewExecutionResult.Stale(request);
        }
        if (request.RequestedMode == MainViewUpdateMode.KeywordFilterUpdated
            && viewRequest.DisplayTargetRevision > 0
            && viewRequest.DisplayTargetRevision != DisplayTargetRevision)
        {
            return PlayHistoryViewExecutionResult.Stale(request);
        }
        if (!IsCurrentRequest(viewRequest.RequestId))
        {
            return PlayHistoryViewExecutionResult.Stale(request);
        }

        if (request.RequestedMode == MainViewUpdateMode.SortUpdated
            || request.RequestedMode == MainViewUpdateMode.KeywordFilterUpdated)
        {
            return ExecutePresentationOnly(request, dependencies);
        }

        return ExecuteRead(request, dependencies);
    }

    private PlayHistoryViewExecutionResult ExecutePresentationOnly(
        PlayHistoryViewExecutionRequest request,
        PlayHistoryViewExecutionDependencies dependencies)
    {
        if (!TrySnapshotCurrentView(request.ViewRequest, out PlayHistoryViewState state))
        {
            return request.RequestedMode == MainViewUpdateMode.KeywordFilterUpdated
                ? ExecuteRead(request, dependencies)
                : PlayHistoryViewExecutionResult.NoCurrentMatchingState(request, fromSortOnly: true);
        }

        long keywordRevision = request.ViewRequest.KeywordFilterRevision > 0
            ? request.ViewRequest.KeywordFilterRevision
            : KeywordRevision;
        long displayTargetRevision = request.ViewRequest.DisplayTargetRevision > 0
            ? request.ViewRequest.DisplayTargetRevision
            : DisplayTargetRevision;
        PlayHistoryPresentationOnlyBuildResult buildResult;
        try
        {
            buildResult = BuildPresentationOnly(
                new PlayHistoryPresentationOnlyBuildRequest(
                    request.RequestedMode,
                    state,
                    request.KeywordFilter,
                    keywordRevision,
                    request.DisplayTarget,
                    displayTargetRevision,
                    SnapshotSummaryFilterTexts(state.Provider)),
                dependencies.PlaylistProvider());
        }
        catch (OperationCanceledException)
        {
            return PlayHistoryViewExecutionResult.Stale(request);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            PlayHistoryDiagnostic diagnostic = CreateReadFailureDiagnostic(
                state.AllProjectedRows.FirstOrDefault()?.SourceProfile ?? new PlayHistorySourceProfile(state.Provider, string.Empty),
                "presentation", failure);
            var failed = new PlayHistoryReadWorkflowResult(false, string.Empty, request.Stopwatch.ElapsedMilliseconds,
                read: null, periodIndex: null, projection: null, presentation: null,
                failedStage: "presentation", diagnostics: [diagnostic]);
            return ApplyReadFailure(request, failed, dependencies);
        }
        if (buildResult.Status != PlayHistoryPresentationOnlyBuildStatus.Built)
        {
            QueueRefreshIfNeeded(buildResult.QueueRefresh, buildResult.Status);
            return PlayHistoryViewExecutionResult.Stale(request, buildResult, fromSortOnly: true);
        }

        return ApplySortedRows(
            request,
            buildResult.State,
            buildResult.SortedRows,
            buildResult.SortSucceeded,
            buildResult.SortProfile,
            readResult: null,
            presentationOnlyResult: buildResult,
            dependencies,
            fromSortOnly: true,
            readMs: 0L,
            projectionIndexMs: 0L,
            projectionIndexCacheHit: false,
            projectionIndexStaleRetries: 0,
            projectionMs: 0L,
            periodIndexMs: 0L,
            keywordMs: buildResult.KeywordMs,
            keywordCount: buildResult.KeywordCount,
            archivePeriodTree: null,
            sortMs: buildResult.SortMs);
    }

    private PlayHistoryViewExecutionResult ExecuteRead(
        PlayHistoryViewExecutionRequest request,
        PlayHistoryViewExecutionDependencies dependencies)
    {
        PlayHistoryReadSourceContext source = dependencies.SourceResolver();
        if (source == null)
        {
            throw new InvalidOperationException("The play-history source resolver returned no source context.");
        }
        lock (PresentationState.SyncRoot)
        {
            if (!IsCurrentRequestUnsafe(request.ViewRequest.RequestId))
            { return PlayHistoryViewExecutionResult.Stale(request); }
            ActiveRequest.SourceSnapshot = source.PlayHistorySnapshot;
        }
        PlayHistoryViewRequest activeViewRequest = new(
            request.ViewRequest.PeriodRequest,
            request.ViewRequest.RequestId,
            request.ViewRequest.KeywordFilterRevision,
            request.ViewRequest.DisplayTargetRevision)
        {
            DetailSourceRetirement = request.ViewRequest.DetailSourceRetirement
        };
        PlayHistoryReadWorkflowResult readResult;
        try
        {
            readResult = BuildReadView(
                new PlayHistoryReadWorkflowRequest(
                    activeViewRequest,
                    source,
                    request.KeywordFilter,
                    request.DisplayTarget,
                    SnapshotSummaryFilterTexts(source.Provider),
                    progress => dependencies.ReportProgress(activeViewRequest, progress)),
                dependencies.LibraryProvider(),
                dependencies.PlaylistProvider());
        }
        catch (OperationCanceledException)
        {
            return PlayHistoryViewExecutionResult.ReadCanceled(request, null);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            PlayHistoryDiagnostic diagnostic = CreateReadFailureDiagnostic(ResolveSourceProfile(source), "presentation", failure);
            readResult = new PlayHistoryReadWorkflowResult(false, string.Empty, request.Stopwatch.ElapsedMilliseconds,
                read: null, periodIndex: null, projection: null, presentation: null,
                failedStage: "presentation", diagnostics: [diagnostic]);
        }
        if (!string.IsNullOrEmpty(readResult.FailedStage))
        {
            return ApplyReadFailure(request, readResult, dependencies);
        }
        if (!readResult.Built)
        {
            return PlayHistoryViewExecutionResult.ReadCanceled(request, readResult);
        }

        PlayHistoryReadPresentationBuildResult presentation = readResult.Presentation;
        return ApplySortedRows(
            request,
            presentation.State,
            presentation.SortedRows,
            presentation.SortSucceeded,
            presentation.SortProfile,
            readResult,
            presentationOnlyResult: null,
            dependencies,
            fromSortOnly: false,
            readMs: readResult.Read.ElapsedMs,
            projectionIndexMs: readResult.Projection.IndexMs,
            projectionIndexCacheHit: readResult.Projection.IndexCacheHit,
            projectionIndexStaleRetries: readResult.Projection.IndexStaleRetries,
            projectionMs: readResult.Projection.ProjectionMs,
            periodIndexMs: readResult.PeriodIndex.ElapsedMs,
            keywordMs: presentation.KeywordMs,
            keywordCount: presentation.KeywordCount,
            archivePeriodTree: presentation.ArchivePeriodTree,
            sortMs: presentation.SortMs);
    }

    /// <summary>現在要求の失敗を既存終端へ適用し、行・カード・選択を失敗表示へ更新します。</summary>
    private PlayHistoryViewExecutionResult ApplyReadFailure(
        PlayHistoryViewExecutionRequest request,
        PlayHistoryReadWorkflowResult readResult,
        PlayHistoryViewExecutionDependencies dependencies)
    {
        bool applied = false;
        bool invoked = dependencies.InvokePresentationSynchronously(() =>
        {
            string diagnosticText = BeMusicSeeker.Properties.Resources.Play_history_read_failed;
            lock (PresentationState.SyncRoot)
            {
                if (!IsCurrentRequestUnsafe(request.ViewRequest.RequestId)
                    || request.ViewRequest.KeywordFilterRevision != PresentationState.KeywordRevision
                    || request.ViewRequest.DisplayTargetRevision != PresentationState.DisplayTargetRevision
                    || !string.Equals(PlaylistRequestFactory.NormalizeKeywordFilter(request.KeywordFilter),
                        PresentationState.CurrentKeywordIdentity, StringComparison.Ordinal)
                    || !string.Equals(request.DisplayTarget.Identity, PresentationState.CurrentDisplayTargetIdentity, StringComparison.Ordinal))
                {
                    return;
                }
            }

            MainChartListColumnSelection columns = dependencies.MainChartList.ResolveColumnSettingForViewUpdate(
                request.Mode, dependencies.ResolveColumnFilterMode());
            PlayHistoryTerminalCommitResult terminal = ApplyTerminal(new PlayHistoryTerminalRequest
            {
                FailedViewRequest = request.ViewRequest,
                ColumnSelection = columns,
                ArchivePeriodTree = [],
                SummaryCards = [],
                DiagnosticText = diagnosticText,
                DetailSourceRetirement = request.ViewRequest.DetailSourceRetirement,
                MainRowsRequest = new MainChartListRowsApplyRequest
                {
                    Rows = Array.Empty<PlayHistoryRow>(),
                    ColumnsSettings = columns.ColumnsSettings,
                    SelectionPolicy = MainChartListSelectionPolicy.Reset,
                    Summary = MainChartListSummaryUpdate.Explicit(diagnosticText),
                    Stopwatch = request.Stopwatch,
                    OperationContextMode = columns.AppliedMode
                }
            }, dependencies.MainChartList, dependencies.PlaylistWorkspace);
            applied = terminal.Applied;
        });
        return invoked && applied
            ? PlayHistoryViewExecutionResult.ReadFailed(request, readResult)
            : PlayHistoryViewExecutionResult.Stale(request, readResult, null, fromSortOnly: false);
    }

    private static PlayHistorySourceProfile ResolveSourceProfile(PlayHistoryReadSourceContext source)
        => source.Provider == PlayHistoryProvider.Beatoraja
            ? PlayHistorySourceProfile.Beatoraja(BeatorajaPlayHistoryReader.ResolveRequestPaths(
                new BeatorajaPlayHistoryReadRequest { ScoreDbPath = source.ScoreDbPath }).ScoreLogDbPath)
            : PlayHistorySourceProfile.Lr2(source.ScoreDbPath);

    private PlayHistoryViewExecutionResult ApplySortedRows(
        PlayHistoryViewExecutionRequest request,
        PlayHistoryViewState state,
        IReadOnlyList<PlayHistoryRow> sortedRows,
        bool sortSucceeded,
        string sortProfile,
        PlayHistoryReadWorkflowResult readResult,
        PlayHistoryPresentationOnlyBuildResult presentationOnlyResult,
        PlayHistoryViewExecutionDependencies dependencies,
        bool fromSortOnly,
        long readMs,
        long projectionIndexMs,
        bool projectionIndexCacheHit,
        int projectionIndexStaleRetries,
        long projectionMs,
        long periodIndexMs,
        long keywordMs,
        int keywordCount,
        IReadOnlyList<PlayHistoryPeriodTreeItem> archivePeriodTree,
        long sortMs)
    {
        PlayHistorySortedRowsApplyResult applyResult = default;
        bool invoked = dependencies.InvokePresentationSynchronously(() =>
        {
            applyResult = ApplySortedRows(
                new PlayHistorySortedRowsApplyRequest(
                    request.Mode,
                    dependencies.ResolveColumnFilterMode(),
                    state,
                    sortedRows,
                    sortSucceeded,
                    sortProfile,
                    request.KeywordFilter,
                    request.DisplayTarget,
                    archivePeriodTree,
                    request.ViewRequest.DetailSourceRetirement),
                request.Stopwatch,
                dependencies.MainChartList,
                dependencies.PlaylistWorkspace);
        });
        if (!invoked)
        {
            return PlayHistoryViewExecutionResult.Stale(request, readResult, presentationOnlyResult, fromSortOnly);
        }

        if (applyResult.Status != PlayHistorySortedRowsApplyStatus.Applied)
        {
            QueueRefreshIfNeeded(applyResult.QueueRefresh, applyResult.Status);
            return PlayHistoryViewExecutionResult.Stale(
                request,
                readResult,
                presentationOnlyResult,
                applyResult,
                fromSortOnly);
        }

        return PlayHistoryViewExecutionResult.Applied(
            request,
            readResult,
            presentationOnlyResult,
            applyResult,
            fromSortOnly,
            readMs,
            projectionIndexMs,
            projectionIndexCacheHit,
            projectionIndexStaleRetries,
            projectionMs,
            periodIndexMs,
            keywordMs,
            keywordCount,
            sortMs);
    }

    private void QueueRefreshIfNeeded(
        bool queueRefresh,
        PlayHistoryPresentationOnlyBuildStatus status)
    {
        if (!queueRefresh)
        {
            return;
        }
        if (status == PlayHistoryPresentationOnlyBuildStatus.DisplayTargetStale)
        {
            QueueDisplayTargetRefresh(CurrentDisplayTargetIdentity, advanceRevision: false);
        }
        else if (status == PlayHistoryPresentationOnlyBuildStatus.KeywordStale)
        {
            QueueKeywordFilterRefresh(CurrentKeywordIdentity, advanceRevision: false);
        }
    }

    private void QueueRefreshIfNeeded(
        bool queueRefresh,
        PlayHistorySortedRowsApplyStatus status)
    {
        if (!queueRefresh)
        {
            return;
        }
        if (status == PlayHistorySortedRowsApplyStatus.DisplayTargetStale)
        {
            QueueDisplayTargetRefresh(CurrentDisplayTargetIdentity, advanceRevision: false);
        }
        else if (status == PlayHistorySortedRowsApplyStatus.KeywordStale)
        {
            QueueKeywordFilterRefresh(CurrentKeywordIdentity, advanceRevision: false);
        }
    }
}

internal sealed class PlayHistoryViewExecutionDependencies
{
    internal PlayHistoryViewExecutionDependencies(
        Func<BMSLibrary> libraryProvider,
        Func<BMSPlaylist> playlistProvider,
        Func<PlayHistoryReadSourceContext> sourceResolver,
        Func<Action, bool> invokePresentationSynchronously,
        Action<PlayHistoryViewRequest, PlayHistoryReadWorkflowProgress> reportProgress,
        Func<MainViewUpdateMode> resolveColumnFilterMode,
        MainChartListViewModel mainChartList,
        PlaylistWorkspaceViewModel playlistWorkspace)
    {
        LibraryProvider = libraryProvider ?? throw new ArgumentNullException(nameof(libraryProvider));
        PlaylistProvider = playlistProvider ?? throw new ArgumentNullException(nameof(playlistProvider));
        SourceResolver = sourceResolver ?? throw new ArgumentNullException(nameof(sourceResolver));
        InvokePresentationSynchronously = invokePresentationSynchronously ?? throw new ArgumentNullException(nameof(invokePresentationSynchronously));
        ReportProgress = reportProgress ?? throw new ArgumentNullException(nameof(reportProgress));
        ResolveColumnFilterMode = resolveColumnFilterMode ?? throw new ArgumentNullException(nameof(resolveColumnFilterMode));
        MainChartList = mainChartList ?? throw new ArgumentNullException(nameof(mainChartList));
        PlaylistWorkspace = playlistWorkspace ?? throw new ArgumentNullException(nameof(playlistWorkspace));
    }

    internal Func<BMSLibrary> LibraryProvider { get; }
    internal Func<BMSPlaylist> PlaylistProvider { get; }
    internal Func<PlayHistoryReadSourceContext> SourceResolver { get; }
    internal Func<Action, bool> InvokePresentationSynchronously { get; }
    internal Action<PlayHistoryViewRequest, PlayHistoryReadWorkflowProgress> ReportProgress { get; }
    internal Func<MainViewUpdateMode> ResolveColumnFilterMode { get; }
    internal MainChartListViewModel MainChartList { get; }
    internal PlaylistWorkspaceViewModel PlaylistWorkspace { get; }
}

internal sealed class PlayHistoryViewExecutionRequest
{
    internal PlayHistoryViewExecutionRequest(
        MainViewUpdateMode mode,
        MainViewUpdateMode requestedMode,
        object parameter,
        Stopwatch stopwatch,
        PlayHistoryViewRequest viewRequest,
        string keywordFilter,
        PlayHistoryDisplayTargetItem displayTarget)
    {
        Mode = mode;
        RequestedMode = requestedMode;
        Parameter = parameter;
        Stopwatch = stopwatch ?? throw new ArgumentNullException(nameof(stopwatch));
        ViewRequest = viewRequest ?? throw new ArgumentNullException(nameof(viewRequest));
        KeywordFilter = keywordFilter;
        DisplayTarget = displayTarget ?? PlayHistoryDisplayTargetItem.All;
    }

    internal MainViewUpdateMode Mode { get; }
    internal MainViewUpdateMode RequestedMode { get; }
    internal object Parameter { get; }
    internal Stopwatch Stopwatch { get; }
    internal PlayHistoryViewRequest ViewRequest { get; }
    internal string KeywordFilter { get; }
    internal PlayHistoryDisplayTargetItem DisplayTarget { get; }
}

internal enum PlayHistoryViewExecutionStatus
{
    Applied,
    Stale,
    NoCurrentMatchingState,
    ReadCanceled,
    ReadFailed
}

internal sealed class PlayHistoryViewExecutionResult
{
    private PlayHistoryViewExecutionResult(
        PlayHistoryViewExecutionRequest request,
        PlayHistoryViewExecutionStatus status,
        PlayHistoryReadWorkflowResult readResult,
        PlayHistoryPresentationOnlyBuildResult presentationOnlyResult,
        PlayHistorySortedRowsApplyResult applyResult,
        bool fromSortOnly,
        long readMs,
        long projectionIndexMs,
        bool projectionIndexCacheHit,
        int projectionIndexStaleRetries,
        long projectionMs,
        long periodIndexMs,
        long keywordMs,
        int keywordCount,
        long sortMs)
    {
        Request = request;
        Status = status;
        ReadResult = readResult;
        PresentationOnlyResult = presentationOnlyResult;
        ApplyResult = applyResult;
        FromSortOnly = fromSortOnly;
        ReadMs = readMs;
        ProjectionIndexMs = projectionIndexMs;
        ProjectionIndexCacheHit = projectionIndexCacheHit;
        ProjectionIndexStaleRetries = projectionIndexStaleRetries;
        ProjectionMs = projectionMs;
        PeriodIndexMs = periodIndexMs;
        KeywordMs = keywordMs;
        KeywordCount = keywordCount;
        SortMs = sortMs;
    }

    internal PlayHistoryViewExecutionRequest Request { get; }
    internal PlayHistoryViewExecutionStatus Status { get; }
    internal PlayHistoryReadWorkflowResult ReadResult { get; }
    internal PlayHistoryPresentationOnlyBuildResult PresentationOnlyResult { get; }
    internal PlayHistorySortedRowsApplyResult ApplyResult { get; }
    internal bool FromSortOnly { get; }
    internal long ReadMs { get; }
    internal long ProjectionIndexMs { get; }
    internal bool ProjectionIndexCacheHit { get; }
    internal int ProjectionIndexStaleRetries { get; }
    internal long ProjectionMs { get; }
    internal long PeriodIndexMs { get; }
    internal long KeywordMs { get; }
    internal int KeywordCount { get; }
    internal long SortMs { get; }

    internal static PlayHistoryViewExecutionResult Stale(PlayHistoryViewExecutionRequest request)
        => new(request, PlayHistoryViewExecutionStatus.Stale, null, null, default, false, 0L, 0L, false, 0, 0L, 0L, 0L, 0, 0L);

    internal static PlayHistoryViewExecutionResult NoCurrentMatchingState(PlayHistoryViewExecutionRequest request, bool fromSortOnly)
        => new(request, PlayHistoryViewExecutionStatus.NoCurrentMatchingState, null, null, default, fromSortOnly, 0L, 0L, false, 0, 0L, 0L, 0L, 0, 0L);

    /// <summary>取得失敗の終端を、取消し・空表示の成功と区別して報告します。</summary>
    internal static PlayHistoryViewExecutionResult ReadFailed(PlayHistoryViewExecutionRequest request, PlayHistoryReadWorkflowResult readResult)
        => new(request, PlayHistoryViewExecutionStatus.ReadFailed, readResult, null, default, false, readResult?.Read?.ElapsedMs ?? 0L, 0L, false, 0, 0L, 0L, 0L, 0, 0L);

    internal static PlayHistoryViewExecutionResult ReadCanceled(PlayHistoryViewExecutionRequest request, PlayHistoryReadWorkflowResult readResult)
        => new(request, PlayHistoryViewExecutionStatus.ReadCanceled, readResult, null, default, false, readResult?.Read?.ElapsedMs ?? 0L, 0L, false, 0, 0L, 0L, 0L, 0, 0L);

    internal static PlayHistoryViewExecutionResult Stale(
        PlayHistoryViewExecutionRequest request,
        PlayHistoryPresentationOnlyBuildResult presentationOnlyResult,
        bool fromSortOnly)
        => new(request, PlayHistoryViewExecutionStatus.Stale, null, presentationOnlyResult, default, fromSortOnly, 0L, 0L, false, 0, 0L, 0L, presentationOnlyResult?.KeywordMs ?? 0L, presentationOnlyResult?.KeywordCount ?? 0, presentationOnlyResult?.SortMs ?? 0L);

    internal static PlayHistoryViewExecutionResult Stale(
        PlayHistoryViewExecutionRequest request,
        PlayHistoryReadWorkflowResult readResult,
        PlayHistoryPresentationOnlyBuildResult presentationOnlyResult,
        bool fromSortOnly)
        => new(request, PlayHistoryViewExecutionStatus.Stale, readResult, presentationOnlyResult, default, fromSortOnly, readResult?.Read?.ElapsedMs ?? 0L, readResult?.Projection?.IndexMs ?? 0L, readResult?.Projection?.IndexCacheHit ?? false, readResult?.Projection?.IndexStaleRetries ?? 0, readResult?.Projection?.ProjectionMs ?? 0L, readResult?.PeriodIndex?.ElapsedMs ?? 0L, presentationOnlyResult?.KeywordMs ?? readResult?.Presentation?.KeywordMs ?? 0L, presentationOnlyResult?.KeywordCount ?? readResult?.Presentation?.KeywordCount ?? 0, presentationOnlyResult?.SortMs ?? readResult?.Presentation?.SortMs ?? 0L);

    internal static PlayHistoryViewExecutionResult Stale(
        PlayHistoryViewExecutionRequest request,
        PlayHistoryReadWorkflowResult readResult,
        PlayHistoryPresentationOnlyBuildResult presentationOnlyResult,
        PlayHistorySortedRowsApplyResult applyResult,
        bool fromSortOnly)
        => new(request, PlayHistoryViewExecutionStatus.Stale, readResult, presentationOnlyResult, applyResult, fromSortOnly, readResult?.Read?.ElapsedMs ?? 0L, readResult?.Projection?.IndexMs ?? 0L, readResult?.Projection?.IndexCacheHit ?? false, readResult?.Projection?.IndexStaleRetries ?? 0, readResult?.Projection?.ProjectionMs ?? 0L, readResult?.PeriodIndex?.ElapsedMs ?? 0L, presentationOnlyResult?.KeywordMs ?? readResult?.Presentation?.KeywordMs ?? 0L, presentationOnlyResult?.KeywordCount ?? readResult?.Presentation?.KeywordCount ?? 0, presentationOnlyResult?.SortMs ?? readResult?.Presentation?.SortMs ?? 0L);

    internal static PlayHistoryViewExecutionResult Applied(
        PlayHistoryViewExecutionRequest request,
        PlayHistoryReadWorkflowResult readResult,
        PlayHistoryPresentationOnlyBuildResult presentationOnlyResult,
        PlayHistorySortedRowsApplyResult applyResult,
        bool fromSortOnly,
        long readMs,
        long projectionIndexMs,
        bool projectionIndexCacheHit,
        int projectionIndexStaleRetries,
        long projectionMs,
        long periodIndexMs,
        long keywordMs,
        int keywordCount,
        long sortMs)
        => new(request, PlayHistoryViewExecutionStatus.Applied, readResult, presentationOnlyResult, applyResult, fromSortOnly, readMs, projectionIndexMs, projectionIndexCacheHit, projectionIndexStaleRetries, projectionMs, periodIndexMs, keywordMs, keywordCount, sortMs);
}
