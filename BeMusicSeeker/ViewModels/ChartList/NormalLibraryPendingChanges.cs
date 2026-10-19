using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

/// <summary>一つの通常入力が未採用の確定結果だけを保持し、新hashの捕捉位置で構造差分を組み込みます。配送済み履歴は残しません。</summary>
internal sealed class NormalLibraryPendingChanges
{
    private readonly SortedDictionary<int, PendingCatalog> catalogs = [];
    private readonly SortedDictionary<long, PlaylistReferenceCommitReceipt> references = [];
    /// <summary>この要求で未採用の構造差分または参照境界があるかを返します。</summary>
    internal bool HasPending => catalogs.Count > 0 || references.Count > 0;
    /// <summary>先頭の新依存が待つ一回捕捉位置です。必要境界なしでは0です。</summary>
    internal long RequiredReferenceRevision => catalogs.Count == 0 ? 0 : catalogs.First().Value.Capture?.Revision ?? 0;
    /// <summary>入力寿命の終了時に未採用候補を解放します。</summary>
    internal void Clear() { catalogs.Clear(); references.Clear(); }

    /// <summary>同じ入力へ実際に適用できた境界を返します。新hashは捕捉位置ちょうどで追加し、未配送ならその入力を保留します。</summary>
    internal IReadOnlyList<Step> Advance(NormalLibrarySourceRows source,
        IReadOnlyList<CatalogCommitReceipt> catalogCommits, IReadOnlyList<PlaylistReferenceCommitReceipt> referenceCommits)
    {
        foreach (CatalogCommitReceipt commit in catalogCommits)
        { if (commit.Revision > source.Snapshot.CatalogRevision) { catalogs.TryAdd(commit.Revision, new(commit)); } }
        foreach (PlaylistReferenceCommitReceipt commit in referenceCommits)
        { if (commit.Revision > source.References.Revision) { references.TryAdd(commit.Revision, commit); } }
        var applied = new List<Step>();
        while (true)
        {
            while (catalogs.Count > 0)
            {
                PendingCatalog pending = catalogs.First().Value;
                CatalogCommitReceipt commit = pending.Commit;
                if (commit.Revision <= source.Snapshot.CatalogRevision) { catalogs.Remove(commit.Revision); continue; }
                if (commit.IsFullReplacement || commit.Snapshot == null || commit.BaseRevision != source.Snapshot.CatalogRevision) { return applied; }
                if (pending.Keys == null)
                {
                    pending.Keys = source.AddedReferenceKeys(commit);
                    if (pending.Keys.Count > 0)
                    {
                        pending.Capture = source.Library?.CapturePlaylistReferenceReadSnapshot(pending.Keys)
                            ?? new(source.References.Revision, Array.Empty<KeyValuePair<PlaylistEntryLookupKey, PlaylistReferenceDisplay>>());
                        source.ObserveWork("reference_dependency_capture", pending.Keys.Count);
                    }
                }
                ChartListReferenceInput input = source.References;
                if (pending.Capture != null)
                {
                    if (pending.Capture.Revision > input.Revision) { break; }
                    input = input.Include(pending.Capture, pending.Keys);
                }
                NormalLibrarySourceRows next = source.WithSnapshot(commit.Snapshot, commit.Changes, input);
                applied.Add(new(source, next, commit, null));
                source = next;
                catalogs.Remove(commit.Revision);
            }
            if (references.Count == 0) { return applied; }
            PlaylistReferenceCommitReceipt reference = references.First().Value;
            if (reference.Revision <= source.References.Revision) { references.Remove(reference.Revision); continue; }
            if (reference.BaseRevision > source.References.Revision) { return applied; }
            long boundary = RequiredReferenceRevision;
            if (boundary > source.References.Revision && reference.Revision > boundary)
            { throw new InvalidOperationException("The captured reference boundary was skipped."); }
            NormalLibrarySourceRows nextSource = source.WithReferenceCommit(reference, out HashSet<OwnedChartToken> changed);
            applied.Add(new(source, nextSource, null, changed));
            source = nextSource;
            references.Remove(reference.Revision);
        }
    }

    /// <summary>旧入力を比較に使い、新入力へ切替える一つの確定境界です。</summary>
    internal sealed record Step(NormalLibrarySourceRows Before, NormalLibrarySourceRows After,
        CatalogCommitReceipt Catalog, HashSet<OwnedChartToken> ReferenceTokens);
    private sealed class PendingCatalog(CatalogCommitReceipt commit)
    {
        internal CatalogCommitReceipt Commit { get; } = commit;
        internal HashSet<PlaylistEntryLookupKey> Keys { get; set; }
        internal PlaylistReferenceReadSnapshot Capture { get; set; }
    }
}
