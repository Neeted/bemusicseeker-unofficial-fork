using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

/// <summary>元集合が必要とする参照bucketと反映済み位置を保持します。初回捕捉後の値は構造共有し、可変索引を行getterから読みません。</summary>
internal sealed class ChartListReferenceInput
{
    private ImmutableDictionary<PlaylistEntryLookupKey, PlaylistReferenceDisplay> values;
    /// <summary>初回投影の開始前に要求を作り、必要hashの登録後に一回捕捉します。</summary>
    internal ChartListReferenceInput(BMSLibrary library) => Library = library;
    private ChartListReferenceInput(BMSLibrary library, long revision, ImmutableDictionary<PlaylistEntryLookupKey, PlaylistReferenceDisplay> values)
    { Library = library; Revision = revision; this.values = values; }
    /// <summary>参照確定の捕捉元です。libraryなしの独立投影ではnullです。</summary>
    internal BMSLibrary Library { get; }
    /// <summary>この入力の値へ適用済みの参照位置です。最新番号の付替えには使いません。</summary>
    internal long Revision { get; private set; }
    /// <summary>初回捕捉が終わり、行・sort・filterへ公開可能かを返します。</summary>
    internal bool IsCaptured => values != null;

    /// <summary>初回のhash登録後、表示値を読む前に必要範囲を一回捕捉します。MD5/SHA両方を保持します。</summary>
    internal void CaptureInitial(IEnumerable<PlaylistEntryLookupKey> keys, CancellationToken cancellationToken = default)
    {
        if (IsCaptured) { return; }
        PlaylistReferenceReadSnapshot snapshot = Library?.CapturePlaylistReferenceReadSnapshot(keys, cancellationToken);
        Revision = snapshot?.Revision ?? 0;
        values = snapshot?.Values.ToImmutableDictionary(CatalogReadSnapshot.LookupKeyComparer)
            ?? ImmutableDictionary.Create<PlaylistEntryLookupKey, PlaylistReferenceDisplay>(CatalogReadSnapshot.LookupKeyComparer);
    }

    /// <summary>存在する空MD5 bucketも優先し、消失した場合だけSHAへ進みます。</summary>
    internal PlaylistReferenceDisplay Resolve(string md5, string sha256)
    {
        if (values == null) { throw new InvalidOperationException("Reference input was read before capture."); }
        if (!string.IsNullOrWhiteSpace(md5) && values.TryGetValue(new(PlaylistEntryLookupKeyKind.Md5, md5), out PlaylistReferenceDisplay display)) { return display; }
        return !string.IsNullOrWhiteSpace(sha256) && values.TryGetValue(new(PlaylistEntryLookupKeyKind.Sha256, sha256), out display)
            ? display : PlaylistReferenceDisplay.Empty;
    }

    /// <summary>連続した参照確定のうち元集合に依存するkeyだけを反映します。空差分でも位置を保持します。</summary>
    internal ChartListReferenceInput Apply(PlaylistReferenceCommitReceipt commit, Func<PlaylistEntryLookupKey, bool> includesKey)
    {
        if (commit.Revision <= Revision) { return this; }
        if (commit.BaseRevision > Revision) { throw new InvalidOperationException("Reference input has an undelivered range."); }
        ImmutableDictionary<PlaylistEntryLookupKey, PlaylistReferenceDisplay> next = values;
        foreach (KeyValuePair<PlaylistEntryLookupKey, PlaylistReferenceDisplay> pair in commit.Changes)
        {
            if (!includesKey(pair.Key)) { continue; }
            next = pair.Value == null ? next.Remove(pair.Key) : next.SetItem(pair.Key, pair.Value);
        }
        return new(Library, commit.Revision, next);
    }

    /// <summary>元集合が最後の依存を失ったkeyだけを解放します。処理済み位置を変えず、退役keyの履歴を残しません。</summary>
    internal ChartListReferenceInput RemoveDependencies(IEnumerable<PlaylistEntryLookupKey> keys)
    {
        ImmutableDictionary<PlaylistEntryLookupKey, PlaylistReferenceDisplay> next = values.RemoveRange(keys);
        return ReferenceEquals(next, values) ? this : new(Library, Revision, next);
    }

    /// <summary>ちょうど捕捉位置へ達したときだけ新依存を組み込み、後続差分も同じ入力から適用できるようにします。</summary>
    internal ChartListReferenceInput Include(PlaylistReferenceReadSnapshot snapshot, IEnumerable<PlaylistEntryLookupKey> keys)
    {
        if (snapshot.Revision != Revision) { throw new InvalidOperationException("New reference dependencies do not match the applied boundary."); }
        ImmutableDictionary<PlaylistEntryLookupKey, PlaylistReferenceDisplay> next = values;
        foreach (PlaylistEntryLookupKey key in keys)
        { next = snapshot.Values.TryGetValue(key, out PlaylistReferenceDisplay value) ? next.SetItem(key, value) : next.Remove(key); }
        return new(Library, Revision, next);
    }
}
