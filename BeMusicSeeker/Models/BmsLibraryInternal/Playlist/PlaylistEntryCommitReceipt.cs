using System;
using System.Collections.Immutable;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>表の同じ確定状態から捕捉した、表示・検索・解決に必要な不変の entry 値です。</summary>
internal sealed record PlaylistEntryValue(
    string Md5, string Sha256, double? Level, string Title, string Artist,
    string Folder, string Lr2BmsId, Uri Url, Uri UrlDiff, Uri EffectiveUrl,
    Uri EffectiveUrlDiff, string NameDiff, string Comment, string Memo,
    DateTime AddDate, bool IsRemoved, string OrgMd5);

/// <summary>同じ生存 entry の値と所属・明示順序の変更です。追加前・削除後の値は null です。</summary>
internal sealed record PlaylistEntryChange(
    BMSTableEntry Entry, PlaylistEntryValue Before, PlaylistEntryValue After,
    long? BeforeOrder, long? AfterOrder);

/// <summary>DB またはランタイム補完の確定時に、値・版・対象差分を一緒に捕捉した結果です。</summary>
internal sealed record PlaylistEntryCommitReceipt(
    BMSTable Table, int BaseRevision, int Revision,
    ImmutableArray<PlaylistEntryChange> Changes,
    ImmutableArray<string> FolderOrder = default, PlaylistHeaderValue Header = null);

/// <summary>DB確定と同じ保護区間で捕捉する小さなheaderです。件数・設定・runtime・所属はそれぞれの所有者に残します。</summary>
internal sealed record PlaylistHeaderValue(int? PlaylistId, string Name, string Symbol, string CompatPrefix,
    string OutputDirectory, DateTime LastUpdate, Uri PageUrl, Uri HeaderUrl, Uri DataUrl,
    bool IsExternalSync, bool IsRootFolder, int? BmtSort, bool? IsBmtOutput, string OutputBaseName)
{
    /// <summary>現在の表のheaderを捕捉します。呼出元が表のreaderまたはwriterを保持します。</summary>
    internal static PlaylistHeaderValue Capture(BMSTable table) => new(table.playlist_id, table.name, table.symbol,
        table.compat_prefix, table.output_dir, table.last_update, table.Page_url, table.Header_url, table.Data_url,
        table.is_external_sync, table.is_root_folder, table.bmt_sort, table.is_bmt_output, table.custom_folder_output_base_name);
}

/// <summary>reader 内で同時に捕捉した表の entry 版と不変値です。元 entry は識別だけに使います。</summary>
internal sealed record PlaylistEntryReadSnapshot(
    BMSTable Table, int Revision,
    ImmutableArray<PlaylistEntryChange> Entries);
