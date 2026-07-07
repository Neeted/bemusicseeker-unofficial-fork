using System.Collections;
using System.Collections.Generic;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// playlist source build stage の集計メトリクスです。
/// </summary>
internal sealed class PlaylistScoreProbeMetrics
{
    internal int TargetCount;

    internal int MatchedScoreCount;

    internal long TotalMs;
}

/// <summary>
/// playlist source build stage の結果です。
/// </summary>
internal sealed class PlaylistSourceBuildResult
{
    internal PlaylistSourceBuildResult(
        List<PlaylistDetailSourceRow> sourceRows,
        int scoreUpdateTargetCount,
        long entryResolveMs,
        long scoreProbeMs,
        long sourceMaterializeMs,
        PlaylistScoreProbeMetrics scoreProbeMetrics)
    {
        SourceRows = sourceRows ?? [];
        SourceCount = SourceRows.Count;
        FolderCount = SourceCount;
        ScoreUpdateTargetCount = scoreUpdateTargetCount;
        EntryResolveMs = entryResolveMs;
        ScoreProbeMs = scoreProbeMs;
        SourceMaterializeMs = sourceMaterializeMs;
        ScoreProbeMetrics = scoreProbeMetrics ?? new PlaylistScoreProbeMetrics();
    }

    internal List<PlaylistDetailSourceRow> SourceRows { get; }

    internal int SourceCount { get; }

    internal int FolderCount { get; }

    internal int ScoreUpdateTargetCount { get; }

    internal long EntryResolveMs { get; }

    internal long ScoreProbeMs { get; }

    internal long SourceMaterializeMs { get; }

    internal PlaylistScoreProbeMetrics ScoreProbeMetrics { get; }
}

/// <summary>
/// playlist source build stage の実行結果です。
/// </summary>
internal sealed class PlaylistSourceBuildStageResult
{
    internal PlaylistSourceBuildStageResult(
        PlaylistSourceBuildResult sourceBuild,
        long folderStageMs,
        long libraryIndexMs,
        string libraryIndexAccess,
        long libraryIndexBuildMs)
    {
        SourceBuild = sourceBuild;
        FolderStageMs = folderStageMs;
        LibraryIndexMs = libraryIndexMs;
        LibraryIndexAccess = libraryIndexAccess;
        LibraryIndexBuildMs = libraryIndexBuildMs;
    }

    internal PlaylistSourceBuildResult SourceBuild { get; }

    internal long FolderStageMs { get; }

    internal long LibraryIndexMs { get; }

    internal string LibraryIndexAccess { get; }

    internal long LibraryIndexBuildMs { get; }
}

/// <summary>
/// playlist source から view rows を作る stage の結果です。
/// </summary>
internal sealed class PlaylistViewApplyResult
{
    internal PlaylistViewApplyResult(
        IList finalRows,
        int sourceCount,
        string sortProfile,
        int keywordCount,
        int modeCount,
        long keywordStageMs,
        long modeStageMs,
        long sortStageMs,
        long viewMaterializeMs)
    {
        FinalRows = finalRows ?? throw new System.ArgumentNullException(nameof(finalRows));
        SourceCount = sourceCount;
        ViewCount = FinalRows.Count;
        SortProfile = sortProfile;
        KeywordCount = keywordCount;
        ModeCount = modeCount;
        KeywordStageMs = keywordStageMs;
        ModeStageMs = modeStageMs;
        SortStageMs = sortStageMs;
        ViewMaterializeMs = viewMaterializeMs;
    }

    internal IList FinalRows { get; }

    internal int SourceCount { get; }

    internal int ViewCount { get; }

    internal string SortProfile { get; }

    internal int KeywordCount { get; }

    internal int ModeCount { get; }

    internal long KeywordStageMs { get; }

    internal long ModeStageMs { get; }

    internal long SortStageMs { get; }

    internal long ViewMaterializeMs { get; }
}

/// <summary>
/// playlist detail view rows を main view へ反映した結果です。
/// </summary>
internal sealed class PlaylistMainViewApplyResult
{
    internal PlaylistMainViewApplyResult(long columnStageMs, long callbackStageMs)
    {
        ColumnStageMs = columnStageMs;
        CallbackStageMs = callbackStageMs;
    }

    internal long ColumnStageMs { get; }

    internal long CallbackStageMs { get; }
}

/// <summary>
/// RebuildPlaylistSource の worker stages が返す読み取り用の結果です。
/// </summary>
internal sealed class PlaylistRebuildExecutionResult
{
    internal PlaylistRebuildExecutionResult(
        PlaylistSourceBuildStageResult sourceBuildStage,
        PlaylistViewApplyResult viewApply)
    {
        SourceBuildStage = sourceBuildStage ?? throw new System.ArgumentNullException(nameof(sourceBuildStage));
        ViewApply = viewApply ?? throw new System.ArgumentNullException(nameof(viewApply));
    }

    internal PlaylistSourceBuildStageResult SourceBuildStage { get; }

    internal PlaylistSourceBuildResult SourceBuild => SourceBuildStage.SourceBuild;

    internal PlaylistViewApplyResult ViewApply { get; }

    internal List<PlaylistDetailSourceRow> SourceRows => SourceBuild.SourceRows;

    internal int SourceCount => SourceBuild.SourceCount;

    internal int FolderCount => SourceBuild.FolderCount;

    internal int ScoreUpdateTargetCount => SourceBuild.ScoreUpdateTargetCount;

    internal long EntryResolveMs => SourceBuild.EntryResolveMs;

    internal long ScoreProbeMs => SourceBuild.ScoreProbeMs;

    internal long SourceMaterializeMs => SourceBuild.SourceMaterializeMs;

    internal PlaylistScoreProbeMetrics ScoreProbeMetrics => SourceBuild.ScoreProbeMetrics;

    internal long FolderStageMs => SourceBuildStage.FolderStageMs;

    internal long LibraryIndexMs => SourceBuildStage.LibraryIndexMs;

    internal string LibraryIndexAccess => SourceBuildStage.LibraryIndexAccess;

    internal long LibraryIndexBuildMs => SourceBuildStage.LibraryIndexBuildMs;

    internal int ViewCount => ViewApply.ViewCount;

    internal string SortProfile => ViewApply.SortProfile;

    internal int KeywordCount => ViewApply.KeywordCount;

    internal int ModeCount => ViewApply.ModeCount;

    internal long KeywordStageMs => ViewApply.KeywordStageMs;

    internal long ModeStageMs => ViewApply.ModeStageMs;

    internal long SortStageMs => ViewApply.SortStageMs;

    internal long ViewMaterializeMs => ViewApply.ViewMaterializeMs;
}
