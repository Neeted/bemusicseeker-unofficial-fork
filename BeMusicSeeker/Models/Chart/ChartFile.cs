using System.Collections.Generic;
using System.Collections.Immutable;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.Models;

internal enum ChartFileKind
{
    Bms,
    Bmson
}

/// <summary>解析または所持カタログから捕捉した、不変の共通基本値と用途別の投影です。保存行を参照しません。</summary>
internal sealed record ChartFile
{
    /// <summary>譜面の解析形式です。パスの拡張子で捕捉済み形式を置換しません。</summary>
    internal ChartFileKind Kind { get; init; }

    /// <summary>捕捉時点の実パスです。DB では exact key、物理対象では従来の大小文字規則で比較します。</summary>
    internal string Path { get; init; }

    /// <summary>捕捉パスの親フォルダです。</summary>
    internal string Directory => string.IsNullOrWhiteSpace(Path) ? null : System.IO.Path.GetDirectoryName(Path);

    /// <summary>捕捉入力の MD5 です。所持項目の識別には使いません。</summary>
    internal string Md5 { get; init; }

    /// <summary>捕捉入力の SHA-256 です。所持項目の識別には使いません。</summary>
    internal string Sha256 { get; init; }

    /// <summary>既存の重複・導入検索で使用する MD5 優先の内容検索キーです。</summary>
    internal string PrimaryLookupHash
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Md5))
            {
                return Md5;
            }
            return string.IsNullOrWhiteSpace(Sha256) ? null : Sha256;
        }
    }

    /// <summary>字幕を含む既存規則で合成した表示題名です。</summary>
    internal string Title { get; init; }

    /// <summary>表示合成前の元の題名です。</summary>
    internal string RawTitle { get; init; }

    /// <summary>表示合成前の字幕です。DB由来の合成値から元記述を復元しません。</summary>
    internal string RawSubtitle { get; init; } = string.Empty;

    /// <summary>表示合成前の作者です。</summary>
    internal string RawArtist { get; init; } = string.Empty;

    /// <summary>ファイルに記述された副作者です。</summary>
    internal string Subartist { get; init; } = string.Empty;

    /// <summary>bmsonファイル由来の譜面名です。DBの合成字幕から復元しません。</summary>
    internal string ChartName { get; init; } = string.Empty;

    /// <summary>bmsonのモード記述を保持します。</summary>
    internal string ModeHint { get; init; } = string.Empty;

    /// <summary>bmsonのプレビュー音源を保持します。</summary>
    internal string PreviewMusic { get; init; } = string.Empty;

    /// <summary>読取り時点のファイル更新時刻です。</summary>
    internal System.DateTime LastWriteTimeUtc { get; init; }

    /// <summary>BMS基本解析が求めた難易度です。</summary>
    internal int? Difficulty { get; init; }

    /// <summary>BMS基本解析が求めた判定幅です。</summary>
    internal int? Judge { get; init; }

    /// <summary>現在の所持項目だけに属する短命な識別です。</summary>
    internal OwnedChartToken Token { get; init; }

    /// <summary>副作者を含む既存規則で合成した表示作者です。</summary>
    internal string Artist { get; init; }

    /// <summary>捕捉したジャンルです。</summary>
    internal string Genre { get; init; }

    /// <summary>通常一覧で表示するフォルダ名です。保存用の LR2 folder hash ではありません。</summary>
    internal string Folder { get; init; }

    /// <summary>利用者が設定したタグです。DBの未設定値はnullのまま保持します。</summary>
    internal string Tag { get; init; }

    /// <summary>LR2の利用者設定であるお気に入り値です。</summary>
    internal int? Favorite { get; init; }

    /// <summary>LR2への初回追加時刻です。未設定の保存値を変更しません。</summary>
    internal int? AddDate { get; init; }

    /// <summary>LR2のテキスト付属フラグです。未設定と0を区別します。</summary>
    internal int? Txt { get; init; }

    /// <summary>LR2形式のファイル更新時刻です。</summary>
    internal int? Date { get; init; }

    /// <summary>既存の形式・詳細に応じた表示レベルです。</summary>
    internal string LevelText { get; init; }

    /// <summary>捕捉した数値レベルです。未記載は null です。</summary>
    internal double? Level { get; init; }

    /// <summary>BMS の鍵盤構成を表す基本値です。</summary>
    internal int? Mode { get; init; }

    /// <summary>この捕捉値に適用した不変の詳細です。最新性の正本は詳細 owner が所有します。</summary>
    internal BeMusicSeeker.Models.ChartDetails ChartInfo { get; init; }

    /// <summary>詳細から生成した表示値です。</summary>
    internal ChartInfoDisplaySnapshot ChartInfoDisplay { get; init; }

    /// <summary>既存の形式別規則で表示する字幕です。</summary>
    internal string Subtitle { get; init; }

    /// <summary>抽出済みの不変結果です。nullは未取得または解放、空集合は成功した0件です。</summary>
    internal ImmutableList<ChartResourceReference> Resources { get; init; }

    /// <summary>元のステージ画像記述です。</summary>
    internal string Stagefile { get; init; }

    /// <summary>元の背景画像記述です。</summary>
    internal string Backbmp { get; init; }

    /// <summary>元のバナー画像記述です。</summary>
    internal string Banner { get; init; }

    /// <summary>Package の導入先を投影した実パスです。</summary>
    internal string InstallDestination { get; init; }

    /// <summary>導入先の代表題名の投影です。</summary>
    internal string InstallDestinationTitle { get; init; }

    /// <summary>導入先の代表作者の投影です。</summary>
    internal string InstallDestinationArtist { get; init; }

    private ImmutableList<string> installDestinationSuggestions = ImmutableList<string>.Empty;

    /// <summary>捕捉した導入先候補です。呼出元の可変集合を保持しません。</summary>
    internal IReadOnlyList<string> InstallDestinationSuggestions
    {
        get => installDestinationSuggestions;
        init => installDestinationSuggestions = (value ?? []).ToImmutableList();
    }

    private ImmutableList<ChartWarning> warnings = ImmutableList<ChartWarning>.Empty;

    /// <summary>専門 owner または Package が捕捉した不変の構造化警告です。</summary>
    internal IReadOnlyList<ChartWarning> Warnings
    {
        get => warnings;
        init => warnings = (value ?? []).ToImmutableList();
    }

    /// <summary>音源の検査結果です。未計算は null です。</summary>
    internal int? WAVHealth { get; init; }

    /// <summary>静止画の検査結果です。未計算は null です。</summary>
    internal int? BGAHealth { get; init; }

    /// <summary>動画の検査結果です。未計算は null です。</summary>
    internal int? MovieHealth { get; init; }

    /// <summary>ステージ画像の検査結果です。未計算は null です。</summary>
    internal bool? StagefileHealth { get; init; }

    /// <summary>バナー画像の検査結果です。未計算は null です。</summary>
    internal bool? BannerHealth { get; init; }

    /// <summary>背景画像の検査結果です。未計算は null です。</summary>
    internal bool? BackbmpHealth { get; init; }

    /// <summary>捕捉した文字コードの表示値です。</summary>
    internal string EncodingName { get; init; }

    /// <summary>用途に応じたスコアの捕捉値です。スコアの正本や購読は保持しません。</summary>
    internal ChartScoreSnapshot Score { get; init; }

    /// <summary>捕捉した譜面の表示・操作状態です。</summary>
    internal ChartFileStatus Status { get; init; }

    /// <summary>資源警告を無視する既存の利用者設定です。</summary>
    internal bool ResourceHealthWarningsIgnored { get; init; }

    /// <summary>検査の由来を含む不変の保守値です。</summary>
    internal BmsLibraryInternal.ResourceHealthMaintenanceSnapshot ResourceHealthMaintenanceSnapshot { get; init; }

    /// <summary>譜面の読取り値を構成します。不変リソース結果は複製せず共有し、nullの未取得と成功0件を区別します。</summary>
    internal ChartFile(
        ChartFileKind kind,
        string path,
        string md5,
        string sha256,
        string title,
        string rawTitle,
        string artist,
        string genre,
        string folder,
        string tag,
        string levelText,
        double? level,
        int? mode,
        BeMusicSeeker.Models.ChartDetails chartInfo,
        string subtitle = null,
        ImmutableList<ChartResourceReference> resources = null,
        string stagefile = null,
        string backbmp = null,
        string banner = null,
        string installDestination = null,
        string installDestinationTitle = null,
        string installDestinationArtist = null,
        IReadOnlyList<string> installDestinationSuggestions = null,
        IReadOnlyList<ChartWarning> warnings = null,
        int? wavHealth = null,
        int? bgaHealth = null,
        int? movieHealth = null,
        bool? stagefileHealth = null,
        bool? bannerHealth = null,
        bool? backbmpHealth = null,
        string encodingName = null,
        ChartScoreSnapshot score = null,
        ChartFileStatus status = ChartFileStatus.NONE,
        bool resourceHealthWarningsIgnored = false,
        BmsLibraryInternal.ResourceHealthMaintenanceSnapshot resourceHealthMaintenanceSnapshot = null)
    {
        Kind = kind;
        Path = string.IsNullOrWhiteSpace(path) ? null : path;
        Md5 = string.IsNullOrWhiteSpace(md5) ? null : md5.Trim();
        Sha256 = string.IsNullOrWhiteSpace(sha256) ? null : sha256.Trim();
        Title = title ?? string.Empty;
        RawTitle = rawTitle ?? Title;
        Artist = artist ?? string.Empty;
        Genre = genre ?? string.Empty;
        Folder = string.IsNullOrWhiteSpace(folder) ? System.IO.Path.GetFileName(Directory) ?? string.Empty : folder;
        Tag = tag;
        LevelText = levelText ?? string.Empty;
        Level = level;
        Mode = mode;
        ChartInfo = chartInfo;
        ChartInfoDisplay = ChartInfoDisplaySnapshot.FromChartInfo(chartInfo);
        Subtitle = subtitle ?? string.Empty;
        Resources = resources;
        Stagefile = stagefile ?? string.Empty;
        Backbmp = backbmp ?? string.Empty;
        Banner = banner ?? string.Empty;
        InstallDestination = installDestination ?? string.Empty;
        InstallDestinationTitle = installDestinationTitle ?? string.Empty;
        InstallDestinationArtist = installDestinationArtist ?? string.Empty;
        InstallDestinationSuggestions = installDestinationSuggestions ?? [];
        Warnings = warnings ?? [];
        WAVHealth = wavHealth;
        BGAHealth = bgaHealth;
        MovieHealth = movieHealth;
        StagefileHealth = stagefileHealth;
        BannerHealth = bannerHealth;
        BackbmpHealth = backbmpHealth;
        EncodingName = encodingName ?? string.Empty;
        Score = score ?? ChartScoreSnapshot.NoScore(Path);
        Status = status;
        ResourceHealthWarningsIgnored = resourceHealthWarningsIgnored;
        ResourceHealthMaintenanceSnapshot = resourceHealthMaintenanceSnapshot;
    }
}
