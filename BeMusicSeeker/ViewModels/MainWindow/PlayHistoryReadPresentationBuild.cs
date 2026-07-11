using System;
using System.Collections.Generic;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

internal sealed class PlayHistoryReadPresentationBuildRequest
{
    internal PlayHistoryReadPresentationBuildRequest(
        long requestId,
        PlayHistoryPeriodRequest periodRequest,
        IReadOnlyList<PlayHistoryRow> projectedRows,
        IReadOnlyList<PlayHistoryDiagnostic> projectionDiagnostics,
        IReadOnlyList<long> periodIndexPlayedAt,
        IReadOnlyList<PlayHistoryDiagnostic> periodIndexDiagnostics,
        PlayHistoryProvider provider,
        Lr2PlayHistorySchemaStatus schemaStatus,
        int sourceCount,
        string keywordFilter,
        long keywordRevision,
        PlayHistoryDisplayTargetItem displayTarget,
        long displayTargetRevision,
        IEnumerable<string> summaryFilterTexts,
        PlayHistoryPeriodSummaryOverride summaryOverride)
    {
        RequestId = requestId;
        PeriodRequest = periodRequest ?? throw new ArgumentNullException(nameof(periodRequest));
        ProjectedRows = projectedRows == null ? [] : [.. projectedRows];
        ProjectionDiagnostics = projectionDiagnostics == null ? [] : [.. projectionDiagnostics];
        PeriodIndexPlayedAt = periodIndexPlayedAt == null ? [] : [.. periodIndexPlayedAt];
        PeriodIndexDiagnostics = periodIndexDiagnostics == null ? [] : [.. periodIndexDiagnostics];
        Provider = provider;
        SchemaStatus = schemaStatus;
        SourceCount = sourceCount;
        KeywordFilter = keywordFilter ?? string.Empty;
        KeywordRevision = keywordRevision;
        DisplayTarget = displayTarget ?? PlayHistoryDisplayTargetItem.All;
        DisplayTargetRevision = displayTargetRevision;
        SummaryFilterTexts = summaryFilterTexts == null ? [] : [.. summaryFilterTexts];
        SummaryOverride = summaryOverride;
    }

    internal long RequestId { get; }
    internal PlayHistoryPeriodRequest PeriodRequest { get; }
    internal IReadOnlyList<PlayHistoryRow> ProjectedRows { get; }
    internal IReadOnlyList<PlayHistoryDiagnostic> ProjectionDiagnostics { get; }
    internal IReadOnlyList<long> PeriodIndexPlayedAt { get; }
    internal IReadOnlyList<PlayHistoryDiagnostic> PeriodIndexDiagnostics { get; }
    internal PlayHistoryProvider Provider { get; }
    internal Lr2PlayHistorySchemaStatus SchemaStatus { get; }
    internal int SourceCount { get; }
    internal string KeywordFilter { get; }
    internal long KeywordRevision { get; }
    internal PlayHistoryDisplayTargetItem DisplayTarget { get; }
    internal long DisplayTargetRevision { get; }
    internal IReadOnlyList<string> SummaryFilterTexts { get; }
    internal PlayHistoryPeriodSummaryOverride SummaryOverride { get; }
}

internal sealed class PlayHistoryReadPresentationBuildResult
{
    private PlayHistoryReadPresentationBuildResult(
        bool built,
        PlayHistoryViewState state,
        IReadOnlyList<PlayHistoryRow> sortedRows,
        bool sortSucceeded,
        string sortProfile,
        long sortMs,
        long keywordMs,
        int keywordCount,
        int displayTargetSourceCount,
        int displayTargetResultCount,
        IReadOnlyList<PlayHistoryPeriodTreeItem> archivePeriodTree)
    {
        Built = built;
        State = state;
        SortedRows = sortedRows == null ? [] : [.. sortedRows];
        SortSucceeded = sortSucceeded;
        SortProfile = sortProfile ?? string.Empty;
        SortMs = sortMs;
        KeywordMs = keywordMs;
        KeywordCount = keywordCount;
        DisplayTargetSourceCount = displayTargetSourceCount;
        DisplayTargetResultCount = displayTargetResultCount;
        ArchivePeriodTree = archivePeriodTree == null ? [] : [.. archivePeriodTree];
    }

    internal bool Built { get; }
    internal PlayHistoryViewState State { get; }
    internal IReadOnlyList<PlayHistoryRow> SortedRows { get; }
    internal bool SortSucceeded { get; }
    internal string SortProfile { get; }
    internal long SortMs { get; }
    internal long KeywordMs { get; }
    internal int KeywordCount { get; }
    internal int DisplayTargetSourceCount { get; }
    internal int DisplayTargetResultCount { get; }
    internal IReadOnlyList<PlayHistoryPeriodTreeItem> ArchivePeriodTree { get; }

    internal static PlayHistoryReadPresentationBuildResult Stale()
    {
        return new PlayHistoryReadPresentationBuildResult(
            built: false,
            state: null,
            sortedRows: [],
            sortSucceeded: false,
            sortProfile: string.Empty,
            sortMs: 0L,
            keywordMs: 0L,
            keywordCount: 0,
            displayTargetSourceCount: 0,
            displayTargetResultCount: 0,
            archivePeriodTree: []);
    }

    internal static PlayHistoryReadPresentationBuildResult Success(
        PlayHistoryViewState state,
        IReadOnlyList<PlayHistoryRow> sortedRows,
        bool sortSucceeded,
        string sortProfile,
        long sortMs,
        long keywordMs,
        int keywordCount,
        int displayTargetSourceCount,
        int displayTargetResultCount,
        IReadOnlyList<PlayHistoryPeriodTreeItem> archivePeriodTree)
    {
        return new PlayHistoryReadPresentationBuildResult(
            built: true,
            state,
            sortedRows,
            sortSucceeded,
            sortProfile,
            sortMs,
            keywordMs,
            keywordCount,
            displayTargetSourceCount,
            displayTargetResultCount,
            archivePeriodTree);
    }
}
