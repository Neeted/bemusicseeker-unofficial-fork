using System;
using System.Globalization;
using System.Text.RegularExpressions;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Livet;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// プレイリスト詳細表示の一覧行です。
/// 表示値は source snapshot から複製し、playlist 編集に必要な一部プロパティだけを更新可能にします。
/// </summary>
internal sealed class PlaylistDetailRow : NotificationObject
{
    private static readonly Regex levelParseRegex = new("([+-]?\\d+(\\.\\d*)?|\\.\\d+)", RegexOptions.Compiled);

    private string level;

    private Uri url;

    private Uri urlDiff;

    private string commentValue;

    private string memoValue;

    /// <summary>
    /// 元の playlist エントリです。
    /// </summary>
    internal BMSTableEntry Entry { get; }

    /// <summary>
    /// 対応する実体譜面です。未所持行では null です。
    /// </summary>
    internal BMSFile RealFile { get; }

    internal LR2SongDBExtended.bmson_song ResolvedBmson { get; }

    internal BMSFile CompatibilityBmsFile { get; }

    /// <summary>
    /// 実体譜面を所持しているかどうかです。
    /// </summary>
    internal bool IsOwned { get; }

    internal ChartFile Chart { get; private set; }

    internal double? EntryLevelSortKey { get; private set; }

    public string Title { get; }

    public string Artist { get; }

    public string genre { get; }

    public int? mode { get; }

    public string tag { get; }

    public bool HasZeroNoteMismatchWarning { get; }

    public bool HasHighlightedWarning { get; }

    public string DisplayWarning { get; }

    public string WarningDigestText { get; }

    public string WarningTooltipText { get; }

    public string hash { get; }

    public string sha256 { get; }

    public string Folder { get; }

    public string path { get; }

    public string instl_dst { get; }

    public string InstallDestinationTitle { get; }

    public string InstallDestinationArtist { get; }

    public int? WAVHealth { get; }

    public int? BGAHealth { get; }

    public int? MovieHealth { get; }

    public string encoding { get; }

    public string RefTablesSymbols { get; }

    public string RefTablesNames { get; }

    public ClearType clear { get; }

    public RankType rank { get; }

    public string ClearDisplayText => ScoreDisplayTextFormatter.FormatClear(clear);

    public string RankDisplayText => ScoreDisplayTextFormatter.FormatRank(rank);

    public double? rate { get; }

    public double? rateDouble => score.HasValue && totalnotes.HasValue && totalnotes.Value > 0 ? (double?)((double)score.Value / 2.0 / totalnotes.Value) : null;

    public int? score { get; }

    public int? totalnotes { get; }

    public int? maxcombo { get; }

    public int? minbp { get; }

    public string rankingString { get; }

    public DateTime? rankingLastupdate { get; }

    public double? stddevVal { get; }

    public double? scoreDifficulty { get; }

    public BMSFile.BMSFileStatus status { get; }

    public string lr2_bmsid { get; }

    public string name_diff { get; }

    public string UrlDownloadIconText => url != null && url.IsAbsoluteUri ? "download" : string.Empty;

    public string UrlToolTipText => url != null && url.IsAbsoluteUri ? url.ToString() : null;

    public string UrlDiffDownloadIconText => urlDiff != null && urlDiff.IsAbsoluteUri ? "download" : string.Empty;

    public string UrlDiffToolTipText
    {
        get
        {
            if (urlDiff == null || !urlDiff.IsAbsoluteUri)
            {
                return null;
            }
            if (!string.IsNullOrWhiteSpace(name_diff))
            {
                if (!name_diff.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                {
                    return name_diff + Environment.NewLine + urlDiff;
                }
                return urlDiff.ToString();
            }
            return urlDiff.ToString();
        }
    }

    public string ChartLevelText { get; }

    public double? ChartLevelSortKey { get; }

    public bool ChartLevelUndefined { get; }

    public string ChartDifficultyText { get; }

    public int? ChartDifficultySortKey { get; }

    public string ChartDifficultyColorKey { get; }

    public bool ChartDifficultyUndefined { get; }

    public string ChartMainBpmText { get; }

    public double? ChartMainBpmSortKey { get; }

    public string ChartMaxBpmText { get; }

    public double? ChartMaxBpmSortKey { get; }

    public string ChartMinBpmText { get; }

    public double? ChartMinBpmSortKey { get; }

    public string ChartDurationText { get; }

    public int? ChartDurationSortKey { get; }

    public string ChartJudgeText { get; }

    public int? ChartJudgeSortKey { get; }

    public string ChartJudgeColorKey { get; }

    public string ChartJudgePercentText { get; }

    public string ChartFeatureText { get; }

    public int? ChartFeatureSortKey { get; }

    public int? ChartNotes { get; }

    public int? ChartLongNotes { get; }

    public int? ChartScratchNotes { get; }

    public string ChartTotalText { get; }

    public double? ChartTotalSortKey { get; }

    public bool ChartTotalUndefined { get; }

    public string ChartTotalPerNoteText { get; }

    public double? ChartTotalPerNoteSortKey { get; }

    public string ChartDensityText { get; }

    public double? ChartDensitySortKey { get; }

    public string ChartPeakDensityText { get; }

    public double? ChartPeakDensitySortKey { get; }

    public string ChartEndDensityText { get; }

    public double? ChartEndDensitySortKey { get; }

    public int? ChartSoflanCount { get; }

    internal PlaylistDetailRow(PlaylistDetailSourceRow source)
    {
        if (source == null)
        {
            throw new ArgumentNullException(nameof(source));
        }
        Entry = source.Entry;
        RealFile = source.RealFile;
        ResolvedBmson = source.ResolvedBmson;
        CompatibilityBmsFile = source.CompatibilityBmsFile;
        IsOwned = source.IsOwned;
        Chart = source.Chart;
        EntryLevelSortKey = source.EntryLevelSortKey;
        level = source.Level;
        url = source.Url;
        urlDiff = source.Url_diff;
        commentValue = source.comment;
        memoValue = source.memo;
        Title = source.Title;
        Artist = source.Artist;
        genre = source.genre;
        mode = source.mode;
        tag = source.tag;
        HasZeroNoteMismatchWarning = source.HasZeroNoteMismatchWarning;
        HasHighlightedWarning = source.HasHighlightedWarning;
        DisplayWarning = source.DisplayWarning;
        WarningDigestText = source.WarningDigestText;
        WarningTooltipText = source.WarningTooltipText;
        hash = source.hash;
        sha256 = source.sha256;
        Folder = source.Folder;
        path = source.path;
        instl_dst = source.instl_dst;
        InstallDestinationTitle = source.InstallDestinationTitle;
        InstallDestinationArtist = source.InstallDestinationArtist;
        WAVHealth = source.WAVHealth;
        BGAHealth = source.BGAHealth;
        MovieHealth = source.MovieHealth;
        encoding = source.encoding;
        RefTablesSymbols = source.RefTablesSymbols;
        RefTablesNames = source.RefTablesNames;
        clear = source.clear;
        rank = source.rank;
        rate = source.rate;
        score = source.score;
        totalnotes = source.totalnotes;
        maxcombo = source.maxcombo;
        minbp = source.minbp;
        rankingString = source.rankingString;
        rankingLastupdate = source.rankingLastupdate;
        stddevVal = source.stddevVal;
        scoreDifficulty = source.scoreDifficulty;
        status = source.status;
        lr2_bmsid = source.lr2_bmsid;
        name_diff = source.name_diff;
        ChartLevelText = source.ChartLevelText;
        ChartLevelSortKey = source.ChartLevelSortKey;
        ChartLevelUndefined = source.ChartLevelUndefined;
        ChartDifficultyText = source.ChartDifficultyText;
        ChartDifficultySortKey = source.ChartDifficultySortKey;
        ChartDifficultyColorKey = source.ChartDifficultyColorKey;
        ChartDifficultyUndefined = source.ChartDifficultyUndefined;
        ChartMainBpmText = source.ChartMainBpmText;
        ChartMainBpmSortKey = source.ChartMainBpmSortKey;
        ChartMaxBpmText = source.ChartMaxBpmText;
        ChartMaxBpmSortKey = source.ChartMaxBpmSortKey;
        ChartMinBpmText = source.ChartMinBpmText;
        ChartMinBpmSortKey = source.ChartMinBpmSortKey;
        ChartDurationText = source.ChartDurationText;
        ChartDurationSortKey = source.ChartDurationSortKey;
        ChartJudgeText = source.ChartJudgeText;
        ChartJudgeSortKey = source.ChartJudgeSortKey;
        ChartJudgeColorKey = source.ChartJudgeColorKey;
        ChartJudgePercentText = source.ChartJudgePercentText;
        ChartFeatureText = source.ChartFeatureText;
        ChartFeatureSortKey = source.ChartFeatureSortKey;
        ChartNotes = source.ChartNotes;
        ChartLongNotes = source.ChartLongNotes;
        ChartScratchNotes = source.ChartScratchNotes;
        ChartTotalText = source.ChartTotalText;
        ChartTotalSortKey = source.ChartTotalSortKey;
        ChartTotalUndefined = source.ChartTotalUndefined;
        ChartTotalPerNoteText = source.ChartTotalPerNoteText;
        ChartTotalPerNoteSortKey = source.ChartTotalPerNoteSortKey;
        ChartDensityText = source.ChartDensityText;
        ChartDensitySortKey = source.ChartDensitySortKey;
        ChartPeakDensityText = source.ChartPeakDensityText;
        ChartPeakDensitySortKey = source.ChartPeakDensitySortKey;
        ChartEndDensityText = source.ChartEndDensityText;
        ChartEndDensitySortKey = source.ChartEndDensitySortKey;
        ChartSoflanCount = source.ChartSoflanCount;
    }

    public string Level
    {
        get
        {
            return level;
        }
        set
        {
            string normalized = value ?? string.Empty;
            if (level == normalized)
            {
                return;
            }
            level = normalized;
            if (string.IsNullOrWhiteSpace(normalized))
            {
                Entry.level = null;
                EntryLevelSortKey = null;
            }
            else
            {
                Match match = levelParseRegex.Match(normalized.Trim());
                if (match.Success)
                {
                    if (double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedLevel))
                    {
                        Entry.level = parsedLevel;
                        EntryLevelSortKey = parsedLevel;
                    }
                }
            }
            RefreshEditableChartSnapshot();
            RaisePropertyChanged(nameof(Level));
        }
    }

    private void RefreshEditableChartSnapshot()
    {
        if (Chart?.BmsFile != null || Chart?.BmsonSong != null)
        {
            return;
        }
        Chart = ChartFileProjection.FromBmsMetadata(
            path,
            hash,
            sha256,
            Title,
            Artist,
            genre,
            Folder,
            tag,
            Entry?.level,
            mode,
            Chart?.ChartInfo);
    }

    public Uri Url
    {
        get
        {
            return url;
        }
        set
        {
            if (Equals(url, value))
            {
                return;
            }
            url = value;
            Entry.Url = value;
            RaisePropertyChanged(nameof(Url));
            RaisePropertyChanged(nameof(UrlDownloadIconText));
            RaisePropertyChanged(nameof(UrlToolTipText));
        }
    }

    public Uri Url_diff
    {
        get
        {
            return urlDiff;
        }
        set
        {
            if (Equals(urlDiff, value))
            {
                return;
            }
            urlDiff = value;
            Entry.Url_diff = value;
            RaisePropertyChanged(nameof(Url_diff));
            RaisePropertyChanged(nameof(UrlDiffDownloadIconText));
            RaisePropertyChanged(nameof(UrlDiffToolTipText));
        }
    }

    public string comment
    {
        get
        {
            return commentValue;
        }
        set
        {
            string normalized = value ?? string.Empty;
            if (commentValue == normalized)
            {
                return;
            }
            commentValue = normalized;
            Entry.comment = normalized;
            RaisePropertyChanged(nameof(comment));
        }
    }

    public string memo
    {
        get
        {
            return memoValue;
        }
        set
        {
            string normalized = value ?? string.Empty;
            if (memoValue == normalized)
            {
                return;
            }
            memoValue = normalized;
            Entry.memo = normalized;
            RaisePropertyChanged(nameof(memo));
        }
    }
}
