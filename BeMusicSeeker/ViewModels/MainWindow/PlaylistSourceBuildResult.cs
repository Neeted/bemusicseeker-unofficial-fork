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
