using System.Collections.Generic;
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
    internal static ChartFile WithWarnings(ChartFile source, IReadOnlyList<ChartWarning> warnings)
    {
        return WithPackageState(
            source,
            source?.InstallDestination,
            source?.InstallDestinationTitle,
            source?.InstallDestinationArtist,
            source?.InstallDestinationSuggestions,
            warnings);
    }

    internal static ChartFile WithPackageState(
        ChartFile source,
        string installDestination,
        string installDestinationTitle,
        string installDestinationArtist,
        IReadOnlyList<ChartWarning> warnings)
    {
        return WithPackageState(
            source,
            installDestination,
            installDestinationTitle,
            installDestinationArtist,
            source?.InstallDestinationSuggestions,
            warnings);
    }

    internal static ChartFile WithPackageState(
        ChartFile source,
        string installDestination,
        string installDestinationTitle,
        string installDestinationArtist,
        IReadOnlyList<string> installDestinationSuggestions,
        IReadOnlyList<ChartWarning> warnings)
    {
        if (source == null)
        {
            return null;
        }

        return new ChartFile(
            source.Kind,
            source.Path,
            source.Md5,
            source.Sha256,
            source.Title,
            source.RawTitle,
            source.Artist,
            source.Genre,
            source.Folder,
            source.Tag,
            source.LevelText,
            source.Level,
            source.Mode,
            source.ChartInfo,
            source.BmsFile,
            source.BmsonSong,
            source.Subtitle,
            installDestination,
            installDestinationTitle,
            installDestinationArtist,
            installDestinationSuggestions,
            warnings,
            source.WAVHealth,
            source.BGAHealth,
            source.MovieHealth,
            source.StagefileHealth,
            source.BannerHealth,
            source.BackbmpHealth,
            source.EncodingName);
    }

    internal static ChartFile FromBmsFile(
        BMSFile file,
        ChartFileLevelParsing levelParsing = ChartFileLevelParsing.Invariant,
        bool includeWarningSnapshot = true)
    {
        if (file == null)
        {
            return null;
        }

        if (file is PendingChartEntry { IsBmsonChart: true, BmsonSong: { } song } pending)
        {
            return FromPendingBmsonAdapter(pending, song, includeWarningSnapshot);
        }

        return new ChartFile(
            ChartFileKind.Bms,
            file.path,
            file.hash,
            file.sha256,
            file.Title,
            file.GetRawTitleForDisplay(),
            file.Artist,
            file.genre,
            GetDisplayFolderFromPath(file.path),
            file.tag,
            file.Level,
            ParseNullableDouble(file.Level, levelParsing),
            file.mode,
            file.ChartInfo,
            file,
            null,
            file.subtitle,
            file.instl_dst,
            file.InstallDestinationTitle,
            file.InstallDestinationArtist,
            file.InstallDestinationSuggestions,
            GetWarnings(file, includeWarningSnapshot),
            file.WAVHealth,
            file.BGAHealth,
            file.MovieHealth,
            file.StagefileHealth,
            file.BannerHealth,
            file.BackbmpHealth,
            file.encoding);
    }

    internal static ChartFile FromBmsonSong(
        LR2SongDBExtended.bmson_song song,
        BMSFile compatibilityBmsFile = null,
        bool includeWarningSnapshot = true)
    {
        return FromBmsonSong(
            song,
            ChartFileTransientState.FromCompatibilityFile(compatibilityBmsFile, includeWarningSnapshot),
            includeWarningSnapshot);
    }

    internal static ChartFile FromBmsonSong(
        LR2SongDBExtended.bmson_song song,
        ChartFileTransientState transientState,
        bool includeWarningSnapshot = true)
    {
        if (song == null)
        {
            return null;
        }
        transientState ??= ChartFileTransientState.Empty;

        return new ChartFile(
            ChartFileKind.Bmson,
            song.path,
            song.md5,
            song.sha256,
            BmsonSongParser.ComposeDisplayTitle(song),
            song.title,
            song.artist,
            song.genre,
            BmsonSongParser.ComposeDisplayFolder(song),
            string.Empty,
            FormatNullableDouble(song.level),
            song.level,
            BmsonSongParser.ResolvePlaylistMode(song.mode_hint),
            song.ChartInfo,
            null,
            song,
            string.IsNullOrWhiteSpace(song.subtitle) ? transientState.Subtitle : song.subtitle,
            transientState.InstallDestination,
            transientState.InstallDestinationTitle,
            transientState.InstallDestinationArtist,
            transientState.InstallDestinationSuggestions,
            includeWarningSnapshot ? transientState.Warnings : [],
            song.MaintenanceInfo?.WAVHealth ?? transientState.WAVHealth,
            song.MaintenanceInfo?.BGAHealth ?? transientState.BGAHealth,
            song.MaintenanceInfo?.MovieHealth ?? transientState.MovieHealth,
            song.MaintenanceInfo?.StagefileHealth ?? transientState.StagefileHealth,
            song.MaintenanceInfo?.BannerHealth ?? transientState.BannerHealth,
            song.MaintenanceInfo?.BackbmpHealth ?? transientState.BackbmpHealth,
            song.MaintenanceInfo?.encoding ?? transientState.EncodingName);
    }

    internal static ChartFile FromBmsMetadata(
        string path,
        string md5,
        string sha256,
        string title,
        string artist,
        string genre,
        string folder,
        string tag,
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
            title,
            artist,
            genre,
            folder,
            tag,
            FormatNullableDouble(level),
            level,
            mode,
            chartInfo,
            null,
            null);
    }

    private static ChartFile FromPendingBmsonAdapter(
        PendingChartEntry pending,
        LR2SongDBExtended.bmson_song song,
        bool includeWarningSnapshot)
    {
        return new ChartFile(
            ChartFileKind.Bmson,
            pending.path,
            pending.hash,
            pending.sha256,
            pending.Title,
            pending.GetRawTitleForDisplay(),
            pending.Artist,
            pending.genre,
            pending.Folder,
            pending.tag,
            pending.Level,
            pending.level ?? song.level,
            pending.mode ?? BmsonSongParser.ResolvePlaylistMode(song.mode_hint),
            pending.ChartInfo,
            null,
            song,
            pending.subtitle,
            pending.instl_dst,
            pending.InstallDestinationTitle,
            pending.InstallDestinationArtist,
            pending.InstallDestinationSuggestions,
            GetWarnings(pending, includeWarningSnapshot),
            pending.WAVHealth,
            pending.BGAHealth,
            pending.MovieHealth,
            pending.StagefileHealth,
            pending.BannerHealth,
            pending.BackbmpHealth,
            pending.encoding);
    }

    private static IReadOnlyList<ChartWarning> GetWarnings(BMSFile file, bool includeWarningSnapshot)
    {
        return includeWarningSnapshot ? file?.Warnings.ToStructuredList() ?? [] : [];
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

    private static string FormatNullableDouble(double? value)
    {
        return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
    }

    private static string GetDisplayFolderFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }
        return System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)) ?? string.Empty;
    }
}
