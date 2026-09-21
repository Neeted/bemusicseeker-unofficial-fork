using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Identifies one existing LR2 song row targeted by a chart-info-only update.
/// Path comparison is exact; MD5 is normalized for LR2 song.hash comparison.
/// </summary>
internal readonly record struct Lr2ChartInfoSongProjectionIdentity(string Path, string Md5);

/// <summary>
/// Immutable, update-only projection of the columns that new-chart generation derives from chart_info.
/// It deliberately excludes basic song metadata, mode, judge, and user-managed columns.
/// </summary>
internal sealed class Lr2ChartInfoSongProjection
{
    private const int FeatureUndefinedLongNote = 1;
    private const int FeatureRandom = 4;
    private const int FeatureLongNote = 8;
    private const int FeatureChargeNote = 16;
    private const int FeatureHellChargeNote = 32;

    private Lr2ChartInfoSongProjection(
        string path,
        string md5,
        int? level,
        int difficulty,
        int? maxBpm,
        int? minBpm,
        int? bga,
        int exLevel,
        int longNote,
        int random,
        int kariNotes)
    {
        Path = path;
        Md5 = md5;
        Level = level;
        Difficulty = difficulty;
        MaxBpm = maxBpm;
        MinBpm = minBpm;
        Bga = bga;
        ExLevel = exLevel;
        LongNote = longNote;
        Random = random;
        KariNotes = kariNotes;
    }

    internal string Path { get; }

    internal string Md5 { get; }

    internal int? Level { get; }

    internal int Difficulty { get; }

    internal int? MaxBpm { get; }

    internal int? MinBpm { get; }

    internal int? Bga { get; }

    internal int ExLevel { get; }

    internal int LongNote { get; }

    internal int Random { get; }

    internal int KariNotes { get; }

    internal Lr2ChartInfoSongProjectionIdentity Identity => new(Path, Md5);

    /// <summary>
    /// Creates the canonical chart-info-derived projection for an existing or newly parsed BMS song.
    /// A mismatched chart_info MD5 is rejected rather than projected to a different owner.
    /// </summary>
    internal static Lr2ChartInfoSongProjection Create(
        string path,
        string md5,
        LR2SongDBExtended.chart_info chartInfo)
    {
        if (chartInfo == null || string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(md5))
        {
            return null;
        }

        string normalizedMd5 = md5.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(chartInfo.md5)
            && !string.Equals(normalizedMd5, chartInfo.md5.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new Lr2ChartInfoSongProjection(
            path,
            normalizedMd5,
            chartInfo.level,
            NormalizeDifficulty(chartInfo.difficulty),
            ToLr2SongInteger(chartInfo.maxbpm),
            ToLr2SongInteger(chartInfo.minbpm),
            chartInfo.bga,
            chartInfo.exlevel ?? 0,
            HasLongNoteFeature(chartInfo.feature) ? 1 : 0,
            (chartInfo.feature & FeatureRandom) != 0 ? 1 : 0,
            chartInfo.notes);
    }

    /// <summary>
    /// Applies only chart-info-derived columns when the target identity still matches.
    /// </summary>
    internal bool ApplyTo(BMSFile song)
    {
        if (song == null
            || !string.Equals(song.path, Path, StringComparison.Ordinal)
            || !string.Equals(song.hash, Md5, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        song.level = Level;
        song.difficulty = Difficulty;
        song.maxbpm = MaxBpm;
        song.minbpm = MinBpm;
        song.bga = Bga;
        song.exlevel = ExLevel;
        song.longnote = LongNote;
        song.random = Random;
        song.karinotes = KariNotes;
        return true;
    }

    internal static int NormalizeDifficulty(int? difficulty)
    {
        return difficulty.HasValue && difficulty.Value >= 0 && difficulty.Value <= 5
            ? difficulty.Value
            : 2;
    }

    internal static int? ToLr2SongInteger(double? value)
    {
        if (!value.HasValue)
        {
            return null;
        }
        if (value.Value > int.MaxValue)
        {
            return int.MaxValue;
        }
        if (value.Value < int.MinValue)
        {
            return int.MinValue;
        }
        return (int)value.Value;
    }

    internal static bool HasLongNoteFeature(int feature)
    {
        const int longNoteFlags = FeatureUndefinedLongNote
            | FeatureLongNote
            | FeatureChargeNote
            | FeatureHellChargeNote;
        return (feature & longNoteFlags) != 0;
    }
}

/// <summary>
/// Result of applying chart-info-only projections to existing LR2 song rows.
/// Missing rows are reported and never inserted.
/// </summary>
internal sealed class Lr2ChartInfoSongProjectionWriteResult
{
    internal static Lr2ChartInfoSongProjectionWriteResult Empty { get; } = new([], [], 0);

    internal Lr2ChartInfoSongProjectionWriteResult(
        IEnumerable<Lr2ChartInfoSongProjection> matchedProjections,
        IEnumerable<Lr2ChartInfoSongProjection> missingProjections,
        int changedCount)
    {
        MatchedProjections = Array.AsReadOnly([.. (matchedProjections ?? []).Where(projection => projection != null)]);
        MissingProjections = Array.AsReadOnly([.. (missingProjections ?? []).Where(projection => projection != null)]);
        ChangedCount = changedCount;
    }

    internal IReadOnlyList<Lr2ChartInfoSongProjection> MatchedProjections { get; }

    internal IReadOnlyList<Lr2ChartInfoSongProjection> MissingProjections { get; }

    internal int RequestedCount => MatchedProjections.Count + MissingProjections.Count;

    internal int MatchedCount => MatchedProjections.Count;

    internal int MissingCount => MissingProjections.Count;

    internal int ChangedCount { get; }
}
