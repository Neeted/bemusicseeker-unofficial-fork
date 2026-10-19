using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>表/keyの多重度と表示だけを保持する派生索引です。entryの第二正本は持たず、変更bucketだけを再生成します。</summary>
internal sealed class PlaylistReferenceIndex
{
    /// <summary>空の派生索引を新規作成します。選択表集合は明示確定で登録します。</summary>
    internal static PlaylistReferenceIndex Empty => new();
    private readonly Dictionary<PlaylistEntryLookupKey, Dictionary<BMSTable, int>> buckets = new(CatalogReadSnapshot.LookupKeyComparer);
    private readonly Dictionary<PlaylistEntryLookupKey, PlaylistReferenceDisplay> displays = new(CatalogReadSnapshot.LookupKeyComparer);
    private readonly Dictionary<BMSTable, TableState> tables = new();
    /// <summary>MD5 bucketの存在を優先し、不在時だけSHAへ進みます。空表示も存在として扱います。</summary>
    internal PlaylistReferenceDisplay Find(string md5, string sha256)
        => TryGet(new(PlaylistEntryLookupKeyKind.Md5, md5), out PlaylistReferenceDisplay value)
            || TryGet(new(PlaylistEntryLookupKeyKind.Sha256, sha256), out value) ? value : PlaylistReferenceDisplay.Empty;
    /// <summary>捕捉済み所持譜面のhashから参照表示を引きます。</summary>
    internal PlaylistReferenceDisplay Find(ChartFile chart) => chart == null ? PlaylistReferenceDisplay.Empty : Find(chart.Md5, chart.Sha256);
    /// <summary>捕捉済み譜面参照のhashから参照表示を引きます。</summary>
    internal PlaylistReferenceDisplay Find(LibraryChartRef chart) => chart == null ? PlaylistReferenceDisplay.Empty : Find(chart.Md5, chart.Sha256);
    /// <summary>空表示も存在として取得します。不在時だけfalseです。</summary>
    internal bool TryGet(PlaylistEntryLookupKey key, out PlaylistReferenceDisplay value) => displays.TryGetValue(key, out value);
    /// <summary>初回捕捉した選択表全体から派生索引を構築します。</summary>
    internal static PlaylistReferenceIndex FromSnapshots(IEnumerable<PlaylistReferenceTableSnapshot> snapshots)
    {
        var index = new PlaylistReferenceIndex(); index.ReplaceTables(snapshots, synchronize: true); return index;
    }
    /// <summary>有限な表の置換、または選択集合全体を置換します。通常entry差分では呼びません。</summary>
    internal ChangeResult ReplaceTables(IEnumerable<PlaylistReferenceTableSnapshot> snapshots, bool synchronize = false, Action<string> observe = null,
        IEnumerable<PlaylistReferenceTableHeader> headers = null, IEnumerable<BMSTable> removals = null)
    {
        PlaylistReferenceTableSnapshot[] source = [.. snapshots];
        var change = new ChangeSession(this, observe);
        foreach (BMSTable table in removals ?? []) { RemoveTableCore(table, change); }
        if (synchronize)
        {
            var retained = source.Select(snapshot => snapshot.Table).ToHashSet();
            foreach (BMSTable table in tables.Keys.Where(table => !retained.Contains(table)).ToArray()) { RemoveTableCore(table, change); }
        }
        foreach (PlaylistReferenceTableSnapshot snapshot in source)
        {
            var counts = new Dictionary<PlaylistEntryLookupKey, int>(CatalogReadSnapshot.LookupKeyComparer);
            foreach (PlaylistReferenceEntrySnapshot entry in snapshot.Entries)
            {
                observe?.Invoke("reference_entry_indexed");
                if (!entry.LookupKey.HasValue) { continue; }
                counts.TryGetValue(entry.LookupKey, out int count); counts[entry.LookupKey] = count + 1;
            }
            if (tables.TryGetValue(snapshot.Table, out TableState previous) && previous.Header == snapshot.Header
                && previous.Counts.Count == counts.Count && counts.All(pair => previous.Counts.TryGetValue(pair.Key, out int count) && count == pair.Value)) { continue; }
            RemoveTableCore(snapshot.Table, change); tables.Add(snapshot.Table, new(snapshot.Header)); change.HasChanges = true;
            foreach ((PlaylistEntryLookupKey key, int count) in counts) { ChangeCount(snapshot.Table, key, count, change); }
        }
        ApplyHeaders(headers, change);
        return change.Complete();
    }
    /// <summary>退役表の登録済みkeyだけを除去します。未読込のlive entryは参照しません。</summary>
    internal ChangeResult RemoveTables(IEnumerable<BMSTable> removals, Action<string> observe = null)
    {
        var change = new ChangeSession(this, observe);
        foreach (BMSTable table in removals) { RemoveTableCore(table, change); }
        return change.Complete();
    }
    /// <summary>全guard解放後に渡された同groupのentry差分とheaderを一回適用します。URL等のkey不変変更は索引を変更しません。</summary>
    internal ChangeResult Apply(IReadOnlyList<PlaylistEntryCommitReceipt> receipts, IEnumerable<PlaylistReferenceTableHeader> headers, Action<string> observe = null)
    {
        var change = new ChangeSession(this, observe);
        ApplyHeaders(headers, change);
        var deltas = new Dictionary<BMSTable, Dictionary<PlaylistEntryLookupKey, int>>();
        foreach (PlaylistEntryCommitReceipt receipt in receipts ?? [])
        {
            if (!tables.ContainsKey(receipt.Table)) { continue; }
            foreach (PlaylistEntryChange entry in receipt.Changes)
            {
                observe?.Invoke("reference_entry_delta");
                PlaylistEntryLookupKey before = LookupKey(entry.Before), after = LookupKey(entry.After);
                if (CatalogReadSnapshot.LookupKeyComparer.Equals(before, after)) { continue; }
                AddDelta(receipt.Table, before, -1);
                AddDelta(receipt.Table, after, 1);
            }
        }
        foreach ((BMSTable table, Dictionary<PlaylistEntryLookupKey, int> values) in deltas)
        {
            foreach ((PlaylistEntryLookupKey key, int delta) in values) { ChangeCount(table, key, delta, change); }
        }
        return change.Complete();

        void AddDelta(BMSTable table, PlaylistEntryLookupKey key, int delta)
        {
            if (!key.HasValue) { return; }
            if (!deltas.TryGetValue(table, out Dictionary<PlaylistEntryLookupKey, int> values))
            {
                deltas.Add(table, values = new(CatalogReadSnapshot.LookupKeyComparer));
            }
            values.TryGetValue(key, out int previous);
            values[key] = previous + delta;
        }
    }
    private void ApplyHeaders(IEnumerable<PlaylistReferenceTableHeader> headers, ChangeSession change)
    {
        foreach (PlaylistReferenceTableHeader header in headers ?? [])
        {
            if (!tables.TryGetValue(header.Table, out TableState state) || state.Header == header) { continue; }
            foreach (PlaylistEntryLookupKey key in state.Counts.Keys) { change.Observe("reference_header_key_visited"); change.Touch(key); }
            state.Header = header; change.HasChanges = true;
        }
    }
    private static PlaylistEntryLookupKey LookupKey(PlaylistEntryValue value)
        => value == null || value.IsRemoved ? default : !string.IsNullOrWhiteSpace(value.Md5)
            ? new(PlaylistEntryLookupKeyKind.Md5, value.Md5) : new(PlaylistEntryLookupKeyKind.Sha256, value.Sha256);
    private void RemoveTableCore(BMSTable table, ChangeSession change)
    {
        if (!tables.TryGetValue(table, out TableState state)) { return; }
        foreach ((PlaylistEntryLookupKey key, int count) in state.Counts.ToArray()) { ChangeCount(table, key, -count, change); }
        tables.Remove(table); change.HasChanges = true;
    }
    private void ChangeCount(BMSTable table, PlaylistEntryLookupKey key, int delta, ChangeSession change)
    {
        if (!key.HasValue || delta == 0) { return; }
        TableState state = tables[table]; state.Counts.TryGetValue(key, out int previous); int count = previous + delta;
        if (count < 0) { throw new InvalidOperationException("Playlist reference multiplicity has no matching entry."); }
        change.Touch(key);
        if (count == 0) { state.Counts.Remove(key); } else { state.Counts[key] = count; }
        if (!buckets.TryGetValue(key, out Dictionary<BMSTable, int> bucket)) { buckets[key] = bucket = []; }
        if (count == 0) { bucket.Remove(table); } else { bucket[table] = count; }
        if (bucket.Count == 0) { buckets.Remove(key); }
        change.HasChanges = true;
    }
    private sealed class TableState(PlaylistReferenceTableHeader header)
    {
        internal PlaylistReferenceTableHeader Header { get; set; } = header;
        internal Dictionary<PlaylistEntryLookupKey, int> Counts { get; } = new(CatalogReadSnapshot.LookupKeyComparer);
    }
    /// <summary>同じowner排他内で不変receiptへ変換する短命な結果です。Changesのnull値はbucket消失です。</summary>
    internal sealed record ChangeResult(bool HasChanges, IReadOnlyDictionary<PlaylistEntryLookupKey, PlaylistReferenceDisplay> Changes);
    private sealed class ChangeSession(PlaylistReferenceIndex owner, Action<string> observe)
    {
        private readonly Dictionary<PlaylistEntryLookupKey, PlaylistReferenceDisplay> before = new(CatalogReadSnapshot.LookupKeyComparer);
        internal bool HasChanges { get; set; }
        internal void Observe(string phase) => observe?.Invoke(phase);
        internal void Touch(PlaylistEntryLookupKey key)
        {
            if (before.ContainsKey(key)) { return; }
            owner.displays.TryGetValue(key, out PlaylistReferenceDisplay value); before.Add(key, value); observe?.Invoke("reference_bucket_update");
        }
        internal ChangeResult Complete()
        {
            var changes = new Dictionary<PlaylistEntryLookupKey, PlaylistReferenceDisplay>(CatalogReadSnapshot.LookupKeyComparer);
            foreach ((PlaylistEntryLookupKey key, PlaylistReferenceDisplay old) in before)
            {
                PlaylistReferenceDisplay next = null;
                if (owner.buckets.TryGetValue(key, out Dictionary<BMSTable, int> bucket))
                {
                    observe?.Invoke("reference_display_generated");
                    next = new PlaylistReferenceDisplay([.. bucket.Keys.Select(table => { observe?.Invoke("reference_bucket_member_visited"); return owner.tables[table].Header; })
                        .OrderBy(header => header.BmtSort ?? int.MaxValue).ThenBy(header => header.TableOrder)
                        .Select(header => new PlaylistReferenceTableDisplaySnapshot(header.Symbol, header.Name))]);
                    owner.displays[key] = next;
                }
                else { owner.displays.Remove(key); }
                if ((old == null) != (next == null) || old != null && next != null && (old.Symbols != next.Symbols || old.Names != next.Names)) { changes[key] = next; }
            }
            return new(HasChanges, changes);
        }
    }
}
