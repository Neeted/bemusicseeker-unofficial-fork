using System.Globalization;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models;

internal enum ChartFileLevelParsing
{
    Invariant,
    CurrentCultureThenInvariant
}

internal static class ChartFileProjection
{
    internal static ChartFile FromBmsFile(BMSFile file, ChartFileLevelParsing levelParsing = ChartFileLevelParsing.Invariant)
    {
        if (file == null)
        {
            return null;
        }

        if (file is PendingChartEntry { IsBmsonChart: true, BmsonSong: { } song } pending)
        {
            return FromPendingBmsonAdapter(pending, song);
        }

        return new ChartFile(
            ChartFileKind.Bms,
            file.path,
            file.hash,
            file.sha256,
            file.Title,
            file.Artist,
            ParseNullableDouble(file.Level, levelParsing),
            file.mode,
            file.ChartInfo,
            file,
            file,
            null);
    }

    internal static ChartFile FromBmsonSong(LR2SongDBExtended.bmson_song song, BMSFile compatibilityBmsFile = null)
    {
        if (song == null)
        {
            return null;
        }

        return new ChartFile(
            ChartFileKind.Bmson,
            song.path,
            song.md5,
            song.sha256,
            BmsonSongParser.ComposeDisplayTitle(song),
            song.artist,
            song.level,
            BmsonSongParser.ResolvePlaylistMode(song.mode_hint),
            song.ChartInfo,
            null,
            compatibilityBmsFile,
            song);
    }

    internal static ChartFile FromBmsMetadata(
        string path,
        string md5,
        string sha256,
        string title,
        string artist,
        double? level,
        int? mode,
        LR2SongDBExtended.chart_info chartInfo)
    {
        return new ChartFile(
            ChartFileKind.Bms,
            path,
            md5,
            sha256,
            title,
            artist,
            level,
            mode,
            chartInfo,
            null,
            null,
            null);
    }

    private static ChartFile FromPendingBmsonAdapter(PendingChartEntry pending, LR2SongDBExtended.bmson_song song)
    {
        return new ChartFile(
            ChartFileKind.Bmson,
            pending.path,
            pending.hash,
            pending.sha256,
            pending.Title,
            pending.Artist,
            pending.level ?? song.level,
            pending.mode ?? BmsonSongParser.ResolvePlaylistMode(song.mode_hint),
            pending.ChartInfo,
            null,
            pending,
            song);
    }

    private static double? ParseNullableDouble(string value, ChartFileLevelParsing levelParsing)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        if (levelParsing == ChartFileLevelParsing.CurrentCultureThenInvariant
            && double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out double currentCultureValue))
        {
            return currentCultureValue;
        }

        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double invariantCultureValue)
            ? invariantCultureValue
            : null;
    }
}
