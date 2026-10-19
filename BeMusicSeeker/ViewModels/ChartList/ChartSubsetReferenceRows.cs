using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

/// <summary>専用一覧の元配列を変更せず、参照差分の実対象投影だけを差し替えます。未採用の新hash捕捉は要求の寿命内に限ります。</summary>
internal sealed class ChartSubsetReferenceRows : IReadOnlyList<ChartListSourceRow>
{
    private readonly IReadOnlyList<ChartListSourceRow> source;
    private readonly Dictionary<int, ChartListSourceRow> changed = [];
    private readonly SortedDictionary<int, PendingCatalog> catalogs = [];
    private readonly SortedDictionary<long, PlaylistReferenceCommitReceipt> commits = [];
    private int catalogRevision;
    private readonly Action<string, int> observe;

    /// <summary>既存の初回全評価に使った入力・参照位置を採用します。呼出元の配列は変更しません。</summary>
    internal ChartSubsetReferenceRows(IReadOnlyList<ChartListSourceRow> source, ChartListReferenceInput references, int catalogRevision, Action<string, int> observe)
    { this.source = source; References = references; this.catalogRevision = catalogRevision; this.observe = observe; }
    /// <summary>この元集合へ適用済みの参照位置と不変値です。</summary>
    internal ChartListReferenceInput References { get; private set; }
    /// <summary>新依存の捕捉位置まで未配送の入力があるかを返します。</summary>
    internal bool HasPending => catalogs.Count > 0 || commits.Count > 0;
    public int Count => source.Count;
    public ChartListSourceRow this[int index] => changed.TryGetValue(index, out ChartListSourceRow row) ? row : source[index];
    public IEnumerator<ChartListSourceRow> GetEnumerator() { for (int i = 0; i < Count; i++) { yield return this[i]; } }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>既存依存を捕捉位置Rへ進めてから新hashを組み込み、その後のrangeを新依存にも適用します。</summary>
    internal HashSet<int> Apply(ChartSubsetViewOrder order, IReadOnlyList<CatalogCommitReceipt> catalogChanges,
        IReadOnlyList<PlaylistReferenceCommitReceipt> referenceChanges, Action referenceValuesChanged)
    {
        foreach (CatalogCommitReceipt commit in catalogChanges)
        { if (commit.Revision > catalogRevision) { catalogs.TryAdd(commit.Revision, new(commit)); } }
        foreach (PlaylistReferenceCommitReceipt commit in referenceChanges)
        { if (commit.Revision > References.Revision) { commits.TryAdd(commit.Revision, commit); } }
        HashSet<int> affected = [];
        while (true)
        {
            while (catalogs.Count > 0)
            {
                PendingCatalog pending = catalogs.First().Value;
                if (pending.Keys == null)
                {
                    pending.Keys = new(CatalogReadSnapshot.LookupKeyComparer);
                    foreach (CatalogChartChange change in pending.Commit.Changes)
                    {
                        if (change.After == null || order.GetCatalogIndexes(change.Token).Count == 0) { continue; }
                        AddNewKey(order, pending.Keys, PlaylistEntryLookupKeyKind.Md5, change.After.Md5);
                        AddNewKey(order, pending.Keys, PlaylistEntryLookupKeyKind.Sha256, change.After.Sha256);
                    }
                    if (pending.Keys.Count > 0)
                    {
                        pending.Capture = References.Library?.CapturePlaylistReferenceReadSnapshot(pending.Keys)
                        ?? new(References.Revision, Array.Empty<KeyValuePair<PlaylistEntryLookupKey, PlaylistReferenceDisplay>>());
                    }
                }
                if (pending.Capture != null)
                {
                    if (pending.Capture.Revision > References.Revision) { break; }
                    References = References.Include(pending.Capture, pending.Keys);
                }
                affected.UnionWith(order.GetIndexes([pending.Commit]));
                foreach (CatalogChartChange change in pending.Commit.Changes)
                {
                    if (change.After != null)
                    { RefreshValues(order.GetCatalogIndexes(change.Token), affected, referenceValuesChanged, change.After); }
                }
                var removedKeys = new HashSet<PlaylistEntryLookupKey>(CatalogReadSnapshot.LookupKeyComparer);
                foreach (CatalogChartChange change in pending.Commit.Changes)
                {
                    if (!string.IsNullOrWhiteSpace(change.Before?.Md5)) { removedKeys.Add(new(PlaylistEntryLookupKeyKind.Md5, change.Before.Md5)); }
                    if (!string.IsNullOrWhiteSpace(change.Before?.Sha256)) { removedKeys.Add(new(PlaylistEntryLookupKeyKind.Sha256, change.Before.Sha256)); }
                }
                References = References.RemoveDependencies(removedKeys.Where(key => !order.ContainsReferenceKey(key)));
                catalogRevision = pending.Commit.Revision;
                catalogs.Remove(catalogRevision);
            }
            if (commits.Count == 0) { return affected; }
            PlaylistReferenceCommitReceipt next = commits.First().Value;
            if (next.Revision <= References.Revision) { commits.Remove(next.Revision); continue; }
            if (next.BaseRevision > References.Revision) { return affected; }
            long boundary = catalogs.Count > 0 ? catalogs.First().Value.Capture?.Revision ?? 0 : 0;
            if (boundary > References.Revision && next.Revision > boundary)
            { throw new InvalidOperationException("The subset reference boundary was skipped."); }
            References = References.Apply(next, order.ContainsReferenceKey);
            RefreshValues(order.GetReferenceIndexes(next.Changes.Keys), affected, referenceValuesChanged);
            commits.Remove(next.Revision);
        }
    }

    private void RefreshValues(IEnumerable<int> indexes, HashSet<int> affected, Action referenceValuesChanged, ChartFile referenceChart = null)
    {
        foreach (int index in indexes)
        {
            observe?.Invoke("source_visit", 1);
            ChartListSourceRow before = this[index];
            ChartListSourceRow after = before.WithReferenceDisplay(References.Resolve(
                referenceChart != null ? referenceChart.Md5 : before.ReferenceMd5,
                referenceChart != null ? referenceChart.Sha256 : before.ReferenceSha256), referenceChart);
            if (ReferenceEquals(before, after)) { continue; }
            observe?.Invoke("projection", 1);
            if (before.RefTablesSymbols != after.RefTablesSymbols || before.RefTablesNames != after.RefTablesNames)
            { referenceValuesChanged?.Invoke(); }
            changed[index] = after;
            affected.Add(index);
        }
    }
    private static void AddNewKey(ChartSubsetViewOrder order, HashSet<PlaylistEntryLookupKey> keys, PlaylistEntryLookupKeyKind kind, string hash)
    {
        if (!string.IsNullOrWhiteSpace(hash) && !order.ContainsReferenceKey(new(kind, hash))) { keys.Add(new(kind, hash)); }
    }
    private sealed class PendingCatalog(CatalogCommitReceipt commit)
    {
        internal CatalogCommitReceipt Commit { get; } = commit;
        internal HashSet<PlaylistEntryLookupKey> Keys { get; set; }
        internal PlaylistReferenceReadSnapshot Capture { get; set; }
    }
}
