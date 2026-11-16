using System;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Applies BeMusicSeeker-generated LR2 song row values before persistence.
/// </summary>
internal static class Lr2SongRowEnricher
{
    internal static void EnrichGeneratedSong(
        LR2SongDB.song song,
        Lr2SongFolderParentNormalizer.Lr2FolderParentHashCache folderParentHashCache = null)
    {
        if (song == null)
        {
            return;
        }
        Lr2SongFolderParentNormalizer.ApplyIfMissingOrInvalid(song, folderParentHashCache);
        song.level ??= 0;
        song.difficulty ??= -1;
        song.mode ??= 5;
        song.judge ??= 2;
        song.exlevel ??= 0;
    }

    internal static void EnrichFromChartInfo(LR2SongDB.song song, BeMusicSeeker.Models.ChartDetails chartInfo)
    {
        if (song == null)
        {
            return;
        }

        Lr2ChartInfoSongProjection.Create(song.path, song.hash, chartInfo)?.ApplyTo(song);
        ApplyLr2ChartMetadataDefaults(song);
    }

    internal static void ApplyLr2ChartMetadataDefaults(LR2SongDB.song song)
    {
        if (song == null)
        {
            return;
        }

        song.difficulty = Lr2ChartInfoSongProjection.NormalizeDifficulty(song.difficulty);
    }

    internal static int ToLr2UnixSeconds(DateTime utcTime)
    {
        long seconds = new DateTimeOffset(DateTime.SpecifyKind(utcTime, DateTimeKind.Utc)).ToUnixTimeSeconds();
        if (seconds > int.MaxValue)
        {
            return int.MaxValue;
        }
        if (seconds < int.MinValue)
        {
            return int.MinValue;
        }
        return (int)seconds;
    }
}
