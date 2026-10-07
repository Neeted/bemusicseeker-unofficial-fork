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

    private readonly Func<ChartFile, BeMusicSeeker.Models.ChartDetails> chartInfoProjectionProvider;

    private readonly Func<ChartListSourceRow, BeMusicSeeker.Models.ChartDetails> chartInfoRowProjectionProvider;

    private readonly Func<int> chartInfoProjectionVersionProvider;

    private readonly Func<int> scoreSnapshotVersionProvider;

    private readonly Func<ChartListSourceRow, ChartScoreSnapshot> scoreSnapshotProjectionProvider;

    private ChartFile sourceChart;

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
        Func<ChartFile, BeMusicSeeker.Models.ChartDetails> chartInfoProjectionProvider,
        Func<ChartListSourceRow, BeMusicSeeker.Models.ChartDetails> chartInfoRowProjectionProvider = null,
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

    internal ChartFileKind Kind => IdentityChart.Kind;

    /// <summary>保存列や表示値とは独立した、所持項目の識別です。</summary>
    internal OwnedChartToken Token => IdentityChart.Token;

    internal bool HasSourceChartProjection => hasSourceChartProjection;

    internal bool HideResourceHealthDigestWhenInstallDestinationSet => hideResourceHealthDigestWhenInstallDestinationSet;

    private ChartFile IdentityChart => packageEntry?.Chart ?? sourceChart;

    internal string Title => IdentityChart.Title;
    internal string Artist => IdentityChart.Artist;
    internal string Genre => IdentityChart.Genre;
    internal string Folder => IdentityChart.Folder;
    internal string Path => IdentityChart.Path;
    internal int? Mode => IdentityChart.Mode;

    internal string WarningDigestText => ChartWarningProjectionFormatter.BuildDigestText(
        CreateChartFile(includeWarningSnapshot: true, includeResourceReferences: false, includeScoreSnapshot: false),
        GetResourceHealthProjection(),
        resourceHealthProjectionProvider != null,
        hideResourceHealthDigestWhenInstallDestinationSet);

    internal string Tag => IdentityChart.Tag ?? string.Empty;
    internal string Level => IdentityChart.LevelText;
    internal double? LevelValue => IdentityChart.Level;
    internal string Hash => IdentityChart.Md5;
    internal string Sha256 => IdentityChart.Sha256;

    /// <summary>同じ所持項目の変更通知を受け、基本値の参照と依存する表示キャッシュを更新します。</summary>
    internal void ApplyCurrentChart(ChartFile chart)
    {
        if (chart == null || sourceChart.Token == null || !ReferenceEquals(sourceChart.Token, chart.Token))
        {
            throw new ArgumentException("The current value must belong to the same owned chart.", nameof(chart));
        }
        sourceChart = chart;
        cachedChartValid = false;
        cachedChartInfoDisplayValid = false;
        cachedScoreSnapshotValid = false;
    }

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

    internal BeMusicSeeker.Models.ChartDetails ChartInfo => ResolveChartInfoProjection();

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
        ChartFile currentSource = IdentityChart;
        ChartFileTransientState transientState = GetChartTransientState(currentSource, includeWarningSnapshot, currentSource);
        ChartFile currentChart = ChartFileProjection.WithTransientState(currentSource, transientState,
            includeWarningSnapshot: includeWarningSnapshot);
        currentChart = currentChart with
        {
            Warnings = includeWarningSnapshot ? currentChart.Warnings : [],
            Resources = includeResourceReferences ? currentSource.Resources : null,
            Score = includeScoreSnapshot ? currentSource.Score : ChartScoreSnapshot.NoScore(currentSource.Path)
        };
        return ApplyScoreProjection(ApplySourceProjectionWarnings(ApplyChartInfoProjection(currentChart), transientState,
            includeWarningSnapshot, currentSource), includeScoreSnapshot);
    }

    private BeMusicSeeker.Models.ChartDetails ResolveChartInfoProjection()
    {
        BeMusicSeeker.Models.ChartDetails rowResolved = chartInfoRowProjectionProvider?.Invoke(this);
        if (rowResolved != null)
        {
            return rowResolved;
        }

        ChartFile chart = IdentityChart;
        return ResolveChartInfoFromProvider(chart)
            ?? chart?.ChartInfo;
    }

    private ChartScoreSnapshot ResolveScoreSnapshot()
    {
        if (packageEntry != null)
        {
            return packageEntry.Chart?.Score ?? ChartScoreSnapshot.MissingChart;
        }

        ChartScoreSnapshot resolved = scoreSnapshotProjectionProvider?.Invoke(this);
        if (resolved != null)
        {
            return resolved;
        }
        ChartScoreSnapshot sourceScore = sourceChart?.Score;
        if (sourceScore != null)
        {
            return sourceScore;
        }
        return ChartScoreSnapshot.NoScore(IdentityChart.Path);
    }

    private ChartFile ApplyScoreProjection(ChartFile chart, bool includeScoreSnapshot)
    {
        if (!includeScoreSnapshot || chart == null)
        {
            return chart;
        }
        if (packageEntry != null && scoreSnapshotProjectionProvider == null)
        {
            return chart;
        }
        ChartScoreSnapshot score = GetScoreSnapshot();
        return AreEquivalentScoreSnapshots(score, chart.Score)
            ? chart
            : ChartFileProjection.WithScore(chart, score);
    }

    private static bool AreEquivalentScoreSnapshots(ChartScoreSnapshot left, ChartScoreSnapshot right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }
        if (left == null || right == null)
        {
            return false;
        }
        return left.Clear == right.Clear
            && left.Rank == right.Rank
            && left.Score == right.Score
            && left.Rate == right.Rate
            && left.RateDouble == right.RateDouble
            && left.TotalNotes == right.TotalNotes
            && left.MinBp == right.MinBp
            && left.MaxCombo == right.MaxCombo
            && left.Ranking == right.Ranking
            && left.RankingNum == right.RankingNum
            && string.Equals(left.RankingString, right.RankingString, StringComparison.Ordinal)
            && left.RankingLastUpdate == right.RankingLastUpdate
            && left.StdDevVal == right.StdDevVal
            && left.ScoreDifficulty == right.ScoreDifficulty
            && left.IsLr2IrScoreUnsent == right.IsLr2IrScoreUnsent;
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
        Func<ChartFile, BeMusicSeeker.Models.ChartDetails> chartInfoProjectionProvider = null,
        Func<ChartListSourceRow, BeMusicSeeker.Models.ChartDetails> chartInfoRowProjectionProvider = null,
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

    internal static ChartListSourceRow FromPackageChartEntry(
        PackageChartEntry entry,
        Func<ChartListSourceRow, ResourceHealthWarningProjection> resourceHealthProjectionProvider = null,
        Func<ChartListSourceRow, PlaylistReferenceDisplay> playlistReferenceDisplayProvider = null,
        Func<ChartFile, bool, ChartFileTransientState> chartTransientStateProvider = null,
        Func<ChartFile, BeMusicSeeker.Models.ChartDetails> chartInfoProjectionProvider = null,
        Func<ChartListSourceRow, BeMusicSeeker.Models.ChartDetails> chartInfoRowProjectionProvider = null,
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
            entry);
    }

    internal static List<ChartListSourceRow> BuildStandardLibraryRows(
        IEnumerable<ChartFile> charts,
        Func<ChartListSourceRow, ResourceHealthWarningProjection> resourceHealthProjectionProvider = null,
        Func<ChartListSourceRow, PlaylistReferenceDisplay> playlistReferenceDisplayProvider = null,
        Func<ChartFile, bool, ChartFileTransientState> chartTransientStateProvider = null,
        Func<ChartFile, BeMusicSeeker.Models.ChartDetails> chartInfoProjectionProvider = null,
        Func<ChartListSourceRow, BeMusicSeeker.Models.ChartDetails> chartInfoRowProjectionProvider = null,
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
        Func<ChartFile, BeMusicSeeker.Models.ChartDetails> chartInfoProjectionProvider = null,
        Func<ChartListSourceRow, BeMusicSeeker.Models.ChartDetails> chartInfoRowProjectionProvider = null,
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
        Func<ChartFile, BeMusicSeeker.Models.ChartDetails> chartInfoProjectionProvider = null,
        Func<ChartListSourceRow, BeMusicSeeker.Models.ChartDetails> chartInfoRowProjectionProvider = null,
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
        BeMusicSeeker.Models.ChartDetails resolved = ResolveChartInfoFromProvider(chart);
        if (chart == null || resolved == null || ReferenceEquals(resolved, chart.ChartInfo))
        {
            return chart;
        }
        return ChartFileProjection.WithChartInfo(chart, resolved);
    }

    private BeMusicSeeker.Models.ChartDetails ResolveChartInfoFromProvider(ChartFile chart)
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

}
