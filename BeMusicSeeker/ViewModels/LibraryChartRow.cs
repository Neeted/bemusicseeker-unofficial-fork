using System;
using System.ComponentModel;
using System.Globalization;
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

    internal bool IsBmson => (BmsFile is PendingChartEntry pending && pending.IsBmsonChart) || (BmsonSong != null && BmsFile == null);

    internal bool IsBms => BmsFile != null && !PendingChartEntry.IsBmsonChartFile(BmsFile);

    internal OwnedChartRef Chart
    {
        get
        {
            LR2SongDBExtended.bmson_song bmsonSong = GetBmsonSong();
            if (bmsonSong != null)
            {
                PendingChartEntry pending = BmsFile as PendingChartEntry;
                bool isPendingBmson = pending?.IsBmsonChart == true;
                return new OwnedChartRef(
                    OwnedChartKind.Bmson,
                    isPendingBmson ? pending.path : bmsonSong.path,
                    isPendingBmson ? pending.hash : bmsonSong.md5,
                    isPendingBmson ? pending.sha256 : bmsonSong.sha256,
                    isPendingBmson ? pending.Title : BmsonSongParser.ComposeDisplayTitle(bmsonSong),
                    isPendingBmson ? pending.Artist : bmsonSong.artist,
                    isPendingBmson ? pending.level ?? bmsonSong.level : bmsonSong.level,
                    isPendingBmson ? pending.mode ?? BmsonSongParser.ResolvePlaylistMode(bmsonSong.mode_hint) : BmsonSongParser.ResolvePlaylistMode(bmsonSong.mode_hint),
                    isPendingBmson ? pending.ChartInfo : bmsonSong.ChartInfo,
                    BmsFile,
                    bmsonSong);
            }
            return new OwnedChartRef(
                OwnedChartKind.Bms,
                BmsFile?.path,
                BmsFile?.hash,
                BmsFile?.sha256,
                BmsFile?.Title,
                BmsFile?.Artist,
                ParseNullableDouble(BmsFile?.Level),
                BmsFile?.mode,
                BmsFile?.ChartInfo,
                BmsFile,
                null);
        }
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
        PendingChartEntry pending = file as PendingChartEntry;
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

    public string warning => BmsFile?.warning ?? string.Empty;

    public bool HasZeroNoteMismatchWarning => BmsFile?.HasZeroNoteMismatchWarning ?? false;

    public bool HasHighlightedWarning => BmsFile?.HasHighlightedWarning ?? false;

    public bool HasFailureStatus => false;

    public string DisplayWarning => BmsFile?.DisplayWarning ?? warning;

    public string WarningDigestText => BmsFile?.WarningDigestText ?? DisplayWarning;

    public string WarningTooltipText => BmsFile?.WarningTooltipText ?? DisplayWarning;

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
        get => BmsFile?.instl_dst ?? string.Empty;
        set
        {
            if (BmsFile != null)
            {
                BmsFile.instl_dst = value;
            }
        }
    }

    public string InstallDestinationTitle => BmsFile?.InstallDestinationTitle ?? string.Empty;

    public string InstallDestinationArtist => BmsFile?.InstallDestinationArtist ?? string.Empty;

    public int? WAVHealth => BmsFile?.WAVHealth ?? BmsonSong?.MaintenanceInfo?.WAVHealth;

    public int? BGAHealth => BmsFile?.BGAHealth ?? BmsonSong?.MaintenanceInfo?.BGAHealth;

    public int? MovieHealth => BmsFile?.MovieHealth ?? BmsonSong?.MaintenanceInfo?.MovieHealth;

    public bool? StagefileHealth => BmsFile?.StagefileHealth ?? BmsonSong?.MaintenanceInfo?.StagefileHealth;

    public bool? BannerHealth => BmsFile?.BannerHealth ?? BmsonSong?.MaintenanceInfo?.BannerHealth;

    public bool? BackbmpHealth => BmsFile?.BackbmpHealth ?? BmsonSong?.MaintenanceInfo?.BackbmpHealth;

    public string encoding => BmsFile?.encoding ?? BmsonSong?.MaintenanceInfo?.encoding ?? string.Empty;

    public string RefTablesSymbols => BmsFile?.RefTablesSymbols ?? string.Empty;

    public string RefTablesNames => BmsFile?.RefTablesNames ?? string.Empty;

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
        return (BmsFile as PendingChartEntry)?.IsBmsonChart == true
            ? ((PendingChartEntry)BmsFile).BmsonSong
            : BmsonSong;
    }

    private static double? ParseNullableDouble(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out double currentCultureValue))
        {
            return currentCultureValue;
        }
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double invariantCultureValue))
        {
            return invariantCultureValue;
        }
        return null;
    }
}
