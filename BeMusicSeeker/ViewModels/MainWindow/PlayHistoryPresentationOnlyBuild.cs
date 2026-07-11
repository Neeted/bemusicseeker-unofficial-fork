using System;
using System.Collections.Generic;

namespace BeMusicSeeker.ViewModels;

internal sealed class PlayHistoryPresentationOnlyBuildRequest
{
    internal PlayHistoryPresentationOnlyBuildRequest(
        MainViewUpdateMode requestedMode,
        PlayHistoryViewState state,
        string keywordFilter,
        long keywordRevision,
        PlayHistoryDisplayTargetItem displayTarget,
        long displayTargetRevision,
        IEnumerable<string> summaryFilterTexts)
    {
        RequestedMode = requestedMode;
        State = state ?? throw new ArgumentNullException(nameof(state));
        KeywordFilter = keywordFilter ?? string.Empty;
        KeywordRevision = keywordRevision;
        DisplayTarget = displayTarget ?? PlayHistoryDisplayTargetItem.All;
        DisplayTargetRevision = displayTargetRevision;
        SummaryFilterTexts = summaryFilterTexts == null ? [] : [.. summaryFilterTexts];
    }

    internal MainViewUpdateMode RequestedMode { get; }

    internal PlayHistoryViewState State { get; }

    internal string KeywordFilter { get; }

    internal long KeywordRevision { get; }

    internal PlayHistoryDisplayTargetItem DisplayTarget { get; }

    internal long DisplayTargetRevision { get; }

    internal IReadOnlyList<string> SummaryFilterTexts { get; }
}

internal sealed class PlayHistoryPresentationOnlyBuildResult
{
    private PlayHistoryPresentationOnlyBuildResult(
        PlayHistoryPresentationOnlyBuildStatus status,
        bool queueRefresh,
        PlayHistoryViewState state,
        IReadOnlyList<PlayHistoryRow> sortedRows,
        bool sortSucceeded,
        string sortProfile,
        long sortMs,
        long keywordMs,
        int keywordCount,
        bool displayTargetApplied,
        int displayTargetSourceCount,
        int displayTargetResultCount,
        bool keywordFilterApplied,
        int keywordSourceCount,
        int keywordProjectedCount)
    {
        Status = status;
        QueueRefresh = queueRefresh;
        State = SnapshotState(state);
        SortedRows = sortedRows == null ? [] : [.. sortedRows];
        SortSucceeded = sortSucceeded;
        SortProfile = sortProfile ?? string.Empty;
        SortMs = sortMs;
        KeywordMs = keywordMs;
        KeywordCount = keywordCount;
        DisplayTargetApplied = displayTargetApplied;
        DisplayTargetSourceCount = displayTargetSourceCount;
        DisplayTargetResultCount = displayTargetResultCount;
        KeywordFilterApplied = keywordFilterApplied;
        KeywordSourceCount = keywordSourceCount;
        KeywordProjectedCount = keywordProjectedCount;
    }

    internal PlayHistoryPresentationOnlyBuildStatus Status { get; }

    internal bool QueueRefresh { get; }

    internal PlayHistoryViewState State { get; }

    internal IReadOnlyList<PlayHistoryRow> SortedRows { get; }

    internal bool SortSucceeded { get; }

    internal string SortProfile { get; }

    internal long SortMs { get; }

    internal long KeywordMs { get; }

    internal int KeywordCount { get; }

    internal bool DisplayTargetApplied { get; }

    internal int DisplayTargetSourceCount { get; }

    internal int DisplayTargetResultCount { get; }

    internal bool KeywordFilterApplied { get; }

    internal int KeywordSourceCount { get; }

    internal int KeywordProjectedCount { get; }

    internal static PlayHistoryPresentationOnlyBuildResult Built(
        PlayHistoryViewState state,
        IReadOnlyList<PlayHistoryRow> sortedRows,
        bool sortSucceeded,
        string sortProfile,
        long sortMs,
        long keywordMs,
        int keywordCount,
        bool displayTargetApplied,
        int displayTargetSourceCount,
        int displayTargetResultCount,
        bool keywordFilterApplied,
        int keywordSourceCount,
        int keywordProjectedCount)
    {
        return new PlayHistoryPresentationOnlyBuildResult(
            PlayHistoryPresentationOnlyBuildStatus.Built,
            queueRefresh: false,
            state,
            sortedRows,
            sortSucceeded,
            sortProfile,
            sortMs,
            keywordMs,
            keywordCount,
            displayTargetApplied,
            displayTargetSourceCount,
            displayTargetResultCount,
            keywordFilterApplied,
            keywordSourceCount,
            keywordProjectedCount);
    }

    internal static PlayHistoryPresentationOnlyBuildResult Stale(
        PlayHistoryPresentationOnlyBuildStatus status,
        bool queueRefresh,
        PlayHistoryViewState state,
        bool displayTargetApplied = false,
        int displayTargetSourceCount = 0,
        int displayTargetResultCount = 0)
    {
        return new PlayHistoryPresentationOnlyBuildResult(
            status,
            queueRefresh,
            state,
            sortedRows: [],
            sortSucceeded: false,
            sortProfile: string.Empty,
            sortMs: 0L,
            keywordMs: 0L,
            keywordCount: 0,
            displayTargetApplied,
            displayTargetSourceCount,
            displayTargetResultCount,
            keywordFilterApplied: false,
            keywordSourceCount: 0,
            keywordProjectedCount: 0);
    }

    private static PlayHistoryViewState SnapshotState(PlayHistoryViewState state)
    {
        if (state == null)
        {
            return null;
        }
        return new PlayHistoryViewState(
            state.RequestId,
            state.PeriodRequest,
            [.. state.AllProjectedRows],
            [.. state.FilterSourceRows],
            [.. state.ProjectedRows],
            [.. state.Diagnostics],
            state.Provider,
            state.SchemaStatus,
            state.SourceCount,
            state.SortSnapshot,
            state.KeywordFilter,
            state.KeywordFilterRevision,
            state.DisplayTarget,
            state.DisplayTargetRevision,
            state.SummaryOverride);
    }
}

internal enum PlayHistoryPresentationOnlyBuildStatus
{
    Built,
    StaleRequest,
    DisplayTargetStale,
    KeywordStale
}
