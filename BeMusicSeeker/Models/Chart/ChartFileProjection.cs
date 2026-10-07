using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.Models;

internal enum ChartFileLevelParsing
{
    Invariant,
    CurrentCultureThenInvariant
}

internal static class ChartFileProjection
{
    /// <summary>確定した保守値を共通現在値へ投影します。基本値と所持識別は継承します。</summary>
    internal static ChartFile WithMaintenance(ChartFile source, ResourceHealthMaintenanceSnapshot maintenance)
    {
        return source with
        {
            ResourceHealthMaintenanceSnapshot = maintenance,
            ResourceHealthWarningsIgnored = maintenance?.FilesWarningIgnored == true,
            WAVHealth = maintenance?.GetWavHealth(),
            BGAHealth = maintenance?.GetBgaHealth(),
            MovieHealth = maintenance?.GetMovieHealth(),
            StagefileHealth = maintenance?.GetStagefileHealth(),
            BannerHealth = maintenance?.GetBannerHealth(),
            BackbmpHealth = maintenance?.GetBackbmpHealth(),
            EncodingName = maintenance?.Encoding,
            Warnings = source.Kind == ChartFileKind.Bms && maintenance?.Lr2WarningFlags == null
                ? source.Warnings
                : [.. (source.Warnings ?? []).Where(warning => warning.Category != ChartWarningCategory.Lr2Compatibility)
                    .Concat(source.Kind == ChartFileKind.Bms ? Lr2CompatibilityWarningProjection.BuildWarnings(maintenance) : [])]
        };
    }

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

    /// <summary>捕捉時点の不変な共通読取り値を作ります。取得済みリソースの原文と解析状態は同じ結果で共有します。</summary>
    internal static ChartFile ToImmutableSnapshot(ChartFile source)
    {
        if (source == null)
        {
            return null;
        }

        bool resourceHealthWarningsIgnored = source.ResourceHealthWarningsIgnored;
        ResourceHealthMaintenanceSnapshot resourceHealthMaintenanceSnapshot = source.ResourceHealthMaintenanceSnapshot;
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
            resourceHealthMaintenanceSnapshot)
        {
            Token = source.Token,
            RawSubtitle = source.RawSubtitle,
            RawArtist = source.RawArtist,
            Subartist = source.Subartist,
            ChartName = source.ChartName,
            ModeHint = source.ModeHint,
            PreviewMusic = source.PreviewMusic,
            LastWriteTimeUtc = source.LastWriteTimeUtc,
            Favorite = source.Favorite,
            AddDate = source.AddDate,
            Txt = source.Txt,
            Date = source.Date,
            Difficulty = source.Difficulty,
            Judge = source.Judge
        };
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

        return source with
        {
            Subtitle = string.IsNullOrWhiteSpace(state.Subtitle) ? source.Subtitle : state.Subtitle,
            InstallDestination = state.HasInstallDestinationState ? state.InstallDestination : source.InstallDestination,
            InstallDestinationTitle = state.HasInstallDestinationState ? state.InstallDestinationTitle : source.InstallDestinationTitle,
            InstallDestinationArtist = state.HasInstallDestinationState ? state.InstallDestinationArtist : source.InstallDestinationArtist,
            InstallDestinationSuggestions = state.HasInstallDestinationState ? state.InstallDestinationSuggestions : source.InstallDestinationSuggestions,
            Warnings = SelectTransientWarnings(source.Warnings, state, includeWarningSnapshot),
            WAVHealth = state.WAVHealth ?? source.WAVHealth,
            BGAHealth = state.BGAHealth ?? source.BGAHealth,
            MovieHealth = state.MovieHealth ?? source.MovieHealth,
            StagefileHealth = state.StagefileHealth ?? source.StagefileHealth,
            BannerHealth = state.BannerHealth ?? source.BannerHealth,
            BackbmpHealth = state.BackbmpHealth ?? source.BackbmpHealth,
            EncodingName = string.IsNullOrWhiteSpace(state.EncodingName) ? source.EncodingName : state.EncodingName
        };
    }

    /// <summary>譜面情報を差し替え、リソースの再抽出や保存主体からの補完を行いません。</summary>
    internal static ChartFile WithChartInfo(ChartFile source, BeMusicSeeker.Models.ChartDetails chartInfo)
    {
        if (source == null)
        {
            return null;
        }

        return source with
        {
            ChartInfo = chartInfo,
            ChartInfoDisplay = ChartInfoDisplaySnapshot.FromChartInfo(chartInfo)
        };
    }

    /// <summary>状態を差し替え、同じ不変リソース結果を共有します。</summary>
    internal static ChartFile WithStatus(ChartFile source, ChartFileStatus status)
    {
        if (source == null)
        {
            return null;
        }

        return source with
        {
            Status = status
        };
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
        return source with
        {
            Score = score,
            Status = status
        };
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

        return source with
        {
            InstallDestination = installDestination ?? string.Empty,
            InstallDestinationTitle = installDestinationTitle ?? string.Empty,
            InstallDestinationArtist = installDestinationArtist ?? string.Empty,
            InstallDestinationSuggestions = installDestinationSuggestions ?? [],
            Warnings = warnings ?? []
        };
    }

    /// <summary>配置を差し替えます。譜面相対の不変リソース結果は共有し、配置に依存する評価は呼出元が行います。</summary>
    internal static ChartFile WithPath(ChartFile source, string path)
    {
        if (source == null)
        {
            return null;
        }

        return source with
        {
            Path = string.IsNullOrWhiteSpace(path) ? null : path,
            Folder = GetDisplayFolderFromPath(path)
        };
    }

    /// <summary>基本情報を維持して明示取得済みの不変リソース結果を受け渡します。</summary>
    internal static ChartFile WithResources(ChartFile source, System.Collections.Immutable.ImmutableList<ChartResourceReference> resources)
    {
        if (source == null)
        {
            return null;
        }

        return source with
        {
            Resources = resources
        };
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

        return current with
        {
            Subtitle = SelectStringOverride(current.Subtitle, overrideChart.Subtitle, baseline.Subtitle),
            InstallDestination = SelectStringOverride(current.InstallDestination, overrideChart.InstallDestination, baseline.InstallDestination),
            InstallDestinationTitle = SelectStringOverride(current.InstallDestinationTitle, overrideChart.InstallDestinationTitle, baseline.InstallDestinationTitle),
            InstallDestinationArtist = SelectStringOverride(current.InstallDestinationArtist, overrideChart.InstallDestinationArtist, baseline.InstallDestinationArtist),
            InstallDestinationSuggestions = SelectStringListOverride(current.InstallDestinationSuggestions, overrideChart.InstallDestinationSuggestions, baseline.InstallDestinationSuggestions),
            Warnings = includeWarningSnapshot ? SelectWarningOverride(current.Warnings, overrideChart.Warnings, baseline.Warnings) : [],
            WAVHealth = SelectNullableOverride(current.WAVHealth, overrideChart.WAVHealth, baseline.WAVHealth),
            BGAHealth = SelectNullableOverride(current.BGAHealth, overrideChart.BGAHealth, baseline.BGAHealth),
            MovieHealth = SelectNullableOverride(current.MovieHealth, overrideChart.MovieHealth, baseline.MovieHealth),
            StagefileHealth = SelectNullableOverride(current.StagefileHealth, overrideChart.StagefileHealth, baseline.StagefileHealth),
            BannerHealth = SelectNullableOverride(current.BannerHealth, overrideChart.BannerHealth, baseline.BannerHealth),
            BackbmpHealth = SelectNullableOverride(current.BackbmpHealth, overrideChart.BackbmpHealth, baseline.BackbmpHealth),
            EncodingName = SelectStringOverride(current.EncodingName, overrideChart.EncodingName, baseline.EncodingName)
        };
    }

    /// <summary>共通基本値を保持し、要求された表示値だけを捕捉します。現在値や保存行へ遡りません。</summary>
    internal static ChartFile CaptureBasicSnapshot(
        ChartFile source,
        ChartFileLevelParsing bmsLevelParsing = ChartFileLevelParsing.Invariant,
        bool includeWarningSnapshot = true,
        bool includeScoreSnapshot = false)
    {
        return source == null ? null : source with
        {
            Warnings = includeWarningSnapshot ? source.Warnings : [],
            Score = includeScoreSnapshot ? source.Score : ChartScoreSnapshot.NoScore(source.Path)
        };
    }

    /// <summary>捕捉済み共通値に画面の一時投影だけを重ねます。</summary>
    internal static ChartFile CaptureBasicSnapshotWithTransientState(
        ChartFile source,
        ChartFileTransientState transientState,
        ChartFileLevelParsing bmsLevelParsing = ChartFileLevelParsing.Invariant,
        bool includeWarningSnapshot = true,
        bool includeScoreSnapshot = false)
    {
        return WithTransientState(CaptureBasicSnapshot(source, bmsLevelParsing,
            includeWarningSnapshot, includeScoreSnapshot), transientState, includeWarningSnapshot);
    }

    /// <summary>既に捕捉した一覧の共通基本値をそのまま返します。</summary>
    internal static ChartFile CaptureListSnapshot(ChartFile source,
        ChartFileLevelParsing bmsLevelParsing = ChartFileLevelParsing.Invariant) => source;

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
        BeMusicSeeker.Models.ChartDetails chartInfo)
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
            chartInfo);
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
