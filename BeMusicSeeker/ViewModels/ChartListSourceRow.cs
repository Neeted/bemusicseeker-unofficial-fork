using System;
using System.Collections.Generic;
using System.Globalization;
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

    private readonly Func<ChartFile, LR2SongDBExtended.chart_info> chartInfoProjectionProvider;

    private readonly Func<ChartListSourceRow, LR2SongDBExtended.chart_info> chartInfoRowProjectionProvider;

    private readonly Func<int> chartInfoProjectionVersionProvider;

    private readonly Func<int> scoreSnapshotVersionProvider;

    private readonly Func<ChartListSourceRow, ChartScoreSnapshot> scoreSnapshotProjectionProvider;

    private readonly ChartFile identityChart;

    private readonly ChartFile sourceChart;

    private readonly BMSFile bmsStorageOwner;

    private readonly LR2SongDBExtended.bmson_song bmsonStorageOwner;

    private readonly PackageChartEntry packageEntry;

    private readonly bool hasSourceChartProjection;

    private readonly bool hideResourceHealthDigestWhenInstallDestinationSet;

    private ChartFile cachedChart;

    private bool cachedChartValid;

    private int cachedChartPackageProjectionVersion;

    private ChartInfoDisplaySnapshot cachedChartInfoDisplay;

    private bool cachedChartInfoDisplayValid;

    private int cachedChartInfoDisplayVersion;

    private ChartScoreSnapshot cachedScoreSnapshot;

    private bool cachedScoreSnapshotValid;

    private int cachedScoreSnapshotVersion;

    private ChartListSourceRow(
        ChartFile sourceChart,
        bool hasSourceChartProjection,
        Func<ChartListSourceRow, ResourceHealthWarningProjection> resourceHealthProjectionProvider,
        Func<ChartListSourceRow, PlaylistReferenceDisplay> playlistReferenceDisplayProvider,
        Func<ChartFile, bool, ChartFileTransientState> chartTransientStateProvider,
        Func<ChartFile, LR2SongDBExtended.chart_info> chartInfoProjectionProvider,
        Func<ChartListSourceRow, LR2SongDBExtended.chart_info> chartInfoRowProjectionProvider = null,
        Func<int> chartInfoProjectionVersionProvider = null,
        Func<int> scoreSnapshotVersionProvider = null,
        Func<ChartListSourceRow, ChartScoreSnapshot> scoreSnapshotProjectionProvider = null,
        PackageChartEntry packageEntry = null,
        bool hideResourceHealthDigestWhenInstallDestinationSet = true)
    {
        this.sourceChart = sourceChart ?? throw new ArgumentNullException(nameof(sourceChart));
        this.hasSourceChartProjection = hasSourceChartProjection;
        this.resourceHealthProjectionProvider = resourceHealthProjectionProvider;
        this.playlistReferenceDisplayProvider = playlistReferenceDisplayProvider;
        this.chartTransientStateProvider = chartTransientStateProvider;
        this.chartInfoProjectionProvider = chartInfoProjectionProvider;
        this.chartInfoRowProjectionProvider = chartInfoRowProjectionProvider;
        this.chartInfoProjectionVersionProvider = chartInfoProjectionVersionProvider;
        this.scoreSnapshotVersionProvider = scoreSnapshotVersionProvider;
        this.scoreSnapshotProjectionProvider = scoreSnapshotProjectionProvider;
        this.packageEntry = packageEntry;
        this.hideResourceHealthDigestWhenInstallDestinationSet = hideResourceHealthDigestWhenInstallDestinationSet;
        bmsStorageOwner = sourceChart.GetBmsStorageOwner();
        bmsonStorageOwner = sourceChart.GetBmsonStorageOwner();
        identityChart = CreateIdentityChartFile();
    }

    private ChartListSourceRow(
        BMSFile bmsStorageOwner,
        LR2SongDBExtended.bmson_song bmsonStorageOwner,
        Func<ChartListSourceRow, ResourceHealthWarningProjection> resourceHealthProjectionProvider,
        Func<ChartListSourceRow, PlaylistReferenceDisplay> playlistReferenceDisplayProvider,
        Func<ChartFile, bool, ChartFileTransientState> chartTransientStateProvider,
        Func<ChartFile, LR2SongDBExtended.chart_info> chartInfoProjectionProvider,
        Func<ChartListSourceRow, LR2SongDBExtended.chart_info> chartInfoRowProjectionProvider = null,
        Func<int> chartInfoProjectionVersionProvider = null,
        Func<int> scoreSnapshotVersionProvider = null,
        Func<ChartListSourceRow, ChartScoreSnapshot> scoreSnapshotProjectionProvider = null)
    {
        this.bmsStorageOwner = bmsStorageOwner;
        this.bmsonStorageOwner = bmsonStorageOwner;
        this.resourceHealthProjectionProvider = resourceHealthProjectionProvider;
        this.playlistReferenceDisplayProvider = playlistReferenceDisplayProvider;
        this.chartTransientStateProvider = chartTransientStateProvider;
        this.chartInfoProjectionProvider = chartInfoProjectionProvider;
        this.chartInfoRowProjectionProvider = chartInfoRowProjectionProvider;
        this.chartInfoProjectionVersionProvider = chartInfoProjectionVersionProvider;
        this.scoreSnapshotVersionProvider = scoreSnapshotVersionProvider;
        this.scoreSnapshotProjectionProvider = scoreSnapshotProjectionProvider;
        hideResourceHealthDigestWhenInstallDestinationSet = true;
    }

    internal ChartFile Chart
    {
        get
        {
            if (packageEntry == null)
            {
                return CreateChartFile();
            }

            int projectionVersion = packageEntry?.ProjectionVersion ?? 0;
            if (!cachedChartValid || cachedChartPackageProjectionVersion != projectionVersion)
            {
                cachedChart = CreateChartFile();
                cachedChartPackageProjectionVersion = projectionVersion;
                cachedChartValid = true;
            }
            return cachedChart;
        }
    }

    internal PackageChartEntry PackageEntry => packageEntry;

    internal ChartFileKind Kind => bmsStorageOwner != null
        ? ChartFileKind.Bms
        : bmsonStorageOwner != null
            ? ChartFileKind.Bmson
            : IdentityChart?.Kind ?? ChartFileKind.Bms;

    internal bool HasSourceChartProjection => hasSourceChartProjection;

    internal bool HideResourceHealthDigestWhenInstallDestinationSet => hideResourceHealthDigestWhenInstallDestinationSet;

    private ChartFile IdentityChart => packageEntry?.Chart ?? identityChart;

    internal string Title => bmsStorageOwner?.Title
        ?? (bmsonStorageOwner == null ? IdentityChart?.Title : BmsonSongParser.ComposeDisplayTitle(bmsonStorageOwner))
        ?? string.Empty;

    internal string Artist => bmsStorageOwner?.Artist
        ?? bmsonStorageOwner?.artist
        ?? IdentityChart?.Artist
        ?? string.Empty;

    internal string Genre => bmsStorageOwner?.genre
        ?? bmsonStorageOwner?.genre
        ?? IdentityChart?.Genre
        ?? string.Empty;

    internal string Folder => bmsStorageOwner != null
        ? GetDisplayFolderFromPath(bmsStorageOwner.path)
        : (bmsonStorageOwner == null ? IdentityChart?.Folder ?? string.Empty : BmsonSongParser.ComposeDisplayFolder(bmsonStorageOwner));

    internal string Path => bmsStorageOwner?.path
        ?? bmsonStorageOwner?.path
        ?? IdentityChart?.Path
        ?? string.Empty;

    internal int? Mode => bmsStorageOwner?.mode
        ?? (bmsonStorageOwner == null ? IdentityChart?.Mode : BmsonSongParser.ResolvePlaylistMode(bmsonStorageOwner.mode_hint));

    internal string WarningDigestText => ChartWarningProjectionFormatter.BuildDigestText(
        CreateChartFile(includeWarningSnapshot: true, includeResourceReferences: false, includeScoreSnapshot: false),
        GetResourceHealthProjection(),
        resourceHealthProjectionProvider != null,
        hideResourceHealthDigestWhenInstallDestinationSet);

    internal string Tag => bmsStorageOwner?.tag
        ?? IdentityChart?.Tag
        ?? string.Empty;

    internal string Level => bmsStorageOwner != null
        ? FormatBmsLevelText(bmsStorageOwner)
        : (bmsonStorageOwner == null ? IdentityChart?.LevelText ?? string.Empty : FormatNullableDouble(bmsonStorageOwner.level));

    internal double? LevelValue => bmsStorageOwner?.level
        ?? bmsonStorageOwner?.level
        ?? IdentityChart?.Level;

    internal string Hash => bmsStorageOwner?.hash
        ?? bmsonStorageOwner?.md5
        ?? IdentityChart?.Md5
        ?? string.Empty;

    internal string Sha256 => bmsStorageOwner?.sha256
        ?? bmsonStorageOwner?.sha256
        ?? IdentityChart?.Sha256
        ?? string.Empty;

    internal string InstallDestination => CreateChartFile(includeWarningSnapshot: false, includeResourceReferences: false, includeScoreSnapshot: false)?.InstallDestination ?? string.Empty;

    internal string InstallDestinationTitle => CreateChartFile(includeWarningSnapshot: false, includeResourceReferences: false, includeScoreSnapshot: false)?.InstallDestinationTitle ?? string.Empty;

    internal string InstallDestinationArtist => CreateChartFile(includeWarningSnapshot: false, includeResourceReferences: false, includeScoreSnapshot: false)?.InstallDestinationArtist ?? string.Empty;

    internal string RefTablesSymbols => GetPlaylistReferenceDisplay().Symbols;

    internal string RefTablesNames => GetPlaylistReferenceDisplay().Names;

    internal int? WAVHealth => CreateChartFile(includeWarningSnapshot: false, includeResourceReferences: false, includeScoreSnapshot: false)?.WAVHealth;

    internal int? BGAHealth => CreateChartFile(includeWarningSnapshot: false, includeResourceReferences: false, includeScoreSnapshot: false)?.BGAHealth;

    internal int? MovieHealth => CreateChartFile(includeWarningSnapshot: false, includeResourceReferences: false, includeScoreSnapshot: false)?.MovieHealth;

    internal string EncodingName => CreateChartFile(includeWarningSnapshot: false, includeResourceReferences: false, includeScoreSnapshot: false)?.EncodingName ?? string.Empty;

    internal ClearType Clear => ScoreSnapshot.Clear;

    internal RankType Rank => ScoreSnapshot.Rank;

    internal double? RateDouble => ScoreSnapshot.RateDouble;

    internal int? Rate => ScoreSnapshot.Rate;

    internal int? Score => ScoreSnapshot.Score;

    internal int? TotalNotes => ScoreSnapshot.TotalNotes;

    internal int? MaxCombo => ScoreSnapshot.MaxCombo;

    internal int? MinBp => ScoreSnapshot.MinBp;

    internal int? Ranking => ScoreSnapshot.Ranking;

    internal int? RankingNum => ScoreSnapshot.RankingNum;

    internal string RankingString => ScoreSnapshot.RankingString;

    internal DateTime? RankingLastUpdate => ScoreSnapshot.RankingLastUpdate;

    internal double? StdDevVal => ScoreSnapshot.StdDevVal;

    internal double? ScoreDifficulty => ScoreSnapshot.ScoreDifficulty;

    private ChartScoreSnapshot ScoreSnapshot => GetScoreSnapshot();

    internal LR2SongDBExtended.chart_info ChartInfo => ResolveChartInfoProjection();

    private ChartInfoDisplaySnapshot ChartInfoDisplay => GetChartInfoDisplay();

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

    private ChartFile CreateChartFile(bool includeWarningSnapshot = true, bool includeResourceReferences = true, bool includeScoreSnapshot = true)
    {
        if (sourceChart == null)
        {
            return CreateStorageOwnerChartFile(includeWarningSnapshot, includeResourceReferences, includeScoreSnapshot);
        }

        ChartFile currentSource = packageEntry?.Chart ?? sourceChart;
        if (currentSource != null)
        {
            if (packageEntry != null)
            {
                return ApplyScoreProjection(ApplyChartInfoProjection(includeWarningSnapshot ? currentSource : ChartFileProjection.WithWarnings(currentSource, [])), includeScoreSnapshot);
            }
            ChartFile identityOwnerChart = ChartFileProjection.FromStorageOwner(
                currentSource,
                includeWarningSnapshot: false,
                includeResourceReferences: includeResourceReferences,
                includeScoreSnapshot: false);
            if (identityOwnerChart != null)
            {
                ChartFileTransientState transientState = GetChartTransientState(identityOwnerChart, includeWarningSnapshot, currentSource);
                ChartFile currentChart = ChartFileProjection.FromStorageOwnerWithTransientState(
                    currentSource,
                    transientState,
                    includeWarningSnapshot: includeWarningSnapshot,
                    includeResourceReferences: includeResourceReferences,
                    includeScoreSnapshot: ShouldIncludeStorageOwnerScoreSnapshot(includeScoreSnapshot));
                return ApplyScoreProjection(ApplySourceProjectionWarnings(ApplyChartInfoProjection(currentChart), transientState, includeWarningSnapshot, currentSource), includeScoreSnapshot);
            }
            return ApplyScoreProjection(ApplyChartInfoProjection(includeWarningSnapshot ? currentSource : ChartFileProjection.WithWarnings(currentSource, [])), includeScoreSnapshot);
        }
        return null;
    }

    private ChartFile CreateStorageOwnerChartFile(bool includeWarningSnapshot, bool includeResourceReferences, bool includeScoreSnapshot)
    {
        ChartFile ownerIdentityChart = CreateStorageOwnerIdentityChart();
        if (ownerIdentityChart == null)
        {
            return null;
        }

        ChartFileTransientState transientState = GetChartTransientState(ownerIdentityChart, includeWarningSnapshot, ownerIdentityChart);
        ChartFile currentChart = ChartFileProjection.FromStorageOwnerWithTransientState(
            ownerIdentityChart,
            transientState,
            includeWarningSnapshot: includeWarningSnapshot,
            includeResourceReferences: includeResourceReferences,
            includeScoreSnapshot: ShouldIncludeStorageOwnerScoreSnapshot(includeScoreSnapshot));
        return ApplyScoreProjection(ApplySourceProjectionWarnings(ApplyChartInfoProjection(currentChart), transientState, includeWarningSnapshot, ownerIdentityChart), includeScoreSnapshot: includeScoreSnapshot);
    }

    private ChartFile CreateStorageOwnerIdentityChart()
    {
        if (bmsStorageOwner != null)
        {
            return ChartFileProjection.FromBmsStorageOwnerIdentity(bmsStorageOwner);
        }
        return bmsonStorageOwner == null ? null : ChartFileProjection.FromBmsonStorageOwnerIdentity(bmsonStorageOwner);
    }

    private ChartFile CreateIdentityChartFile()
    {
        if (sourceChart == null)
        {
            return null;
        }
        if (!hasSourceChartProjection)
        {
            return sourceChart;
        }

        return ChartFileProjection.FromStorageOwnerListIdentity(sourceChart)
            ?? (hasSourceChartProjection ? sourceChart : ChartFileProjection.WithWarnings(sourceChart, []));
    }

    private LR2SongDBExtended.chart_info ResolveChartInfoProjection()
    {
        LR2SongDBExtended.chart_info rowResolved = chartInfoRowProjectionProvider?.Invoke(this);
        if (rowResolved != null)
        {
            return rowResolved;
        }

        ChartFile chart = sourceChart ?? CreateStorageOwnerIdentityChart();
        return ResolveChartInfoFromProvider(chart)
            ?? chart?.ChartInfo;
    }

    private ChartScoreSnapshot ResolveScoreSnapshot()
    {
        if (packageEntry != null)
        {
            return Chart?.Score ?? ChartScoreSnapshot.MissingChart;
        }

        ChartScoreSnapshot resolved = scoreSnapshotProjectionProvider?.Invoke(this);
        if (resolved != null)
        {
            return resolved;
        }
        if (bmsStorageOwner != null)
        {
            return ChartScoreSnapshot.FromBmsFile(bmsStorageOwner);
        }
        if (bmsonStorageOwner != null)
        {
            return ChartScoreSnapshot.NoScore(bmsonStorageOwner.path);
        }
        return Chart?.Score ?? ChartScoreSnapshot.MissingChart;
    }

    private ChartFile ApplyScoreProjection(ChartFile chart, bool includeScoreSnapshot)
    {
        if (!includeScoreSnapshot || chart == null || scoreSnapshotProjectionProvider == null)
        {
            return chart;
        }
        return ChartFileProjection.WithScore(chart, GetScoreSnapshot());
    }

    private bool ShouldIncludeStorageOwnerScoreSnapshot(bool includeScoreSnapshot)
    {
        return includeScoreSnapshot && scoreSnapshotProjectionProvider == null;
    }

    private static bool HasWarningProjection(ChartFileTransientState state)
    {
        return state?.HasWarningProjection == true || state?.HasInstallEstimationWarningProjection == true;
    }

    private ChartFile ApplySourceProjectionWarnings(
        ChartFile currentChart,
        ChartFileTransientState transientState,
        bool includeWarningSnapshot,
        ChartFile projectionSource)
    {
        projectionSource ??= sourceChart;
        if (!includeWarningSnapshot || currentChart == null || (projectionSource?.Warnings?.Count ?? 0) == 0)
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
                MergeNonInstallSourceProjectionWarnings(currentChart.Warnings, projectionSource.Warnings));
        }
        return ChartFileProjection.WithWarnings(currentChart, projectionSource.Warnings);
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

    private ChartFileTransientState GetChartTransientState(ChartFile chart, bool includeWarningSnapshot, ChartFile projectionSource = null)
    {
        if (chart == null)
        {
            return ChartFileTransientState.Empty;
        }
        projectionSource ??= sourceChart;
        ChartFileTransientState providerState = chartTransientStateProvider?.Invoke(chart, includeWarningSnapshot);
        if (providerState?.HasState == true)
        {
            if (includeWarningSnapshot
                && !HasWarningProjection(providerState)
                && (providerState.Warnings?.Count ?? 0) == 0
                && (projectionSource?.Warnings?.Count ?? 0) > 0)
            {
                return ChartFileTransientState.FromChartFile(
                    ChartFileProjection.WithPackageState(
                        projectionSource,
                        providerState.HasInstallDestinationState ? providerState.InstallDestination : projectionSource.InstallDestination,
                        providerState.HasInstallDestinationState ? providerState.InstallDestinationTitle : projectionSource.InstallDestinationTitle,
                        providerState.HasInstallDestinationState ? providerState.InstallDestinationArtist : projectionSource.InstallDestinationArtist,
                        providerState.HasInstallDestinationState ? providerState.InstallDestinationSuggestions : projectionSource.InstallDestinationSuggestions,
                        projectionSource.Warnings));
            }
            return providerState;
        }
        return projectionSource == null ? ChartFileTransientState.Empty : ChartFileTransientState.FromChartFile(projectionSource, includeWarningSnapshot);
    }

    internal static ChartListSourceRow FromChartFile(
        ChartFile chart,
        ChartListSourceProjectionMode projectionMode = ChartListSourceProjectionMode.PreserveSourceProjection,
        Func<ChartListSourceRow, ResourceHealthWarningProjection> resourceHealthProjectionProvider = null,
        Func<ChartListSourceRow, PlaylistReferenceDisplay> playlistReferenceDisplayProvider = null,
        Func<ChartFile, bool, ChartFileTransientState> chartTransientStateProvider = null,
        Func<ChartFile, LR2SongDBExtended.chart_info> chartInfoProjectionProvider = null,
        Func<ChartListSourceRow, LR2SongDBExtended.chart_info> chartInfoRowProjectionProvider = null,
        Func<int> chartInfoProjectionVersionProvider = null,
        Func<int> scoreSnapshotVersionProvider = null,
        Func<ChartListSourceRow, ChartScoreSnapshot> scoreSnapshotProjectionProvider = null,
        bool hideResourceHealthDigestWhenInstallDestinationSet = true)
    {
        return chart == null ? null : new ChartListSourceRow(
            chart,
            projectionMode == ChartListSourceProjectionMode.PreserveSourceProjection,
            resourceHealthProjectionProvider,
            playlistReferenceDisplayProvider,
            chartTransientStateProvider,
            chartInfoProjectionProvider,
            chartInfoRowProjectionProvider,
            chartInfoProjectionVersionProvider,
            scoreSnapshotVersionProvider,
            scoreSnapshotProjectionProvider,
            hideResourceHealthDigestWhenInstallDestinationSet: hideResourceHealthDigestWhenInstallDestinationSet);
    }

    internal static ChartListSourceRow FromBmsStorageOwner(
        BMSFile file,
        Func<ChartListSourceRow, ResourceHealthWarningProjection> resourceHealthProjectionProvider = null,
        Func<ChartListSourceRow, PlaylistReferenceDisplay> playlistReferenceDisplayProvider = null,
        Func<ChartFile, bool, ChartFileTransientState> chartTransientStateProvider = null,
        Func<ChartFile, LR2SongDBExtended.chart_info> chartInfoProjectionProvider = null,
        Func<ChartListSourceRow, LR2SongDBExtended.chart_info> chartInfoRowProjectionProvider = null,
        Func<int> chartInfoProjectionVersionProvider = null,
        Func<int> scoreSnapshotVersionProvider = null,
        Func<ChartListSourceRow, ChartScoreSnapshot> scoreSnapshotProjectionProvider = null)
    {
        return file == null ? null : new ChartListSourceRow(
            file,
            null,
            resourceHealthProjectionProvider,
            playlistReferenceDisplayProvider,
            chartTransientStateProvider,
            chartInfoProjectionProvider,
            chartInfoRowProjectionProvider,
            chartInfoProjectionVersionProvider,
            scoreSnapshotVersionProvider,
            scoreSnapshotProjectionProvider);
    }

    internal static ChartListSourceRow FromBmsonStorageOwner(
        LR2SongDBExtended.bmson_song song,
        Func<ChartListSourceRow, ResourceHealthWarningProjection> resourceHealthProjectionProvider = null,
        Func<ChartListSourceRow, PlaylistReferenceDisplay> playlistReferenceDisplayProvider = null,
        Func<ChartFile, bool, ChartFileTransientState> chartTransientStateProvider = null,
        Func<ChartFile, LR2SongDBExtended.chart_info> chartInfoProjectionProvider = null,
        Func<ChartListSourceRow, LR2SongDBExtended.chart_info> chartInfoRowProjectionProvider = null,
        Func<int> chartInfoProjectionVersionProvider = null,
        Func<int> scoreSnapshotVersionProvider = null,
        Func<ChartListSourceRow, ChartScoreSnapshot> scoreSnapshotProjectionProvider = null)
    {
        return song == null ? null : new ChartListSourceRow(
            null,
            song,
            resourceHealthProjectionProvider,
            playlistReferenceDisplayProvider,
            chartTransientStateProvider,
            chartInfoProjectionProvider,
            chartInfoRowProjectionProvider,
            chartInfoProjectionVersionProvider,
            scoreSnapshotVersionProvider,
            scoreSnapshotProjectionProvider);
    }

    internal static ChartListSourceRow FromPackageChartEntry(
        PackageChartEntry entry,
        Func<ChartListSourceRow, ResourceHealthWarningProjection> resourceHealthProjectionProvider = null,
        Func<ChartListSourceRow, PlaylistReferenceDisplay> playlistReferenceDisplayProvider = null,
        Func<ChartFile, bool, ChartFileTransientState> chartTransientStateProvider = null,
        Func<ChartFile, LR2SongDBExtended.chart_info> chartInfoProjectionProvider = null,
        Func<ChartListSourceRow, LR2SongDBExtended.chart_info> chartInfoRowProjectionProvider = null,
        Func<int> chartInfoProjectionVersionProvider = null,
        Func<int> scoreSnapshotVersionProvider = null)
    {
        ChartFile chart = entry?.Chart;
        return chart == null ? null : new ChartListSourceRow(
            chart,
            hasSourceChartProjection: true,
            resourceHealthProjectionProvider,
            playlistReferenceDisplayProvider,
            chartTransientStateProvider,
            chartInfoProjectionProvider,
            chartInfoRowProjectionProvider,
            chartInfoProjectionVersionProvider,
            scoreSnapshotVersionProvider,
            scoreSnapshotProjectionProvider: null,
            entry,
            hideResourceHealthDigestWhenInstallDestinationSet: false);
    }

    internal static List<ChartListSourceRow> BuildStandardLibraryRows(
        IEnumerable<ChartFile> charts,
        Func<ChartListSourceRow, ResourceHealthWarningProjection> resourceHealthProjectionProvider = null,
        Func<ChartListSourceRow, PlaylistReferenceDisplay> playlistReferenceDisplayProvider = null,
        Func<ChartFile, bool, ChartFileTransientState> chartTransientStateProvider = null,
        Func<ChartFile, LR2SongDBExtended.chart_info> chartInfoProjectionProvider = null,
        Func<ChartListSourceRow, LR2SongDBExtended.chart_info> chartInfoRowProjectionProvider = null,
        Func<int> chartInfoProjectionVersionProvider = null,
        Func<int> scoreSnapshotVersionProvider = null,
        Func<ChartListSourceRow, ChartScoreSnapshot> scoreSnapshotProjectionProvider = null)
    {
        return BuildStandardLibraryRows(
            charts,
            ChartListSourceProjectionMode.PreserveSourceProjection,
            resourceHealthProjectionProvider,
            playlistReferenceDisplayProvider,
            chartTransientStateProvider,
            chartInfoProjectionProvider,
            chartInfoRowProjectionProvider,
            chartInfoProjectionVersionProvider,
            scoreSnapshotVersionProvider,
            scoreSnapshotProjectionProvider);
    }

    internal static List<ChartListSourceRow> BuildStandardLibraryRows(
        IEnumerable<ChartFile> charts,
        ChartListSourceProjectionMode projectionMode,
        Func<ChartListSourceRow, ResourceHealthWarningProjection> resourceHealthProjectionProvider = null,
        Func<ChartListSourceRow, PlaylistReferenceDisplay> playlistReferenceDisplayProvider = null,
        Func<ChartFile, bool, ChartFileTransientState> chartTransientStateProvider = null,
        Func<ChartFile, LR2SongDBExtended.chart_info> chartInfoProjectionProvider = null,
        Func<ChartListSourceRow, LR2SongDBExtended.chart_info> chartInfoRowProjectionProvider = null,
        Func<int> chartInfoProjectionVersionProvider = null,
        Func<int> scoreSnapshotVersionProvider = null,
        Func<ChartListSourceRow, ChartScoreSnapshot> scoreSnapshotProjectionProvider = null)
    {
        return [.. (charts ?? [])
            .Select(chart => FromChartFile(chart, projectionMode, resourceHealthProjectionProvider, playlistReferenceDisplayProvider, chartTransientStateProvider, chartInfoProjectionProvider, chartInfoRowProjectionProvider, chartInfoProjectionVersionProvider, scoreSnapshotVersionProvider, scoreSnapshotProjectionProvider))
            .Where(row => row != null)];
    }

    internal static List<ChartListSourceRow> BuildPackageRows(
        IEnumerable<PackageChartEntry> entries,
        Func<ChartListSourceRow, ResourceHealthWarningProjection> resourceHealthProjectionProvider = null,
        Func<ChartListSourceRow, PlaylistReferenceDisplay> playlistReferenceDisplayProvider = null,
        Func<ChartFile, bool, ChartFileTransientState> chartTransientStateProvider = null,
        Func<ChartFile, LR2SongDBExtended.chart_info> chartInfoProjectionProvider = null,
        Func<ChartListSourceRow, LR2SongDBExtended.chart_info> chartInfoRowProjectionProvider = null,
        Func<int> chartInfoProjectionVersionProvider = null,
        Func<int> scoreSnapshotVersionProvider = null)
    {
        return [.. (entries ?? [])
            .Select(entry => FromPackageChartEntry(entry, resourceHealthProjectionProvider, playlistReferenceDisplayProvider, chartTransientStateProvider, chartInfoProjectionProvider, chartInfoRowProjectionProvider, chartInfoProjectionVersionProvider, scoreSnapshotVersionProvider))
            .Where(row => row != null)];
    }

    private ChartInfoDisplaySnapshot GetChartInfoDisplay()
    {
        if (chartInfoProjectionVersionProvider == null && packageEntry == null)
        {
            return ChartInfoDisplaySnapshot.FromChartInfo(ChartInfo);
        }

        int version = GetChartInfoDisplayCacheVersion();
        if (cachedChartInfoDisplayValid && cachedChartInfoDisplayVersion == version)
        {
            return cachedChartInfoDisplay ?? ChartInfoDisplaySnapshot.Empty;
        }

        cachedChartInfoDisplay = ChartInfoDisplaySnapshot.FromChartInfo(ChartInfo);
        cachedChartInfoDisplayVersion = version;
        cachedChartInfoDisplayValid = true;
        return cachedChartInfoDisplay ?? ChartInfoDisplaySnapshot.Empty;
    }

    private ChartScoreSnapshot GetScoreSnapshot()
    {
        if (scoreSnapshotVersionProvider == null && packageEntry == null)
        {
            return ResolveScoreSnapshot();
        }

        int version = GetScoreSnapshotCacheVersion();
        if (cachedScoreSnapshotValid && cachedScoreSnapshotVersion == version)
        {
            return cachedScoreSnapshot ?? ChartScoreSnapshot.MissingChart;
        }

        cachedScoreSnapshot = ResolveScoreSnapshot();
        cachedScoreSnapshotVersion = version;
        cachedScoreSnapshotValid = true;
        return cachedScoreSnapshot ?? ChartScoreSnapshot.MissingChart;
    }

    private int GetChartInfoDisplayCacheVersion()
    {
        unchecked
        {
            int version = chartInfoProjectionVersionProvider?.Invoke() ?? 0;
            if (packageEntry != null)
            {
                version = (version * 397) ^ packageEntry.ProjectionVersion;
            }
            return version;
        }
    }

    private int GetScoreSnapshotCacheVersion()
    {
        unchecked
        {
            int version = scoreSnapshotVersionProvider?.Invoke() ?? 0;
            if (packageEntry != null)
            {
                version = (version * 397) ^ packageEntry.ProjectionVersion;
            }
            return version;
        }
    }

    private ChartFile ApplyChartInfoProjection(ChartFile chart)
    {
        LR2SongDBExtended.chart_info resolved = ResolveChartInfoFromProvider(chart);
        if (chart == null || resolved == null || ReferenceEquals(resolved, chart.ChartInfo))
        {
            return chart;
        }
        return ChartFileProjection.WithChartInfo(chart, resolved);
    }

    private LR2SongDBExtended.chart_info ResolveChartInfoFromProvider(ChartFile chart)
    {
        return chart == null ? null : chartInfoProjectionProvider?.Invoke(chart);
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

    private static string FormatBmsLevelText(BMSFile file)
    {
        return file?.level.HasValue == true
            ? file.level.Value.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
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
