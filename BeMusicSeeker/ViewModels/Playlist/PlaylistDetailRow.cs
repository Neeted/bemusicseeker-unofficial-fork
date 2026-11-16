using System;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Livet;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// プレイリスト詳細表示の一覧行です。
/// 表示値は同じ不変 source 投影を参照し、所持 BMS・bmson の再生状態は共通の再生管理主体の現在値をstatus getterだけへ重ねます。
/// playlist 編集に必要な一部プロパティだけを更新可能にします。
/// </summary>
internal sealed class PlaylistDetailRow : NotificationObject
{

    private PlaylistDetailSourceRow source;
    private readonly System.Collections.Generic.Dictionary<string, object> drafts = [];

    private Func<ChartFile, ChartFileStatus> playbackStatusProvider;

    /// <summary>現在の再生ownerをstatus getterだけへ接続し、Chartと編集値を変更しません。</summary>
    internal void SetPlaybackStatusProvider(Func<ChartFile, ChartFileStatus> provider) => playbackStatusProvider = provider;

    /// <summary>
    /// 元の playlist エントリです。
    /// </summary>
    internal BMSTableEntry Entry => source.Entry;

    /// <summary>
    /// 実体譜面を所持しているかどうかです。
    /// </summary>
    internal bool IsOwned => source.IsOwned;

    internal ChartFile Chart => source.Chart;

    internal double? EntryLevelSortKey => source.EntryLevelSortKey;

    public string Title => source.Title;

    public string Artist => source.Artist;

    public string genre => source.genre;

    public int? mode => source.mode;

    public string tag => source.tag;

    public bool HasZeroNoteMismatchWarning => source.HasZeroNoteMismatchWarning;

    public bool HasHighlightedWarning => source.HasHighlightedWarning;

    public string DisplayWarning => source.DisplayWarning;

    public string WarningDigestText => source.WarningDigestText;

    public string WarningTooltipText => source.WarningTooltipText;

    public string hash => source.hash;

    public string sha256 => source.sha256;

    public string Folder => source.Folder;

    public string path => source.path;

    public string instl_dst => source.instl_dst;

    public string InstallDestinationTitle => source.InstallDestinationTitle;

    public string InstallDestinationArtist => source.InstallDestinationArtist;

    public int? WAVHealth => source.WAVHealth;

    public int? BGAHealth => source.BGAHealth;

    public int? MovieHealth => source.MovieHealth;

    public string encoding => source.encoding;

    public string RefTablesSymbols => source.RefTablesSymbols;

    public string RefTablesNames => source.RefTablesNames;

    public ClearType clear => source.clear;

    public RankType rank => source.rank;

    public string ClearDisplayText => ScoreDisplayTextFormatter.FormatClear(clear);

    public string RankDisplayText => ScoreDisplayTextFormatter.FormatRank(rank);

    public double? rate => source.rate;

    public double? rateDouble => score.HasValue && totalnotes.HasValue && totalnotes.Value > 0 ? (double?)((double)score.Value / 2.0 / totalnotes.Value) : null;

    public int? score => source.score;

    public int? totalnotes => source.totalnotes;

    public int? maxcombo => source.maxcombo;

    public int? minbp => source.minbp;

    public string rankingString => source.rankingString;

    public DateTime? rankingLastupdate => source.rankingLastupdate;

    public double? stddevVal => source.stddevVal;

    public double? scoreDifficulty => source.scoreDifficulty;

    /// <summary>
    /// 譜面の現在状態です。接続された共通の再生管理主体から BMS・bmson の再生状態を読み、再生以外の投影状態を保ちます。
    /// </summary>
    public ChartFileStatus status => (SourceStatus & ~ChartFileStatus.PLAYALL)
        | (playbackStatusProvider?.Invoke(Chart) ?? ChartFileStatus.NONE);

    private ChartFileStatus SourceStatus => Chart?.Status ?? ChartFileStatus.NONE;

    public string lr2_bmsid => source.lr2_bmsid;

    public string name_diff => source.name_diff;

    public string UrlDownloadIconText => Url != null && Url.IsAbsoluteUri ? "download" : string.Empty;

    public string UrlToolTipText => Url != null && Url.IsAbsoluteUri ? Url.ToString() : null;

    public string UrlDiffDownloadIconText => Url_diff != null && Url_diff.IsAbsoluteUri ? "download" : string.Empty;

    public string UrlDiffToolTipText
    {
        get
        {
            if (Url_diff == null || !Url_diff.IsAbsoluteUri)
            {
                return null;
            }
            if (!string.IsNullOrWhiteSpace(name_diff))
            {
                if (!name_diff.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                {
                    return name_diff + Environment.NewLine + Url_diff;
                }
                return Url_diff.ToString();
            }
            return Url_diff.ToString();
        }
    }

    public string ChartLevelText => source.ChartLevelText;

    public double? ChartLevelSortKey => source.ChartLevelSortKey;

    public bool ChartLevelUndefined => source.ChartLevelUndefined;

    public string ChartDifficultyText => source.ChartDifficultyText;

    public int? ChartDifficultySortKey => source.ChartDifficultySortKey;

    public string ChartDifficultyColorKey => source.ChartDifficultyColorKey;

    public bool ChartDifficultyUndefined => source.ChartDifficultyUndefined;

    public string ChartMainBpmText => source.ChartMainBpmText;

    public double? ChartMainBpmSortKey => source.ChartMainBpmSortKey;

    public string ChartMaxBpmText => source.ChartMaxBpmText;

    public double? ChartMaxBpmSortKey => source.ChartMaxBpmSortKey;

    public string ChartMinBpmText => source.ChartMinBpmText;

    public double? ChartMinBpmSortKey => source.ChartMinBpmSortKey;

    public string ChartDurationText => source.ChartDurationText;

    public int? ChartDurationSortKey => source.ChartDurationSortKey;

    public string ChartJudgeText => source.ChartJudgeText;

    public int? ChartJudgeSortKey => source.ChartJudgeSortKey;

    public string ChartJudgeColorKey => source.ChartJudgeColorKey;

    public string ChartJudgePercentText => source.ChartJudgePercentText;

    public string ChartFeatureText => source.ChartFeatureText;

    public int? ChartFeatureSortKey => source.ChartFeatureSortKey;

    public int? ChartNotes => source.ChartNotes;

    public int? ChartLongNotes => source.ChartLongNotes;

    public int? ChartScratchNotes => source.ChartScratchNotes;

    public string ChartTotalText => source.ChartTotalText;

    public double? ChartTotalSortKey => source.ChartTotalSortKey;

    public bool ChartTotalUndefined => source.ChartTotalUndefined;

    public string ChartTotalPerNoteText => source.ChartTotalPerNoteText;

    public double? ChartTotalPerNoteSortKey => source.ChartTotalPerNoteSortKey;

    public string ChartDensityText => source.ChartDensityText;

    public double? ChartDensitySortKey => source.ChartDensitySortKey;

    public string ChartPeakDensityText => source.ChartPeakDensityText;

    public double? ChartPeakDensitySortKey => source.ChartPeakDensitySortKey;

    public string ChartEndDensityText => source.ChartEndDensityText;

    public double? ChartEndDensitySortKey => source.ChartEndDensitySortKey;

    public int? ChartSoflanCount => source.ChartSoflanCount;

    internal PlaylistDetailRow(PlaylistDetailSourceRow source)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
    }

    /// <summary>確定入力だけを差し替えます。編集中の短命 draft は保持します。</summary>
    internal void ApplySource(PlaylistDetailSourceRow next, bool notify = true)
    {
        source = next ?? throw new ArgumentNullException(nameof(next));
        if (notify) { PublishSourceChanged(); }
    }

    /// <summary>無通知で確定した表示入力の変更を lock 解放後に公開します。</summary>
    internal void PublishSourceChanged() => RaisePropertyChanged(string.Empty);

    /// <summary>編集終了時に draft を破棄し、確定した投影値を表示します。</summary>
    internal void EndDraft()
    {
        drafts.Clear();
        RaisePropertyChanged(string.Empty);
    }

    private T GetDraft<T>(string name, T value) => drafts.TryGetValue(name, out object draft) ? (T)draft : value;

    private void SetDraft(string name, object value)
    {
        drafts[name] = value;
        RaisePropertyChanged(name);
    }

    public string Level { get => GetDraft(nameof(Level), source.Level); set => SetDraft(nameof(Level), value ?? string.Empty); }
    public Uri Url { get => GetDraft(nameof(Url), source.Url); set => SetDraft(nameof(Url), value); }
    public Uri Url_diff { get => GetDraft(nameof(Url_diff), source.Url_diff); set => SetDraft(nameof(Url_diff), value); }
    public string comment { get => GetDraft(nameof(comment), source.comment); set => SetDraft(nameof(comment), value ?? string.Empty); }
    public string memo { get => GetDraft(nameof(memo), source.memo); set => SetDraft(nameof(memo), value ?? string.Empty); }
}
