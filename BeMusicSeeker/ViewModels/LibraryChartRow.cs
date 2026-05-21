using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Livet;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// 通常一覧に表示する所持譜面 row です。
/// BMS / bmson の storage model はそのままに、表示境界だけを共通化します。
/// </summary>
internal sealed class LibraryChartRow : NotificationObject
{
    private readonly ChartFile sourceChart;

    private readonly Func<ChartFile> chartProvider;

    private readonly bool hasSourceChartProjection;

    private readonly ChartFile sourceTransientBaseline;

    private BMSFile BmsFile { get; }

    private LR2SongDBExtended.bmson_song BmsonSong { get; set; }

    internal PackageChartEntry PackageEntry { get; }

    private Func<LibraryChartRow, ResourceHealthWarningProjection> resourceHealthProjectionProvider;

    private Func<LibraryChartRow, PlaylistReferenceDisplay> playlistReferenceDisplayProvider;

    private Func<LR2SongDBExtended.bmson_song, bool, ChartFileTransientState> bmsonTransientStateProvider;

    internal bool IsBmson => BmsonSong != null && BmsFile == null;

    internal bool IsBms => BmsFile != null;

    internal ChartFile Chart => CreateChartFile();

    internal ChartFile CreateChartFile()
    {
        ChartFile providedChart = chartProvider?.Invoke();
        if (providedChart != null)
        {
            return providedChart;
        }
        if (hasSourceChartProjection)
        {
            if (BmsFile != null)
            {
                return CreateBmsOwnerBackedChart();
            }
            LR2SongDBExtended.bmson_song sourceBmsonSong = GetBmsonSong();
            if (sourceBmsonSong != null)
            {
                return CreateBmsonOwnerBackedChart();
            }
            return sourceChart;
        }
        if (BmsFile != null)
        {
            return CreateBmsOwnerBackedChart();
        }
        LR2SongDBExtended.bmson_song bmsonSong = GetBmsonSong();
        if (bmsonSong != null)
        {
            return CreateBmsonOwnerBackedChart();
        }
        return sourceChart;
    }

    private ChartFile CreateBmsOwnerBackedChart()
    {
        ChartFile currentChart = ChartFileProjection.FromBmsFile(BmsFile, ChartFileLevelParsing.CurrentCultureThenInvariant);
        return sourceTransientBaseline != null
            ? ChartFileProjection.WithTransientOverrides(currentChart, sourceChart, sourceTransientBaseline)
            : currentChart;
    }

    private ChartFile CreateBmsonOwnerBackedChart()
    {
        LR2SongDBExtended.bmson_song bmsonSong = GetBmsonSong();
        ChartFile currentChart = ChartFileProjection.FromBmsonSong(
            bmsonSong,
            GetBmsonTransientState(includeWarningSnapshot: true));
        return sourceTransientBaseline != null
            ? ChartFileProjection.WithTransientOverrides(currentChart, sourceChart, sourceTransientBaseline)
            : currentChart;
    }

    private LibraryChartRow(ChartFile sourceChart, bool hasSourceChartProjection = false, Func<ChartFile> chartProvider = null, PackageChartEntry packageEntry = null)
    {
        this.sourceChart = sourceChart ?? throw new ArgumentNullException(nameof(sourceChart));
        this.hasSourceChartProjection = hasSourceChartProjection;
        BmsFile = sourceChart.GetBmsStorageOwner();
        BmsonSong = sourceChart.GetBmsonStorageOwner();
        sourceTransientBaseline = CreateSourceTransientBaseline();
        this.chartProvider = chartProvider;
        PackageEntry = packageEntry;
        if (BmsFile is INotifyPropertyChanged propertyChangedSource)
        {
            PropertyChangedEventManager.AddHandler(propertyChangedSource, OnSourcePropertyChanged, string.Empty);
        }
    }

    internal static LibraryChartRow FromBmsFile(BMSFile file)
    {
        return FromBmsFile(file, null);
    }

    internal static LibraryChartRow FromBmsFile(BMSFile file, PackageChartEntry packageEntry)
    {
        if (file == null)
        {
            return null;
        }
        return new LibraryChartRow(
            ChartFileProjection.FromBmsFile(file, ChartFileLevelParsing.CurrentCultureThenInvariant, includeWarningSnapshot: false),
            packageEntry: packageEntry);
    }

    internal static LibraryChartRow FromBmsonSong(LR2SongDBExtended.bmson_song song)
    {
        return song == null
            ? null
            : new LibraryChartRow(ChartFileProjection.FromBmsonSong(song, includeWarningSnapshot: false));
    }

    internal static LibraryChartRow FromChartFile(ChartFile chart)
    {
        if (chart == null)
        {
            return null;
        }
        return new LibraryChartRow(chart, hasSourceChartProjection: true);
    }

    internal static LibraryChartRow FromPackageChartEntry(PackageChartEntry entry)
    {
        ChartFile chart = entry?.Chart;
        if (chart == null)
        {
            return null;
        }
        return new LibraryChartRow(chart, hasSourceChartProjection: true, chartProvider: () => entry.Chart, packageEntry: entry);
    }

    internal void UpdateFromBmsonSong(LR2SongDBExtended.bmson_song song)
    {
        if (song == null)
        {
            return;
        }
        BmsonSong = song;
        RaisePropertyChanged(string.Empty);
    }

    internal void SetResourceHealthProjectionProvider(Func<LibraryChartRow, ResourceHealthWarningProjection> provider)
    {
        resourceHealthProjectionProvider = provider;
    }

    internal void SetPlaylistReferenceDisplayProvider(Func<LibraryChartRow, PlaylistReferenceDisplay> provider)
    {
        playlistReferenceDisplayProvider = provider;
    }

    internal void SetBmsonTransientStateProvider(Func<LR2SongDBExtended.bmson_song, bool, ChartFileTransientState> provider)
    {
        bmsonTransientStateProvider = provider;
    }

    internal BMSFile GetBmsStorageOwner()
    {
        return BmsFile;
    }

    internal LR2SongDBExtended.bmson_song GetBmsonStorageOwner()
    {
        return BmsonSong;
    }

    internal bool ReferencesBmsonStorageOwner(LR2SongDBExtended.bmson_song song)
    {
        return ReferenceEquals(BmsonSong, song);
    }

    internal void RaisePlaylistReferenceDisplayChanged()
    {
        RaisePropertyChanged(nameof(RefTablesSymbols));
        RaisePropertyChanged(nameof(RefTablesNames));
    }

    public string Title => Chart?.Title ?? string.Empty;

    public string Artist => Chart?.Artist ?? string.Empty;

    public string genre => Chart?.Genre ?? string.Empty;

    public int? mode => Chart?.Mode;

    public string tag => Chart?.Tag ?? string.Empty;

    public string Level
    {
        get => Chart?.LevelText ?? string.Empty;
        set
        {
            if (BmsFile != null)
            {
                BmsFile.Level = value;
            }
        }
    }

    public double? level => Chart?.Level;

    public bool HasZeroNoteMismatchWarning => BmsFile?.HasZeroNoteMismatchWarning ?? false;

    public bool HasHighlightedWarning => ChartWarningProjectionFormatter.HasHighlightedWarning(Chart, GetResourceHealthProjection(), resourceHealthProjectionProvider != null);

    public bool HasFailureStatus => false;

    public string DisplayWarning => ChartWarningProjectionFormatter.BuildDisplayText(Chart, GetResourceHealthProjection(), resourceHealthProjectionProvider != null);

    public string WarningDigestText => ChartWarningProjectionFormatter.BuildDigestText(Chart, GetResourceHealthProjection(), resourceHealthProjectionProvider != null);

    public string WarningTooltipText => ChartWarningProjectionFormatter.BuildTooltipText(Chart, GetResourceHealthProjection(), resourceHealthProjectionProvider != null);

    public string hash => Chart?.Md5 ?? string.Empty;

    public string sha256 => Chart?.Sha256 ?? string.Empty;

    public string Folder
    {
        get => BmsFile?.Folder ?? (BmsonSong != null ? BmsonSongParser.ComposeDisplayFolder(BmsonSong) : Chart?.Folder ?? string.Empty);
        set
        {
            if (BmsFile != null)
            {
                BmsFile.Folder = value;
            }
        }
    }

    public string path => BmsFile?.path ?? BmsonSong?.path ?? Chart?.Path ?? string.Empty;

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

    public ChartFileStatus status => Chart?.Status ?? ChartFileStatus.NONE;

    public string lr2_bmsid => string.Empty;

    public string name_diff => string.Empty;

    public Uri Url => null;

    public Uri Url_diff => null;

    public string comment => string.Empty;

    public string memo => string.Empty;

    internal LR2SongDBExtended.chart_info ChartInfo => Chart?.ChartInfo;

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

    private void OnSourcePropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        RaisePropertyChanged(e.PropertyName);
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(BMSFile.Warnings))
        {
            RaiseWarningPresentationPropertiesChanged();
        }
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(BMSFile.ChartInfo))
        {
            RaiseChartInfoDisplayPropertiesChanged();
        }
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(BMSFile.bmsScore))
        {
            RaiseScoreDisplayPropertiesChanged();
        }
    }

    private void RaiseWarningPresentationPropertiesChanged()
    {
        RaisePropertyChanged(nameof(DisplayWarning));
        RaisePropertyChanged(nameof(WarningDigestText));
        RaisePropertyChanged(nameof(WarningTooltipText));
        RaisePropertyChanged(nameof(HasHighlightedWarning));
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
    }

    private LR2SongDBExtended.bmson_song GetBmsonSong()
    {
        return BmsonSong;
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

    private ChartFileTransientState GetBmsonTransientState(bool includeWarningSnapshot)
    {
        LR2SongDBExtended.bmson_song bmsonSong = GetBmsonSong();
        if (bmsonSong == null)
        {
            return ChartFileTransientState.Empty;
        }
        ChartFileTransientState providerState = bmsonTransientStateProvider?.Invoke(bmsonSong, includeWarningSnapshot);
        if (providerState?.HasState == true)
        {
            if (includeWarningSnapshot
                && (providerState.Warnings?.Count ?? 0) == 0
                && (sourceChart?.Warnings?.Count ?? 0) > 0)
            {
                return ChartFileTransientState.FromChartFile(
                    ChartFileProjection.WithPackageState(
                        sourceChart,
                        providerState.InstallDestination,
                        providerState.InstallDestinationTitle,
                        providerState.InstallDestinationArtist,
                        providerState.InstallDestinationSuggestions,
                        sourceChart.Warnings));
            }
            return providerState;
        }
        return ChartFileTransientState.Empty;
    }

    private ChartFileTransientState GetSourceChartTransientState(bool includeWarningSnapshot)
    {
        if (sourceTransientBaseline == null || sourceChart == null)
        {
            return ChartFileTransientState.Empty;
        }
        return ChartFileTransientState.FromChartFile(
            ChartFileProjection.WithTransientOverrides(sourceTransientBaseline, sourceChart, sourceTransientBaseline, includeWarningSnapshot),
            includeWarningSnapshot);
    }

    private ChartFile CreateSourceTransientBaseline()
    {
        if (!hasSourceChartProjection || sourceChart == null)
        {
            return null;
        }

        if (BmsFile != null)
        {
            return ChartFileProjection.FromBmsFile(BmsFile, ChartFileLevelParsing.CurrentCultureThenInvariant);
        }
        if (BmsonSong != null)
        {
            return ChartFileProjection.FromBmsonSong(BmsonSong);
        }
        return null;
    }

}
