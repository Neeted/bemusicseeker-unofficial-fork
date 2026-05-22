using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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

    internal static ChartFile WithTransientState(
        ChartFile source,
        ChartFileTransientState state,
        bool includeWarningSnapshot = true)
    {
        if (source == null || state?.HasState != true)
        {
            return source;
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
            source.GetBmsStorageOwner(),
            source.GetBmsonStorageOwner(),
            string.IsNullOrWhiteSpace(state.Subtitle) ? source.Subtitle : state.Subtitle,
            source.AudioResourcePaths,
            source.VisualResourcePaths,
            source.Stagefile,
            source.Backbmp,
            source.Banner,
            state.HasInstallDestinationState ? state.InstallDestination : source.InstallDestination,
            state.HasInstallDestinationState ? state.InstallDestinationTitle : source.InstallDestinationTitle,
            state.HasInstallDestinationState ? state.InstallDestinationArtist : source.InstallDestinationArtist,
            state.HasInstallDestinationState ? state.InstallDestinationSuggestions : source.InstallDestinationSuggestions,
            SelectTransientWarnings(source.Warnings, state, includeWarningSnapshot),
            state.WAVHealth ?? source.WAVHealth,
            state.BGAHealth ?? source.BGAHealth,
            state.MovieHealth ?? source.MovieHealth,
            state.StagefileHealth ?? source.StagefileHealth,
            state.BannerHealth ?? source.BannerHealth,
            state.BackbmpHealth ?? source.BackbmpHealth,
            string.IsNullOrWhiteSpace(state.EncodingName) ? source.EncodingName : state.EncodingName,
            source.Score,
            source.Status);
    }

    internal static ChartFile WithChartInfo(ChartFile source, LR2SongDBExtended.chart_info chartInfo)
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
            chartInfo,
            source.GetBmsStorageOwner(),
            source.GetBmsonStorageOwner(),
            source.Subtitle,
            source.AudioResourcePaths,
            source.VisualResourcePaths,
            source.Stagefile,
            source.Backbmp,
            source.Banner,
            source.InstallDestination,
            source.InstallDestinationTitle,
            source.InstallDestinationArtist,
            source.InstallDestinationSuggestions,
            source.Warnings,
            source.WAVHealth,
            source.BGAHealth,
            source.MovieHealth,
            source.StagefileHealth,
            source.BannerHealth,
            source.BackbmpHealth,
            source.EncodingName,
            source.Score,
            source.Status);
    }

    internal static ChartFile WithStatus(ChartFile source, ChartFileStatus status)
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
            source.GetBmsStorageOwner(),
            source.GetBmsonStorageOwner(),
            source.Subtitle,
            source.AudioResourcePaths,
            source.VisualResourcePaths,
            source.Stagefile,
            source.Backbmp,
            source.Banner,
            source.InstallDestination,
            source.InstallDestinationTitle,
            source.InstallDestinationArtist,
            source.InstallDestinationSuggestions,
            source.Warnings,
            source.WAVHealth,
            source.BGAHealth,
            source.MovieHealth,
            source.StagefileHealth,
            source.BannerHealth,
            source.BackbmpHealth,
            source.EncodingName,
            source.Score,
            status);
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
            source.GetBmsStorageOwner(),
            source.GetBmsonStorageOwner(),
            source.Subtitle,
            source.AudioResourcePaths,
            source.VisualResourcePaths,
            source.Stagefile,
            source.Backbmp,
            source.Banner,
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
            source.EncodingName,
            source.Score,
            source.Status);
    }

    internal static ChartFile WithPath(ChartFile source, string path)
    {
        if (source == null)
        {
            return null;
        }

        return new ChartFile(
            source.Kind,
            path,
            source.Md5,
            source.Sha256,
            source.Title,
            source.RawTitle,
            source.Artist,
            source.Genre,
            null,
            source.Tag,
            source.LevelText,
            source.Level,
            source.Mode,
            source.ChartInfo,
            source.GetBmsStorageOwner(),
            source.GetBmsonStorageOwner(),
            source.Subtitle,
            source.AudioResourcePaths,
            source.VisualResourcePaths,
            source.Stagefile,
            source.Backbmp,
            source.Banner,
            source.InstallDestination,
            source.InstallDestinationTitle,
            source.InstallDestinationArtist,
            source.InstallDestinationSuggestions,
            source.Warnings,
            source.WAVHealth,
            source.BGAHealth,
            source.MovieHealth,
            source.StagefileHealth,
            source.BannerHealth,
            source.BackbmpHealth,
            source.EncodingName,
            source.Score,
            source.Status);
    }

    internal static ChartFile WithTransientOverrides(
        ChartFile current,
        ChartFile overrideChart,
        ChartFile baseline,
        bool includeWarningSnapshot = true)
    {
        if (current == null || overrideChart == null || baseline == null)
        {
            return current;
        }

        return new ChartFile(
            current.Kind,
            current.Path,
            current.Md5,
            current.Sha256,
            current.Title,
            current.RawTitle,
            current.Artist,
            current.Genre,
            current.Folder,
            current.Tag,
            current.LevelText,
            current.Level,
            current.Mode,
            current.ChartInfo,
            current.GetBmsStorageOwner(),
            current.GetBmsonStorageOwner(),
            SelectStringOverride(current.Subtitle, overrideChart.Subtitle, baseline.Subtitle),
            current.AudioResourcePaths,
            current.VisualResourcePaths,
            current.Stagefile,
            current.Backbmp,
            current.Banner,
            SelectStringOverride(current.InstallDestination, overrideChart.InstallDestination, baseline.InstallDestination),
            SelectStringOverride(current.InstallDestinationTitle, overrideChart.InstallDestinationTitle, baseline.InstallDestinationTitle),
            SelectStringOverride(current.InstallDestinationArtist, overrideChart.InstallDestinationArtist, baseline.InstallDestinationArtist),
            SelectStringListOverride(current.InstallDestinationSuggestions, overrideChart.InstallDestinationSuggestions, baseline.InstallDestinationSuggestions),
            includeWarningSnapshot ? SelectWarningOverride(current.Warnings, overrideChart.Warnings, baseline.Warnings) : [],
            SelectNullableOverride(current.WAVHealth, overrideChart.WAVHealth, baseline.WAVHealth),
            SelectNullableOverride(current.BGAHealth, overrideChart.BGAHealth, baseline.BGAHealth),
            SelectNullableOverride(current.MovieHealth, overrideChart.MovieHealth, baseline.MovieHealth),
            SelectNullableOverride(current.StagefileHealth, overrideChart.StagefileHealth, baseline.StagefileHealth),
            SelectNullableOverride(current.BannerHealth, overrideChart.BannerHealth, baseline.BannerHealth),
            SelectNullableOverride(current.BackbmpHealth, overrideChart.BackbmpHealth, baseline.BackbmpHealth),
            SelectStringOverride(current.EncodingName, overrideChart.EncodingName, baseline.EncodingName),
            current.Score,
            current.Status);
    }

    internal static ChartFile FromBmsFile(
        BMSFile file,
        ChartFileLevelParsing levelParsing = ChartFileLevelParsing.Invariant,
        bool includeWarningSnapshot = true,
        bool includeResourceReferences = true)
    {
        if (file == null)
        {
            return null;
        }
        BMSFileMaintenanceInfo maintenanceInfo = file.HasValidMaintenanceInfoSnapshot
            ? file.TryGetMaintenanceInfoWithoutCreating()
            : null;
        ChartScoreSnapshot score = ChartScoreSnapshot.FromBmsFile(file);
        ChartFileStatus status = ChartFileStatusMapper.FromBmsFileStatus(file.status);
        if (score.IsLr2IrScoreUnsent)
        {
            status |= ChartFileStatus.SCORE_UNSENT;
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
            FormatBmsLevelText(file),
            GetBmsLevelValue(file),
            file.mode,
            null,
            file,
            null,
            file.subtitle,
            includeResourceReferences ? file.WAVfiles?.ToArray() : null,
            includeResourceReferences ? file.BGAfiles?.ToArray() : null,
            file.stagefile,
            file.backbmp,
            file.banner,
            null,
            null,
            null,
            [],
            GetWarnings(file, includeWarningSnapshot),
            maintenanceInfo?.WAVHealth,
            maintenanceInfo?.BGAHealth,
            maintenanceInfo?.MovieHealth,
            maintenanceInfo?.StagefileHealth,
            maintenanceInfo?.BannerHealth,
            maintenanceInfo?.BackbmpHealth,
            maintenanceInfo?.encoding,
            score,
            status);
    }

    internal static ChartFile FromBmsonSong(
        LR2SongDBExtended.bmson_song song,
        bool includeWarningSnapshot = true,
        bool includeResourceReferences = true)
    {
        return FromBmsonSong(
            song,
            ChartFileTransientState.Empty,
            includeWarningSnapshot,
            includeResourceReferences);
    }

    internal static ChartFile FromBmsonSong(
        LR2SongDBExtended.bmson_song song,
        ChartFileTransientState transientState,
        bool includeWarningSnapshot = true,
        bool includeResourceReferences = true)
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
            null,
            null,
            song,
            string.IsNullOrWhiteSpace(song.subtitle) ? transientState.Subtitle : song.subtitle,
            includeResourceReferences ? song.wav_files : null,
            includeResourceReferences ? song.bga_files : null,
            song.stagefile,
            song.backbmp,
            song.banner,
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

    internal static ChartFile FromStorageOwner(
        ChartFile source,
        ChartFileLevelParsing bmsLevelParsing = ChartFileLevelParsing.Invariant,
        bool includeWarningSnapshot = true,
        bool includeResourceReferences = true)
    {
        if (source == null)
        {
            return null;
        }

        BMSFile bmsFile = source.GetBmsStorageOwner();
        if (bmsFile != null)
        {
            return FromBmsFile(bmsFile, bmsLevelParsing, includeWarningSnapshot, includeResourceReferences);
        }

        LR2SongDBExtended.bmson_song bmsonSong = source.GetBmsonStorageOwner();
        return bmsonSong == null ? null : FromBmsonSong(bmsonSong, includeWarningSnapshot, includeResourceReferences);
    }

    internal static ChartFile FromStorageOwnerWithTransientState(
        ChartFile source,
        ChartFileTransientState transientState,
        ChartFileLevelParsing bmsLevelParsing = ChartFileLevelParsing.Invariant,
        bool includeWarningSnapshot = true,
        bool includeResourceReferences = true)
    {
        LR2SongDBExtended.bmson_song bmsonSong = source?.GetBmsonStorageOwner();
        if (bmsonSong != null)
        {
            return FromBmsonSong(bmsonSong, transientState, includeWarningSnapshot, includeResourceReferences);
        }

        return WithTransientState(
            FromStorageOwner(source, bmsLevelParsing, includeWarningSnapshot, includeResourceReferences),
            transientState,
            includeWarningSnapshot);
    }

    internal static ChartFile FromStorageOwnerListIdentity(
        ChartFile source,
        ChartFileLevelParsing bmsLevelParsing = ChartFileLevelParsing.Invariant)
    {
        if (source == null)
        {
            return null;
        }

        BMSFile bmsFile = source.GetBmsStorageOwner();
        if (bmsFile != null)
        {
            return new ChartFile(
                ChartFileKind.Bms,
                bmsFile.path,
                bmsFile.hash,
                bmsFile.sha256,
                bmsFile.Title,
                bmsFile.GetRawTitleForDisplay(),
                bmsFile.Artist,
                bmsFile.genre,
                GetDisplayFolderFromPath(bmsFile.path),
                bmsFile.tag,
                FormatBmsLevelText(bmsFile),
                GetBmsLevelValue(bmsFile),
                bmsFile.mode,
                null,
                bmsFile,
                null,
                bmsFile.subtitle);
        }

        LR2SongDBExtended.bmson_song bmsonSong = source.GetBmsonStorageOwner();
        if (bmsonSong == null)
        {
            return null;
        }

        return new ChartFile(
            ChartFileKind.Bmson,
            bmsonSong.path,
            bmsonSong.md5,
            bmsonSong.sha256,
            BmsonSongParser.ComposeDisplayTitle(bmsonSong),
            bmsonSong.title,
            bmsonSong.artist,
            bmsonSong.genre,
            BmsonSongParser.ComposeDisplayFolder(bmsonSong),
            string.Empty,
            FormatNullableDouble(bmsonSong.level),
            bmsonSong.level,
            BmsonSongParser.ResolvePlaylistMode(bmsonSong.mode_hint),
            null,
            null,
            bmsonSong,
            bmsonSong.subtitle);
    }

    internal static List<ChartFile> FromStorageRows(
        IEnumerable<BMSFile> bmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs,
        bool includeWarningSnapshot = false,
        bool requireBmsonPath = false,
        bool orderBmsonByPath = false,
        bool includeResourceReferences = true)
    {
        List<ChartFile> charts = FromBmsFiles(bmsFiles, includeWarningSnapshot, includeResourceReferences);
        charts.AddRange(FromBmsonSongs(bmsonSongs, includeWarningSnapshot, requireBmsonPath, orderBmsonByPath, includeResourceReferences));
        return charts;
    }

    internal static List<ChartFile> FromBmsFiles(
        IEnumerable<BMSFile> files,
        bool includeWarningSnapshot = false,
        bool includeResourceReferences = true)
    {
        return [.. (files ?? [])
            .Where(file => file != null)
            .Select(file => FromBmsFile(file, includeWarningSnapshot: includeWarningSnapshot, includeResourceReferences: includeResourceReferences))
            .Where(chart => chart != null)];
    }

    internal static ChartFile FromBmsStorageOwnerIdentity(BMSFile file)
    {
        if (file == null)
        {
            return null;
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
            FormatBmsLevelText(file),
            GetBmsLevelValue(file),
            file.mode,
            null,
            file,
            null,
            file.subtitle);
    }

    internal static List<ChartFile> FromBmsStorageOwnerIdentities(IEnumerable<BMSFile> files)
    {
        return [.. (files ?? [])
            .Where(file => file != null)
            .Select(FromBmsStorageOwnerIdentity)
            .Where(chart => chart != null)];
    }

    internal static ChartFile FromBmsonStorageOwnerIdentity(LR2SongDBExtended.bmson_song song)
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
            song.title,
            song.artist,
            song.genre,
            BmsonSongParser.ComposeDisplayFolder(song),
            string.Empty,
            FormatNullableDouble(song.level),
            song.level,
            BmsonSongParser.ResolvePlaylistMode(song.mode_hint),
            null,
            null,
            song,
            song.subtitle);
    }

    internal static List<ChartFile> FromBmsonStorageOwnerIdentities(IEnumerable<LR2SongDBExtended.bmson_song> songs)
    {
        return [.. (songs ?? [])
            .Where(song => song != null)
            .Select(FromBmsonStorageOwnerIdentity)
            .Where(chart => chart != null)];
    }

    internal static List<ChartFile> FromBmsonSongs(
        IEnumerable<LR2SongDBExtended.bmson_song> songs,
        bool includeWarningSnapshot = false,
        bool requirePath = false,
        bool orderByPath = false,
        bool includeResourceReferences = true)
    {
        IEnumerable<LR2SongDBExtended.bmson_song> source = (songs ?? []).Where(song => song != null);
        if (requirePath)
        {
            source = source.Where(song => !string.IsNullOrWhiteSpace(song.path));
        }
        if (orderByPath)
        {
            source = source.OrderBy(song => song.path, System.StringComparer.OrdinalIgnoreCase);
        }
        return [.. source
            .Select(song => FromBmsonSong(song, includeWarningSnapshot, includeResourceReferences))
            .Where(chart => chart != null)];
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

    private static IReadOnlyList<ChartWarning> GetWarnings(BMSFile file, bool includeWarningSnapshot)
    {
        return includeWarningSnapshot ? file?.Warnings.ToStructuredList() ?? [] : [];
    }

    private static string SelectStringOverride(string current, string overrideValue, string baseline)
    {
        return string.Equals(overrideValue ?? string.Empty, baseline ?? string.Empty, System.StringComparison.Ordinal)
            ? current
            : overrideValue;
    }

    private static T? SelectNullableOverride<T>(T? current, T? overrideValue, T? baseline)
        where T : struct
    {
        return EqualityComparer<T?>.Default.Equals(overrideValue, baseline) ? current : overrideValue;
    }

    private static IReadOnlyList<string> SelectStringListOverride(IReadOnlyList<string> current, IReadOnlyList<string> overrideValue, IReadOnlyList<string> baseline)
    {
        return StringSequenceEquals(overrideValue, baseline) ? current : overrideValue ?? [];
    }

    private static IReadOnlyList<ChartWarning> SelectWarningOverride(IReadOnlyList<ChartWarning> current, IReadOnlyList<ChartWarning> overrideValue, IReadOnlyList<ChartWarning> baseline)
    {
        if (WarningSequenceEquals(overrideValue, baseline))
        {
            return current;
        }
        if ((overrideValue?.Count ?? 0) == 0 && (baseline?.Count ?? 0) > 0)
        {
            return current;
        }
        return overrideValue ?? [];
    }

    private static IReadOnlyList<ChartWarning> SelectTransientWarnings(IReadOnlyList<ChartWarning> sourceWarnings, ChartFileTransientState state, bool includeWarningSnapshot)
    {
        if (!includeWarningSnapshot)
        {
            return [];
        }
        if (state?.HasWarningProjection == true)
        {
            return state.Warnings ?? [];
        }
        if (state?.HasInstallEstimationWarningProjection == true)
        {
            return [..
                (sourceWarnings ?? []).Where(warning => warning?.Category != ChartWarningCategory.InstallEstimation)
                .Concat((state.Warnings ?? []).Where(warning => warning?.Category == ChartWarningCategory.InstallEstimation))];
        }
        return sourceWarnings ?? [];
    }

    private static bool StringSequenceEquals(IReadOnlyList<string> first, IReadOnlyList<string> second)
    {
        first ??= [];
        second ??= [];
        return first.SequenceEqual(second, System.StringComparer.Ordinal);
    }

    private static bool WarningSequenceEquals(IReadOnlyList<ChartWarning> first, IReadOnlyList<ChartWarning> second)
    {
        first ??= [];
        second ??= [];
        if (first.Count != second.Count)
        {
            return false;
        }
        for (int i = 0; i < first.Count; i++)
        {
            ChartWarning left = first[i];
            ChartWarning right = second[i];
            if (left == null || right == null)
            {
                if (!ReferenceEquals(left, right))
                {
                    return false;
                }
                continue;
            }
            if (left.Kind != right.Kind
                || left.Category != right.Category
                || left.Priority != right.Priority
                || left.HighlightRow != right.HighlightRow
                || left.ShowInDigest != right.ShowInDigest
                || left.ShowInTooltip != right.ShowInTooltip
                || !string.Equals(left.DigestLabel, right.DigestLabel, System.StringComparison.Ordinal)
                || !string.Equals(left.Message, right.Message, System.StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
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

    private static string FormatBmsLevelText(BMSFile file)
    {
        return file?.level.HasValue == true
            ? file.level.Value.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
    }

    private static double? GetBmsLevelValue(BMSFile file)
    {
        return file?.level.HasValue == true
            ? file.level.Value
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
