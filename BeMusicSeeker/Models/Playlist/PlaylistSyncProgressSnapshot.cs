using System;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.Models;

/// <summary>
/// プレイリスト系進捗の操作種別です。表示の書式・作業名は種別から表示側が現在の資源で作り、
/// 生成元は翻訳済みの文言を持ちません。
/// </summary>
internal enum PlaylistSyncProgressKind
{
    /// <summary>既定の表同期です。</summary>
    Default,

    /// <summary>外部プレイリストの同期です。</summary>
    ExternalPlaylistSync,

    /// <summary>手動の再読込みです。</summary>
    ManualPlaylistReload,

    /// <summary>プレイリスト属性保存に伴う更新です。</summary>
    PropertyUpdate,

    /// <summary>概要からの外部属性初期化です。</summary>
    ExternalPropertyInitialization,

    /// <summary>custom folder出力の修復です。</summary>
    CustomFolderRepair,

    /// <summary>beatoraja BMT出力です。</summary>
    BmtOutput,

    /// <summary>概要から行うcustom folder出力です。</summary>
    CustomFolderOutput,

    /// <summary>外部プレイリストの取込みです。</summary>
    ExternalPlaylistImport,

    /// <summary>beatoraja表URLの取込みです。</summary>
    BeatorajaTableUrlImport
}

/// <summary>取込み系進捗の段階です。段階名は表示側が現在の資源で作ります。</summary>
internal enum PlaylistImportProgressPhase
{
    /// <summary>段階表示なしです。</summary>
    None,

    /// <summary>URLの確認です。</summary>
    CheckUrls,

    /// <summary>表の読込みです。</summary>
    LoadTables,

    /// <summary>BMT設定の復元です。</summary>
    RestoreBmt,

    /// <summary>重複の確認です。</summary>
    CheckDuplicates,

    /// <summary>プレイリストの登録です。</summary>
    RegisterPlaylists,

    /// <summary>参照の更新です。</summary>
    UpdateReferences,

    /// <summary>BMT並び順の適用です。</summary>
    ApplyBmtSort,

    /// <summary>終了処理です。</summary>
    Finish
}

/// <summary>custom folder処理の通知が表す段階です。</summary>
internal enum CustomFolderProgressStage
{
    /// <summary>処理中の表名を持つ通知です。</summary>
    TableName,

    /// <summary>出力段階を示す通知です。</summary>
    Output,

    /// <summary>LR2のDB同期段階を示す通知です。</summary>
    DbSync
}

/// <summary>
/// custom folder処理の callback へ渡す詳細です。段階の事実か生の表名を持ち、翻訳済みの文言は持ちません。
/// </summary>
/// <param name="Stage">通知が表す段階。</param>
/// <param name="TableName">段階が <see cref="CustomFolderProgressStage.TableName"/> のときの生の表名。</param>
internal readonly record struct CustomFolderProgressDetail(CustomFolderProgressStage Stage, string TableName)
{
    /// <summary>処理中の表名を持つ詳細を作ります。</summary>
    internal static CustomFolderProgressDetail ForTable(string tableName) => new(CustomFolderProgressStage.TableName, tableName ?? string.Empty);

    /// <summary>段階を示す詳細を作ります。</summary>
    internal static CustomFolderProgressDetail ForStage(CustomFolderProgressStage stage) => new(stage, string.Empty);
}

internal sealed class PlaylistSyncProgressSnapshot
{
    /// <summary>独立して実行できる進捗の生産元を識別します。</summary>
    public string Source { get; set; } = "playlist";

    /// <summary>対応する要求の発生元。開始・更新・終端で同じ値を渡します。</summary>
    internal OperationProgressRequest Request { get; set; }

    public bool IsActive { get; set; }

    public long OperationId { get; set; }

    public int TotalTableCount { get; set; }

    public int CompletedTableCount { get; set; }

    /// <summary>処理中の生の表名です。段階名や書式は含みません。</summary>
    public string CurrentTableName { get; set; }

    public Uri CurrentUri { get; set; }

    /// <summary>操作の種別です。作業名と書式は種別から表示側が作ります。</summary>
    public PlaylistSyncProgressKind Kind { get; set; }

    /// <summary>取込み系の段階です。取込み以外では <see cref="PlaylistImportProgressPhase.None"/> です。</summary>
    public PlaylistImportProgressPhase ImportPhase { get; set; }

    /// <summary>custom folder処理の通知が段階を表す場合の段階です。表名の通知では <see cref="CustomFolderProgressStage.TableName"/> です。</summary>
    public CustomFolderProgressStage CustomFolderStage { get; set; }
}
