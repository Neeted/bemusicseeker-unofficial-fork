using System;
using System.ComponentModel;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Livet;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// 通常一覧の共通現在値を表示します。保存行の通知を購読せず、項目または各投影所有者の通知を受けます。
/// </summary>
internal sealed class LibraryChartRow : NotificationObject
{
    private ChartFile sourceChart;

    private readonly Func<ChartFile> chartProvider;

    private readonly bool hideResourceHealthDigestWhenInstallDestinationSet;

    internal PackageChartEntry PackageEntry { get; }

    private Func<LibraryChartRow, ResourceHealthWarningProjection> resourceHealthProjectionProvider;

    private Func<LibraryChartRow, PlaylistReferenceDisplay> playlistReferenceDisplayProvider;

    private Func<ChartFile, bool, ChartFileTransientState> chartTransientStateProvider;

    private Func<ChartFile, BeMusicSeeker.Models.ChartDetails> chartInfoProjectionProvider;

    private Func<ChartFile, ChartFileStatus> playbackStatusProvider;
    private Func<ChartFile, ChartScoreSnapshot> scoreProjectionProvider;
    private Func<int> scoreProjectionVersionProvider;
    private int cachedScoreProjectionVersion;

    private ChartFile cachedChart;

    private bool cachedChartValid;

    internal bool IsBmson => sourceChart.Kind == ChartFileKind.Bmson;

    internal bool IsBms => sourceChart.Kind == ChartFileKind.Bms;

    internal ChartFile Chart
    {
        get
        {
            int version = scoreProjectionVersionProvider?.Invoke() ?? 0;
            if (!cachedChartValid || cachedScoreProjectionVersion != version)
            {
                cachedChart = CreateChartFile();
                cachedScoreProjectionVersion = version;
                cachedChartValid = true;
            }
            return cachedChart;
        }
    }

    /// <summary>再生一時状態をChartへ保存せず、status読取り時だけ参照するdelegateを接続します。</summary>
    internal void SetPlaybackStatusProvider(Func<ChartFile, ChartFileStatus> provider) => playbackStatusProvider = provider;

    /// <summary>共通基本値と既存所有者の表示投影を合成します。</summary>
    internal ChartFile CreateChartFile()
    {
        ChartFile current = chartProvider?.Invoke() ?? sourceChart;
        ChartFileTransientState transientState = GetChartTransientState(current, includeWarningSnapshot: true);
        if (transientState?.HasState == true)
        {
            current = ChartFileProjection.WithTransientState(current, transientState);
        }
        current = ApplyChartInfoProjection(current);
        ChartScoreSnapshot score = scoreProjectionProvider?.Invoke(current);
        return score == null ? current : ChartFileProjection.WithScore(current, score);
    }

    /// <summary>スコアの既存所有者から読取り値と表示版を取得します。</summary>
    internal void SetScoreProjectionProvider(Func<ChartFile, ChartScoreSnapshot> provider, Func<int> versionProvider)
    {
        scoreProjectionProvider = provider;
        scoreProjectionVersionProvider = versionProvider;
        InvalidateChartCache();
    }

    private LibraryChartRow(
        ChartFile sourceChart,
        Func<ChartFile> chartProvider = null,
        PackageChartEntry packageEntry = null,
        bool hideResourceHealthDigestWhenInstallDestinationSet = true)
    {
        this.sourceChart = sourceChart ?? throw new ArgumentNullException(nameof(sourceChart));
        this.hideResourceHealthDigestWhenInstallDestinationSet = hideResourceHealthDigestWhenInstallDestinationSet;
        this.chartProvider = chartProvider;
        PackageEntry = packageEntry;
        if (packageEntry is INotifyPropertyChanged propertyChangedPackageEntry)
        {
            PropertyChangedEventManager.AddHandler(propertyChangedPackageEntry, OnPackageEntryPropertyChanged, string.Empty);
        }
    }

    internal static LibraryChartRow FromChartFile(ChartFile chart, bool hideResourceHealthDigestWhenInstallDestinationSet = true)
    {
        if (chart == null)
        {
            return null;
        }
        return new LibraryChartRow(
            chart,
            hideResourceHealthDigestWhenInstallDestinationSet: hideResourceHealthDigestWhenInstallDestinationSet);
    }

    internal static LibraryChartRow FromPackageChartEntry(PackageChartEntry entry)
    {
        ChartFile chart = entry?.Chart;
        if (chart == null)
        {
            return null;
        }
        return new LibraryChartRow(
            chart,
            chartProvider: () => entry.Chart,
            packageEntry: entry);
    }

    /// <summary>
    /// 対象集合の入力行から表示行を作り、再評価時にも入力行の現在の投影を使います。
    /// パッケージの変更通知と、導入先・警告の明示的なクリアを維持します。
    /// </summary>
    /// <param name="sourceRow">現在値とパッケージ項目を提供する入力行。</param>
    /// <returns>入力行に接続した表示行。入力がない場合は null。</returns>
    internal static LibraryChartRow FromSourceRow(ChartListSourceRow sourceRow)
    {
        return sourceRow == null ? null : new LibraryChartRow(
            sourceRow.Chart,
            chartProvider: () => sourceRow.Chart,
            packageEntry: sourceRow.PackageEntry,
            hideResourceHealthDigestWhenInstallDestinationSet: sourceRow.HideResourceHealthDigestWhenInstallDestinationSet);
    }

    internal void UpdateSourceProjection(ChartFile chart)
    {
        if (chart == null)
        {
            return;
        }
        sourceChart = chart;
        InvalidateChartCache();
    }

    internal void SetResourceHealthProjectionProvider(Func<LibraryChartRow, ResourceHealthWarningProjection> provider)
    {
        if (!ReferenceEquals(resourceHealthProjectionProvider, provider))
        {
            InvalidateChartCache();
        }
        resourceHealthProjectionProvider = provider;
    }

    internal void SetPlaylistReferenceDisplayProvider(Func<LibraryChartRow, PlaylistReferenceDisplay> provider)
    {
        playlistReferenceDisplayProvider = provider;
    }

    internal void SetChartTransientStateProvider(Func<ChartFile, bool, ChartFileTransientState> provider)
    {
        if (!ReferenceEquals(chartTransientStateProvider, provider))
        {
            InvalidateChartCache();
        }
        chartTransientStateProvider = provider;
    }

    internal void SetChartInfoProjectionProvider(Func<ChartFile, BeMusicSeeker.Models.ChartDetails> provider)
    {
        if (!ReferenceEquals(chartInfoProjectionProvider, provider))
        {
            InvalidateChartCache();
        }
        chartInfoProjectionProvider = provider;
    }

    internal void RaisePlaylistReferenceDisplayChanged()
    {
        RaisePropertyChanged(nameof(RefTablesSymbols));
        RaisePropertyChanged(nameof(RefTablesNames));
    }

    internal void RefreshDisplayForDataDependency(MainViewDataDependency dependency)
    {
        InvalidateChartCache();
        switch (dependency)
        {
            case MainViewDataDependency.ChartInfo:
                RaiseChartInfoDisplayPropertiesChanged();
                break;
            case MainViewDataDependency.Score:
                RaiseScoreDisplayPropertiesChanged();
                break;
            case MainViewDataDependency.Maintenance:
                RaiseMaintenanceDisplayPropertiesChanged();
                RaiseWarningPresentationPropertiesChanged();
                break;
            case MainViewDataDependency.Warning:
                RaiseWarningPresentationPropertiesChanged();
                break;
            default:
                RaisePropertyChanged(string.Empty);
                break;
        }
    }

    public string Title => Chart?.Title ?? string.Empty;

    public string Artist => Chart?.Artist ?? string.Empty;

    public string genre => Chart?.Genre ?? string.Empty;

    public int? mode => Chart?.Mode;

    public string tag => Chart?.Tag ?? string.Empty;

    public string Level => Chart?.LevelText ?? string.Empty;

    public double? level => Chart?.Level;

    public bool HasZeroNoteMismatchWarning => Chart?.Warnings?.Any(warning => warning.Kind == ChartWarningKind.ZeroNoteMismatch) ?? false;

    public bool HasHighlightedWarning => ChartWarningProjectionFormatter.HasHighlightedWarning(Chart, GetResourceHealthProjection(), resourceHealthProjectionProvider != null);

    public bool HasFailureStatus => false;

    public string DisplayWarning => ChartWarningProjectionFormatter.BuildDisplayText(Chart, GetResourceHealthProjection(), resourceHealthProjectionProvider != null);

    public string WarningDigestText => ChartWarningProjectionFormatter.BuildDigestText(
        Chart,
        GetResourceHealthProjection(),
        resourceHealthProjectionProvider != null,
        hideResourceHealthDigestWhenInstallDestinationSet);

    public string WarningTooltipText => ChartWarningProjectionFormatter.BuildTooltipText(Chart, GetResourceHealthProjection(), resourceHealthProjectionProvider != null);

    public string hash => Chart?.Md5 ?? string.Empty;

    public string sha256 => Chart?.Sha256 ?? string.Empty;

    public string Folder => Chart?.Folder ?? string.Empty;

    public string path => Chart?.Path ?? string.Empty;

    public string instl_dst => Chart?.InstallDestination ?? string.Empty;

    public string InstallDestinationTitle => Chart?.InstallDestinationTitle ?? string.Empty;

    public string InstallDestinationArtist => Chart?.InstallDestinationArtist ?? string.Empty;

    public int? WAVHealth => Chart?.WAVHealth;

    public int? BGAHealth => Chart?.BGAHealth;

    public int? MovieHealth => Chart?.MovieHealth;

    public bool? StagefileHealth => Chart?.StagefileHealth;

    public bool? BannerHealth => Chart?.BannerHealth;

    public bool? BackbmpHealth => Chart?.BackbmpHealth;

    public string encoding => Chart?.EncodingName ?? string.Empty;

    public string RefTablesSymbols => GetPlaylistReferenceDisplay().Symbols;

    public string RefTablesNames => GetPlaylistReferenceDisplay().Names;

    public ClearType clear => Chart?.Score?.Clear ?? ChartScoreSnapshot.MissingChart.Clear;

    public RankType rank => Chart?.Score?.Rank ?? RankType.INVALID;

    public string ClearDisplayText => ScoreDisplayTextFormatter.FormatClear(clear);

    public string RankDisplayText => ScoreDisplayTextFormatter.FormatRank(rank);

    public int? score => Chart?.Score?.Score;

    public int? rate => Chart?.Score?.Rate;

    public double? rateDouble => Chart?.Score?.RateDouble;

    public int? totalnotes => Chart?.Score?.TotalNotes;

    public int? minbp => Chart?.Score?.MinBp;

    public int? maxcombo => Chart?.Score?.MaxCombo;

    public int? ranking => Chart?.Score?.Ranking;

    public int? rankingNum => Chart?.Score?.RankingNum;

    public string rankingString => Chart?.Score?.RankingString ?? string.Empty;

    public DateTime? rankingLastupdate => Chart?.Score?.RankingLastUpdate;

    public double? stddevVal => Chart?.Score?.StdDevVal;

    public double? scoreDifficulty => Chart?.Score?.ScoreDifficulty;

    /// <summary>元の再生以外の状態を保ち、現在対象の再生bitを読取り時だけ合成します。</summary>
    public ChartFileStatus status => ((Chart?.Status ?? ChartFileStatus.NONE) & ~ChartFileStatus.PLAYALL)
        | (playbackStatusProvider?.Invoke(Chart) ?? ChartFileStatus.NONE);

    public string lr2_bmsid => string.Empty;

    public string name_diff => string.Empty;

    public Uri Url => null;

    public Uri Url_diff => null;

    public string comment => string.Empty;

    public string memo => string.Empty;

    internal BeMusicSeeker.Models.ChartDetails ChartInfo => Chart?.ChartInfo;

    private ChartInfoDisplaySnapshot ChartInfoDisplay => Chart?.ChartInfoDisplay ?? ChartInfoDisplaySnapshot.Empty;

    public string ChartLevelText => ChartInfoDisplay.ChartLevelText;

    public double? ChartLevelSortKey => ChartInfoDisplay.ChartLevelSortKey;

    public bool ChartLevelUndefined => ChartInfoDisplay.ChartLevelUndefined;

    public string ChartDifficultyText => ChartInfoDisplay.ChartDifficultyText;

    public int? ChartDifficultySortKey => ChartInfoDisplay.ChartDifficultySortKey;

    public string ChartDifficultyColorKey => ChartInfoDisplay.ChartDifficultyColorKey;

    public bool ChartDifficultyUndefined => ChartInfoDisplay.ChartDifficultyUndefined;

    public string ChartMainBpmText => ChartInfoDisplay.ChartMainBpmText;

    public double? ChartMainBpmSortKey => ChartInfoDisplay.ChartMainBpmSortKey;

    public string ChartMaxBpmText => ChartInfoDisplay.ChartMaxBpmText;

    public double? ChartMaxBpmSortKey => ChartInfoDisplay.ChartMaxBpmSortKey;

    public string ChartMinBpmText => ChartInfoDisplay.ChartMinBpmText;

    public double? ChartMinBpmSortKey => ChartInfoDisplay.ChartMinBpmSortKey;

    public string ChartDurationText => ChartInfoDisplay.ChartDurationText;

    public int? ChartDurationSortKey => ChartInfoDisplay.ChartDurationSortKey;

    public string ChartJudgeText => ChartInfoDisplay.ChartJudgeText;

    public int? ChartJudgeSortKey => ChartInfoDisplay.ChartJudgeSortKey;

    public string ChartJudgeColorKey => ChartInfoDisplay.ChartJudgeColorKey;

    public string ChartJudgePercentText => ChartInfoDisplay.ChartJudgePercentText;

    public string ChartFeatureText => ChartInfoDisplay.ChartFeatureText;

    public int? ChartFeatureSortKey => ChartInfoDisplay.ChartFeatureSortKey;

    public int? ChartNotes => ChartInfoDisplay.ChartNotes;

    public int? ChartLongNotes => ChartInfoDisplay.ChartLongNotes;

    public int? ChartScratchNotes => ChartInfoDisplay.ChartScratchNotes;

    public string ChartTotalText => ChartInfoDisplay.ChartTotalText;

    public double? ChartTotalSortKey => ChartInfoDisplay.ChartTotalSortKey;

    public bool ChartTotalUndefined => ChartInfoDisplay.ChartTotalUndefined;

    public string ChartTotalPerNoteText => ChartInfoDisplay.ChartTotalPerNoteText;

    public double? ChartTotalPerNoteSortKey => ChartInfoDisplay.ChartTotalPerNoteSortKey;

    public string ChartDensityText => ChartInfoDisplay.ChartDensityText;

    public double? ChartDensitySortKey => ChartInfoDisplay.ChartDensitySortKey;

    public string ChartPeakDensityText => ChartInfoDisplay.ChartPeakDensityText;

    public double? ChartPeakDensitySortKey => ChartInfoDisplay.ChartPeakDensitySortKey;

    public string ChartEndDensityText => ChartInfoDisplay.ChartEndDensityText;

    public double? ChartEndDensitySortKey => ChartInfoDisplay.ChartEndDensitySortKey;

    public int? ChartSoflanCount => ChartInfoDisplay.ChartSoflanCount;

    private void OnPackageEntryPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        InvalidateChartCache();
        RaisePropertyChanged(string.Empty);
    }

    private void InvalidateChartCache()
    {
        cachedChart = null;
        cachedChartValid = false;
    }

    private void RaiseWarningPresentationPropertiesChanged()
    {
        RaisePropertyChanged(nameof(DisplayWarning));
        RaisePropertyChanged(nameof(WarningDigestText));
        RaisePropertyChanged(nameof(WarningTooltipText));
        RaisePropertyChanged(nameof(HasHighlightedWarning));
        RaisePropertyChanged(nameof(HasZeroNoteMismatchWarning));
    }

    private void RaiseChartInfoDisplayPropertiesChanged()
    {
        RaisePropertyChanged(nameof(ChartLevelText));
        RaisePropertyChanged(nameof(ChartLevelSortKey));
        RaisePropertyChanged(nameof(ChartLevelUndefined));
        RaisePropertyChanged(nameof(ChartDifficultyText));
        RaisePropertyChanged(nameof(ChartDifficultySortKey));
        RaisePropertyChanged(nameof(ChartDifficultyColorKey));
        RaisePropertyChanged(nameof(ChartDifficultyUndefined));
        RaisePropertyChanged(nameof(ChartMainBpmText));
        RaisePropertyChanged(nameof(ChartMainBpmSortKey));
        RaisePropertyChanged(nameof(ChartMaxBpmText));
        RaisePropertyChanged(nameof(ChartMaxBpmSortKey));
        RaisePropertyChanged(nameof(ChartMinBpmText));
        RaisePropertyChanged(nameof(ChartMinBpmSortKey));
        RaisePropertyChanged(nameof(ChartDurationText));
        RaisePropertyChanged(nameof(ChartDurationSortKey));
        RaisePropertyChanged(nameof(ChartJudgeText));
        RaisePropertyChanged(nameof(ChartJudgeSortKey));
        RaisePropertyChanged(nameof(ChartJudgeColorKey));
        RaisePropertyChanged(nameof(ChartJudgePercentText));
        RaisePropertyChanged(nameof(ChartFeatureText));
        RaisePropertyChanged(nameof(ChartFeatureSortKey));
        RaisePropertyChanged(nameof(ChartNotes));
        RaisePropertyChanged(nameof(ChartLongNotes));
        RaisePropertyChanged(nameof(ChartScratchNotes));
        RaisePropertyChanged(nameof(ChartTotalText));
        RaisePropertyChanged(nameof(ChartTotalSortKey));
        RaisePropertyChanged(nameof(ChartTotalUndefined));
        RaisePropertyChanged(nameof(ChartTotalPerNoteText));
        RaisePropertyChanged(nameof(ChartTotalPerNoteSortKey));
        RaisePropertyChanged(nameof(ChartDensityText));
        RaisePropertyChanged(nameof(ChartDensitySortKey));
        RaisePropertyChanged(nameof(ChartPeakDensityText));
        RaisePropertyChanged(nameof(ChartPeakDensitySortKey));
        RaisePropertyChanged(nameof(ChartEndDensityText));
        RaisePropertyChanged(nameof(ChartEndDensitySortKey));
        RaisePropertyChanged(nameof(ChartSoflanCount));
    }

    private void RaiseScoreDisplayPropertiesChanged()
    {
        RaisePropertyChanged(nameof(clear));
        RaisePropertyChanged(nameof(rank));
        RaisePropertyChanged(nameof(ClearDisplayText));
        RaisePropertyChanged(nameof(RankDisplayText));
        RaisePropertyChanged(nameof(score));
        RaisePropertyChanged(nameof(rate));
        RaisePropertyChanged(nameof(rateDouble));
        RaisePropertyChanged(nameof(totalnotes));
        RaisePropertyChanged(nameof(minbp));
        RaisePropertyChanged(nameof(maxcombo));
        RaisePropertyChanged(nameof(ranking));
        RaisePropertyChanged(nameof(rankingNum));
        RaisePropertyChanged(nameof(rankingString));
        RaisePropertyChanged(nameof(rankingLastupdate));
        RaisePropertyChanged(nameof(stddevVal));
        RaisePropertyChanged(nameof(scoreDifficulty));
        RaisePropertyChanged(nameof(status));
    }

    private void RaiseMaintenanceDisplayPropertiesChanged()
    {
        RaisePropertyChanged(nameof(WAVHealth));
        RaisePropertyChanged(nameof(BGAHealth));
        RaisePropertyChanged(nameof(MovieHealth));
        RaisePropertyChanged(nameof(StagefileHealth));
        RaisePropertyChanged(nameof(BannerHealth));
        RaisePropertyChanged(nameof(BackbmpHealth));
        RaisePropertyChanged(nameof(encoding));
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
        return ChartFileTransientState.Empty;
    }

    private ChartFile ApplyChartInfoProjection(ChartFile chart)
    {
        BeMusicSeeker.Models.ChartDetails resolved = chartInfoProjectionProvider?.Invoke(chart);
        if (chart == null || resolved == null || ReferenceEquals(resolved, chart.ChartInfo))
        {
            return chart;
        }
        return ChartFileProjection.WithChartInfo(chart, resolved);
    }

    private static bool HasWarningProjection(ChartFileTransientState state)
    {
        return state?.HasWarningProjection == true || state?.HasInstallEstimationWarningProjection == true;
    }


}
