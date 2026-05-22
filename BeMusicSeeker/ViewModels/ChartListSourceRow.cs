using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.ViewModels;

internal enum ChartListSourceProjectionMode
{
    PreserveSourceProjection,
    OwnerBacked
}

internal sealed class ChartListSourceRow
{
    private readonly Func<ChartListSourceRow, ResourceHealthWarningProjection> resourceHealthProjectionProvider;

    private readonly Func<ChartListSourceRow, PlaylistReferenceDisplay> playlistReferenceDisplayProvider;

    private readonly Func<ChartFile, bool, ChartFileTransientState> chartTransientStateProvider;

    private readonly ChartFile identityChart;

    private readonly ChartFile sourceChart;

    private readonly bool hasSourceChartProjection;

    private ChartListSourceRow(
        ChartFile sourceChart,
        bool hasSourceChartProjection,
        Func<ChartListSourceRow, ResourceHealthWarningProjection> resourceHealthProjectionProvider,
        Func<ChartListSourceRow, PlaylistReferenceDisplay> playlistReferenceDisplayProvider,
        Func<ChartFile, bool, ChartFileTransientState> chartTransientStateProvider)
    {
        this.sourceChart = sourceChart ?? throw new ArgumentNullException(nameof(sourceChart));
        this.hasSourceChartProjection = hasSourceChartProjection;
        this.resourceHealthProjectionProvider = resourceHealthProjectionProvider;
        this.playlistReferenceDisplayProvider = playlistReferenceDisplayProvider;
        this.chartTransientStateProvider = chartTransientStateProvider;
        identityChart = CreateChartFile(includeWarningSnapshot: false);
    }

    internal ChartFile Chart => CreateChartFile();

    internal bool HasSourceChartProjection => hasSourceChartProjection;

    internal string Title => identityChart?.Title ?? string.Empty;

    internal string Artist => identityChart?.Artist ?? string.Empty;

    internal string Genre => identityChart?.Genre ?? string.Empty;

    internal string Folder => identityChart?.Folder ?? string.Empty;

    internal string Path => identityChart?.Path ?? string.Empty;

    internal int? Mode => identityChart?.Mode;

    internal string WarningDigestText => ChartWarningProjectionFormatter.BuildDigestText(
        Chart,
        GetResourceHealthProjection(),
        resourceHealthProjectionProvider != null);

    internal string Tag => identityChart?.Tag ?? string.Empty;

    internal string Level => identityChart?.LevelText ?? string.Empty;

    internal double? LevelValue => identityChart?.Level;

    internal string Hash => identityChart?.Md5 ?? string.Empty;

    internal string Sha256 => identityChart?.Sha256 ?? string.Empty;

    internal string InstallDestination => CreateChartFile(includeWarningSnapshot: false)?.InstallDestination ?? string.Empty;

    internal string InstallDestinationTitle => CreateChartFile(includeWarningSnapshot: false)?.InstallDestinationTitle ?? string.Empty;

    internal string InstallDestinationArtist => CreateChartFile(includeWarningSnapshot: false)?.InstallDestinationArtist ?? string.Empty;

    internal string RefTablesSymbols => GetPlaylistReferenceDisplay().Symbols;

    internal string RefTablesNames => GetPlaylistReferenceDisplay().Names;

    internal int? WAVHealth => Chart?.WAVHealth;

    internal int? BGAHealth => Chart?.BGAHealth;

    internal int? MovieHealth => Chart?.MovieHealth;

    internal string EncodingName => Chart?.EncodingName ?? string.Empty;

    internal ClearType Clear => Chart?.Score?.Clear ?? ChartScoreSnapshot.MissingChart.Clear;

    internal RankType Rank => Chart?.Score?.Rank ?? RankType.INVALID;

    internal double? RateDouble => Chart?.Score?.RateDouble;

    internal int? Rate => Chart?.Score?.Rate;

    internal int? Score => Chart?.Score?.Score;

    internal int? TotalNotes => Chart?.Score?.TotalNotes;

    internal int? MaxCombo => Chart?.Score?.MaxCombo;

    internal int? MinBp => Chart?.Score?.MinBp;

    internal int? Ranking => Chart?.Score?.Ranking;

    internal int? RankingNum => Chart?.Score?.RankingNum;

    internal string RankingString => Chart?.Score?.RankingString ?? string.Empty;

    internal DateTime? RankingLastUpdate => Chart?.Score?.RankingLastUpdate;

    internal double? StdDevVal => Chart?.Score?.StdDevVal;

    internal double? ScoreDifficulty => Chart?.Score?.ScoreDifficulty;

    internal LR2SongDBExtended.chart_info ChartInfo => ResolveChartInfoProjection();

    private ChartInfoDisplaySnapshot ChartInfoDisplay => ChartInfoDisplaySnapshot.FromChartInfo(ChartInfo);

    internal double? ChartLevelSortKey => ChartInfoDisplay.ChartLevelSortKey;

    internal int? ChartDifficultySortKey => ChartInfoDisplay.ChartDifficultySortKey;

    internal double? ChartMainBpmSortKey => ChartInfoDisplay.ChartMainBpmSortKey;

    internal double? ChartMaxBpmSortKey => ChartInfoDisplay.ChartMaxBpmSortKey;

    internal double? ChartMinBpmSortKey => ChartInfoDisplay.ChartMinBpmSortKey;

    internal int? ChartDurationSortKey => ChartInfoDisplay.ChartDurationSortKey;

    internal int? ChartJudgeSortKey => ChartInfoDisplay.ChartJudgeSortKey;

    internal int? ChartFeatureSortKey => ChartInfoDisplay.ChartFeatureSortKey;

    internal int? ChartNotes => ChartInfoDisplay.ChartNotes;

    internal int? ChartLongNotes => ChartInfoDisplay.ChartLongNotes;

    internal int? ChartScratchNotes => ChartInfoDisplay.ChartScratchNotes;

    internal double? ChartTotalSortKey => ChartInfoDisplay.ChartTotalSortKey;

    internal double? ChartTotalPerNoteSortKey => ChartInfoDisplay.ChartTotalPerNoteSortKey;

    internal double? ChartDensitySortKey => ChartInfoDisplay.ChartDensitySortKey;

    internal double? ChartPeakDensitySortKey => ChartInfoDisplay.ChartPeakDensitySortKey;

    internal double? ChartEndDensitySortKey => ChartInfoDisplay.ChartEndDensitySortKey;

    internal int? ChartSoflanCount => ChartInfoDisplay.ChartSoflanCount;

    private ChartFile CreateChartFile(bool includeWarningSnapshot = true)
    {
        if (sourceChart != null)
        {
            ChartFile identityOwnerChart = ChartFileProjection.FromStorageOwner(sourceChart, includeWarningSnapshot: false);
            if (identityOwnerChart != null)
            {
                ChartFileTransientState transientState = GetChartTransientState(identityOwnerChart, includeWarningSnapshot);
                ChartFile currentChart = ChartFileProjection.FromStorageOwnerWithTransientState(
                    sourceChart,
                    transientState,
                    includeWarningSnapshot: includeWarningSnapshot);
                return ApplySourceProjectionWarnings(currentChart, transientState, includeWarningSnapshot);
            }
            return includeWarningSnapshot ? sourceChart : ChartFileProjection.WithWarnings(sourceChart, []);
        }
        return null;
    }

    private LR2SongDBExtended.chart_info ResolveChartInfoProjection()
    {
        return ChartFileProjection.ResolveCurrentStorageOwnerChartInfo(sourceChart);
    }

    private static bool HasWarningProjection(ChartFileTransientState state)
    {
        return state?.HasWarningProjection == true || state?.HasInstallEstimationWarningProjection == true;
    }

    private ChartFile ApplySourceProjectionWarnings(ChartFile currentChart, ChartFileTransientState transientState, bool includeWarningSnapshot)
    {
        if (!includeWarningSnapshot || currentChart == null || (sourceChart?.Warnings?.Count ?? 0) == 0)
        {
            return currentChart;
        }
        if (transientState?.HasWarningProjection == true)
        {
            return currentChart;
        }
        if (transientState?.HasInstallEstimationWarningProjection == true)
        {
            return ChartFileProjection.WithWarnings(
                currentChart,
                MergeNonInstallSourceProjectionWarnings(currentChart.Warnings, sourceChart.Warnings));
        }
        return ChartFileProjection.WithWarnings(currentChart, sourceChart.Warnings);
    }

    private static IReadOnlyList<ChartWarning> MergeNonInstallSourceProjectionWarnings(
        IReadOnlyList<ChartWarning> currentWarnings,
        IReadOnlyList<ChartWarning> sourceWarnings)
    {
        var warningsByKind = new Dictionary<ChartWarningKind, ChartWarning>();
        foreach (ChartWarning warning in currentWarnings ?? [])
        {
            if (warning != null)
            {
                warningsByKind[warning.Kind] = warning;
            }
        }
        foreach (ChartWarning warning in sourceWarnings ?? [])
        {
            if (warning != null && warning.Category != ChartWarningCategory.InstallEstimation && !warningsByKind.ContainsKey(warning.Kind))
            {
                warningsByKind[warning.Kind] = warning;
            }
        }
        return [.. warningsByKind.Values.OrderBy(warning => warning.Priority).ThenBy(warning => warning.Kind)];
    }

    private ChartFileTransientState GetChartTransientState(ChartFile chart, bool includeWarningSnapshot)
    {
        if (chart == null)
        {
            return ChartFileTransientState.Empty;
        }
        ChartFileTransientState providerState = chartTransientStateProvider?.Invoke(chart, includeWarningSnapshot);
        if (providerState?.HasState == true)
        {
            if (includeWarningSnapshot
                && !HasWarningProjection(providerState)
                && (providerState.Warnings?.Count ?? 0) == 0
                && (sourceChart?.Warnings?.Count ?? 0) > 0)
            {
                return ChartFileTransientState.FromChartFile(
                    ChartFileProjection.WithPackageState(
                        sourceChart,
                        providerState.HasInstallDestinationState ? providerState.InstallDestination : sourceChart.InstallDestination,
                        providerState.HasInstallDestinationState ? providerState.InstallDestinationTitle : sourceChart.InstallDestinationTitle,
                        providerState.HasInstallDestinationState ? providerState.InstallDestinationArtist : sourceChart.InstallDestinationArtist,
                        providerState.HasInstallDestinationState ? providerState.InstallDestinationSuggestions : sourceChart.InstallDestinationSuggestions,
                        sourceChart.Warnings));
            }
            return providerState;
        }
        return sourceChart == null ? ChartFileTransientState.Empty : ChartFileTransientState.FromChartFile(sourceChart, includeWarningSnapshot);
    }

    internal static ChartListSourceRow FromChartFile(
        ChartFile chart,
        ChartListSourceProjectionMode projectionMode = ChartListSourceProjectionMode.PreserveSourceProjection,
        Func<ChartListSourceRow, ResourceHealthWarningProjection> resourceHealthProjectionProvider = null,
        Func<ChartListSourceRow, PlaylistReferenceDisplay> playlistReferenceDisplayProvider = null,
        Func<ChartFile, bool, ChartFileTransientState> chartTransientStateProvider = null)
    {
        return chart == null ? null : new ChartListSourceRow(
            chart,
            projectionMode == ChartListSourceProjectionMode.PreserveSourceProjection,
            resourceHealthProjectionProvider,
            playlistReferenceDisplayProvider,
            chartTransientStateProvider);
    }

    internal static List<ChartListSourceRow> BuildStandardLibraryRows(
        IEnumerable<ChartFile> charts,
        Func<ChartListSourceRow, ResourceHealthWarningProjection> resourceHealthProjectionProvider = null,
        Func<ChartListSourceRow, PlaylistReferenceDisplay> playlistReferenceDisplayProvider = null,
        Func<ChartFile, bool, ChartFileTransientState> chartTransientStateProvider = null)
    {
        return BuildStandardLibraryRows(
            charts,
            ChartListSourceProjectionMode.PreserveSourceProjection,
            resourceHealthProjectionProvider,
            playlistReferenceDisplayProvider,
            chartTransientStateProvider);
    }

    internal static List<ChartListSourceRow> BuildStandardLibraryRows(
        IEnumerable<ChartFile> charts,
        ChartListSourceProjectionMode projectionMode,
        Func<ChartListSourceRow, ResourceHealthWarningProjection> resourceHealthProjectionProvider = null,
        Func<ChartListSourceRow, PlaylistReferenceDisplay> playlistReferenceDisplayProvider = null,
        Func<ChartFile, bool, ChartFileTransientState> chartTransientStateProvider = null)
    {
        return [.. (charts ?? [])
            .Select(chart => FromChartFile(chart, projectionMode, resourceHealthProjectionProvider, playlistReferenceDisplayProvider, chartTransientStateProvider))
            .Where(row => row != null)];
    }

    private ResourceHealthWarningProjection GetResourceHealthProjection()
    {
        return resourceHealthProjectionProvider == null
            ? ResourceHealthWarningProjection.Empty
            : resourceHealthProjectionProvider.Invoke(this) ?? ResourceHealthWarningProjection.Empty;
    }

    private PlaylistReferenceDisplay GetPlaylistReferenceDisplay()
    {
        return playlistReferenceDisplayProvider == null
            ? PlaylistReferenceDisplay.Empty
            : playlistReferenceDisplayProvider.Invoke(this) ?? PlaylistReferenceDisplay.Empty;
    }
}
