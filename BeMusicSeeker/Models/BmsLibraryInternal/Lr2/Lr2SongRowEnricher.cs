using System;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Applies BeMusicSeeker-generated LR2 song row values before persistence.
/// </summary>
internal static class Lr2SongRowEnricher
{
    internal static void EnrichParsedSong(
        BMSFile song,
        ChartFileSnapshot snapshot,
        int textFlag,
        BMSFile existingSong,
        Lr2SongFolderParentNormalizer.Lr2FolderParentHashCache folderParentHashCache = null)
    {
        if (song == null)
        {
            return;
        }
        if (snapshot != null)
        {
            song.date = ToLr2UnixSeconds(snapshot.LastWriteTimeUtc);
        }
        song.SetTextGroupFlag(textFlag);
        song.PreserveUserSongColumnsFrom(existingSong);
        EnrichGeneratedSong(song, folderParentHashCache);
    }

    internal static BMSFile CreateParsedSongRowFromSnapshot(
        ChartFileSnapshot snapshot,
        int textFlag,
        BMSFile existingSong,
        Lr2SongFolderParentNormalizer.Lr2FolderParentHashCache folderParentHashCache = null)
    {
        if (snapshot == null)
        {
            return null;
        }

        BMSFile.BmsEncodingDetectionResult detectionResult = BMSFile.DetectEncodingOfBMSFileDetailed(snapshot);
        var song = BMSFile.CreateBMSFileFromSnapshot(snapshot, detectionResult);
        song.RememberSnapshotEncodingDetectionResult(snapshot, detectionResult);
        EnrichParsedSong(song, snapshot, textFlag, existingSong, folderParentHashCache);
        return song;
    }

    internal static void EnrichGeneratedSong(
        BMSFile song,
        Lr2SongFolderParentNormalizer.Lr2FolderParentHashCache folderParentHashCache = null)
    {
        if (song == null)
        {
            return;
        }
        Lr2SongFolderParentNormalizer.ApplyIfMissingOrInvalid(song, folderParentHashCache);
        song.ApplyLr2LightweightDefaults();
        song.exlevel ??= 0;
    }

    internal static void EnrichFromChartInfo(BMSFile song, LR2SongDBExtended.chart_info chartInfo)
    {
        if (song == null)
        {
            return;
        }

        Lr2ChartInfoSongProjection.Create(song.path, song.hash, chartInfo)?.ApplyTo(song);
        ApplyLr2ChartMetadataDefaults(song);
    }

    internal static void ApplyLr2ChartMetadataDefaults(BMSFile song)
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
