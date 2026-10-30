using System;
using System.Collections.Generic;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

/// <summary>同じ成績取得操作の履歴と取得世代を捕捉します。設定やファイルの現在状態から再選択しません。</summary>
internal sealed class PlayHistoryReadSourceContext
{
    private PlayHistoryReadSourceContext(BMSLibrary.ScoreSnapshot snapshot)
    {
        PlayHistorySnapshot = snapshot?.PlayHistorySnapshot;
        SourceGeneration = snapshot?.SourceGeneration ?? 0;
        Provider = PlayHistorySnapshot?.Source == ActiveScoreSource.Beatoraja ? PlayHistoryProvider.Beatoraja : PlayHistoryProvider.Lr2;
        ScoreDbPath = PlayHistorySnapshot?.ScoreDbPath ?? string.Empty;
        IsLr2LinkedProfile = PlayHistorySnapshot?.Lr2?.SchemaStatus != Lr2PlayHistorySchemaStatus.SkippedProfile;
    }
    /// <summary>取得時点で選んだproviderです。</summary>
    internal PlayHistoryProvider Provider { get; }
    /// <summary>取得時点の成績DBパスです。</summary>
    internal string ScoreDbPath { get; }
    /// <summary>取得時点でLR2連携を使ったかを示します。</summary>
    internal bool IsLr2LinkedProfile { get; }
    /// <summary>表示版とは独立した既存の取得世代です。</summary>
    internal long SourceGeneration { get; }
    /// <summary>生行・診断・集計時系列を保持する取得組です。</summary>
    internal ScorePlayHistorySnapshot PlayHistorySnapshot { get; }
    /// <summary>公開済み成績の取得組だけから履歴表示入力を構成します。未準備なら取得せず利用不可です。</summary>
    internal static PlayHistoryReadSourceContext FromSnapshot(BMSLibrary.ScoreSnapshot snapshot) => new(snapshot);
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
        Lr2PlayHistorySchemaStatusSnapshot lr2SchemaStatusSnapshot)
    {
        Stage = stage;
        Read = read;
        PeriodIndex = periodIndex;
        Projection = projection;
        Lr2SchemaStatusSnapshot = lr2SchemaStatusSnapshot;
    }

    internal PlayHistoryReadWorkflowProgressStage Stage { get; }
    internal PlayHistoryReadStageMetrics Read { get; }
    internal PlayHistoryPeriodIndexStageMetrics PeriodIndex { get; }
    internal PlayHistoryProjectionStageMetrics Projection { get; }
    internal Lr2PlayHistorySchemaStatusSnapshot Lr2SchemaStatusSnapshot { get; }

    internal static PlayHistoryReadWorkflowProgress ReadCompleted(
        PlayHistoryReadStageMetrics read,
        Lr2PlayHistorySchemaStatusSnapshot schemaStatusSnapshot) => new(
            PlayHistoryReadWorkflowProgressStage.ReadCompleted,
            read,
            periodIndex: null,
            projection: null,
            lr2SchemaStatusSnapshot: schemaStatusSnapshot);

    internal static PlayHistoryReadWorkflowProgress PeriodIndexCompleted(
        PlayHistoryReadStageMetrics read,
        PlayHistoryPeriodIndexStageMetrics periodIndex) => new(
            PlayHistoryReadWorkflowProgressStage.PeriodIndexCompleted,
            read,
            periodIndex,
            projection: null,
            lr2SchemaStatusSnapshot: null);

    internal static PlayHistoryReadWorkflowProgress ProjectionCompleted(
        PlayHistoryReadStageMetrics read,
        PlayHistoryPeriodIndexStageMetrics periodIndex,
        PlayHistoryProjectionStageMetrics projection) => new(
            PlayHistoryReadWorkflowProgressStage.ProjectionCompleted,
            read,
            periodIndex,
            projection,
            lr2SchemaStatusSnapshot: null);
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
    Canceled,
    Failed
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
    /// <summary>成功、取消し、取得失敗を区別し、未採用の失敗診断だけを表示側へ渡します。</summary>
    internal PlayHistoryReadWorkflowResult(
        bool built,
        string canceledStage,
        long elapsedThroughCancellationMs,
        PlayHistoryReadStageMetrics read,
        PlayHistoryPeriodIndexStageMetrics periodIndex,
        PlayHistoryProjectionStageMetrics projection,
        PlayHistoryReadPresentationBuildResult presentation,
        string failedStage = null,
        IReadOnlyList<PlayHistoryDiagnostic> diagnostics = null)
    {
        Built = built;
        CanceledStage = canceledStage ?? string.Empty;
        ElapsedThroughCancellationMs = elapsedThroughCancellationMs;
        Read = read;
        PeriodIndex = periodIndex;
        Projection = projection;
        Presentation = presentation;
        FailedStage = failedStage ?? string.Empty;
        Diagnostics = diagnostics ?? [];
    }

    /// <summary>失敗を取消しや正常0件へ読み替えず、採用側へ渡します。</summary>
    internal string FailedStage { get; }
    /// <summary>非採用となった段階の診断。元例外を表示用に保存しません。</summary>
    internal IReadOnlyList<PlayHistoryDiagnostic> Diagnostics { get; }

    internal bool Built { get; }
    internal string CanceledStage { get; }
    internal long ElapsedThroughCancellationMs { get; }
    internal PlayHistoryReadStageMetrics Read { get; }
    internal PlayHistoryPeriodIndexStageMetrics PeriodIndex { get; }
    internal PlayHistoryProjectionStageMetrics Projection { get; }
    internal PlayHistoryReadPresentationBuildResult Presentation { get; }
}
