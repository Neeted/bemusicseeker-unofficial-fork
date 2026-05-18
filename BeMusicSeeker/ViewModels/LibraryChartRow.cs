using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
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
    internal BMSFile BmsFile { get; }

    internal LR2SongDBExtended.bmson_song BmsonSong { get; private set; }

    private PendingChartEntry bmsonChartAdapter;

    private Func<LR2SongDBExtended.bmson_song, PendingChartEntry> bmsonChartAdapterProvider;

    private Func<LibraryChartRow, ResourceHealthWarningProjection> resourceHealthProjectionProvider;

    private Func<LibraryChartRow, PlaylistReferenceDisplay> playlistReferenceDisplayProvider;

    internal bool IsBmson => (BmsFile is PendingChartEntry pending && pending.IsBmsonChart) || (BmsonSong != null && BmsFile == null);

    internal bool IsBms => BmsFile != null && !PendingChartEntry.IsBmsonChartFile(BmsFile);

    internal ChartFile Chart => CreateChartFile(materializeBmsonChartAdapter: true);

    internal BMSFile CompatibilityBmsFile => BmsFile ?? GetOrCreateBmsonChartAdapter();

    internal ChartFile CreateChartFile(bool materializeBmsonChartAdapter)
    {
        LR2SongDBExtended.bmson_song bmsonSong = GetBmsonSong();
        if (bmsonSong != null)
        {
            var pending = BmsFile as PendingChartEntry;
            bool isPendingBmson = pending?.IsBmsonChart == true;
            return isPendingBmson
                ? ChartFileProjection.FromBmsFile(BmsFile, ChartFileLevelParsing.CurrentCultureThenInvariant)
                : ChartFileProjection.FromBmsonSong(
                    bmsonSong,
                    materializeBmsonChartAdapter ? GetOrCreateBmsonChartAdapter() : GetExistingBmsonChartAdapter());
        }
        return ChartFileProjection.FromBmsFile(BmsFile, ChartFileLevelParsing.CurrentCultureThenInvariant);
    }

    private LibraryChartRow(BMSFile bmsFile, LR2SongDBExtended.bmson_song bmsonSong)
    {
        BmsFile = bmsFile;
        BmsonSong = bmsonSong;
        if (bmsFile is INotifyPropertyChanged propertyChangedSource)
        {
            PropertyChangedEventManager.AddHandler(propertyChangedSource, OnSourcePropertyChanged, string.Empty);
        }
    }

    internal static LibraryChartRow FromBmsFile(BMSFile file)
    {
        if (file == null)
        {
            return null;
        }
        var pending = file as PendingChartEntry;
        return new LibraryChartRow(file, pending?.IsBmsonChart == true ? pending.BmsonSong : null);
    }

    internal static LibraryChartRow FromBmsonSong(LR2SongDBExtended.bmson_song song)
    {
        return song == null ? null : new LibraryChartRow(null, song);
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

    private PendingChartEntry GetOrCreateBmsonChartAdapter()
    {
        if (BmsonSong == null)
        {
            return null;
        }
        PendingChartEntry provided = bmsonChartAdapterProvider?.Invoke(BmsonSong);
        if (provided != null)
        {
            if (!ReferenceEquals(bmsonChartAdapter, provided))
            {
                SetBmsonChartAdapter(provided);
            }
            return bmsonChartAdapter;
        }
        if (bmsonChartAdapter == null || !ReferenceEquals(bmsonChartAdapter.BmsonSong, BmsonSong))
        {
            SetBmsonChartAdapter(PendingChartEntry.CreateFromBmsonSong(BmsonSong));
        }
        else if (!string.Equals(bmsonChartAdapter.path, BmsonSong.path, StringComparison.OrdinalIgnoreCase))
        {
            bmsonChartAdapter.UpdateFromBmsonSong(BmsonSong);
        }
        return bmsonChartAdapter;
    }

    private PendingChartEntry GetExistingBmsonChartAdapter()
    {
        if (BmsonSong == null || bmsonChartAdapter == null)
        {
            return null;
        }
        if (!ReferenceEquals(bmsonChartAdapter.BmsonSong, BmsonSong))
        {
            return null;
        }
        if (!string.Equals(bmsonChartAdapter.path, BmsonSong.path, StringComparison.OrdinalIgnoreCase))
        {
            bmsonChartAdapter.UpdateFromBmsonSong(BmsonSong);
        }
        return bmsonChartAdapter;
    }

    internal void SetBmsonChartAdapterProvider(Func<LR2SongDBExtended.bmson_song, PendingChartEntry> provider)
    {
        bmsonChartAdapterProvider = provider;
    }

    private void SetBmsonChartAdapter(PendingChartEntry entry)
    {
        if (bmsonChartAdapter is INotifyPropertyChanged oldSource)
        {
            PropertyChangedEventManager.RemoveHandler(oldSource, OnSourcePropertyChanged, string.Empty);
        }
        bmsonChartAdapter = entry;
        if (bmsonChartAdapter is INotifyPropertyChanged newSource)
        {
            PropertyChangedEventManager.AddHandler(newSource, OnSourcePropertyChanged, string.Empty);
        }
    }

    internal void SetResourceHealthProjectionProvider(Func<LibraryChartRow, ResourceHealthWarningProjection> provider)
    {
        resourceHealthProjectionProvider = provider;
    }

    internal void SetPlaylistReferenceDisplayProvider(Func<LibraryChartRow, PlaylistReferenceDisplay> provider)
    {
        playlistReferenceDisplayProvider = provider;
    }

    internal void RaisePlaylistReferenceDisplayChanged()
    {
        RaisePropertyChanged(nameof(RefTablesSymbols));
        RaisePropertyChanged(nameof(RefTablesNames));
    }

    public string Title => BmsFile?.Title ?? BmsonSongParser.ComposeDisplayTitle(BmsonSong);

    public string Artist => BmsFile?.Artist ?? BmsonSong?.artist ?? string.Empty;

    public string genre => BmsFile?.genre ?? BmsonSong?.genre ?? string.Empty;

    public int? mode => BmsFile?.mode ?? BmsonSongParser.ResolvePlaylistMode(BmsonSong?.mode_hint);

    public string tag => BmsFile?.tag ?? string.Empty;

    public string Level
    {
        get => BmsFile?.Level ?? (BmsonSong?.level.HasValue == true ? BmsonSong.level.Value.ToString(CultureInfo.InvariantCulture) : string.Empty);
        set
        {
            if (BmsFile != null)
            {
                BmsFile.Level = value;
            }
        }
    }

    public double? level => BmsFile?.level ?? BmsonSong?.level;

    public bool HasZeroNoteMismatchWarning => BmsFile?.HasZeroNoteMismatchWarning ?? false;

    public bool HasHighlightedWarning => ChartWarningProjectionFormatter.HasHighlightedWarning(Chart, GetResourceHealthProjection(), resourceHealthProjectionProvider != null);

    public bool HasFailureStatus => false;

    public string DisplayWarning => ChartWarningProjectionFormatter.BuildDisplayText(Chart, GetResourceHealthProjection(), resourceHealthProjectionProvider != null);

    public string WarningDigestText => ChartWarningProjectionFormatter.BuildDigestText(Chart, GetResourceHealthProjection(), resourceHealthProjectionProvider != null);

    public string WarningTooltipText => ChartWarningProjectionFormatter.BuildTooltipText(Chart, GetResourceHealthProjection(), resourceHealthProjectionProvider != null);

    public string hash => BmsFile?.hash ?? BmsonSong?.md5 ?? string.Empty;

    public string sha256 => BmsFile?.sha256 ?? BmsonSong?.sha256 ?? string.Empty;

    public string Folder
    {
        get => BmsFile?.Folder ?? BmsonSongParser.ComposeDisplayFolder(BmsonSong);
        set
        {
            if (BmsFile != null)
            {
                BmsFile.Folder = value;
            }
        }
    }

    public string path => BmsFile?.path ?? BmsonSong?.path ?? string.Empty;

    public string instl_dst
    {
        get => Chart?.InstallDestination ?? string.Empty;
        set
        {
            BMSFile compatibilityBmsFile = CompatibilityBmsFile;
            if (compatibilityBmsFile != null)
            {
                compatibilityBmsFile.instl_dst = value;
            }
        }
    }

    public string InstallDestinationTitle => Chart?.InstallDestinationTitle ?? string.Empty;

    public string InstallDestinationArtist => Chart?.InstallDestinationArtist ?? string.Empty;

    public int? WAVHealth => Chart?.WAVHealth;

    public int? BGAHealth => Chart?.BGAHealth;

    public int? MovieHealth => Chart?.MovieHealth;

    public bool? StagefileHealth => Chart?.StagefileHealth;

    public bool? BannerHealth => Chart?.BannerHealth;

    public bool? BackbmpHealth => Chart?.BackbmpHealth;

    public string encoding => Chart?.EncodingName ?? string.Empty;

    public string RefTablesSymbols => BmsFile?.RefTablesSymbols ?? GetPlaylistReferenceDisplay().Symbols;

    public string RefTablesNames => BmsFile?.RefTablesNames ?? GetPlaylistReferenceDisplay().Names;

    public ClearType clear => BmsFile?.clear ?? (string.IsNullOrWhiteSpace(path) ? ClearType.NO_SONG : ClearType.NO_PLAY);

    public RankType rank => BmsFile?.rank ?? RankType.INVALID;

    public string ClearDisplayText => ScoreDisplayTextFormatter.FormatClear(clear);

    public string RankDisplayText => ScoreDisplayTextFormatter.FormatRank(rank);

    public int? score => BmsFile?.score;

    public int? rate => BmsFile?.rate;

    public double? rateDouble => BmsFile?.rateDouble;

    public int? totalnotes => BmsFile?.totalnotes;

    public int? minbp => BmsFile?.minbp;

    public int? maxcombo => BmsFile?.maxcombo;

    public int? ranking => BmsFile?.ranking;

    public int? rankingNum => BmsFile?.rankingNum;

    public string rankingString => BmsFile?.rankingString ?? string.Empty;

    public DateTime? rankingLastupdate => BmsFile?.rankingLastupdate;

    public double? stddevVal => BmsFile?.stddevVal;

    public double? scoreDifficulty => BmsFile?.scoreDifficulty;

    public BMSFile.BMSFileStatus status => BmsFile?.status ?? BMSFile.BMSFileStatus.NONE;

    public string lr2_bmsid => string.Empty;

    public string name_diff => string.Empty;

    public Uri Url => null;

    public Uri Url_diff => null;

    public string comment => string.Empty;

    public string memo => string.Empty;

    internal LR2SongDBExtended.chart_info ChartInfo => BmsFile?.ChartInfo ?? BmsonSong?.ChartInfo;

    public string ChartLevelText => ChartInfoDisplayFormatter.FormatOptionalInt(ChartInfo?.level);

    public double? ChartLevelSortKey => ChartInfo?.level ?? 0;

    public bool ChartLevelUndefined => ChartInfo == null || !ChartInfo.level.HasValue;

    public string ChartDifficultyText => ChartInfoDisplayFormatter.FormatDifficulty(ChartInfo?.difficulty);

    public int? ChartDifficultySortKey => ChartInfo?.difficulty;

    public string ChartDifficultyColorKey => ChartInfoDisplayFormatter.GetDifficultyColorKey(ChartInfo?.difficulty);

    public bool ChartDifficultyUndefined => ChartInfo == null || !ChartInfo.difficulty_defined;

    public string ChartMainBpmText => ChartInfoDisplayFormatter.FormatOptionalDouble(ChartInfo?.mainbpm);

    public double? ChartMainBpmSortKey => ChartInfo?.mainbpm;

    public string ChartMaxBpmText => ChartInfoDisplayFormatter.FormatOptionalDouble(ChartInfo?.maxbpm);

    public double? ChartMaxBpmSortKey => ChartInfo?.maxbpm;

    public string ChartMinBpmText => ChartInfoDisplayFormatter.FormatOptionalDouble(ChartInfo?.minbpm);

    public double? ChartMinBpmSortKey => ChartInfo?.minbpm;

    public string ChartDurationText => ChartInfoDisplayFormatter.FormatDuration(ChartInfo?.length);

    public int? ChartDurationSortKey => ChartInfo?.length;

    public string ChartJudgeText => ChartInfoDisplayFormatter.FormatJudge(ChartInfo?.judge);

    public int? ChartJudgeSortKey => ChartInfo?.judge;

    public string ChartJudgeColorKey => ChartInfoDisplayFormatter.GetJudgeColorKey(ChartInfo?.judge);

    public string ChartJudgePercentText => ChartInfoDisplayFormatter.FormatOptionalInt(ChartInfo?.judge);

    public string ChartFeatureText => ChartInfo == null ? string.Empty : ChartInfoDisplayFormatter.FormatFeature(ChartInfo.feature);

    public int? ChartFeatureSortKey => ChartInfo?.feature;

    public int? ChartNotes => ChartInfo?.notes;

    public int? ChartLongNotes => ChartInfo?.ln;

    public int? ChartScratchNotes => ChartInfoDisplayFormatter.GetScratchNotes(ChartInfo);

    public string ChartTotalText => ChartInfoDisplayFormatter.FormatOptionalDouble(ChartInfo?.total);

    public double? ChartTotalSortKey => ChartInfo?.total;

    public bool ChartTotalUndefined => ChartInfo == null || !ChartInfo.total_defined;

    public string ChartTotalPerNoteText => ChartInfoDisplayFormatter.FormatFixedTwo(ChartInfoDisplayFormatter.GetTotalPerNote(ChartInfo));

    public double? ChartTotalPerNoteSortKey => ChartInfoDisplayFormatter.GetTotalPerNote(ChartInfo);

    public string ChartDensityText => ChartInfoDisplayFormatter.FormatOptionalDouble(ChartInfo?.density);

    public double? ChartDensitySortKey => ChartInfo?.density;

    public string ChartPeakDensityText => ChartInfoDisplayFormatter.FormatOptionalDouble(ChartInfo?.peakdensity);

    public double? ChartPeakDensitySortKey => ChartInfo?.peakdensity;

    public string ChartEndDensityText => ChartInfoDisplayFormatter.FormatOptionalDouble(ChartInfo?.enddensity);

    public double? ChartEndDensitySortKey => ChartInfo?.enddensity;

    public int? ChartSoflanCount => ChartInfo?.speedchange_count;

    private void OnSourcePropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        RaisePropertyChanged(e.PropertyName);
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(BMSFile.clear) || e.PropertyName == nameof(BMSFile.rank) || e.PropertyName == nameof(BMSFile.bmsScore))
        {
            RaisePropertyChanged(nameof(ClearDisplayText));
            RaisePropertyChanged(nameof(RankDisplayText));
        }
    }

    private LR2SongDBExtended.bmson_song GetBmsonSong()
    {
        return BmsFile is PendingChartEntry { IsBmsonChart: true }
            ? ((PendingChartEntry)BmsFile).BmsonSong
            : BmsonSong;
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
