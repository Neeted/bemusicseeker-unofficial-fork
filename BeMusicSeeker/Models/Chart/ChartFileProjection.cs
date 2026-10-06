using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading;
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

    /// <summary>保存主体を外した読取り値を作ります。取得済みリソースの原文と解析状態は同じ不変結果で共有します。</summary>
    internal static ChartFile ToImmutableSnapshot(ChartFile source)
    {
        if (source == null)
        {
            return null;
        }

        bool resourceHealthWarningsIgnored = source.ResourceHealthWarningsIgnored;
        ResourceHealthMaintenanceSnapshot resourceHealthMaintenanceSnapshot = source.ResourceHealthMaintenanceSnapshot;
        BMSFile bmsFile = source.GetBmsStorageOwner();
        if (bmsFile?.TryGetMaintenanceInfoWithoutCreating() is BMSFileMaintenanceInfo bmsMaintenanceInfo)
        {
            resourceHealthWarningsIgnored = bmsMaintenanceInfo.is_files_warning_ignored;
            resourceHealthMaintenanceSnapshot = ResourceHealthMaintenanceSnapshot.From(bmsMaintenanceInfo);
        }
        else if (source.GetBmsonStorageOwner()?.MaintenanceInfo is BMSFileMaintenanceInfo bmsonMaintenanceInfo)
        {
            resourceHealthWarningsIgnored = bmsonMaintenanceInfo.is_files_warning_ignored;
            resourceHealthMaintenanceSnapshot = ResourceHealthMaintenanceSnapshot.From(bmsonMaintenanceInfo);
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
            null,
            null,
            null,
            source.Subtitle,
            source.Resources,
            source.Stagefile,
            source.Backbmp,
            source.Banner,
            source.InstallDestination,
            source.InstallDestinationTitle,
            source.InstallDestinationArtist,
            [.. (source.InstallDestinationSuggestions ?? [])],
            [.. (source.Warnings ?? [])],
            source.WAVHealth,
            source.BGAHealth,
            source.MovieHealth,
            source.StagefileHealth,
            source.BannerHealth,
            source.BackbmpHealth,
            source.EncodingName,
            source.Score,
            source.Status,
            resourceHealthWarningsIgnored,
            resourceHealthMaintenanceSnapshot);
    }

    internal static ChartFile FromIdentitySnapshot(
        ChartFileKind kind,
        string path,
        string md5,
        string sha256)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return new ChartFile(
            kind,
            path,
            md5,
            sha256,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            null,
            string.Empty,
            string.Empty,
            null,
            null,
            null,
            null,
            null);
    }

    /// <summary>一時状態を投影し、取得済みリソース結果の寿命と内容を維持します。</summary>
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
            source.Resources,
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

    /// <summary>譜面情報を差し替え、リソースの再抽出や保存主体からの補完を行いません。</summary>
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
            source.Resources,
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

    /// <summary>状態を差し替え、同じ不変リソース結果を共有します。</summary>
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
            source.Resources,
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

    /// <summary>スコアを差し替え、同じ不変リソース結果を共有します。</summary>
    internal static ChartFile WithScore(ChartFile source, ChartScoreSnapshot score)
    {
        if (source == null)
        {
            return null;
        }

        score ??= source.Score;
        ChartFileStatus status = score?.IsLr2IrScoreUnsent == true
            ? source.Status | ChartFileStatus.SCORE_UNSENT
            : source.Status & ~ChartFileStatus.SCORE_UNSENT;
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
            source.Resources,
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
            score,
            status);
    }

    /// <summary>パッケージの一時状態を差し替え、抽出した原文と用途を同じ不変結果で維持します。</summary>
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

    /// <summary>パッケージの一時状態を差し替え、抽出した原文と用途を同じ不変結果で維持します。</summary>
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
            source.Resources,
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

    /// <summary>配置を差し替えます。譜面相対の不変リソース結果は共有し、配置に依存する評価は呼出元が行います。</summary>
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
            source.Resources,
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

    /// <summary>基本情報を維持して明示取得済みの不変リソース結果を受け渡します。</summary>
    internal static ChartFile WithResources(ChartFile source, System.Collections.Immutable.ImmutableList<ChartResourceReference> resources)
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
            resources,
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

    /// <summary>変更した一時値を引き継ぎ、現在値の不変リソース結果を共有します。</summary>
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
            current.Resources,
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

    /// <summary>BMSの基本情報を投影します。指定時は取得済みリソースを共有し、非取得指定や未取得では補完や読取りを行いません。</summary>
    internal static ChartFile FromBmsFile(
        BMSFile file,
        ChartFileLevelParsing levelParsing = ChartFileLevelParsing.Invariant,
        bool includeWarningSnapshot = true,
        bool includeResourceReferences = true,
        bool includeScoreSnapshot = false)
    {
        return ProjectBmsBasicInfo(file, includeResourceReferences ? file?.Resources : null,
            levelParsing, includeWarningSnapshot, includeScoreSnapshot);
    }

    /// <summary>基本情報を保存主体から投影し、呼出元が選んだ不変リソース結果をそのまま受け渡します。</summary>
    private static ChartFile ProjectBmsBasicInfo(BMSFile file, ImmutableList<ChartResourceReference> resources,
        ChartFileLevelParsing levelParsing, bool includeWarningSnapshot, bool includeScoreSnapshot)
    {
        if (file == null)
        {
            return null;
        }
        BMSFileMaintenanceInfo maintenanceInfo = file.HasValidMaintenanceInfoSnapshot
            ? file.TryGetMaintenanceInfoWithoutCreating()
            : null;
        ChartScoreSnapshot score = includeScoreSnapshot
            ? ChartScoreSnapshot.FromBmsFile(file)
            : ChartScoreSnapshot.NoScore(file.path);
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
            resources,
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
            status,
            maintenanceInfo?.is_files_warning_ignored == true,
            ResourceHealthMaintenanceSnapshot.From(maintenanceInfo));
    }

    /// <summary>bmsonの基本情報を投影します。指定時は取得済みリソースを共有し、非取得指定や未取得では補完や読取りを行いません。</summary>
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

    /// <summary>bmsonの基本情報を投影します。指定時は取得済みリソースを共有し、非取得指定や未取得では補完や読取りを行いません。</summary>
    internal static ChartFile FromBmsonSong(
        LR2SongDBExtended.bmson_song song,
        ChartFileTransientState transientState,
        bool includeWarningSnapshot = true,
        bool includeResourceReferences = true)
    {
        return ProjectBmsonBasicInfo(song, transientState, includeResourceReferences ? song?.Resources : null,
            includeWarningSnapshot);
    }

    /// <summary>基本情報と一時状態を投影し、呼出元が選んだ不変リソース結果をそのまま受け渡します。</summary>
    private static ChartFile ProjectBmsonBasicInfo(LR2SongDBExtended.bmson_song song,
        ChartFileTransientState transientState, ImmutableList<ChartResourceReference> resources, bool includeWarningSnapshot)
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
            resources,
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
            song.MaintenanceInfo?.encoding ?? transientState.EncodingName,
            null,
            ChartFileStatus.NONE,
            song.MaintenanceInfo?.is_files_warning_ignored == true,
            ResourceHealthMaintenanceSnapshot.From(song.MaintenanceInfo));
    }

    /// <summary>基本情報だけを保存主体の現在値へ更新し、未取得も含めsourceの不変リソース結果を共有します。</summary>
    internal static ChartFile FromStorageOwner(
        ChartFile source,
        ChartFileLevelParsing bmsLevelParsing = ChartFileLevelParsing.Invariant,
        bool includeWarningSnapshot = true,
        bool includeScoreSnapshot = false)
    {
        if (source == null)
        {
            return null;
        }

        BMSFile bmsFile = source.GetBmsStorageOwner();
        if (bmsFile != null)
        {
            return ProjectBmsBasicInfo(bmsFile, source.Resources, bmsLevelParsing, includeWarningSnapshot, includeScoreSnapshot);
        }

        LR2SongDBExtended.bmson_song bmsonSong = source.GetBmsonStorageOwner();
        return bmsonSong == null ? null : ProjectBmsonBasicInfo(bmsonSong, ChartFileTransientState.Empty, source.Resources, includeWarningSnapshot);
    }

    /// <summary>基本情報と一時状態を更新し、リソースはsourceの確定結果を共有します。保存主体から補完しません。</summary>
    internal static ChartFile FromStorageOwnerWithTransientState(
        ChartFile source,
        ChartFileTransientState transientState,
        ChartFileLevelParsing bmsLevelParsing = ChartFileLevelParsing.Invariant,
        bool includeWarningSnapshot = true,
        bool includeScoreSnapshot = false)
    {
        LR2SongDBExtended.bmson_song bmsonSong = source?.GetBmsonStorageOwner();
        if (bmsonSong != null)
        {
            return ProjectBmsonBasicInfo(bmsonSong, transientState, source.Resources, includeWarningSnapshot);
        }

        return WithTransientState(
            FromStorageOwner(source, bmsLevelParsing, includeWarningSnapshot, includeScoreSnapshot),
            transientState,
            includeWarningSnapshot);
    }

    /// <summary>一覧用の基本情報を更新し、sourceの短命なリソース結果を同じ寿命の読取り値へ受け渡します。</summary>
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
                bmsFile.subtitle,
                source.Resources);
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
            bmsonSong.subtitle,
            source.Resources);
    }

    internal static List<ChartFile> FromStorageRows(
        IEnumerable<BMSFile> bmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs,
        bool includeWarningSnapshot = false,
        bool requirePath = false,
        bool orderBmsonByPath = false,
        bool includeResourceReferences = true,
        bool includeScoreSnapshot = false)
    {
        List<ChartFile> charts = FromBmsFiles(bmsFiles, includeWarningSnapshot, requirePath, includeResourceReferences, includeScoreSnapshot);
        charts.AddRange(FromBmsonSongs(bmsonSongs, includeWarningSnapshot, requirePath, orderBmsonByPath, includeResourceReferences));
        return charts;
    }

    internal static List<ChartFile> FromBmsFiles(
        IEnumerable<BMSFile> files,
        bool includeWarningSnapshot = false,
        bool requirePath = false,
        bool includeResourceReferences = true,
        bool includeScoreSnapshot = false)
    {
        return [.. (files ?? [])
            .Where(file => file != null && (!requirePath || !string.IsNullOrWhiteSpace(file.path)))
            .Select(file => FromBmsFile(file, includeWarningSnapshot: includeWarningSnapshot, includeResourceReferences: includeResourceReferences, includeScoreSnapshot: includeScoreSnapshot))
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
        return FromBmsStorageOwnerIdentities(files, CancellationToken.None);
    }

    internal static List<ChartFile> FromBmsStorageOwnerIdentities(
        IEnumerable<BMSFile> files,
        CancellationToken cancellationToken)
    {
        List<ChartFile> charts = [];
        foreach (BMSFile file in files ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            ChartFile chart = FromBmsStorageOwnerIdentity(file);
            if (chart != null)
            {
                charts.Add(chart);
            }
        }
        return charts;
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
        return FromBmsonStorageOwnerIdentities(songs, CancellationToken.None);
    }

    internal static List<ChartFile> FromBmsonStorageOwnerIdentities(
        IEnumerable<LR2SongDBExtended.bmson_song> songs,
        CancellationToken cancellationToken)
    {
        List<ChartFile> charts = [];
        foreach (LR2SongDBExtended.bmson_song song in songs ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            ChartFile chart = FromBmsonStorageOwnerIdentity(song);
            if (chart != null)
            {
                charts.Add(chart);
            }
        }
        return charts;
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
