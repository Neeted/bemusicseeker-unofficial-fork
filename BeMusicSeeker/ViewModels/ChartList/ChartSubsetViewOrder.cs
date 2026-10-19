using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

/// <summary>専用subsetの捕捉添字・hash依存を保ち、scoreの実対象だけを検索・順序へ反映します。</summary>
internal sealed class ChartSubsetViewOrder
{
    private readonly IReadOnlyList<ChartListSourceRow> source;
    private readonly ChartListOrder order;
    private readonly Func<ChartListSourceRow, bool> matches;
    private readonly Action<string, int> observe;
    private ImmutableList<Entry> entries;
    private readonly Dictionary<int, Entry> byIndex = [];
    private readonly Dictionary<string, HashSet<int>> byMd5 = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<int>> bySha256 = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<OwnedChartToken, HashSet<int>> byToken = [];
    private readonly Dictionary<string, int> folders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>必要な初回全評価の結果を採用し、表示filter前の元集合を一回だけ依存登録します。</summary>
    internal ChartSubsetViewOrder(IReadOnlyList<ChartListSourceRow> source, ChartListOrder order,
        Func<ChartListSourceRow, bool> matches, Action<string, int> observe)
    {
        this.source = source;
        this.order = order;
        this.matches = matches ?? (_ => true);
        this.observe = observe;
        ImmutableList<Entry>.Builder initial = ImmutableList.CreateBuilder<Entry>();
        foreach (int index in order.Indexes)
        {
            Entry entry = Capture(index);
            initial.Add(entry);
            byIndex.Add(index, entry);
            AddFolder(entry.Folder);
        }
        observe?.Invoke("order_buffer", initial.Count);
        entries = initial.ToImmutable();
        for (int i = 0; i < source.Count; i++)
        {
            observe?.Invoke("hash_dependency_visit", 1);
            AddKey(byMd5, source[i].Hash, i);
            AddKey(bySha256, source[i].Sha256, i);
            if (source[i].PackageEntry != null && source[i].Token is OwnedChartToken token)
            {
                if (!byToken.TryGetValue(token, out HashSet<int> indexes)) { byToken.Add(token, indexes = []); }
                indexes.Add(i);
            }
        }
    }

    /// <summary>現在のfilterを通過した件数です。</summary>
    internal int Count => entries.Count;
    /// <summary>表示所属と同時に局所加減算したfolder数です。</summary>
    internal int DistinctFolderCount => folders.Count;
    /// <summary>表示位置に対応する捕捉元集合の添字を返します。</summary>
    internal int SourceIndexAt(int index) => entries[index].SourceIndex;
    /// <summary>元集合の添字から現在位置を取得し、未訪問行を実体化しません。</summary>
    internal int IndexOf(int sourceIndex) => byIndex.TryGetValue(sourceIndex, out Entry entry) ? FindInsertion(entry) : -1;

    /// <summary>scoreの変更hashから非表示行を含む実依存添字を取得します。</summary>
    internal HashSet<int> GetIndexes(ScoreSnapshotChange change)
    {
        HashSet<int> result = [];
        CaptureIndexes(byMd5, change.Md5Keys, result);
        CaptureIndexes(bySha256, change.Sha256Keys, result);
        return result;
    }

    /// <summary>動的package入力の実対象tokenを取得し、path移転後も保持中のfolder情報を局所的に更新します。</summary>
    internal HashSet<int> GetIndexes(IReadOnlyList<CatalogCommitReceipt> commits)
    {
        HashSet<int> result = [];
        foreach (CatalogCommitReceipt commit in commits)
        {
            foreach (CatalogChartChange change in commit.Changes)
            {
                if (!byToken.TryGetValue(change.Token, out HashSet<int> indexes)) { continue; }
                result.UnionWith(indexes);
                if (StringComparer.OrdinalIgnoreCase.Equals(change.Before?.Md5, change.After?.Md5)
                    && StringComparer.OrdinalIgnoreCase.Equals(change.Before?.Sha256, change.After?.Sha256)) { continue; }
                foreach (int index in indexes)
                {
                    RemoveKey(byMd5, change.Before?.Md5, index);
                    RemoveKey(bySha256, change.Before?.Sha256, index);
                    AddKey(byMd5, source[index].Hash, index);
                    AddKey(bySha256, source[index].Sha256, index);
                }
            }
        }
        return result;
    }

    /// <summary>対象行だけの適否と比較キーを再取得し、不変木の変更経路だけを置換します。</summary>
    internal bool Refresh(HashSet<int> indexes)
    {
        bool changed = false;
        foreach (int index in indexes)
        {
            int before = IndexOf(index);
            Entry next = matches(source[index]) ? Capture(index) : null;
            if (before >= 0 && next != null && entries[before].Folder == next.Folder && Compare(entries[before], next) == 0) { continue; }
            if (before >= 0)
            {
                RemoveFolder(entries[before].Folder);
                entries = entries.RemoveAt(before);
                byIndex.Remove(index);
            }
            int after = -1;
            if (next != null)
            {
                after = FindInsertion(next);
                entries = entries.Insert(after, next);
                byIndex.Add(index, next);
                AddFolder(next.Folder);
            }
            changed |= before != after;
        }
        return changed;
    }

    private Entry Capture(int index)
    {
        observe?.Invoke("order_key", 1);
        return new(index, ChartListOrder.CaptureKey(source[index], order.ColumnName), source[index].Folder);
    }
    private int Compare(Entry left, Entry right)
    {
        observe?.Invoke("order_compare", 1);
        int result = ChartListOrder.CompareKeys(left.Key, right.Key, order.Direction);
        return result != 0 ? result : left.SourceIndex.CompareTo(right.SourceIndex);
    }
    private int FindInsertion(Entry entry)
    {
        int low = 0, high = entries.Count;
        while (low < high)
        {
            observe?.Invoke("order_access", 1);
            int middle = low + (high - low) / 2;
            if (Compare(entries[middle], entry) < 0) { low = middle + 1; } else { high = middle; }
        }
        return low;
    }
    private void AddFolder(string folder)
    {
        if (!string.IsNullOrWhiteSpace(folder)) { folders[folder] = folders.GetValueOrDefault(folder) + 1; }
    }
    private void RemoveFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !folders.TryGetValue(folder, out int count)) { return; }
        if (count == 1) { folders.Remove(folder); } else { folders[folder] = count - 1; }
    }
    private static void AddKey(Dictionary<string, HashSet<int>> target, string key, int index)
    {
        if (string.IsNullOrWhiteSpace(key)) { return; }
        if (!target.TryGetValue(key, out HashSet<int> indexes)) { target.Add(key, indexes = []); }
        indexes.Add(index);
    }
    private static void RemoveKey(Dictionary<string, HashSet<int>> target, string key, int index)
    {
        if (!string.IsNullOrWhiteSpace(key) && target.TryGetValue(key, out HashSet<int> indexes)
            && indexes.Remove(index) && indexes.Count == 0) { target.Remove(key); }
    }
    private static void CaptureIndexes(Dictionary<string, HashSet<int>> source, IEnumerable<string> keys, HashSet<int> result)
    {
        foreach (string key in keys)
        {
            if (!string.IsNullOrWhiteSpace(key) && source.TryGetValue(key, out HashSet<int> indexes)) { result.UnionWith(indexes); }
        }
    }
    private sealed record Entry(int SourceIndex, ChartListCapturedSortKey Key, string Folder);
}
