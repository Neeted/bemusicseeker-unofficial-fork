using System;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// プレイリスト詳細表示の source snapshot 1 行分を表します。
/// keyword/mode/sort 用の軽量な元データとして保持し、UI へは <see cref="PlaylistDetailRow"/> を生成して渡します。
/// </summary>
internal sealed class PlaylistDetailSourceRow
{
    /// <summary>
    /// プレイリストエントリ本体です。
    /// </summary>
    internal BMSTableEntry Entry { get; }

    /// <summary>
    /// ライブラリ上の実体譜面です。未所持行では null です。
    /// </summary>
    internal BMSFile RealFile { get; }

    /// <summary>
    /// 実体譜面を所持しているかどうかです。
    /// </summary>
    internal bool IsOwned => RealFile != null && !string.IsNullOrWhiteSpace(RealFile.path);

    internal string Title { get; }

    internal string Artist { get; }

    internal string genre { get; }

    internal int? mode { get; }

    internal string tag { get; }

    internal Uri Url { get; }

    internal Uri Url_diff { get; }

    internal string name_diff { get; }

    internal string warning { get; }

    internal bool HasZeroNoteMismatchWarning { get; }

    internal bool HasHighlightedWarning { get; }

    internal string DisplayWarning { get; }

    internal string comment { get; }

    internal string memo { get; }

    internal string hash { get; }

    internal string Folder { get; }

    internal string path { get; }

    internal string instl_dst { get; }

    internal int? WAVHealth { get; }

    internal int? BGAHealth { get; }

    internal int? MovieHealth { get; }

    internal string encoding { get; }

    internal string RefTablesSymbols { get; }

    internal string RefTablesNames { get; }

    internal ClearType clear { get; }

    internal RankType rank { get; }

    internal double? rate { get; }

    internal double? rateDouble => rate;

    internal int? score { get; }

    internal int? totalnotes { get; }

    internal int? maxcombo { get; }

    internal int? minbp { get; }

    internal string rankingString { get; }

    internal DateTime? rankingLastupdate { get; }

    internal double? stddevVal { get; }

    internal double? scoreDifficulty { get; }

    internal BMSFile.BMSFileStatus status { get; }

    internal string lr2_bmsid { get; }

    internal double? EntryLevelSortKey { get; }

    internal string Level { get; }

    internal string SearchText { get; }

    internal PlaylistDetailSourceRow(BMSTableEntry entry, BMSFile realFile, BMSFile scoreProbe = null)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        RealFile = realFile;
        BMSFile snapshotSource = realFile ?? scoreProbe;
        Title = realFile?.Title ?? entry.title ?? string.Empty;
        Artist = realFile?.Artist ?? entry.artist ?? string.Empty;
        genre = realFile?.genre ?? string.Empty;
        mode = realFile?.mode ?? scoreProbe?.mode;
        tag = realFile?.tag ?? string.Empty;
        Url = entry.Url;
        Url_diff = entry.Url_diff;
        name_diff = entry.name_diff ?? string.Empty;
        warning = snapshotSource?.warning ?? string.Empty;
        HasZeroNoteMismatchWarning = snapshotSource?.HasZeroNoteMismatchWarning ?? false;
        HasHighlightedWarning = snapshotSource?.HasHighlightedWarning ?? false;
        DisplayWarning = snapshotSource?.DisplayWarning ?? warning;
        comment = entry.comment ?? string.Empty;
        memo = entry.memo ?? string.Empty;
        hash = realFile?.hash ?? entry.md5 ?? string.Empty;
        Folder = entry.folder ?? string.Empty;
        path = realFile?.path ?? string.Empty;
        instl_dst = snapshotSource?.instl_dst ?? string.Empty;
        WAVHealth = snapshotSource?.WAVHealth;
        BGAHealth = snapshotSource?.BGAHealth;
        MovieHealth = snapshotSource?.MovieHealth;
        encoding = snapshotSource?.encoding ?? string.Empty;
        RefTablesSymbols = realFile?.RefTablesSymbols ?? string.Empty;
        RefTablesNames = realFile?.RefTablesNames ?? string.Empty;
        clear = snapshotSource?.clear ?? ClearType.NO_SONG;
        rank = snapshotSource?.rank ?? RankType.INVALID;
        rate = snapshotSource?.rate;
        score = snapshotSource?.score;
        totalnotes = snapshotSource?.totalnotes;
        maxcombo = snapshotSource?.maxcombo;
        minbp = snapshotSource?.minbp;
        rankingString = snapshotSource?.rankingString ?? string.Empty;
        rankingLastupdate = snapshotSource?.rankingLastupdate;
        stddevVal = snapshotSource?.stddevVal;
        scoreDifficulty = snapshotSource?.scoreDifficulty;
        status = snapshotSource?.status ?? BMSFile.BMSFileStatus.NONE;
        lr2_bmsid = entry.lr2_bmsid ?? string.Empty;
        EntryLevelSortKey = entry.level;
        Level = BuildLevelText(entry, realFile);
        SearchText = BuildSearchText();
    }

    /// <summary>
    /// UI 表示用の lightweight row を生成します。
    /// </summary>
    /// <returns>DataGrid 表示用 row。</returns>
    internal PlaylistDetailRow CreateViewRow()
    {
        return new PlaylistDetailRow(this);
    }

    private static string BuildLevelText(BMSTableEntry entry, BMSFile realFile)
    {
        if (entry?.level != null)
        {
            return entry.level.ToString();
        }
        return realFile?.level?.ToString() ?? string.Empty;
    }

    private string BuildSearchText()
    {
        StringBuilder builder = new StringBuilder(128);
        builder.Append(Title);
        builder.Append('@');
        builder.Append(genre);
        builder.Append('@');
        builder.Append(Artist);
        builder.Append('@');
        builder.Append(tag);
        builder.Append('@');
        builder.Append(path);
        builder.Append('@');
        builder.Append(RefTablesSymbols);
        builder.Append('@');
        builder.Append(memo);
        builder.Append('@');
        builder.Append(comment);
        return builder.ToString().ToUpperInvariant();
    }
}

/// <summary>
/// playlist 未所持行の score snapshot 取得に使う最小の BMSFile 実装です。
/// UI 表示へは公開せず、source build 中だけ利用します。
/// </summary>
internal sealed class PlaylistScoreProbeBmsFile : BMSFile
{
    /// <summary>
    /// エントリ情報から score lookup 用の最小 snapshot を適用します。
    /// </summary>
    /// <param name="entry">対象エントリ。</param>
    internal void ApplyEntrySnapshot(BMSTableEntry entry)
    {
        if (entry == null)
        {
            throw new ArgumentNullException(nameof(entry));
        }
        hash = entry.md5;
        path = string.Empty;
        mode = null;
    }
}
