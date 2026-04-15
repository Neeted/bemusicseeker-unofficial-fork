using System;
using System.Globalization;
using System.Text.RegularExpressions;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using Livet;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// プレイリスト詳細表示の DataGrid 行です。
/// 表示値は source snapshot から複製し、playlist 編集に必要な一部プロパティだけを更新可能にします。
/// </summary>
internal sealed class PlaylistDetailRow : NotificationObject
{
    private static readonly Regex levelParseRegex = new Regex("([+-]?\\d+(\\.\\d*)?|\\.\\d+)", RegexOptions.Compiled);

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

    /// <summary>
    /// 実体譜面を所持しているかどうかです。
    /// </summary>
    internal bool IsOwned { get; }

    internal double? EntryLevelSortKey { get; private set; }

    public string Title { get; }

    public string Artist { get; }

    public string genre { get; }

    public int? mode { get; }

    public string tag { get; }

    public string warning { get; }

    public bool HasZeroNoteMismatchWarning { get; }

    public bool HasHighlightedWarning { get; }

    public string DisplayWarning { get; }

    public string hash { get; }

    public string sha256 { get; }

    public string Folder { get; }

    public string path { get; }

    public string instl_dst { get; }

    public int? WAVHealth { get; }

    public int? BGAHealth { get; }

    public int? MovieHealth { get; }

    public string encoding { get; }

    public string RefTablesSymbols { get; }

    public string RefTablesNames { get; }

    public ClearType clear { get; }

    public RankType rank { get; }

    public double? rate { get; }

    public double? rateDouble => rate;

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

    internal PlaylistDetailRow(PlaylistDetailSourceRow source)
    {
        if (source == null)
        {
            throw new ArgumentNullException(nameof(source));
        }
        Entry = source.Entry;
        RealFile = source.RealFile;
        IsOwned = source.IsOwned;
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
        warning = source.warning;
        HasZeroNoteMismatchWarning = source.HasZeroNoteMismatchWarning;
        HasHighlightedWarning = source.HasHighlightedWarning;
        DisplayWarning = source.DisplayWarning;
        hash = source.hash;
        sha256 = source.sha256;
        Folder = source.Folder;
        path = source.path;
        instl_dst = source.instl_dst;
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
            RaisePropertyChanged(nameof(Level));
        }
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
