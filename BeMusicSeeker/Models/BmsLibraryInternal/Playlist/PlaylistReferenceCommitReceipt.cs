#nullable enable
using System.Collections.Generic;
using System.Collections.Immutable;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>参照索引の同時確定した版範囲と表示差分です。nullは消失、非nullの空表示はbucketの存在を表します。</summary>
internal sealed class PlaylistReferenceCommitReceipt
{
    /// <summary>変更キーだけを不変化します。表示差分が空でも多重度等の実変更による版範囲を保持します。</summary>
    internal PlaylistReferenceCommitReceipt(long baseRevision, long revision, IEnumerable<KeyValuePair<PlaylistEntryLookupKey, PlaylistReferenceDisplay?>> changes)
    {
        BaseRevision = baseRevision; Revision = revision;
        Changes = changes.ToImmutableDictionary(CatalogReadSnapshot.LookupKeyComparer);
    }
    /// <summary>この確定の直前の索引位置です。</summary>
    internal long BaseRevision { get; }
    /// <summary>同じ索引・表示差分を採用した確定位置です。</summary>
    internal long Revision { get; }
    /// <summary>同じ確定時の値です。後続索引を読んで先行結果を補いません。</summary>
    internal IReadOnlyDictionary<PlaylistEntryLookupKey, PlaylistReferenceDisplay?> Changes { get; }
}

/// <summary>要求した参照キーの存在するbucketだけを、同じ索引版とともに捕捉した不変入力です。</summary>
internal sealed class PlaylistReferenceReadSnapshot
{
    /// <summary>要求範囲の存在する値だけを、同時捕捉した索引位置とともに不変化します。</summary>
    internal PlaylistReferenceReadSnapshot(long revision, IEnumerable<KeyValuePair<PlaylistEntryLookupKey, PlaylistReferenceDisplay>> values)
    {
        Revision = revision; Values = values.ToImmutableDictionary(CatalogReadSnapshot.LookupKeyComparer);
    }
    /// <summary>Valuesを捕捉した同じ索引位置です。</summary>
    internal long Revision { get; }
    /// <summary>不存在キーは含みません。空表示もMD5存在優先判定に必要なので保持します。</summary>
    internal IReadOnlyDictionary<PlaylistEntryLookupKey, PlaylistReferenceDisplay> Values { get; }
}
