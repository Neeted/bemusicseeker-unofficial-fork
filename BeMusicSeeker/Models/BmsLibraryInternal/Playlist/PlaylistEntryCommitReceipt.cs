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
    ImmutableArray<PlaylistEntryChange> Changes, bool IsFullReplacement = false,
    ImmutableArray<string> FolderOrder = default);

/// <summary>reader 内で同時に捕捉した表の entry 版と不変値です。元 entry は識別だけに使います。</summary>
internal sealed record PlaylistEntryReadSnapshot(
    BMSTable Table, int Revision,
    ImmutableArray<PlaylistEntryChange> Entries);
