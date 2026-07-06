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
