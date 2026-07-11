using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

internal sealed class BeatorajaPlayHistoryScoreContext
{
    internal static BeatorajaPlayHistoryScoreContext Empty { get; } = new(
        0,
        new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase));

    internal BeatorajaPlayHistoryScoreContext(
        int scoreSnapshotVersion,
        IReadOnlyDictionary<string, BMSScore> scoresBySha256)
    {
        ScoreSnapshotVersion = Math.Max(0, scoreSnapshotVersion);
        ScoresBySha256 = scoresBySha256 == null
            ? new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase)
            : scoresBySha256.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    internal int ScoreSnapshotVersion { get; }

    internal IReadOnlyDictionary<string, BMSScore> ScoresBySha256 { get; }
}

internal sealed class PlayHistoryReadSourceContext
{
    private PlayHistoryReadSourceContext(
        PlayHistoryProvider provider,
        string scoreDbPath,
        bool isLr2LinkedProfile,
        BeatorajaPlayHistoryScoreContext beatorajaScoreContext)
    {
        Provider = provider;
        ScoreDbPath = scoreDbPath ?? string.Empty;
        IsLr2LinkedProfile = isLr2LinkedProfile;
        BeatorajaScoreContext = beatorajaScoreContext ?? BeatorajaPlayHistoryScoreContext.Empty;
    }

    internal PlayHistoryProvider Provider { get; }
    internal string ScoreDbPath { get; }
    internal bool IsLr2LinkedProfile { get; }
    internal BeatorajaPlayHistoryScoreContext BeatorajaScoreContext { get; }

    internal static PlayHistoryReadSourceContext Lr2(string scoreDbPath, bool isLr2LinkedProfile)
    {
        return new PlayHistoryReadSourceContext(
            PlayHistoryProvider.Lr2,
            scoreDbPath,
            isLr2LinkedProfile,
            BeatorajaPlayHistoryScoreContext.Empty);
    }

    internal static PlayHistoryReadSourceContext Beatoraja(
        string scoreDbPath,
        BeatorajaPlayHistoryScoreContext scoreContext)
    {
        return new PlayHistoryReadSourceContext(
            PlayHistoryProvider.Beatoraja,
            scoreDbPath,
            isLr2LinkedProfile: false,
            scoreContext);
    }
}

internal sealed class PlayHistoryReadWorkflowRequest
{
    internal PlayHistoryReadWorkflowRequest(
        PlayHistoryViewRequest viewRequest,
        PlayHistoryReadSourceContext source,
        string keywordFilter,
        PlayHistoryDisplayTargetItem displayTarget,
        IEnumerable<string> summaryFilterTexts,
        Action<PlayHistoryReadWorkflowProgress> reportProgress = null)
    {
        ViewRequest = viewRequest ?? throw new ArgumentNullException(nameof(viewRequest));
        Source = source ?? throw new ArgumentNullException(nameof(source));
        KeywordFilter = keywordFilter ?? string.Empty;
        DisplayTarget = displayTarget ?? PlayHistoryDisplayTargetItem.All;
        SummaryFilterTexts = summaryFilterTexts == null ? [] : [.. summaryFilterTexts];
        ReportProgress = reportProgress;
    }

    internal PlayHistoryViewRequest ViewRequest { get; }
    internal PlayHistoryReadSourceContext Source { get; }
    internal string KeywordFilter { get; }
    internal PlayHistoryDisplayTargetItem DisplayTarget { get; }
    internal IReadOnlyList<string> SummaryFilterTexts { get; }
    internal Action<PlayHistoryReadWorkflowProgress> ReportProgress { get; }
}

internal enum PlayHistoryReadWorkflowProgressStage
{
    ReadCompleted,
    PeriodIndexCompleted,
    ProjectionCompleted
}

internal sealed class PlayHistoryReadWorkflowProgress
{
    private PlayHistoryReadWorkflowProgress(
        PlayHistoryReadWorkflowProgressStage stage,
        PlayHistoryReadStageMetrics read,
        PlayHistoryPeriodIndexStageMetrics periodIndex,
        PlayHistoryProjectionStageMetrics projection,
        Lr2PlayHistorySchemaCheckResult lr2SchemaCheckResult)
    {
        Stage = stage;
        Read = read;
        PeriodIndex = periodIndex;
        Projection = projection;
        Lr2SchemaCheckResult = lr2SchemaCheckResult;
    }

    internal PlayHistoryReadWorkflowProgressStage Stage { get; }
    internal PlayHistoryReadStageMetrics Read { get; }
    internal PlayHistoryPeriodIndexStageMetrics PeriodIndex { get; }
    internal PlayHistoryProjectionStageMetrics Projection { get; }
    internal Lr2PlayHistorySchemaCheckResult Lr2SchemaCheckResult { get; }

    internal static PlayHistoryReadWorkflowProgress ReadCompleted(
        PlayHistoryReadStageMetrics read,
        Lr2PlayHistorySchemaCheckResult schemaCheckResult) => new(
            PlayHistoryReadWorkflowProgressStage.ReadCompleted,
            read,
            periodIndex: null,
            projection: null,
            lr2SchemaCheckResult: schemaCheckResult);

    internal static PlayHistoryReadWorkflowProgress PeriodIndexCompleted(
        PlayHistoryReadStageMetrics read,
        PlayHistoryPeriodIndexStageMetrics periodIndex) => new(
            PlayHistoryReadWorkflowProgressStage.PeriodIndexCompleted,
            read,
            periodIndex,
            projection: null,
            lr2SchemaCheckResult: null);

    internal static PlayHistoryReadWorkflowProgress ProjectionCompleted(
        PlayHistoryReadStageMetrics read,
        PlayHistoryPeriodIndexStageMetrics periodIndex,
        PlayHistoryProjectionStageMetrics projection) => new(
            PlayHistoryReadWorkflowProgressStage.ProjectionCompleted,
            read,
            periodIndex,
            projection,
            lr2SchemaCheckResult: null);
}

internal sealed class PlayHistoryReadStageMetrics
{
    internal PlayHistoryReadStageMetrics(
        bool completed,
        PlayHistoryProvider provider,
        Lr2PlayHistorySchemaStatus schemaStatus,
        bool cacheHit,
        int rowCount,
        int diagnosticCount,
        long elapsedMs)
    {
        Completed = completed;
        Provider = provider;
        SchemaStatus = schemaStatus;
        CacheHit = cacheHit;
        RowCount = rowCount;
        DiagnosticCount = diagnosticCount;
        ElapsedMs = elapsedMs;
    }

    internal bool Completed { get; }
    internal PlayHistoryProvider Provider { get; }
    internal Lr2PlayHistorySchemaStatus SchemaStatus { get; }
    internal bool CacheHit { get; }
    internal int RowCount { get; }
    internal int DiagnosticCount { get; }
    internal long ElapsedMs { get; }
}

internal sealed class PlayHistoryPeriodIndexStageMetrics
{
    internal PlayHistoryPeriodIndexStageMetrics(
        PlayHistoryPeriodIndexStageStatus status,
        bool cacheHit,
        int dayCount,
        int diagnosticCount,
        long elapsedMs)
    {
        Status = status;
        CacheHit = cacheHit;
        DayCount = dayCount;
        DiagnosticCount = diagnosticCount;
        ElapsedMs = elapsedMs;
    }

    internal PlayHistoryPeriodIndexStageStatus Status { get; }
    internal bool CacheHit { get; }
    internal int DayCount { get; }
    internal int DiagnosticCount { get; }
    internal long ElapsedMs { get; }
}

internal enum PlayHistoryPeriodIndexStageStatus
{
    NotStarted,
    Completed,
    SkippedSchemaUnavailable,
    Canceled
}

internal sealed class PlayHistoryProjectionStageMetrics
{
    internal PlayHistoryProjectionStageMetrics(
        PlayHistoryProjectionStageStatus status,
        int rawCount,
        int projectedCount,
        int diagnosticCount,
        long indexMs,
        bool indexCacheHit,
        int indexStaleRetries,
        long projectionMs,
        Exception failure)
    {
        Status = status;
        RawCount = rawCount;
        ProjectedCount = projectedCount;
        DiagnosticCount = diagnosticCount;
        IndexMs = indexMs;
        IndexCacheHit = indexCacheHit;
        IndexStaleRetries = indexStaleRetries;
        ProjectionMs = projectionMs;
        Failure = failure;
    }

    internal PlayHistoryProjectionStageStatus Status { get; }
    internal int RawCount { get; }
    internal int ProjectedCount { get; }
    internal int DiagnosticCount { get; }
    internal long IndexMs { get; }
    internal bool IndexCacheHit { get; }
    internal int IndexStaleRetries { get; }
    internal long ProjectionMs { get; }
    internal Exception Failure { get; }
}

internal enum PlayHistoryProjectionStageStatus
{
    NotStarted,
    Completed,
    SkippedNoRows,
    Fallback,
    Canceled
}

internal sealed class PlayHistoryReadWorkflowResult
{
    internal PlayHistoryReadWorkflowResult(
        bool built,
        string canceledStage,
        long elapsedThroughCancellationMs,
        PlayHistoryReadStageMetrics read,
        PlayHistoryPeriodIndexStageMetrics periodIndex,
        PlayHistoryProjectionStageMetrics projection,
        Lr2PlayHistorySchemaCheckResult lr2SchemaCheckResult,
        PlayHistoryReadPresentationBuildResult presentation)
    {
        Built = built;
        CanceledStage = canceledStage ?? string.Empty;
        ElapsedThroughCancellationMs = elapsedThroughCancellationMs;
        Read = read;
        PeriodIndex = periodIndex;
        Projection = projection;
        Lr2SchemaCheckResult = lr2SchemaCheckResult;
        Presentation = presentation;
    }

    internal bool Built { get; }
    internal string CanceledStage { get; }
    internal long ElapsedThroughCancellationMs { get; }
    internal PlayHistoryReadStageMetrics Read { get; }
    internal PlayHistoryPeriodIndexStageMetrics PeriodIndex { get; }
    internal PlayHistoryProjectionStageMetrics Projection { get; }
    internal Lr2PlayHistorySchemaCheckResult Lr2SchemaCheckResult { get; }
    internal PlayHistoryReadPresentationBuildResult Presentation { get; }
}
