using System;
using System.Collections.Generic;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

internal sealed class PlayHistorySortedRowsApplyRequest
{
    internal PlayHistorySortedRowsApplyRequest(
        MainViewUpdateMode mode,
        MainViewUpdateMode columnFilterMode,
        PlayHistoryViewState state,
        IReadOnlyList<PlayHistoryRow> sortedRows,
        bool sortSucceeded,
        string sortProfile,
        string currentKeywordFilter,
        PlayHistoryDisplayTargetItem currentDisplayTarget,
        IEnumerable<string> selectedSummaryFilterKeys,
        IReadOnlyList<PlayHistoryPeriodTreeItem> archivePeriodTree)
    {
        Mode = mode;
        ColumnFilterMode = columnFilterMode;
        State = state ?? throw new ArgumentNullException(nameof(state));
        SortedRows = sortedRows == null
            ? throw new ArgumentNullException(nameof(sortedRows))
            : [.. sortedRows];
        SortSucceeded = sortSucceeded;
        SortProfile = sortProfile ?? string.Empty;
        CurrentKeywordFilter = currentKeywordFilter ?? string.Empty;
        CurrentDisplayTarget = currentDisplayTarget ?? PlayHistoryDisplayTargetItem.All;
        SelectedSummaryFilterKeys = [.. new HashSet<string>(selectedSummaryFilterKeys ?? [], StringComparer.Ordinal)];
        ArchivePeriodTree = archivePeriodTree == null ? null : [.. archivePeriodTree];
    }

    internal MainViewUpdateMode Mode { get; }

    internal MainViewUpdateMode ColumnFilterMode { get; }

    internal PlayHistoryViewState State { get; }

    internal IReadOnlyList<PlayHistoryRow> SortedRows { get; }

    internal bool SortSucceeded { get; }

    internal string SortProfile { get; }

    internal string CurrentKeywordFilter { get; }

    internal PlayHistoryDisplayTargetItem CurrentDisplayTarget { get; }

    internal IReadOnlyList<string> SelectedSummaryFilterKeys { get; }

    internal IReadOnlyList<PlayHistoryPeriodTreeItem> ArchivePeriodTree { get; }
}

internal sealed class PlayHistorySortedRowsApplyResult
{
    private PlayHistorySortedRowsApplyResult(
        PlayHistorySortedRowsApplyStatus status,
        bool queueRefresh,
        bool sortSucceeded,
        string sortProfile,
        long additionalSortMs,
        int viewCount,
        IReadOnlyList<PlayHistoryDiagnostic> diagnostics,
        PlayHistoryTerminalCommitResult terminalCommit)
    {
        Status = status;
        QueueRefresh = queueRefresh;
        SortSucceeded = sortSucceeded;
        SortProfile = sortProfile ?? string.Empty;
        AdditionalSortMs = additionalSortMs;
        ViewCount = viewCount;
        Diagnostics = diagnostics == null ? [] : [.. diagnostics];
        TerminalCommit = terminalCommit;
    }

    internal PlayHistorySortedRowsApplyStatus Status { get; }

    internal bool QueueRefresh { get; }

    internal bool SortSucceeded { get; }

    internal string SortProfile { get; }

    internal long AdditionalSortMs { get; }

    internal int ViewCount { get; }

    internal IReadOnlyList<PlayHistoryDiagnostic> Diagnostics { get; }

    internal PlayHistoryTerminalCommitResult TerminalCommit { get; }

    internal static PlayHistorySortedRowsApplyResult Applied(
        bool sortSucceeded,
        string sortProfile,
        long additionalSortMs,
        int viewCount,
        IReadOnlyList<PlayHistoryDiagnostic> diagnostics,
        PlayHistoryTerminalCommitResult terminalCommit)
    {
        return new PlayHistorySortedRowsApplyResult(
            PlayHistorySortedRowsApplyStatus.Applied,
            queueRefresh: false,
            sortSucceeded,
            sortProfile,
            additionalSortMs,
            viewCount,
            diagnostics,
            terminalCommit);
    }

    internal static PlayHistorySortedRowsApplyResult Stale(
        PlayHistorySortedRowsApplyStatus status,
        bool queueRefresh,
        bool sortSucceeded,
        string sortProfile,
        long additionalSortMs)
    {
        return new PlayHistorySortedRowsApplyResult(
            status,
            queueRefresh,
            sortSucceeded,
            sortProfile,
            additionalSortMs,
            viewCount: 0,
            diagnostics: [],
            terminalCommit: null);
    }
}

internal enum PlayHistorySortedRowsApplyStatus
{
    Applied,
    StaleRequest,
    DisplayTargetStale,
    KeywordStale
}
